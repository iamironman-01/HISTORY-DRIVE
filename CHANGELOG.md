# Changelog

All notable changes to DriveMonitor are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

Nothing has been released yet; the version in `Directory.Build.props` is 0.1.0.

## [Unreleased]

### Added

- **Engine** (`DriveMonitor.Core`): real-time monitoring of the Windows kernel's file I/O trace through ETW,
  with TraceEvent 4.0.0. It reports reads, writes, creates, opens, deletes, renames, metadata changes,
  flushes and folder listings on the selected drives, with the process that did each one.
  - Completion matching by request address and timestamps, for real byte counts, success or failure with
    the NTSTATUS reason, and whether a file was created or only opened.
  - Statistics per file, per drive and per process, with paging I/O kept separate.
  - Refreshable kernel-to-DOS path mapping, so files on a pendrive plugged in during monitoring get their
    drive letter.
  - Names of files that were already open when monitoring started (the kernel's file-name rundown), and the
    new names of renamed files (`FileIo_PathOperation`).
  - A continuous CSV log and CSV export, with protection against formula injection.
  - Automatic restart of a kernel session that stops unexpectedly, and a cross-process guard that keeps two
    DriveMonitors from sharing a session.
  - Limits on the tables and queues that grow with file activity.
- **CLI** (`drivemon`): live monitoring with filters (`--opens`, `--listings`, `--other`, `--paging`,
  `--failed`), `--csv`, `--duration`, `--quiet` and a summary on exit; `--list`; `--version`; and
  `--self-test`, an end-to-end check of 17 points on a real kernel trace, with `--report`.
- **App** (`DriveMonitor.exe`, WPF): a drive panel that picks up new USB drives automatically; Live
  activity, Files and Processes tabs; filters, search, grouping of repeated operations, pause and sorting;
  Record log and CSV export; banners and diagnostics in the status bar; one window per Windows session,
  settings kept between runs, and unexpected errors written to `errors.log`.
- **Scripts**: `scripts/Invoke-SelfTest.ps1` builds `drivemon` and runs the self-test in one step, asking
  for administrator rights when needed; `scripts/Stop-DriveMonitorSessions.ps1` stops kernel trace
  sessions left behind by a DriveMonitor that was killed.
- **Packaging**: package versions managed centrally in `Directory.Packages.props`; locked restores
  (`packages.lock.json`, enforced in CI); release metadata and a product version that carries the commit it
  was built from; publish profiles for a framework-dependent win-x64 build and self-contained, single-file
  win-x64 and win-arm64 builds, which keep TraceEvent's native DLLs beside the executable where TraceEvent
  loads them.
- **Tests**: unit tests for the engine, and tests that call the real Windows APIs it depends on (drive
  enumeration, the storage query, device and process-image lookups).
- **CI**: a GitHub Actions workflow that builds and tests every push and every pull request to `main`, with
  test counts and code coverage on the run's summary page and a 60 % floor for `DriveMonitor.Core` line
  coverage; runs the kernel-trace self-test on elevated Windows Server 2022 and 2025 runners and checks
  that the cleanup script stops a session left by a killed `drivemon`; and publishes the app and the CLI
  from `main` as a zip with a SHA-256 checksum. A Release workflow that publishes a GitHub release from a
  version tag, CodeQL code scanning, Dependabot for NuGet packages and GitHub Actions, and `global.json`
  pinning the .NET 9 SDK.
- **Documentation**: the README, the architecture and troubleshooting guides, the security policy, the
  contributing guide, this changelog, and issue and pull request templates.

### Changed

- **Engine** (`DriveMonitor.Core`)
  - The activity log (the app's **Record log** and `drivemon --csv`) is bounded, so a long recording can no
    longer fill its drive. Three new `MonitorOptions`, read when a log starts, set the limits; 0 turns each
    off:
    - `MaxLogFileMB` (100): at this size the log continues in a new file, named like the first with `.002`,
      `.003` and so on before the extension. Each file starts with its own header row.
    - `MaxLogFiles` (10): starting a new file deletes the oldest files of the same log. Only files that this
      log created are deleted; a file that another program holds open is kept and tried again at the next
      file.
    - `MinLogFreeSpaceMB` (512): `MonitorEngine.StartLog` throws `IOException` when the log's drive already
      has less free, and a running log stops with that `IOException` in `Error`. The free space is checked at
      most once a second while the log writes, and before every new file; a log on a network (UNC) path is
      not checked.
  - `CsvActivityLog` takes these limits in its constructor (`filePath, maxFileBytes, maxFiles, minFreeBytes,
    capacity`) and gains `CurrentFilePath`, the file being written, and `IsLogFile`, with which the engine
    leaves every file of its own log out of the events. Rows are collected into chunks of up to about 64 KB,
    and each chunk is written once it is full or no more events are waiting; `Written` counts only rows whose
    write completed.
  - Events now reach completion matching in the order the kernel logged them (see Fixed), so each one is held
    back for a moment. The new `MonitorOptions.ReorderWindowMs` sets how long: by default `FlushIntervalMs`
    plus 250 ms, so 750 ms (1,250 ms when `FlushIntervalMs` is 0); 0 turns reordering off, and it is not used
    without `UseCompletionEvents`. Events reach the statistics, the activity log and `DrainEvents` about 0.75
    seconds later than before. The new `MonitorEngine.ReorderOverflows` counts the events passed on early
    because 250,000 were already held.
- **CLI** (`drivemon`)
  - Without `--drives`, `drivemon` also monitors drives plugged in after it started. Every 200 ms it checks
    whether a drive letter came or went, and every 10 seconds it reads the drive list anyway; each new drive
    of the default selection is added and announced as `Now monitoring F: STICK (SanDisk Ultra)`, also with
    `--quiet`. With no such drive at start, it waits for one (`Waiting for a drive to be plugged in`) instead
    of exiting with "No drives to monitor". Drives are never taken out of the selection.
- **Tests**: the test project uses xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.11.0, xunit.runner.visualstudio
  4.0.1 and coverlet.collector 10.1.0.

### Fixed

- **Engine** (`DriveMonitor.Core`)
  - A kernel session is never started after the engine is disposed. A `Start` racing with or following
    `Dispose`, for example the window closing while auto-start was still creating the session, could create
    a kernel session that nothing would stop. `Start` now throws `ObjectDisposedException` after `Dispose`,
    and a faulted session is not restarted once the engine is disposed.
  - A failure in the trace session's constructor is reported as a readable `MonitorStartException` instead
    of reaching the caller as a raw exception.
  - Each file's process-list text is cached and rebuilt only when a new process name arrives, so statistics
    snapshots hold the lock that the trace consumer needs for less time during bursts.
  - A monitored operation could take the completion or rename target of another operation that reused its
    request address, such as an open on an unmonitored drive: a 4 KB read was reported as failed with 0
    bytes, or an open as "created". The trace source now also reports the starts it does not pass on, such as
    those on other drives and every cleanup, close, query, file-system control and change notification, and
    a completion or path logged after another start on the same address is refused. An operation whose own
    completion is missing is then published as `Unknown` instead of taking another's.
  - Two mix-ups remained that the order in which events arrive cannot settle, because Windows hands over each
    processor's buffer separately: a completion of a newer use of a request address that arrived before the
    earlier uses went to an earlier operation, which left the newer one `Unknown`; and a monitored start that
    arrived late took the completion of an unmonitored operation that had reused its address since (a read
    reported as failed "not found" with 0 bytes, an open as "created"). Starts, completions, rename targets
    and the starts of unreported operations now pass through a reorder buffer, which hands them to completion
    matching in timestamp (QPC) order. Events that Windows delivers later than the window are matched as
    before.
  - A rename or hard-link target on another drive than the file's was reported, in the Files table and in the
    Target column of the CSV files too. Neither can leave its volume, so such a target was the path of an
    unrelated operation matched through a recycled request address. It is now ignored, and the event reads
    "renamed or moved" or "hard link created".
  - A rename that only changes the case of a name, such as `E:\photos` to `E:\Photos`, was reported without
    its target. Windows keeps the case a name is given, so the rename is now reported with the new name. A
    target that differs only in the case of a folder above the name still names the same file and is ignored.
  - Closing the activity log (turning Record log off, closing the app, the end of a `drivemon --csv` run)
    could lose the end of the log silently, let an `InvalidOperationException` escape, or hang on a drive
    that stopped accepting data. The log's writer task now owns the file and writes it asynchronously.
    `Dispose` waits as long as the drive keeps accepting data, at most 30 seconds, gives up after 5 seconds
    without progress, counts what was not written as dropped and never throws.
  - After a write error (disk full, drive removed), the activity log kept accepting events that it never
    wrote and never counted. It now refuses later events and counts them, and everything still queued, as
    dropped; `Error` is set last, so the counts are final when it appears.
  - When closing the activity log gave up on a drive that does not end a stuck write, `Error` stayed empty,
    so the log looked complete although rows were missing. `Dispose` now sets `Error` in that case too, to the
    `TimeoutException` the log uses when it gives up (*The drive stopped accepting the activity log; events
    not yet written were dropped.*); the rows of the stuck write are counted once it returns.
  - The kernel's lost-event count is now read on every flush-timer tick, and every second when
    `FlushIntervalMs` is 0. When another program stopped the session, that session's losses could be missing
    from `LostEvents` (the app's **Lost**, `drivemon`'s "lost by kernel"); now at most the last tick's are.
  - A read that ends at the end of the file (`STATUS_END_OF_FILE`, which ends every read-until-empty loop)
    was reported as failed. It is now a successful read of 0 bytes; a write with that status still fails.
  - Reads and writes of 2 GiB or more were counted as 2 GiB - 1 bytes at most, or as 0 bytes without a
    completion. Request sizes now keep the kernel's full value, up to 4 GiB - 1.
  - Folders could get Files-table rows that called them files: a folder that existed before monitoring,
    when it was deleted or renamed, and any folder that a folder watcher or backup tool opened. An open that
    accepts a folder as well as a file now carries the new flag `MaybeDirectory` (64) and creates no row
    unless it deletes; folders seen opened or listed are remembered, and later evidence corrects whether a
    row is a folder.
  - Live activity and the CSV files showed a folder's delete or rename as a file's, because Windows reports
    both without the folder flag. The engine now remembers the paths it has seen opened or created as folders,
    or listed, by the rules and within the limit of the statistics, and publishes a delete or rename of such a
    path with the `Directory` flag, which the CSV files write in their Flags column. Only a delete or rename
    known to have succeeded forgets the folder, so the retry that follows a request FAT rejected is flagged
    too. When Windows reports a renamed folder's new path, the folder stays a folder there, in the engine and
    in the Files table. **Clear** forgets these folders; stopping and starting monitoring does not.
  - A cancelled delete left the file marked as deleted. A request that cancels a pending delete now carries
    the new flag `DeleteCancelled` (32), which clears the Deleted status.
  - Events arrive roughly, not strictly, in time order, so first and last times, the last operation and
    process, a drive's last activity, the target of the last rename and the Deleted status could move
    backwards. They now follow the event timestamps.
- **CLI** (`drivemon`)
  - `--duration` values such as `Infinity`, `1e400` or `3e11` crashed `drivemon` with a stack trace. It now
    takes more than 0 and at most 31536000 seconds (365 days); anything else is a usage error (exit code 2).
    The run is timed on a monotonic clock, so changing the system clock no longer shortens or stretches it.
  - A `--csv` file that cannot be written (open in another program, a folder, an invalid path) crashed
    `drivemon`. It now prints `Cannot write the CSV file: <reason>` and exits with code 1 before the kernel
    session starts.
  - Ctrl+C during `--self-test` ended the process at once and left the `DriveMonitor-SelfTest` kernel session
    running and the test files on the drive. Ctrl+C now stops the session and removes the files, then prints
    `Cancelled: the kernel session is stopped and the test files are removed.` and exits with code 1. Closing
    the console window or a crash also stops the session and removes the files; in live monitoring, a crash
    stops the session and closes the CSV log.
  - A `--csv` log that stopped during the run (its drive was pulled out or fell below 512 MB free, or a write
    failed) was not reported, the summary had no line for the log, and the exit code stayed 0. `drivemon` now
    says so on stderr within about 200 ms, `The CSV log stopped: <reason> <n> rows were written to <file>;
    later events are not logged.`, and keeps monitoring. At the end it finishes the log before the summary,
    printing `Finishing the CSV log...` when that takes more than a second; reports a failure it has not
    reported yet, also with `--no-summary`; ends the summary with `CSV log: <file>  rows written: <n>  rows
    dropped: <d>`, plus `error: <reason>` when the log failed; and exits with code 1 when the log stopped on
    an error.
  - `drivemon` did not say when monitoring started although another program holds the session guard (see
    Security). It now prints `MonitorEngine.StartWarning` once on stderr after the start: in live monitoring
    before the `Monitoring ...` line, in the self-test after `Session started in N ms`. The self-test's
    `--report` file does not include it.
- **App** (`DriveMonitor.exe`)
  - The status bar's **Unnamed** tooltip and the engine's documentation now say that unnamed I/O is counted
    for all drives, because without a file name the drive is unknown.
  - A new pendrive was monitored only after a 750 ms delay and a full read of the drive list, so its first
    activity (AutoPlay, an antivirus scan, a copy started at once) was lost. With **Monitor new USB drives
    automatically** on, its letter is now monitored as soon as Windows reports the new volume, and the drive
    list makes the final choice afterwards.
  - A refresh with more than 200 new rows collapsed a multi-row selection to one row, and the context-menu
    commands then acted on the wrong rows. Selections now survive refreshes, and the menu acts on the rows
    that were selected when it opened.
  - With thousands of rows selected, every refresh could take seconds and freeze the window. Such refreshes
    are now reported to the grid as one reset, which keeps the selection.
  - Sorted tables were not re-sorted when rows changed in place, such as a file whose written bytes grew.
    Changed rows now move to their place about once a second.
  - The Files search was checked only when a row was added, so a file that started to match later (read by
    the program searched for, or renamed) stayed hidden, and it could not match a program beyond the three in
    the Processes column, which **Filter to this process** relies on. It now checks rows again when they
    change and matches every program stored for the file (`FileStatsSnapshot` has a new optional last
    parameter, `ProcessNames`, for this).
  - Live activity grouped different kinds of change, and different failures, into one row that kept only the
    last detail and the first NTSTATUS. Grouped operations now also share their NTSTATUS and detail; size
    changes of a file still group, showing the latest size.
  - A saved window size or position that no screen can have (a hand-edited `1e300`, say) stopped the app
    from starting, every time. Such bounds are now dropped, and if a start fails anyway, `settings.json` is
    moved to `settings.bad.json` so that the next start uses the defaults.
  - Screen readers announced every row of the three tables, and some cells, by a .NET class name, and never
    read banners out. Rows, cells, banners and drive cards now have names, and a new banner is read out:
    errors interrupt the screen reader, other banners do not.
  - Keyboard focus was invisible in the tables, and several colours missed WCAG contrast. The tables now draw
    a focus ring, a selected row has an accent bar at its left edge, a checked toolbar toggle has an accent
    border, and tab counts, failed rows, status texts, operation badges and check box outlines are darker.
  - The app did not say when monitoring started although another program holds the session guard (see
    Security). It now shows `MonitorEngine.StartWarning` in an amber banner, which screen readers read out
    and which goes when monitoring stops.
  - Closing the window stopped everything on the UI thread once the window had gone, so DriveMonitor kept
    running unseen for up to about a minute while a start finished, the kernel session stopped and a log on a
    slow drive was saved, and starting it again meanwhile said "DriveMonitor is already running.". During a
    logoff the window froze, Windows listed DriveMonitor as a program that prevents signing out, and the
    settings were saved last. Now the window stays open and responsive while that work runs on the thread
    pool: it shows *Saving the activity log… DriveMonitor closes when it is done.* while a log is saved, greys
    out Start/Stop, Record log and the three Export commands, and closes by itself when the work is done;
    closing it again meanwhile starts no second stop. A logoff or shutdown saves the settings first and waits
    at most 3 seconds for the rest.
  - When stopping the kernel session failed at exit, the activity log was not ended: its file stayed open and
    the events it still held were lost. The failure is still written to `errors.log`, and the engine is now
    disposed anyway, which stops the session again and ends the log.

### Security

- **Engine** (`DriveMonitor.Core`)
  - Any user could stop every DriveMonitor from monitoring by creating or holding the semaphore
    `Global\DriveMonitor.Session.<session name>` first, which needs no privilege. Only an administrator can
    start the kernel session, so when the guard cannot be taken, `Start` now asks Windows whether a session
    with that name is running: if one is, or the sessions cannot be listed, it refuses as before; if none
    appears within 2 seconds, it monitors without the guard and sets the new `MonitorEngine.StartWarning`,
    which the app shows in an amber banner and `drivemon` prints on stderr.
  - Heavy file activity on any drive, monitored or not, could stall the trace consumer for seconds, so the
    kernel dropped events on the monitored drives and the window froze; any user could cause this on
    purpose. Completions that matched no operation yet were purged in quadratic time on recycled request
    addresses. Waiting operations and stored completions now sit on doubly linked chains sorted by
    timestamp, with at most 1,024 stored values per address, so each one leaves in constant time, and the app
    reads the number of waiting operations without the correlator lock.
  - Per-process statistics had no limit, so a session with many short-lived programs on a monitored drive
    kept growing. `ActivityAggregator` now keeps at most 50,000 processes (new constructor parameter
    `maxTrackedProcesses`, with `MaxTrackedProcesses` and `TrackedProcesses`); events of later processes
    still count in the drive and file totals and in the new `UntrackedProcessEvents`.
  - The activity log runs as administrator, often in a folder that other users can change (the root of a
    pendrive, a shared folder, `C:\Temp`) and under names they can predict. It created each file by path with
    `FileMode.Create` and deleted old files by path, so a junction, symbolic link or hard link planted at one
    of those names, or a junction swapped in for its folder between two files, could make it empty or delete
    a file that only administrators may change. The log now opens its folder once through `LinkSafeFolder`,
    which refuses a link at the folder itself, and holds it open until the log ends, so the folder cannot be
    renamed, deleted or replaced meanwhile. Every file is created, emptied and deleted relative to that
    folder, never by path, and a name that is a link, a folder or a file with more than one name is refused:
    the log does not start, or stops, with an error that names the file. Junctions in the folders above the
    log's folder are still followed.
  - Process names come from the kernel trace, from Windows and from command lines, which whoever starts a
    process chooses, so a name could carry control characters or a bidirectional override into the app, the
    CSV export and the CSV log (`invoice` + U+202E + `txt.exe` reads as `invoiceexe.txt`). The engine now
    replaces control characters, the Unicode line and paragraph separators and bidirectional formatting
    characters with `?` in every process name it stores. A name from a command line that has one of them, or
    a character no file name can have, is ignored, and the kernel's 15-character name is kept.
- **CLI** (`drivemon`)
  - The self-test runs as administrator, but it used a folder name anyone could predict,
    `.drivemonitor-selftest-<process id>`, adopted a folder of that name that already existed and deleted it
    recursively, so it could delete what another user put there, or files elsewhere through a junction. It
    now creates a new folder with a random name and an access list that lets only administrators and SYSTEM
    change it, keeps it open so that it cannot be renamed or replaced, refuses a path that leads through a
    junction, symbolic link or SUBST drive, and at the end deletes only the files it created, then the
    folder if it is empty. Anything else in the folder is reported and left in place.
  - Paths, process names (taken from command lines, which whoever starts a process chooses), listing patterns
    and error messages that quote a path were printed as they were, so a program could overwrite or fake
    `drivemon`'s lines with control characters and escape sequences. Control and bidirectional formatting
    characters are now printed as `?` in event lines, the summary, `--list`, the `Now monitoring` line, the
    start warning, the reason live monitoring could not start, the kernel session's fault and restart lines,
    the CSV log's lines and the lines that say where the self-test's report was saved or why it was not.
- **App** (`DriveMonitor.exe`)
  - The app runs elevated but writes `settings.json` and `errors.log` in `%LOCALAPPDATA%\DriveMonitor`,
    where every program of the user can plant links, and it followed them, so it could be made to overwrite
    or delete other files as administrator. Both files are now written through the new `LinkSafeFolder`,
    which refuses junctions, symbolic links and hard-linked files and checks the handles that it then uses;
    `errors.log` is emptied instead of deleted once it passes 1 MiB.
  - Any program in the user's Windows session could keep the app from starting by holding the mutex
    `Local\DriveMonitor.App.SingleInstance` ("DriveMonitor is already running."). The app now gives way only
    when another elevated process with its own image name runs in the same Windows session.

[Unreleased]: https://github.com/iamironman-01/HISTORY-DRIVE/commits/main
