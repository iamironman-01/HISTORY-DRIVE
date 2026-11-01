using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Export;
using DriveMonitor.Core.Filtering;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Tracing;
using DriveMonitor.Core.Volumes;
using Microsoft.Diagnostics.Tracing.Session;

namespace DriveMonitor.Core;

/// <summary>
/// Watches every file read, write, create, delete and rename on the monitored drives.
/// </summary>
/// <remarks>
/// Pipeline: kernel ETW session (whole system) → drive/process filter → reordering by timestamp →
/// completion matching → classification → statistics, optional CSV log, and a queue that the UI or CLI
/// drains with <see cref="DrainEvents"/>. Requires administrator rights.
/// </remarks>
public sealed class MonitorEngine : IDisposable, IFileIoSink
{
    private const int ReleaseIntervalMs = 250;
    private const int MaxRestartsPerMinute = 3;
    private const int SessionCheckIntervalMs = 200;

    /// <summary>How long Start waits for the session of a DriveMonitor that holds the guard but has none running yet.</summary>
    private static readonly TimeSpan SessionSettleTime = TimeSpan.FromSeconds(2);

    private readonly object _lifecycleLock = new();
    private readonly object _correlatorLock = new();
    private readonly IoCorrelator _correlator;
    private readonly ConcurrentQueue<FileActivityEvent> _queue = new();
    private readonly Action<PendingIo> _publish;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly Queue<long> _restartTicks = new();
    private volatile Sources _sources = new(null, 0, 0, 0, null, 0);
    private Semaphore? _sessionGuard;
    private Timer? _releaseTimer;
    private CsvActivityLog? _log;
    private volatile bool _pathOperationsSeen;
    private bool _disposed;
    private int _queued;
    private int _restarts;
    private long _sequence;
    private long _published;
    private long _dropped;
    private long _unnamed;

    /// <summary>The running source plus totals of sources that already ended (restarts), swapped as one unit.</summary>
    private sealed record Sources(
        EtwFileIoSource? Current, int RetiredLost, long RetiredKernelEvents, long RetiredCallbackErrors, string? RetiredLastError,
        long RetiredReorderOverflows);

    public MonitorEngine(MonitorOptions? options = null)
    {
        Options = options ?? new MonitorOptions();
        Volumes = new VolumeMapper();
        Drives = new DriveFilter();
        Stats = new ActivityAggregator(Options.MaxTrackedFiles);
        Folders = new FolderTracker(Options.MaxTrackedFiles);
        ProcessNames = new ProcessNameCache();
        _correlator = new IoCorrelator(Options.CompletionWaitMs, earlyKeepMs: Options.CompletionWaitMs);
        _publish = Publish;
    }

    public MonitorOptions Options { get; }

    public VolumeMapper Volumes { get; }

    /// <summary>Which drive letters are monitored; may be changed at any time.</summary>
    public DriveFilter Drives { get; }

    public ActivityAggregator Stats { get; }

    public ProcessNameCache ProcessNames { get; }

    public bool IsRunning { get; private set; }

    public DateTime? StartedAt { get; private set; }

    /// <summary>
    /// Something the last <see cref="Start"/> worked around and the user may want to know, or null: another
    /// program holds the guard that keeps two DriveMonitors off one session name although no such session
    /// is running (a stale guard, or one created to block monitoring).
    /// </summary>
    public string? StartWarning { get; private set; }

    /// <summary>Events published on monitored drives.</summary>
    public long PublishedEvents => Interlocked.Read(ref _published);

    /// <summary>Events discarded because nobody drained the queue fast enough.</summary>
    public long DroppedEvents => Interlocked.Read(ref _dropped);

    /// <summary>
    /// I/O whose file name was unknown (typically files opened before monitoring started), counted for
    /// the whole system rather than only the monitored drives: without a name the drive is unknown, so
    /// the drive filter cannot apply.
    /// </summary>
    public long UnnamedEvents => Interlocked.Read(ref _unnamed);

    /// <summary>Events the kernel dropped because the consumer fell behind.</summary>
    public int LostEvents
    {
        get
        {
            Sources s = _sources;
            return s.RetiredLost + (s.Current?.EventsLost ?? 0);
        }
    }

    /// <summary>File I/O events received from the kernel for the whole system.</summary>
    public long KernelEvents
    {
        get
        {
            Sources s = _sources;
            return s.RetiredKernelEvents + (s.Current?.KernelEvents ?? 0);
        }
    }

    /// <summary>Exceptions caught inside trace callbacks; each cost one event, not the session.</summary>
    public long CallbackErrors
    {
        get
        {
            Sources s = _sources;
            return s.RetiredCallbackErrors + (s.Current?.CallbackErrors ?? 0);
        }
    }

    public string? LastCallbackError => _sources.Current?.LastCallbackError ?? _sources.RetiredLastError;

    /// <summary>
    /// Events passed on for matching before <see cref="MonitorOptions.ReorderWindowMs"/> was over because the reorder
    /// buffer was full; like events that Windows delivers later than the window, they may be matched out of order.
    /// </summary>
    public long ReorderOverflows
    {
        get
        {
            Sources s = _sources;
            return s.RetiredReorderOverflows + (s.Current?.ReorderOverflows ?? 0);
        }
    }

    /// <summary>How often the kernel session was restarted after stopping unexpectedly.</summary>
    public int Restarts => Volatile.Read(ref _restarts);

    /// <summary>True once Windows has reported the new name of a renamed file.</summary>
    public bool PathOperationsSeen => _pathOperationsSeen;

    /// <summary>Whether Windows accepted the request for names of files already open at start.</summary>
    public bool FileNameRundownRequested => _sources.Current?.RundownRequested ?? false;

    /// <summary>
    /// Operations held back for their completion, or behind one that is. Read without the correlator
    /// lock, so the UI polling it never waits behind the ETW thread.
    /// </summary>
    public int PendingCompletions => _correlator.PendingCount;

    public CsvActivityLog? Log => Volatile.Read(ref _log);

    public event EventHandler? StateChanged;

    /// <summary>Monitoring stopped on its own and could not be restarted. Raised on a worker thread.</summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>The kernel session stopped unexpectedly and was restarted; events in between may be missing. Raised on a worker thread.</summary>
    public event EventHandler<Exception>? Restarted;

    /// <summary>Diagnostics: sees each raw operation (with its completion) just before it is classified. Must be fast.</summary>
    internal Action<PendingIo>? RawOperationObserver { get; set; }

    /// <summary>
    /// The paths known to be folders, which give a folder's delete or rename the Directory flag before the
    /// event is published. Forgotten with the statistics, which remember folders the same way.
    /// </summary>
    internal FolderTracker Folders { get; }

    public static bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="MonitorStartException">Not elevated, already monitoring elsewhere, or the kernel session could not be created.</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            // Checked under the lock Dispose sets it under: a session started after Dispose would never be stopped.
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
                return;
            if (!IsElevated)
            {
                throw new MonitorStartException(
                    "DriveMonitor needs administrator rights to read the Windows kernel file trace. Run it as administrator.");
            }

            _sessionGuard = AcquireSessionGuard(Options.SessionName, IsTraceSessionRunning, SessionSettleTime, out string? warning);
            StartWarning = warning;
            try
            {
                Volumes.Refresh();
                _sources = _sources with { Current = StartSource() };
            }
            catch
            {
                ReleaseSessionGuard();
                throw;
            }

            _releaseTimer = new Timer(_ => ReleaseDue(flushAll: false), null, ReleaseIntervalMs, ReleaseIntervalMs);

            // .NET does not run finalizers at exit, and a kernel session outlives its process.
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            IsRunning = true;
            StartedAt = DateTime.Now;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
                return;

            _releaseTimer?.Dispose();
            _releaseTimer = null;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            if (_sources.Current is { } source)
                RetireSource(source);
            IsRunning = false;
            ReleaseSessionGuard();
        }

        // Publish what is still waiting for a completion.
        ReleaseDue(flushAll: true);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves up to <paramref name="max"/> published events into <paramref name="buffer"/>.</summary>
    public int DrainEvents(List<FileActivityEvent> buffer, int max = int.MaxValue)
    {
        int count = 0;
        while (count < max && _queue.TryDequeue(out FileActivityEvent? ev))
        {
            Interlocked.Decrement(ref _queued);
            buffer.Add(ev);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Starts writing every published event to a CSV file (replacing any current log), within the size
    /// and free-space limits in <see cref="Options"/>.
    /// </summary>
    /// <exception cref="IOException">The file cannot be created, or its drive is already short of free space.</exception>
    public void StartLog(string filePath)
    {
        const long MB = 1024 * 1024;
        var log = new CsvActivityLog(
            filePath,
            maxFileBytes: Options.MaxLogFileMB * MB,
            maxFiles: Options.MaxLogFiles,
            minFreeBytes: Options.MinLogFreeSpaceMB * MB);
        Interlocked.Exchange(ref _log, log)?.Dispose();
    }

    public void StopLog() => Interlocked.Exchange(ref _log, null)?.Dispose();

    /// <summary>Clears statistics and the folders known by path, and discards events not yet drained.</summary>
    public void ClearStatistics()
    {
        Stats.Clear();
        Folders.Clear();
        while (_queue.TryDequeue(out _))
            Interlocked.Decrement(ref _queued);
    }

    /// <summary>Stops monitoring and the log for good; <see cref="Start"/> throws afterwards. Safe to call more than once.</summary>
    public void Dispose()
    {
        // A Start that took the lock first is stopped below; any later one throws. Repeated calls still
        // go through Stop, so one racing an earlier call returns only once the kernel session has stopped.
        lock (_lifecycleLock)
            _disposed = true;

        Stop();
        StopLog();
    }

    bool IFileIoSink.TryAccept(ref string path, int processId, out char drive)
    {
        // A name recorded before its volume had a drive letter (a pendrive that was just plugged in)
        // is still a kernel path; the volume map may know that volume by now.
        if (path.Length > 0 && path[0] == '\\')
            path = Volumes.ToDosPath(path);

        if (!Drives.TryMatch(path, out drive))
        {
            if (path.Length == 0)
                Interlocked.Increment(ref _unnamed);
            return false;
        }

        if (processId == _ownProcessId && Options.ExcludeOwnProcess)
            return false;

        // Never report our own log files, or every write to them would create more events to log.
        return Volatile.Read(ref _log) is not { } log || !log.IsLogFile(path);
    }

    void IFileIoSink.OnBegin(PendingIo io)
    {
        if (!Options.UseCompletionEvents)
        {
            Publish(io);
            return;
        }

        // Once Windows is known to report rename targets, hold renames briefly until theirs arrives.
        if (_pathOperationsSeen && ExpectsPath(io))
            io.AwaitingPath = true;

        long now = Environment.TickCount64;
        lock (_correlatorLock)
        {
            _correlator.Begin(io, now);
            _correlator.Release(now, _publish);
        }
    }

    void IFileIoSink.OnForeignBegin(ulong irp, long timestampQpc)
    {
        if (!Options.UseCompletionEvents)
            return;

        lock (_correlatorLock)
            _correlator.ForeignBegin(irp, timestampQpc);
    }

    void IFileIoSink.OnComplete(ulong irp, long timestampQpc, ulong extraInfo, int ntStatus)
    {
        long now = Environment.TickCount64;
        lock (_correlatorLock)
        {
            if (_correlator.Complete(irp, timestampQpc, extraInfo, ntStatus, now))
                _correlator.Release(now, _publish);
        }
    }

    void IFileIoSink.OnPathOperation(ulong irp, long timestampQpc, string path)
    {
        _pathOperationsSeen = true;
        if (!Options.UseCompletionEvents)
            return;

        long now = Environment.TickCount64;
        lock (_correlatorLock)
        {
            if (_correlator.Annotate(irp, timestampQpc, path, now))
                _correlator.Release(now, _publish);
        }
    }

    private static bool ExpectsPath(PendingIo io) =>
        io.Op == RawFileOp.Rename
        || (io.Op == RawFileOp.SetInfo && io.InfoClass is FileIoClassifier.FileRenameInformation
            or FileIoClassifier.FileRenameInformationEx
            or FileIoClassifier.FileLinkInformation
            or FileIoClassifier.FileLinkInformationEx);

    private EtwFileIoSource StartSource()
    {
        var source = new EtwFileIoSource(this, ProcessNames, Volumes.ToDosPath, Options);
        source.Faulted += error => OnSourceFaulted(source, error);
        source.Start();
        return source;
    }

    /// <summary>
    /// Stops a source (draining its last events, and its reorder buffer into the correlator) and folds its counters
    /// into the retired totals. The callers' final release comes after, so it finds everything.
    /// </summary>
    private void RetireSource(EtwFileIoSource source)
    {
        source.Dispose();
        Sources s = _sources;
        _sources = new Sources(
            null,
            s.RetiredLost + source.EventsLost,
            s.RetiredKernelEvents + source.KernelEvents,
            s.RetiredCallbackErrors + source.CallbackErrors,
            source.LastCallbackError ?? s.RetiredLastError,
            s.RetiredReorderOverflows + source.ReorderOverflows);
    }

    private void ReleaseDue(bool flushAll)
    {
        lock (_correlatorLock)
            _correlator.Release(Environment.TickCount64, _publish, flushAll);
    }

    private void Publish(PendingIo io)
    {
        RawOperationObserver?.Invoke(io);
        FileActivityEvent? ev = FileIoClassifier.Classify(io, Interlocked.Increment(ref _sequence));
        if (ev is null)
            return;

        // Before anything sees the event, so that the statistics, the log and the feed all get the folder flag
        // Windows leaves off a folder's delete or rename. Classify returns null only for file-pointer moves and
        // kinds it does not report, never for a create, open or listing, so every folder open teaches the
        // tracker here, whatever the queue or the log later does with its event.
        ev = Folders.Apply(ev);
        Stats.Record(ev);
        Volatile.Read(ref _log)?.Write(ev);
        Interlocked.Increment(ref _published);

        _queue.Enqueue(ev);
        if (Interlocked.Increment(ref _queued) > Options.MaxQueuedEvents && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
        }
    }

    private void OnSourceFaulted(EtwFileIoSource faulted, Exception error)
    {
        // Raised on the ETW thread; stopping joins that thread, so hand off first.
        ThreadPool.QueueUserWorkItem(_ => HandleFault(faulted, error));
    }

    private void HandleFault(EtwFileIoSource faulted, Exception error)
    {
        bool restarted = false;
        Exception reported = error;
        lock (_lifecycleLock)
        {
            // Once disposed no new session may start; the Stop inside Dispose shuts this one down.
            if (_disposed || !IsRunning || !ReferenceEquals(_sources.Current, faulted))
                return; // disposing, already stopped, or replaced

            RetireSource(faulted);
            ReleaseDue(flushAll: true);

            if (Options.AutoRestart && AllowRestart())
            {
                try
                {
                    Volumes.Refresh();
                    _sources = _sources with { Current = StartSource() };
                    Interlocked.Increment(ref _restarts);
                    restarted = true;
                }
                catch (Exception ex)
                {
                    reported = ex;
                }
            }

            if (!restarted)
            {
                _releaseTimer?.Dispose();
                _releaseTimer = null;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                IsRunning = false;
                ReleaseSessionGuard();
            }
        }

        if (restarted)
        {
            Restarted?.Invoke(this, error);
        }
        else
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            Faulted?.Invoke(this, reported);
        }
    }

    private bool AllowRestart()
    {
        long now = Environment.TickCount64;
        while (_restartTicks.Count > 0 && now - _restartTicks.Peek() > 60_000)
            _restartTicks.Dequeue();
        if (_restartTicks.Count >= MaxRestartsPerMinute)
            return false;
        _restartTicks.Enqueue(now);
        return true;
    }

    internal static string SessionGuardName(string sessionName) =>
        @"Global\DriveMonitor.Session." + sessionName.Replace('\\', '_');

    /// <summary>
    /// TraceEvent replaces a running session that has the same name, which would silently end another
    /// DriveMonitor's monitoring; a machine-wide semaphore per session name prevents that.
    /// </summary>
    /// <remarks>
    /// Any user can create that semaphore first, or hold it, to block monitoring, but only an administrator
    /// can start the kernel session. So when the guard cannot be taken, the session decides: if one with
    /// this name is running, another DriveMonitor is monitoring. If none appears within
    /// <paramref name="settleTime"/> (a DriveMonitor that has just taken the guard, or is restarting its
    /// session, has none for a moment), the guard is stale or squatted and monitoring goes ahead without it;
    /// another DriveMonitor then finds this one by its session.
    /// </remarks>
    /// <param name="isSessionRunning">Whether a trace session with the name is running; null when that cannot be told.</param>
    /// <returns>The guard, taken; or null when it was ignored, with <paramref name="warning"/> saying why.</returns>
    /// <exception cref="MonitorStartException">Another DriveMonitor is monitoring with the session name, or that cannot be ruled out.</exception>
    internal static Semaphore? AcquireSessionGuard(
        string sessionName, Func<string, bool?> isSessionRunning, TimeSpan settleTime, out string? warning)
    {
        string name = SessionGuardName(sessionName);
        Semaphore? guard = null;
        Exception? openError = null;
        try
        {
            guard = new Semaphore(1, 1, name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // Created by another program, with a DACL that shuts us out or as another kind of object.
            openError = ex;
        }

        warning = null;
        var waited = Stopwatch.StartNew();
        while (guard?.WaitOne(0) != true)
        {
            bool? running = isSessionRunning(sessionName);
            if (running != false)
            {
                guard?.Dispose();
                if (running is null && openError is not null)
                    throw new MonitorStartException($"Could not check whether another DriveMonitor is running: {openError.Message}", openError);
                throw new MonitorStartException(
                    $"Another DriveMonitor window or command is already monitoring with the session \"{sessionName}\". Close it first.");
            }

            if (waited.Elapsed >= settleTime)
            {
                guard?.Dispose();
                warning = $"Another program holds {name}, which keeps two DriveMonitors from sharing the session \"{sessionName}\", " +
                    "but no such session is running, so monitoring started anyway.";
                return null;
            }

            Thread.Sleep(SessionCheckIntervalMs);
        }

        return guard;
    }

    private static bool? IsTraceSessionRunning(string sessionName)
    {
        try
        {
            return TraceEventSession.GetActiveSessionNames().Contains(sessionName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return null; // the caller then assumes another DriveMonitor is running, as before
        }
    }

    private void ReleaseSessionGuard()
    {
        Semaphore? guard = Interlocked.Exchange(ref _sessionGuard, null);
        if (guard is null)
            return;
        try
        {
            guard.Release();
        }
        catch (SemaphoreFullException)
        {
            // Not held; nothing to release.
        }

        guard.Dispose();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Stop();
}
