# Architecture

DriveMonitor is one engine, `MonitorEngine` in `src/DriveMonitor.Core`, with two front ends: the WPF app
(`src/DriveMonitor.App`) and the `drivemon` CLI (`src/DriveMonitor.Cli`). This document describes how the
engine turns the Windows kernel's file I/O trace into events and totals. Statements about TraceEvent 4.0.0
and the kernel trace come from reading TraceEvent's source and Microsoft's ETW documentation; the CI
self-test checks the most important of them on a real kernel.

## Components

| Type | File | Role |
| --- | --- | --- |
| `MonitorEngine` | `MonitorEngine.cs` | Owns the pipeline: lifecycle, filtering, publication, counters |
| `MonitorOptions` | `MonitorOptions.cs` | Session name, buffer size, completion wait, limits |
| `EtwFileIoSource` | `Tracing/EtwFileIoSource.cs` | The kernel trace session, its consumer thread and DriveMonitor's own name tables |
| `PendingIo` | `Tracing/PendingIo.cs` | The fields copied out of one kernel initiation event, plus its completion |
| `IoCorrelator` | `Tracing/IoCorrelator.cs` | Matches completions and rename targets to their operations |
| `FileIoClassifier` | `Tracing/FileIoClassifier.cs` | Turns a matched operation into a `FileActivityEvent` |
| `NtStatusText` | `Tracing/NtStatusText.cs` | Short descriptions of NTSTATUS codes ("not found", "access denied") |
| `ProcessNameCache` | `Tracing/ProcessNameCache.cs` | Process ID to image name |
| `VolumeMapper` | `Volumes/VolumeMapper.cs` | Kernel paths to DOS paths |
| `DriveCatalog`, `StorageQuery` | `Volumes/` | Drive letters with type, bus, model, size and file system |
| `DriveFilter` | `Filtering/DriveFilter.cs` | The monitored drive letters |
| `ActivityAggregator` | `Aggregation/ActivityAggregator.cs` | Totals per file, per drive and per process |
| `CsvActivityLog`, `Csv` | `Export/` | The continuous CSV log, with its limits, and CSV export |
| `LinkSafeFolder` | `IO/LinkSafeFolder.cs` | Writes files in a folder without following links (the app's settings and error log) |

Paths are relative to `src/DriveMonitor.Core`.

## Pipeline

```mermaid
flowchart TD
    K["Windows kernel<br/>real-time trace session"] -->|events| S
    S["EtwFileIoSource<br/>ETW consumer thread:<br/>copy fields, resolve names"] -->|initiations| F
    S -->|completions, rename targets,<br/>starts of unreported kinds| C
    F["Filter: MonitorEngine.TryAccept<br/>monitored drive? own process? own log?"] -->|accepted| C
    F -->|rejected: address and time only| C
    C["IoCorrelator<br/>completion matching"] -->|in initiation order| X
    X["FileIoClassifier<br/>FileActivityEvent"] --> A
    X --> L
    X --> Q
    A["ActivityAggregator<br/>per-file, per-drive and per-process totals"]
    L["CsvActivityLog<br/>optional CSV log"]
    Q["Event queue<br/>MonitorEngine.DrainEvents"]
    Q --> U["WPF app or drivemon CLI"]
    A -->|snapshots| U
```

1. **Kernel session.** `EtwFileIoSource` starts a real-time kernel session in system-logger mode with these
   kernel keywords:
   - `FileIOInit`: the initiation events (create, read, write, set information, delete, rename, cleanup,
     close, flush, folder enumeration);
   - `FileIO`: the `FileIo_OpEnd` completions, with the final NTSTATUS and the bytes transferred (only
     while `UseCompletionEvents` is on, the default);
   - `DiskFileIO`, plus `DiskIO`, which it requires: the events that bind a file name to a FileKey.

   TraceEvent adds the Process and Thread keywords itself. The kernel cannot filter file events by volume,
   so the session receives the file activity of the whole system.
2. **Copy.** The callbacks for initiation events copy the fields they need into a `PendingIo` (TraceEvent
   reuses its event objects), resolving the file name and the process name on the way. Initiations and
   completions are counted in `KernelEvents`; the starts that are only passed on for matching (step 3) are
   not.
3. **Filter.** For every initiation event, `MonitorEngine.TryAccept` converts a path that is still in
   kernel form, then rejects it when the path is not on a monitored drive (an empty path is counted as
   `UnnamedEvents`), when it comes from DriveMonitor's own process and `ExcludeOwnProcess` is set, or when
   it is one of the engine's own CSV log files (`CsvActivityLog.IsLogFile`: the first file or a later
   part). A rejected initiation still passes its request address and timestamp to the correlator through
   `IFileIoSink.OnForeignBegin`, and so do the starts of the kinds DriveMonitor does not report: cleanup,
   close, query information, file-system control, change notification, and folder enumeration when listings
   are not captured. The engine ignores them while `UseCompletionEvents` is off. Completions and rename
   targets carry no file name of their own, so they go straight to matching.
4. **Completion matching.** `IoCorrelator` attaches completions and rename targets to their operations and
   releases the operations in the order they started (see [Completion matching](#completion-matching)).
5. **Classification.** `FileIoClassifier.Classify` turns each released operation into a
   `FileActivityEvent` (see [Classification](#classification)).
6. **Fan-out.** `MonitorEngine.Publish` records the event in `ActivityAggregator`, writes it to the CSV log
   if one is running, and then adds it to a `ConcurrentQueue`. Statistics and the log come before the
   queue, so an event dropped from a full queue still counts in the totals and in the log.
7. **Front ends.** The app drains the queue on a UI timer and takes snapshots of the statistics that
   changed; the CLI drains it and prints each event.

### Classification

| Kernel operation | Becomes | Details |
| --- | --- | --- |
| Read, Write | `Read`, `Write` | Bytes from the completion (never more than requested, nor more than 4 GiB - 1 when no request size was recorded); the requested size when no completion was seen; 0 when it failed. The kernel's request size is unsigned, up to 4 GiB - 1; TraceEvent returns it as an `int`, which `PendingIo.RequestSize` converts back. `Paging` from `IRP_PAGING_IO` (0x2), `NonCached` from `IRP_NOCACHE` (0x1). |
| Create | `Create` or `Open` | From the completion: `FILE_CREATED` → Create "created", `FILE_OVERWRITTEN` → "overwritten", `FILE_SUPERSEDED` → "replaced", anything else → Open. Without a completion, inferred from the requested disposition ("new file requested", ...). `FILE_DIRECTORY_FILE` sets `Directory`. An Open that asked for neither `FILE_DIRECTORY_FILE` nor `FILE_NON_DIRECTORY_FILE`, as `FILE_FLAG_BACKUP_SEMANTICS` opens do, sets `MaybeDirectory`: it may have opened a folder. `FILE_DELETE_ON_CLOSE` sets `DeleteOnClose` unless the open failed. |
| Delete, or set information with class 13 or 64 | `Delete` | The requested state is in ExtraInfo; a request without the delete bit becomes `SetInfo` "delete cancelled", with the `DeleteCancelled` flag. |
| Rename, or set information with class 10 or 65 | `Rename` | Target from the path event when Windows sent one ("→ new path", or "moved to the Recycle Bin"), otherwise "renamed or moved". |
| Set information with class 11 or 72 | `SetInfo` | "hard link created" or "hard link → target". |
| Set information with class 20 | `SetInfo` | "size → N bytes", `SizeChange`, `Bytes` = the new size. |
| Other set information | `SetInfo` | Allocation size, valid data length, "attributes or timestamps", "short name changed", or "information class N". Class 14 (file-pointer moves) produces no event. |
| Flush | `Flush` | |
| Folder enumeration | `List` | `Directory`; "pattern X" unless the pattern is `*`. |

An operation without a completion has status `Unknown`. With one, it is `Success` when the NTSTATUS is a
success code, for a folder enumeration that ended with "no more files" or "no such file", or for a read that
ended with `STATUS_END_OF_FILE` (0xC0000011: the read started at the end of the file and moved 0 bytes, as
the last read of every read-until-empty loop does; the NTSTATUS stays on the event). Otherwise it is `Failed`
and its detail becomes the reason from `NtStatusText`; a write that ends with `STATUS_END_OF_FILE` fails.

### Statistics

`ActivityAggregator` keeps the totals per file, per drive and per process. Failed operations are only counted
(`FailedEvents`), and paging I/O goes into separate paging totals and is never attributed to a process.

- **Folders** get a file row only when they are created, deleted or renamed; their opens and listings would
  flood the table while someone browses. Windows reports the delete or rename of a folder without the folder
  flag, and a `MaybeDirectory` open can take a folder or a file, so the type comes from evidence. A path is a
  folder when an event carries `Directory` or lists it, and a file when something creates it without
  `FILE_DIRECTORY_FILE`, opens it with `FILE_NON_DIRECTORY_FILE`, or reads, writes or resizes it (not paging
  I/O, which can reach a folder's index). Folders seen opened as folders, or listed, are remembered without a
  row, so a later delete or rename of the path gives a folder row, and the type of an existing row follows
  later evidence. A `MaybeDirectory` open creates no row unless it deletes; on an existing row it counts as an
  open. Only the statistics use this: the event itself, in Live activity and the CSV log, keeps the flags
  the classifier gave it.
- **Time order.** Events arrive roughly, not strictly, in time order: the kernel delivers each processor's
  buffer on its own, up to a flush interval apart. A file's or process's first seen therefore takes the
  earliest timestamp, and last seen, last operation and a drive's last activity the latest. The last process
  follows the latest event that is not paging I/O, the new name of the last rename the latest rename, and
  the Deleted status the latest create, delete, delete-on-close or cancel. Of two events with the same
  timestamp, the one recorded last wins.
- **Cancelled deletes.** A `DeleteCancelled` event clears the Deleted status. This follows the usual single
  handle: another handle opened with delete-on-close would still delete the file.
- **Limits.** At most `MaxTrackedFiles` (200,000) files and 50,000 processes get rows (`ActivityAggregator`'s
  `maxTrackedProcesses`; `MonitorEngine` passes only `MaxTrackedFiles`, so the process limit keeps its
  default). Events of new files beyond the limit still count in the drive and process totals and in
  `UntrackedEvents`; events of new processes still count in the drive and file totals and in
  `UntrackedProcessEvents`. The folders remembered without a row are bounded by `MaxTrackedFiles` too, and
  start over when full. `TrackedFiles` and `TrackedProcesses` give the number of rows.
- **Process names per file.** A row's Processes text names three processes and adds "+N more". The file
  keeps 16 names and counts up to 4,096 more; once there are more than three, `FileStatsSnapshot.ProcessNames`
  holds all of them, so the app's Files search can match any of them.

## Threading model

| Thread | What runs there |
| --- | --- |
| ETW consumer (`DriveMonitor ETW consumer`, background, above-normal priority so kernel buffers do not overflow) | TraceEvent's `Process()` loop and every callback: copying fields, the name tables, the path mapper, `TryAccept`, and the correlator's `Begin`, `Complete` and `Annotate`, each followed by releasing the operations that are ready |
| Release timer (thread pool, every 250 ms) | Releases operations whose wait for a completion ran out |
| Flush timer (thread pool, every `FlushIntervalMs`, 500 ms by default; every second when it is 0) | `session.Flush()` (not when `FlushIntervalMs` is 0), so Windows hands over buffered events sooner than the 1 s flush timer TraceEvent sets for the session; then it reads the session's lost-event count, which can no longer be read once another program has stopped the session |
| CSV log writer (thread pool task) | Drains a bounded channel into the log file, so a slow disk never stalls the consumer; the only code that writes or closes the log's files (see [Activity log](#activity-log)) |
| Fault handler (thread pool) | Retires a failed session and restarts or reports it |
| App UI thread | Every 250 ms: drains up to 50,000 events into Live activity and applies the statistics that changed. Every second: re-sorts the sorted tables whose rows changed in place, never sooner than ten times as long as the last re-sort took. When Windows reports a new volume: refreshes the volume map |
| App thread pool | `Start` and `Stop`, drive enumeration, exports, starting and stopping the log, File Explorer |
| CLI main thread | Every 200 ms: drains the queue and prints. Without `--drives`, it also checks for new drive letters; when one came or went, and every 10 seconds, it refreshes the volume map and reads the drive list |

Publication (classification, statistics, log, queue) runs on whichever thread releases the operation (the
consumer thread, the release timer, or the thread that stops the session) and always under
`_correlatorLock`, so events are published one at a time and in initiation order.

How the threads share state:

- `_lifecycleLock` serialises `Start`, `Stop` and fault handling.
- `_correlatorLock` guards the correlator and publication. `PendingCompletions` is the exception: the
  correlator publishes its number of waiting operations with `Volatile.Write`, so the app's diagnostics,
  refreshed every second, read it without waiting for the lock that the ETW consumer holds.
- `ActivityAggregator` has one lock, held briefly by `Record` and by the snapshot methods. The UI asks only
  for what changed since its last call (`TakeChangedFiles`, `TakeChangedProcesses`).
- `EtwFileIoSource._sessionLock` guards the session reference, `EventsLost` and `Flush`.
- The name tables are touched only on the consumer thread.
- Lock-free: the monitored drives are a 26-bit mask (`Interlocked` writes, `Volatile` reads); the volume
  map is an immutable snapshot replaced atomically; process names are a `ConcurrentDictionary`; counters
  use `Interlocked`; the running source and the totals of retired sources are one record swapped as a
  unit; `ExcludeOwnProcess` is volatile and is the only option that may change while monitoring.
- `Faulted` and `Restarted` are raised on a worker thread, and `StateChanged` on the thread that started or
  stopped monitoring; the app posts all three to the UI thread with `Dispatcher.BeginInvoke`.

## Completion matching

A kernel initiation event (create, read, write, set information, delete, rename, flush, folder
enumeration) and its `FileIo_OpEnd` completion carry the same request address, `IrpPtr` (Filter Manager
callback data). Each event also has an exact QueryPerformanceCounter timestamp. Two things make the pairing
hard:

- **Out-of-order delivery.** A real-time session delivers each CPU's buffer independently, so a completion
  logged on one CPU can arrive before the initiation logged on another.
- **Address reuse.** Windows recycles request addresses within microseconds, so a completion can belong to
  an older or a newer use of the same address.

The rule that resolves both: **an address serves one operation at a time, so a completion C belongs to
initiation B on the same address only if C was logged at or after B and no other operation started on the
address in between; among those candidates the closest one wins.** In detail:

- **Every start counts.** The rule needs the starts of the operations DriveMonitor does not follow too:
  those on other drives, of excluded processes or of its own log, and every cleanup, close, query, file-system
  control and change notification, whose completions arrive on the same recycled addresses. `EtwFileIoSource`
  reports them through `OnForeignBegin` (step 3 of the [pipeline](#pipeline)). Each waiting operation keeps
  `ReusedQpc`, the earliest start known on its address after its own. Every start, followed or not, lowers
  it on the latest waiting operation that began before it; each earlier operation is already limited by the
  start of the one after it, so this normally takes one step. The latest start on each address is also
  remembered, in two generations of 65,536 addresses each (the older is dropped when the newer is full), so
  that an initiation delivered after a later start takes its limit from that start, or from a waiting
  operation that began after it. Releasing everything, at `Stop` or when a failed session is retired, clears
  this table.
- **Waiting operations** are chained per address, in a doubly linked chain sorted by start, latest first,
  so one leaves in constant time when it completes or expires. A completion goes to the latest operation on
  its address that started at or before the completion's timestamp, unless it was logged at or after that
  operation's `ReusedQpc`; then it goes to the early store instead.
- **Early store.** A completion that matches nothing yet is kept, keyed by address, for `CompletionWaitMs`
  (2,000 ms), in case its initiation is still in flight. When an initiation arrives, it takes the earliest
  stored completion on its address that was logged at or after its own timestamp, unless that one was logged
  at or after its `ReusedQpc`. Completions of operations that are not followed (their initiations were
  filtered out, or are of a kind DriveMonitor does not report) also land here and simply expire. Each stored
  value is on two doubly linked lists: its address's chain, sorted by timestamp, and one list of all values
  in arrival order, which expiry and the cap of 500,000 values work through. A value leaves either list in
  constant time; an insert walks from the latest end, which takes no steps when values arrive in order; and
  a lookup stops at the first value logged before the initiation. At most 1,024 values are kept per address;
  beyond that, the earliest logged is dropped.
- **Release order.** Operations leave the correlator in initiation order. An operation is released as soon
  as it is complete and everything before it has been released; an incomplete one waits up to
  `CompletionWaitMs` and is then published with status `Unknown`. An operation without a request address
  never waits, because nothing could be matched to it. `Stop` releases everything at once.
- **Rename targets** use the same rule, `ReusedQpc` included. Once the engine has seen one
  `FileIo_PathOperation` event, renames and hard-link operations wait for their path as well as their
  completion (up to the same 2,000 ms). A path that arrives first waits in a second early store; a path for
  an operation that was not told to wait (it started before the first path event) is still attached while
  that operation waits for its completion.
- With `UseCompletionEvents` off, nothing is matched: operations are published as they start.

Every completion in the system passes through the correlator, and recycled addresses put thousands of them on
a few hot addresses, so nothing here walks a whole chain: a burst of I/O on an unmonitored drive cannot stall
the consumer thread, which takes `_correlatorLock` for every completion. An operation whose own completion
never arrives is published as `Unknown` (a rename: without its target) instead of with the outcome of an
operation that reused its address. Two gaps remain. A start delivered after the completion that it would
rule out comes too late, and an initiation delivered after several later starts on its address knows only
the latest of them; so two followed operations on one address whose events all arrive in reverse order can
still be mismatched.

## Name resolution

### File names

The kernel names files in two kinds of events:

- `FileIo_Create` carries the path being opened; TraceEvent associates it with the FILE_OBJECT of that open.
- The `FileIo_Name` events (Name, FileCreate, FileRundown) bind a name to a FileKey, the file system's
  per-stream context that every open of the stream shares. `FileIo_Name` FileDelete (opcode 35) means that
  binding was released; it does not mean a file was deleted. Deletes come from `FileIo_Delete` and from
  disposition requests.

Read, write, set-information, delete, rename, flush and folder-enumeration events carry only the FileKey
and the FileObject. TraceEvent looks them up in its own table, FileKey first, but in a real-time session it
forgets a FileObject's name at Cleanup (the last handle closed) and a FileKey's name when the name is
released and at rundown. Paging I/O, such as the lazy writer flushing a file that was already closed, can
arrive after that. `EtwFileIoSource` therefore keeps two tables of its own:

- **By FileKey:** filled from the Name, FileCreate and FileRundown events, and learned from any event whose
  name TraceEvent did resolve, so that later cache flushes through another file object can be named.
  Removed when the name is released.
- **By FileObject:** filled from `FileIo_Create`. Removed at Close (the file object is freed), not at
  Cleanup, because paging I/O can still use the file object after its last handle closed.

A name is resolved in this order: TraceEvent's name, the FileKey table, the FileObject table. When all three
fail, the path is empty: the event cannot be tied to a drive and is counted as unnamed. Each table holds at
most 500,000 entries and is cleared when it is full; entries normally leave at Close or name release, so the
cap only guards against lost events.

### Files that were already open

A file opened before the session started produces no Create or Name event. After the session is running,
DriveMonitor enables the kernel rundown provider `{3b9c9951-3480-4220-9377-9c8e5184f5cd}` with keyword 0x10
(`KERNEL_FILE_KEYWORD_FILENAME`) on it, which makes the kernel list the names of open files (FileIo opcode
36). This is not documented by Microsoft; the technique comes from System Informer. Because TraceEvent drops
a FileKey's name at rundown, these names survive only in DriveMonitor's own FileKey table. If Windows
ignores the request, those files stay unnamed until they are opened again. The app's diagnostics ("Names of
already open files requested") and the self-test's "name rundown requested" say whether Windows accepted
the request, not whether it acted on it.

### Rename targets

The kernel's rename event carries no new name. Windows 11 also sends undocumented `FileIo_PathOperation`
events (opcode 80 for a rename, 81 for a hard link), which TraceEvent 4.0 does not parse. DriveMonitor reads
them from TraceEvent's unhandled events and parses the payload itself: `IrpPtr`, `FileObject`, `FileKey` and
`ExtraInfo` (pointer-sized), `TTID` and `InfoClass` (32-bit), then the NUL-terminated UTF-16 name. A name
that is not a kernel path is ignored; the rest are converted to DOS paths and matched to their operation by
request address and timestamp. The classifier uses a target only when it is a DOS path that differs from
the old path. `PathOperationsSeen` (the app's "Windows reports new names of renamed files") turns true at
the first such event.

### Process names

`ProcessNameCache` is filled from process start events and from the list of running processes the kernel
sends when the session starts. The kernel's image name is cut to 15 characters (`SearchProtocolH`), so the
executable name from the command line wins when it extends the short name. For a process the trace has not
named, DriveMonitor asks Windows (`QueryFullProcessImageName` with `PROCESS_QUERY_LIMITED_INFORMATION`),
then falls back to TraceEvent's name or `PID n`; PID 0 is `Idle` and PID 4 is `System`. An operation is
attributed to the process and thread of its initiation event, never to those of its completion.

## Kernel-to-DOS path mapping

The kernel reports paths such as `\Device\HarddiskVolume5\docs\a.txt`; DriveMonitor shows `E:\docs\a.txt`.

- **The map.** `VolumeMapper` asks `QueryDosDevice` for the device behind each letter A to Z. SUBST drives,
  which point at `\??\C:\folder`, are skipped, and a volume with several letters maps to the first one.
- **Installed in TraceEvent.** TraceEvent converts a name once, when it decodes the Create or Name event,
  through `KernelPathToUserPathMapper`. Its default mapper reads the drive letters once and never learns
  about drives added later, so `EtwFileIoSource` replaces it with `VolumeMapper.ToDosPath` before
  `Process()` starts. The mapper never throws or returns null, returns DOS paths unchanged (so mapping twice
  is harmless) and leaves non-paths such as a `*` search pattern alone.
- **Refreshable.** Lookups read an immutable snapshot; `Refresh()` builds a new one and swaps it in
  atomically. The map is refreshed at every start and restart; by the app as soon as Windows reports a new
  local volume, and again when a volume arrived or was removed (`WM_DEVICECHANGE`, debounced by 750 ms),
  before it reads the drive list again; by `drivemon` without `--drives` each time it reads the drive list
  (when a drive letter came or went, and every 10 seconds); and automatically when a lookup misses on a
  device that looks like a storage volume (`HarddiskVolumeN`, `CdRom`, `Floppy`, but not shadow copies), at
  most once every 2 seconds.
- **Remapped when an event arrives.** A name recorded before its volume had a drive letter, for example on a
  pendrive that was just plugged in, is still a kernel path. `TryAccept` converts any path that still starts
  with `\` again each time an event uses it.
- **Other forms.** `\??\E:\x` and `\\?\E:\x` become `E:\x`; `\??\UNC\server\share\x`,
  `\Device\Mup\server\share\x` and `\Device\LanmanRedirector\server\share\x` become `\\server\share\x`;
  `\SystemRoot\...` becomes the Windows directory. Paths that cannot be mapped (named pipes, volumes without
  a letter, shadow copies, the redirector paths of mapped network drives) stay in kernel form. Neither these
  nor `\\server\share` paths have a drive letter, so they never match a monitored drive.

## Activity log

`CsvActivityLog` writes every published event to a CSV file: the app's **Record log** and `drivemon --csv`.
`MonitorEngine.Publish` hands it each event with `Write`, which never blocks: the event goes into a bounded
channel of 100,000 events, or is counted in `Dropped` when the channel is full or the log has ended.

- **One owner.** Once the constructor returns, a background task is the only code that writes or closes the
  log's files. It formats the rows itself and writes them in chunks of about 64 KB with `WriteAsync`, on an
  unbuffered `FileStream` opened with `FileOptions.Asynchronous`, so a write stuck on a stalled drive can be
  cancelled and closing a file never flushes. `Written` counts the rows whose write completed.
- **Parts.** At `MaxLogFileMB` (100 MB) the log continues in a new file, a part, named like the first with
  `.002`, `.003` and so on before the extension: `activity.csv`, `activity.002.csv`, and so on. Each part
  starts with its own byte-order mark and header row and holds whole rows; a part exceeds the limit only when
  a single row does. `CurrentFilePath` names the part being written. `IsLogFile` recognises the first file
  and every later part, and `TryAccept` uses it to leave the log's own writes out of the events (the lazy
  writer flushes them as the System process, so the process filter alone would not).
- **Number of parts.** Starting a part deletes the oldest parts of this log, so that at most `MaxLogFiles`
  (10) remain: about 1 GB of one recording by default. Only parts this log created are deleted; one that
  another program holds open is kept and tried again before the next part. Files of other logs are never
  deleted: each of the app's recordings has its own timestamped name, and parts left by an earlier `drivemon`
  run with the same `--csv` path stay until a new part of the same name replaces them.
- **Free space.** The constructor throws `IOException`, and creates no file, when the log's drive has less
  than `MinLogFreeSpaceMB` (512 MB) free. While logging, the free space is checked at most once a second and
  before every new part; below the minimum the log stops with an `IOException` in `Error` (its `HResult` is
  `ERROR_DISK_FULL`), such as *Only 300 MB is free on C:\\; the activity log stops when less than 512 MB is
  left.* The free space comes from `DriveInfo`, so a log on a network (UNC) path is not checked; a full share
  still stops it with its write error.
- **Errors.** Any write error (disk full, drive removed, network share gone) ends the log in order: it
  refuses later events, so that `Write` counts them as dropped; it counts the rows it had buffered and the
  events still queued as dropped; it closes the file; and only then does it set `Error`, so whoever sees
  `Error` also sees the final counts.
- **Dispose** completes the channel and waits as long as the log makes progress. It gives up after 5 seconds
  without a completed write, or after 30 seconds in all: it cancels the stuck write, counts whatever will
  never be written as dropped, sets `Error` to a `TimeoutException`, and returns without waiting for the
  write. It never touches the file and never throws. `StopLog`, `MonitorEngine.Dispose`, the end of a
  `drivemon` run and closing the app can therefore take up to 30 seconds on a slow drive.

The three limits are `MonitorOptions` settings, read when a log starts; 0 turns each one off, and the app and
`drivemon` keep the defaults. The app shows `Written`, `Dropped` and `Error` in its diagnostics, reports a log
that cannot start or that stops (*Could not start the activity log: ...*, *The activity log stopped: ...*)
and turns **Record log** off. `drivemon` reports a log that cannot start (*Cannot write the CSV file: ...*),
but not one that stops during the run.

## Session lifecycle

### Start

`MonitorEngine.Start`, under `_lifecycleLock`:

1. Throws `ObjectDisposedException` once the engine is disposed, returns if it is already running, and
   throws `MonitorStartException` when the process is not elevated.
2. Takes the cross-process guard, or decides to go on without it, and sets `StartWarning` accordingly
   (below).
3. Refreshes the volume map and starts a new `EtwFileIoSource`:
   1. `new TraceEventSession(name, TraceEventSessionOptions.Create)` with `StopOnDispose` and `BufferSizeMB`.
      `Create` replaces a leftover session with the same name, for example one left behind by a crash.
   2. **`EnableKernelProvider` first.** With a custom session name, TraceEvent starts the session here, as a
      system logger. Reading `session.Source` first would start an ordinary session that cannot take kernel
      keywords.
   3. Reads `session.Source` exactly once. Reading it again after `Dispose` would silently start a new
      session that nothing stops.
   4. Installs the path mapper and subscribes the callbacks.
   5. Starts the consumer thread, requests the file-name rundown and starts the flush timer, which runs even
      when `FlushIntervalMs` is 0 (then every second, only to read the lost-event count).

   A failure in steps 1 to 4, the session's constructor included, disposes the half-built session (when
   one was created) and is rethrown as a `MonitorStartException` with a readable message: access denied becomes "Administrator rights are
   required...", `0x800705AA` (`ERROR_NO_SYSTEM_RESOURCES`) becomes "Windows has no free kernel trace
   session..." (Windows runs at most eight system-logger sessions, and two of the slots are reserved), and
   anything else becomes "The kernel trace session could not be started: ...".
4. Starts the release timer, subscribes to `AppDomain.ProcessExit`, marks the engine running and raises
   `StateChanged`.

### Stop

`MonitorEngine.Stop` disposes the release timer, unsubscribes from `ProcessExit`, stops the source, adds its
counters to the totals of retired sources, marks the engine stopped and releases the guard. Then, outside
the lock, it publishes every operation still waiting for a completion and raises `StateChanged`.

`EtwFileIoSource.Stop` works in the order **Stop, then join, then Dispose**:

1. It marks the source as stopping, disposes the flush timer and records the final `EventsLost`. When another
   program has already stopped the session, that read fails, and the count the flush timer read last, at
   most one tick old, stays.
2. `session.Stop()`: the kernel delivers what is still buffered, and `Process()` returns by itself.
3. It joins the consumer thread, for up to 5 seconds (not when called on that thread).
4. It disposes the session. Disposing closes the consumer and discards undelivered events, which is why it
   comes after the drain. If the join timed out, it joins once more, for up to 5 seconds.

### Dispose

`MonitorEngine.Dispose` first marks the engine as disposed under `_lifecycleLock`, then stops monitoring and
the log (up to 30 seconds on a slow drive; see [Activity log](#activity-log)). A `Start` that takes the lock
afterwards throws `ObjectDisposedException`, so a start racing with `Dispose` (for example the window closing
while auto-start is still creating the session) cannot leave a kernel session that nothing would stop. A
faulted session is never restarted once the engine is disposed, and calling `Dispose` again is harmless.

### The cross-process guard

TraceEvent's `Create` option replaces a running session that has the same name, which would silently end
another DriveMonitor's monitoring. `Start` therefore takes a machine-wide named semaphore,
`Global\DriveMonitor.Session.<session name>`, with a count of one. `Stop`, and a fault that ends monitoring,
release it; as a kernel object it also disappears when the process holding it exits.

Creating or holding that semaphore needs no privilege, so any user could take it first to keep DriveMonitor
from monitoring; only starting the kernel session needs administrator rights. So when the guard cannot be
taken (another process holds it, or another program created the name as another kind of object or with an
access list that shuts DriveMonitor out), the trace session decides. `Start` asks Windows for the running
sessions (`TraceEventSession.GetActiveSessionNames`):

- **A session with this name is running:** another DriveMonitor is monitoring, and `Start` fails with
  *Another DriveMonitor window or command is already monitoring with the session "\<name>". Close it first.*
- **None is running:** `Start` asks again every 200 ms for up to 2 seconds, because a DriveMonitor that has
  just taken the guard, or is restarting its session, has no session for a moment. A session that appears
  means the refusal above; a guard released in the meantime is taken. Otherwise the guard is stale or held
  to block monitoring: `Start` goes on without it and sets `MonitorEngine.StartWarning` to *Another program
  holds Global\DriveMonitor.Session.\<name>, which keeps two DriveMonitors from sharing the session "\<name>",
  but no such session is running, so monitoring started anyway.* A DriveMonitor started later finds this one
  by its session and refuses. The app and `drivemon` do not display `StartWarning`.
- **The sessions cannot be listed:** `Start` refuses, with *Could not check whether another DriveMonitor is
  running: ...* when the guard could not even be opened, and with the message above otherwise.

| Session name | Used by |
| --- | --- |
| `DriveMonitor-FileIO` | The app (the `MonitorOptions` default) |
| `DriveMonitor-CLI` | `drivemon` |
| `DriveMonitor-SelfTest` | `drivemon --self-test` |

Because the names differ, the app and the CLI can monitor at the same time, each with its own kernel
session. Separately, the app allows one window per Windows session through the mutex
`Local\DriveMonitor.App.SingleInstance`. Any program in the Windows session can hold that name too, so when
the app cannot take the mutex (it waits up to 10 seconds when started by **Restart as administrator**), it
looks for another process with its own image name in the same Windows session whose token is elevated;
`DriveMonitor.exe` always runs elevated, because its manifest requires it. Only if there is one does the app
say *DriveMonitor is already running.* and exit; otherwise it runs without the mutex. A process whose
elevation cannot be read counts only when the app itself is not elevated, because a process that is not
elevated cannot look at an elevated one.

### Faults and auto-restart

If `Process()` returns although nobody called `Stop` ("The kernel trace session was stopped by another
program."), or throws, the source raises `Faulted` on the consumer thread. Stopping joins that thread, so the
engine hands the fault to the thread pool. There, under `_lifecycleLock`, it ignores the fault when the
engine is disposed or stopped or the source is no longer current; otherwise it retires the failed source and
publishes what was waiting. If `AutoRestart` is on and fewer
than three restarts were attempted in the last 60 seconds, it refreshes the volume map, starts a new source
and raises `Restarted`; the app shows a banner and the CLI a message, because events in between may be missing.
Otherwise it stops, releases the guard and raises `Faulted`.

Every callback catches its own exceptions, because one escaping into TraceEvent would end the session. It
counts them in `CallbackErrors` and keeps the last message in `LastCallbackError`; each costs one event, not
the session. The self-test fails if any occurred.

### Process exit

.NET does not run finalizers at exit, and a kernel session outlives the process that started it. `Start`
therefore subscribes to `AppDomain.ProcessExit`, whose handler calls `Stop`. The app also disposes the engine
when it exits and when Windows logs off or shuts down, and stops it first in its unhandled-exception
handlers. Before disposing, the app waits up to 15 seconds for a `Start` or `Stop` that is still running on
the thread pool.

`drivemon` stops the engine from an `AppDomain.UnhandledException` handler, because a crash on a worker
thread, such as the release timer, runs neither `finally` blocks nor `ProcessExit`; there, live monitoring
also closes the CSV log, and the self-test removes its test files. Live monitoring handles Ctrl+C itself and
stops normally. The self-test turns Ctrl+C and Ctrl+Break into a request that its main thread answers within
a quarter of a second, or once the file operation under way is done, by stopping the session and removing
its test files. Closing the console window raises `ProcessExit`; the self-test then stops the session at
once and, if the main thread has not removed the files within 3 seconds, removes them itself.

A process that is killed can do none of this: its session keeps running until a DriveMonitor with the same
session name starts (which replaces it) or someone stops it with `logman stop <name> -ets` (see
[TROUBLESHOOTING.md](TROUBLESHOOTING.md)).

### Options

`MonitorOptions` holds the engine settings. The front ends set only the session name,
`CaptureDirectoryListings` (the CLI's `--listings`, always on for the app and the self-test) and
`ExcludeOwnProcess` (the app's "Hide DriveMonitor's own activity"; off for the self-test). The rest keep
their defaults:

| Option | Default | Meaning |
| --- | --- | --- |
| `SessionName` | `DriveMonitor-FileIO` | Kernel session name |
| `BufferSizeMB` | 64 | Kernel buffer memory for the session |
| `UseCompletionEvents` | true | Match completions (real byte counts, success or failure, created or opened) |
| `CompletionWaitMs` | 2,000 | How long an operation waits for its completion |
| `MaxQueuedEvents` | 250,000 | Events kept for `DrainEvents` |
| `MaxTrackedFiles` | 200,000 | Distinct files with statistics |
| `CaptureDirectoryListings` | true | Report folder listings |
| `FlushIntervalMs` | 500 | How often Windows is asked to hand over buffered events (0: about once a second) |
| `RequestFileNameRundown` | true | Ask for the names of files already open at start |
| `ParsePathOperations` | true | Learn rename targets from `FileIo_PathOperation` |
| `AutoRestart` | true | Restart a session that stopped unexpectedly, at most three times a minute |
| `MaxLogFileMB` | 100 | Size at which the activity log continues in a new file (0: one file of any size) |
| `MaxLogFiles` | 10 | Files of one activity log that are kept; starting another deletes the oldest (0: all) |
| `MinLogFreeSpaceMB` | 512 | Free space on the log's drive below which the activity log does not start, or stops (0: no check) |
| `ExcludeOwnProcess` | true | Hide DriveMonitor's own file activity |

## Data-bounding caps

The tables, queues and files that grow with file activity have limits, so a long session or a burst cannot
exhaust memory or fill a drive.

| What | Limit | When it is reached |
| --- | --- | --- |
| Kernel buffers | 64 MB (`BufferSizeMB`) | The kernel drops events: **Lost** |
| Event queue for the front ends | 250,000 events (`MaxQueuedEvents`) | The oldest queued event is dropped: **Dropped**. Totals and the log already have it. |
| Operations waiting for a completion | 200,000 | The oldest is released without its completion |
| Wait for a completion or a rename target | 2,000 ms (`CompletionWaitMs`) | The operation is published without it (`Unknown`, or a rename without a target) |
| Completions and rename targets that arrived early | 500,000 each, kept 2,000 ms; 1,024 per request address | The oldest are discarded; on one address, the earliest logged |
| Request addresses whose latest start is remembered | 65,536 in each of two generations | The older generation is dropped |
| File-name tables (by FileKey, by FileObject) | 500,000 entries each | The table is cleared |
| Process names | 65,536 | The cache is cleared |
| Files with statistics | 200,000 (`MaxTrackedFiles`) | New files are counted as untracked; files already tracked keep updating |
| Folders remembered without a row | 200,000 (`MaxTrackedFiles`) | The set is cleared and starts over |
| Processes with statistics | 50,000 (`ActivityAggregator`'s `maxTrackedProcesses`, not a `MonitorOptions` setting) | New processes get no row and are counted in `UntrackedProcessEvents`; their events still count in the drive and file totals |
| Process names per file | 16 listed, up to 4,096 more counted | Further names are neither listed nor counted, nor matched by the app's Files search |
| CSV log backlog | 100,000 events | New events are dropped and counted (shown in the app's diagnostics) |
| CSV log file | 100 MB (`MaxLogFileMB`) | The log continues in a new file |
| CSV log files | 10 per log (`MaxLogFiles`) | The oldest files of that log are deleted |
| Free space on the CSV log's drive | 512 MB (`MinLogFreeSpaceMB`) | The log does not start, or stops; what it has not written is counted as dropped |
| Closing the CSV log | 30 s in all, 5 s without a completed write | The rest is counted as dropped |
| App: Live activity rows | 20,000 (`MaxRows` in `settings.json`, 1,000 to 1,000,000) | The oldest rows are removed |
| App: events held while the view is paused | The same as `MaxRows` | The oldest are not kept; a banner says how many |
| App: events drained per refresh | 50,000 every 250 ms | The rest wait in the queue for the next refresh |
| App: `errors.log` | 1 MiB | The file is emptied and started again |
| Automatic restarts | 3 per minute | Monitoring stops and reports the fault |

## Front ends

- **App.** `MainViewModel` owns the engine. A UI timer drains events into `ActivityFeed` (grouping, row
  limit, pause buffer) and applies statistics snapshots to `StatsTable`s for Files and Processes, which
  update rows in place. `ViewResort` re-sorts a sorted view after rows changed in place, and a table reports a
  batch as one reset when so many rows are selected that item-by-item changes would cost more. `DeviceChangeWatcher` listens for volume arrival
  and removal, and passes the letters of new local volumes on at once; while **Monitor new USB drives
  automatically** is on, `DriveArrivals` then monitors each letter that has no drive listed, or whose drive
  was external when it was last connected, until the next drive enumeration makes the final choice.
  `SettingsStore` keeps `settings.json`, `ErrorLog` writes `errors.log`, and `SingleInstance` keeps to one
  window per Windows session. Kernel sessions are started and stopped on the thread pool.

  The app runs elevated, but `%LOCALAPPDATA%\DriveMonitor` belongs to the user, so any program the user runs
  can plant junctions and links there. `SettingsStore` and `ErrorLog` therefore write through
  `LinkSafeFolder`, which checks the handles it then uses rather than paths, so a link swapped in after a
  check gains nothing. `Open` opens the folder with `FILE_FLAG_OPEN_REPARSE_POINT`, refuses it when it is a
  junction, symbolic link or other reparse point, and keeps it open without `FILE_SHARE_DELETE`, so it cannot
  be renamed and replaced while in use. `OpenFile` opens a name relative to that handle (`NtCreateFile` with
  `RootDirectory` and `FILE_OPEN_REPARSE_POINT`) and refuses links, folders and files with more than one name
  (hard links); it has no mode that truncates a file before that check. `Rename` renames an open file within
  the folder through its handle and refuses to replace a link, a folder or a hard-linked file.
  `settings.json` is written to `settings.json.tmp` and renamed over the old file; `errors.log` is truncated
  through its open handle once it passes 1 MiB. When a check fails, the settings are not saved and the error
  is not logged. After a failed start, `SettingsStore.TrySetAside` moves `settings.json` to
  `settings.bad.json`, so that the next start uses the defaults.
- **CLI.** `CliOptions` parses the arguments (`Program.cs`), `LiveMonitor` prints events and the summary,
  and `SelfTest` runs the end-to-end check. Without `--drives`, `DriveWatcher` keeps the default selection
  up to date: on each 200 ms pass it compares the drive letters that exist (`GetLogicalDrives`, which reads
  no device) with those it saw last, and only when one came or went, or every 10 seconds, it refreshes the
  volume map, enumerates the drives and adds each drive of the default selection that is not monitored yet.
  It never removes one. `ConsoleText.Clean` replaces control characters and bidirectional formatting
  characters with `?` in what other people choose before it is printed: paths, process names, details,
  volume labels and drive models.
- **Self-test.** It reads every raw operation, with its completion, through the engine's internal
  `RawOperationObserver`. It works in a `TestFolder`, which keeps everything another user controls out of an
  elevated process's writes and deletes:
  - the folder is new: one `NtCreateFile` call with `FILE_CREATE` creates it and returns its handle, and
    fails if anything of that name exists; the name has 64 random bits;
  - the same call sets a protected access list: full control for Administrators and SYSTEM, inherited by the
    files inside, and only `READ_CONTROL` for the owner (FAT and exFAT ignore it, but have no links either);
  - the handle is kept until the cleanup without `FILE_SHARE_DELETE`, so nobody can rename or replace the
    folder;
  - `GetFinalPathNameByHandle` on that handle must give the requested path, so the path leads through no
    junction, symbolic link or SUBST drive;
  - the cleanup deletes only the files the test recorded as created, then the folder with a delete that is
    not recursive, and reports anything else instead of deleting it.
- **Tests.** The unit tests in `tests/DriveMonitor.Tests` exercise the engine through its `IFileIoSink`
  side and test the correlator, classifier, path mapper, statistics, CSV code, `LinkSafeFolder` and the
  session guard directly, as well as parts of the app (the activity feed, tables, search, drive arrivals,
  settings, single instance) and of the CLI (options, drive watcher, console text, the CSV start, the
  self-test folder), so they need no kernel session and no elevation. A few call the real Windows APIs the
  engine depends on (drive enumeration, the storage query, `QueryDosDevice`, process image paths) on the
  test machine, which also needs no administrator rights. The self-test covers the kernel itself, locally
  and in CI.
