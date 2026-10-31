using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace DriveMonitor.Core.Tracing;

/// <summary>Receives FileIo events from <see cref="EtwFileIoSource"/> on the ETW consumer thread.</summary>
internal interface IFileIoSink
{
    /// <summary>
    /// Early filter, called for every file event in the system. May rewrite <paramref name="path"/>
    /// (a kernel path whose volume has received a drive letter since the name was recorded).
    /// False for events on unmonitored drives, unnamed files and excluded processes.
    /// </summary>
    bool TryAccept(ref string path, int processId, out char drive);

    void OnBegin(PendingIo io);

    void OnComplete(ulong irp, long timestampQpc, ulong extraInfo, int ntStatus);

    /// <summary>A path from a FileIo_PathOperation event (rename or hard-link target), already converted to a DOS path when possible.</summary>
    void OnPathOperation(ulong irp, long timestampQpc, string path);
}

/// <summary>
/// Real-time kernel ETW session that forwards file I/O initiations and completions to an
/// <see cref="IFileIoSink"/>. Events arrive for the whole system; the sink filters them.
/// </summary>
/// <remarks>
/// Built around how TraceEvent 4.0 behaves (verified in its source): the kernel provider must be
/// enabled before <c>session.Source</c> is first read; <c>session.Source</c> must never be read
/// after Dispose (that silently starts a new session); an exception escaping a callback ends the
/// session; TraceEvent forgets a FileObject's name at Cleanup and a FileKey's name at rundown,
/// so this class keeps its own name tables as a fallback.
/// </remarks>
internal sealed class EtwFileIoSource : IDisposable
{
    /// <summary>Event-trace class GUID of the kernel FileIo events.</summary>
    internal static readonly Guid FileIoTaskGuid = new("90cbdc39-4a3e-11d1-84f4-0000f80464e3");

    // Kernel rundown control GUID. Enabling KERNEL_FILE_KEYWORD_FILENAME on it makes the kernel list
    // the names of files that are already open (FileIo opcode 36). Not documented by Microsoft; the
    // technique comes from System Informer. If it does nothing, those files simply stay unnamed.
    private static readonly Guid KernelRundownGuid = new("3b9c9951-3480-4220-9377-9c8e5184f5cd");
    private const ulong KernelFileKeywordFileName = 0x10;

    // Undocumented FileIo_PathOperation opcodes on Windows 11 (not parsed by TraceEvent 4.0).
    private const int OpcodeRenamePath = 80;
    private const int OpcodeSetLinkPath = 81;

    private const int MaxCachedNames = 500_000;
    private const int ErrorNoSystemResources = unchecked((int)0x800705AA);

    /// <summary>How often the lost-event count is read when <see cref="MonitorOptions.FlushIntervalMs"/> sets no timer of its own.</summary>
    private const int EventsLostPollMs = 1000;

    private readonly IFileIoSink _sink;
    private readonly ProcessNameCache _processNames;
    private readonly Func<string, string> _toDosPath;
    private readonly MonitorOptions _options;
    private readonly object _sessionLock = new();

    // Names this class remembers itself; touched only on the ETW consumer thread.
    private readonly Dictionary<ulong, string> _nameByFileKey = new();
    private readonly Dictionary<ulong, string> _nameByFileObject = new();

    private TraceEventSession? _session;
    private Thread? _thread;
    private Timer? _flushTimer;
    private volatile bool _stopping;
    private long _kernelEvents;
    private long _callbackErrors;
    private volatile string? _lastCallbackError;
    private volatile bool _pathOperationsSeen;
    private int _eventsLost;

    public EtwFileIoSource(IFileIoSink sink, ProcessNameCache processNames, Func<string, string> toDosPath, MonitorOptions options)
    {
        _sink = sink;
        _processNames = processNames;
        _toDosPath = toDosPath;
        _options = options;
    }

    /// <summary>Raised on the consumer thread when the session ends without <see cref="Stop"/> being called.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>File I/O events received from the kernel, before any filtering.</summary>
    public long KernelEvents => Volatile.Read(ref _kernelEvents);

    /// <summary>Exceptions caught inside event callbacks (each one cost a single event, not the session).</summary>
    public long CallbackErrors => Interlocked.Read(ref _callbackErrors);

    public string? LastCallbackError => _lastCallbackError;

    /// <summary>True once Windows has delivered at least one FileIo_PathOperation event.</summary>
    public bool PathOperationsSeen => _pathOperationsSeen;

    /// <summary>Whether the request for names of already-open files was accepted (not whether Windows acted on it).</summary>
    public bool RundownRequested { get; private set; }

    public string? RundownError { get; private set; }

    /// <summary>
    /// Events the kernel dropped because the consumer fell behind (live while running, last value after).
    /// The timer also reads it, so a session that another program stops leaves out at most one tick of losses.
    /// </summary>
    public int EventsLost
    {
        get
        {
            lock (_sessionLock)
            {
                if (_session is not null && !_stopping)
                    ReadEventsLost(_session);
                return _eventsLost;
            }
        }
    }

    /// <exception cref="MonitorStartException">The session could not be created.</exception>
    public void Start()
    {
        if (_session is not null)
            throw new InvalidOperationException("The trace session is already running.");

        _stopping = false;

        TraceEventSession? session = null;
        ETWTraceEventSource source;
        try
        {
            // Create replaces a leftover session with the same name (e.g. after a crash). Assigned before
            // its properties are set, so the catch below releases it whichever step fails.
            session = new TraceEventSession(_options.SessionName, TraceEventSessionOptions.Create);
            session.StopOnDispose = true;
            session.BufferSizeMB = _options.BufferSizeMB;

            // Must be the first call on the session: with a custom name TraceEvent starts it here as a
            // system logger, and reading session.Source first would start an ordinary session that
            // cannot take kernel keywords. TraceEvent adds the Process and Thread keywords itself.
            session.EnableKernelProvider(KernelKeywords());

            // Read Source exactly once: after Dispose, reading it again silently starts a new session.
            source = session.Source;
            KernelTraceEventParser kernel = source.Kernel;

            // Replaces TraceEvent's own mapper, which reads drive letters once and never learns about
            // drives plugged in later. Names are converted when Create/Name events are decoded, so the
            // mapper has to be in place before Process().
            kernel.KernelPathToUserPathMapper = MapPath;
            Subscribe(source, kernel);
        }
        catch (Exception ex)
        {
            // Null when the constructor itself failed; then there is nothing to release.
            if (session is not null)
                DisposeQuietly(session);
            throw Translate(ex);
        }

        lock (_sessionLock)
            _session = session;

        _thread = new Thread(ProcessEvents)
        {
            IsBackground = true,
            Name = "DriveMonitor ETW consumer",
            // A starved consumer lets kernel buffers overflow, which loses events.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start(source);

        if (_options.RequestFileNameRundown)
            RequestFileNameRundown(session);

        // Runs even when nothing is flushed: once another program stops the session, its lost-event
        // count can no longer be read, so the timer keeps a recent copy.
        int period = _options.FlushIntervalMs > 0 ? _options.FlushIntervalMs : EventsLostPollMs;
        _flushTimer = new Timer(_ => FlushBuffers(), null, period, period);
    }

    public void Stop()
    {
        _stopping = true;
        Interlocked.Exchange(ref _flushTimer, null)?.Dispose();

        TraceEventSession? session;
        lock (_sessionLock)
        {
            session = _session;
            if (session is not null)
                ReadEventsLost(session);
            _session = null;
        }

        if (session is null)
            return;

        // Stop first: the kernel then delivers what is still buffered and Process() returns by itself.
        // Dispose closes the consumer and discards undelivered events, so it comes after the drain.
        try
        {
            session.Stop(noThrow: true);
        }
        catch (Exception)
        {
            // Stopped by someone else already.
        }

        Thread? thread = Interlocked.Exchange(ref _thread, null);
        bool drained = thread is null || thread == Thread.CurrentThread || thread.Join(TimeSpan.FromSeconds(5));
        DisposeQuietly(session);
        if (!drained)
            thread!.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Parses the payload of a FileIo_PathOperation event: IrpPtr, FileObject, FileKey, ExtraInfo
    /// (pointer-sized), TTID (u32), InfoClass (u32), FileName (NUL-terminated UTF-16).
    /// </summary>
    /// <returns>False unless the payload has that shape and the name is a kernel path.</returns>
    internal static bool TryParsePathOperation(ReadOnlySpan<byte> payload, int pointerSize, out ulong irp, out string path)
    {
        irp = 0;
        path = "";
        if (pointerSize is not (4 or 8))
            return false;

        int nameOffset = (4 * pointerSize) + 8;
        if (payload.Length < nameOffset + 2)
            return false;

        irp = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(payload) : BinaryPrimitives.ReadUInt32LittleEndian(payload);
        ReadOnlySpan<byte> name = payload[nameOffset..];
        int end = 0;
        while (end + 1 < name.Length && (name[end] | name[end + 1]) != 0)
            end += 2;
        if (end == 0)
            return false;

        path = Encoding.Unicode.GetString(name[..end]);
        return path[0] == '\\';
    }

    private KernelTraceEventParser.Keywords KernelKeywords()
    {
        // FileIOInit: create, read, write, set-info, delete, rename, cleanup, close, flush, dir-enum.
        // DiskFileIO (+ DiskIO, which it requires): FileKey → name events.
        // FileIO: FileIo_OpEnd completions with the final status and bytes transferred.
        var keywords = KernelTraceEventParser.Keywords.FileIOInit
            | KernelTraceEventParser.Keywords.DiskFileIO
            | KernelTraceEventParser.Keywords.DiskIO;
        if (_options.UseCompletionEvents)
            keywords |= KernelTraceEventParser.Keywords.FileIO;
        return keywords;
    }

    private void Subscribe(ETWTraceEventSource source, KernelTraceEventParser kernel)
    {
        kernel.ProcessStart += OnProcess;
        kernel.ProcessDCStart += OnProcess;

        kernel.FileIOName += OnName;
        kernel.FileIOFileCreate += OnName;
        kernel.FileIOFileRundown += OnName;
        kernel.FileIOFileDelete += OnNameReleased; // the name record was released, NOT a file deletion
        kernel.FileIOClose += OnClose;

        kernel.FileIOCreate += OnCreate;
        kernel.FileIORead += OnRead;
        kernel.FileIOWrite += OnWrite;
        kernel.FileIOSetInfo += OnSetInfo;
        kernel.FileIODelete += OnDelete;
        kernel.FileIORename += OnRename;
        kernel.FileIOFlush += OnFlush;
        if (_options.CaptureDirectoryListings)
            kernel.FileIODirEnum += OnDirEnum;
        if (_options.UseCompletionEvents)
            kernel.FileIOOperationEnd += OnOperationEnd;
        if (_options.ParsePathOperations)
            source.UnhandledEvents += OnUnhandled;
    }

    private void ProcessEvents(object? state)
    {
        var source = (ETWTraceEventSource)state!;
        try
        {
            source.Process();
            if (!_stopping)
                Faulted?.Invoke(new InvalidOperationException("The kernel trace session was stopped by another program."));
        }
        catch (Exception ex)
        {
            if (!_stopping)
                Faulted?.Invoke(ex);
        }
    }

    private void RequestFileNameRundown(TraceEventSession session)
    {
        try
        {
            session.EnableProvider(KernelRundownGuid, TraceEventLevel.Always, KernelFileKeywordFileName);
            RundownRequested = true;
        }
        catch (Exception ex)
        {
            RundownError = ex.Message;
        }
    }

    private void FlushBuffers()
    {
        lock (_sessionLock)
        {
            if (_session is null || _stopping)
                return;

            // TraceEvent fixes the kernel flush timer at 1 s; flushing more often shortens the delay
            // before events reach the screen.
            if (_options.FlushIntervalMs > 0)
            {
                try
                {
                    _session.Flush();
                }
                catch (Exception)
                {
                    // Session ending.
                }
            }

            ReadEventsLost(_session);
        }
    }

    /// <summary>Keeps the kernel's count of lost events while the session can still report it. Call under <c>_sessionLock</c>.</summary>
    private void ReadEventsLost(TraceEventSession session)
    {
        try
        {
            _eventsLost = session.EventsLost;
        }
        catch (Exception)
        {
            // The session is ending or was stopped by another program: keep the last count read.
        }
    }

    private void OnProcess(ProcessTraceData d)
    {
        try
        {
            _processNames.OnProcessStarted(d.ProcessID, ProcessNameCache.BestImageName(d.ImageFileName, d.CommandLine));
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    private void OnName(FileIONameTraceData d)
    {
        try
        {
            Remember(_nameByFileKey, d.FileKey, d.FileName);
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    private void OnNameReleased(FileIONameTraceData d)
    {
        try
        {
            _nameByFileKey.Remove(d.FileKey);
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    private void OnClose(FileIOSimpleOpTraceData d)
    {
        try
        {
            // Close, not Cleanup: paging I/O can still use the file object after its last handle closed.
            _nameByFileObject.Remove(d.FileObject);
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    private void OnCreate(FileIOCreateTraceData d)
    {
        try
        {
            _kernelEvents++;
            string path = d.FileName ?? "";
            Remember(_nameByFileObject, d.FileObject, path);
            int pid = d.ProcessID;
            if (!_sink.TryAccept(ref path, pid, out char drive))
                return;

            _sink.OnBegin(new PendingIo
            {
                Op = RawFileOp.Create,
                Irp = d.IrpPtr,
                FileObject = d.FileObject,
                TimestampQpc = Qpc(d),
                Timestamp = d.TimeStamp,
                Path = path,
                Drive = drive,
                ProcessId = pid,
                ThreadId = d.ThreadID,
                ProcessName = ProcessName(pid, d),
                CreateOptions = (int)d.CreateOptions,
                CreateDisposition = (int)d.CreateDisposition,
            });
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    private void OnRead(FileIOReadWriteTraceData d) => OnReadWrite(d, RawFileOp.Read);

    private void OnWrite(FileIOReadWriteTraceData d) => OnReadWrite(d, RawFileOp.Write);

    private void OnReadWrite(FileIOReadWriteTraceData d, RawFileOp op)
    {
        try
        {
            _kernelEvents++;
            string path = ResolveName(d.FileName, d.FileKey, d.FileObject);
            int pid = d.ProcessID;
            if (!_sink.TryAccept(ref path, pid, out char drive))
                return;

            _sink.OnBegin(new PendingIo
            {
                Op = op,
                Irp = d.IrpPtr,
                FileKey = d.FileKey,
                FileObject = d.FileObject,
                TimestampQpc = Qpc(d),
                Timestamp = d.TimeStamp,
                Path = path,
                Drive = drive,
                ProcessId = pid,
                ThreadId = d.ThreadID,
                ProcessName = ProcessName(pid, d),
                IoSize = d.IoSize,
                Offset = d.Offset,
                IrpFlags = d.IoFlags,
            });
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    private void OnSetInfo(FileIOInfoTraceData d) => OnInfo(d, RawFileOp.SetInfo);

    private void OnDelete(FileIOInfoTraceData d) => OnInfo(d, RawFileOp.Delete);

    private void OnRename(FileIOInfoTraceData d) => OnInfo(d, RawFileOp.Rename);

    private void OnInfo(FileIOInfoTraceData d, RawFileOp op)
    {
        try
        {
            _kernelEvents++;
            string path = ResolveName(d.FileName, d.FileKey, d.FileObject);
            int pid = d.ProcessID;
            if (!_sink.TryAccept(ref path, pid, out char drive))
                return;

            _sink.OnBegin(new PendingIo
            {
                Op = op,
                Irp = d.IrpPtr,
                FileKey = d.FileKey,
                FileObject = d.FileObject,
                TimestampQpc = Qpc(d),
                Timestamp = d.TimeStamp,
                Path = path,
                Drive = drive,
                ProcessId = pid,
                ThreadId = d.ThreadID,
                ProcessName = ProcessName(pid, d),
                InfoClass = d.InfoClass,
                ExtraInfo = d.ExtraInfo,
            });
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    private void OnFlush(FileIOSimpleOpTraceData d)
    {
        try
        {
            _kernelEvents++;
            string path = ResolveName(d.FileName, d.FileKey, d.FileObject);
            int pid = d.ProcessID;
            if (!_sink.TryAccept(ref path, pid, out char drive))
                return;

            _sink.OnBegin(new PendingIo
            {
                Op = RawFileOp.Flush,
                Irp = d.IrpPtr,
                FileKey = d.FileKey,
                FileObject = d.FileObject,
                TimestampQpc = Qpc(d),
                Timestamp = d.TimeStamp,
                Path = path,
                Drive = drive,
                ProcessId = pid,
                ThreadId = d.ThreadID,
                ProcessName = ProcessName(pid, d),
            });
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    private void OnDirEnum(FileIODirEnumTraceData d)
    {
        try
        {
            _kernelEvents++;
            string path = ResolveName(d.DirectoryName, d.FileKey, d.FileObject);
            int pid = d.ProcessID;
            if (!_sink.TryAccept(ref path, pid, out char drive))
                return;

            _sink.OnBegin(new PendingIo
            {
                Op = RawFileOp.DirEnum,
                Irp = d.IrpPtr,
                FileKey = d.FileKey,
                FileObject = d.FileObject,
                TimestampQpc = Qpc(d),
                Timestamp = d.TimeStamp,
                Path = path,
                Drive = drive,
                ProcessId = pid,
                ThreadId = d.ThreadID,
                ProcessName = ProcessName(pid, d),
                Pattern = d.FileName,
            });
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    private void OnOperationEnd(FileIOOpEndTraceData d)
    {
        try
        {
            _kernelEvents++;
            _sink.OnComplete(d.IrpPtr, Qpc(d), d.ExtraInfo, d.NtStatus);
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    private void OnUnhandled(TraceEvent data)
    {
        try
        {
            int opcode = (int)data.Opcode;
            if (opcode is not (OpcodeRenamePath or OpcodeSetLinkPath))
                return;
            if (data.TaskGuid != FileIoTaskGuid && data.ProviderGuid != FileIoTaskGuid)
                return;
            if (!TryParsePathOperation(data.EventData(), data.PointerSize, out ulong irp, out string kernelPath))
                return;

            _pathOperationsSeen = true;
            _sink.OnPathOperation(irp, Qpc(data), MapPath(kernelPath));
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
        }
    }

    /// <summary>
    /// TraceEvent's name (it tries FileKey, then FileObject), else the names this class kept after
    /// TraceEvent dropped them. Also learns FileKey → name so later cache flushes, which often use
    /// another file object, can be named.
    /// </summary>
    private string ResolveName(string? traced, ulong fileKey, ulong fileObject)
    {
        if (!string.IsNullOrEmpty(traced))
        {
            if (fileKey != 0 && !_nameByFileKey.ContainsKey(fileKey))
                Remember(_nameByFileKey, fileKey, traced);
            return traced;
        }

        if (fileKey != 0 && _nameByFileKey.TryGetValue(fileKey, out string? byKey))
            return byKey;
        if (fileObject != 0 && _nameByFileObject.TryGetValue(fileObject, out string? byObject))
            return byObject;
        return "";
    }

    private static void Remember(Dictionary<ulong, string> names, ulong key, string name)
    {
        if (key == 0 || string.IsNullOrEmpty(name))
            return;

        // Entries normally leave at Close / name release; this only guards against lost events.
        if (names.Count >= MaxCachedNames && !names.ContainsKey(key))
            names.Clear();
        names[key] = name;
    }

    /// <summary>
    /// The KernelPathToUserPathMapper. TraceEvent stores its result as the file name, so it must
    /// never throw or return null, and must leave non-paths such as a "*" search pattern alone.
    /// </summary>
    private string MapPath(string kernelPath)
    {
        if (string.IsNullOrEmpty(kernelPath))
            return "";
        try
        {
            return _toDosPath(kernelPath) ?? kernelPath;
        }
        catch (Exception ex)
        {
            RecordCallbackError(ex);
            return kernelPath;
        }
    }

    private string ProcessName(int pid, TraceEvent data) =>
        _processNames.TryGet(pid) ?? _processNames.Resolve(pid, data.ProcessName);

    private void RecordCallbackError(Exception ex)
    {
        Interlocked.Increment(ref _callbackErrors);
        _lastCallbackError = $"{ex.GetType().Name}: {ex.Message}";
    }

    private static MonitorStartException Translate(Exception ex) => ex switch
    {
        MonitorStartException start => start,
        UnauthorizedAccessException => new MonitorStartException(
            "Administrator rights are required to start the kernel trace session.", ex),
        COMException { HResult: ErrorNoSystemResources } => new MonitorStartException(
            "Windows has no free kernel trace session: other tracing tools (Windows Performance Recorder, " +
            "profilers, security software) are using all of them. Close one of them and try again.", ex),
        _ => new MonitorStartException($"The kernel trace session could not be started: {ex.Message}", ex),
    };

    private static void DisposeQuietly(TraceEventSession session)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception)
        {
            // Nothing left to release.
        }
    }

    // TraceEvent marks TimeStampQPC "not obsolete but discouraged" in favour of a double;
    // completion matching needs the exact integer clock to order events logged microseconds apart.
#pragma warning disable CS0618
    private static long Qpc(TraceEvent data) => data.TimeStampQPC;
#pragma warning restore CS0618
}
