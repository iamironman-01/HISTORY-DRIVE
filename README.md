# DriveMonitor

[![CI](https://github.com/iamironman-01/HISTORY-DRIVE/actions/workflows/ci.yml/badge.svg)](https://github.com/iamironman-01/HISTORY-DRIVE/actions/workflows/ci.yml)

See **every file read, write, create, delete and rename** on your pendrives, memory cards and secondary
disks, live, together with the program that did it.

DriveMonitor comes as a Windows desktop app (`DriveMonitor.exe`) and a command-line tool (`drivemon.exe`)
that share one engine. Both read the Windows kernel's own file I/O trace, so they see file activity from
every program, including Windows itself, antivirus scanners and the search indexer.

## What it does

For each file operation on the drives you choose, DriveMonitor shows:

| What | Example |
| --- | --- |
| The operation | Read, Write, Create, Open, Delete, Rename, Change (size, attributes, timestamps), Flush, List (folder listing) |
| The file's full path | `E:\photos\img_0042.jpg` |
| The program and its process ID | `explorer.exe`, PID 5312 |
| For reads and writes, the bytes transferred and the file offset | `1 MB` at offset `4,194,304` |
| Whether it worked, and why not | `failed: access denied`, `failed: write protected`, `failed: disk full` |
| What happened | `created`, `overwritten`, `size → 1,000 bytes`, `→ E:\photos\holiday.jpg`, `moved to the Recycle Bin` |

It also keeps running totals per file, per drive and per program. The app, and `drivemon` unless you name the
drives with `--drives`, notice when a pendrive is plugged in and start watching it automatically.

### How it works

Windows reports file activity through **Event Tracing for Windows (ETW)**, the instrumentation behind
Resource Monitor and Windows Performance Analyzer. DriveMonitor starts a real-time kernel trace session
(using Microsoft's [TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent)
library), keeps only the events for the drives you selected, matches each operation with its completion
event to learn the real byte count and whether it succeeded, and shows the result.
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) describes the pipeline in detail.

As a result:

- **No driver or service is installed.**
- **Your files are never read or scanned.** Everything DriveMonitor knows comes from the kernel trace; it
  keeps its own settings and logs in `%LOCALAPPDATA%\DriveMonitor`.
- **Administrator rights are required**, because Windows only lets administrators start a kernel trace
  session.

### Verified by CI

On every push, and on pull requests to `main`, the [CI workflow](.github/workflows/ci.yml) builds
DriveMonitor, runs the unit tests and then runs `drivemon --self-test` against a real kernel trace on
GitHub's elevated Windows Server 2022 and Windows Server 2025 runners. The [self-test](#the-self-test)
performs known file operations and checks that DriveMonitor reports each of them correctly:

- a new file is reported as created;
- 1 MiB of writes, 1 MiB of cached reads and 1 MiB of unbuffered reads are seen, and read bytes are not
  over-counted;
- a rename is seen, and its new name is reported;
- a truncate to 1,000 bytes is seen;
- a delete is seen, and so is a file opened with delete-on-close;
- I/O on a file that was opened before monitoring started is still named;
- a folder listing is seen;
- opening a file that does not exist is reported as failed ("not found");
- every event is attributed to the right process;
- completions are matched to their operations, the kernel dropped no events, and no trace callback failed.

A CI run in October 2026 passed all 17 checks with no warnings, no lost events and every completion matched
(65 of 65). CI also checks that `scripts/Stop-DriveMonitorSessions.ps1` stops the kernel session that a
killed `drivemon` leaves behind.

## Requirements

- Windows 10 or Windows 11 (x64). CI builds and tests on x64 and runs the self-test on Windows Server 2022
  and 2025; the published builds are for win-x64.
- To run a published build: the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
  (x64). To build from source: the .NET 9 SDK, 9.0.300 or a later 9.0 SDK ([global.json](global.json)).
- Administrator rights.
- Smart App Control must allow the binaries; see [Smart App Control](#smart-app-control).

## Quick start

### Download a build

Every successful CI run for a push to `main` publishes the app and the CLI as one zip:

1. Open the [CI runs on main](https://github.com/iamironman-01/HISTORY-DRIVE/actions/workflows/ci.yml?query=branch%3Amain+is%3Asuccess)
   and choose the latest successful run.
2. Under **Artifacts**, download **DriveMonitor-win-x64**. GitHub only offers artifact downloads to
   signed-in users, and deletes them after the repository's retention period; if the artifact has expired,
   build from source.
3. The downloaded artifact is itself a zip. Extract it to get `DriveMonitor-win-x64.zip` and
   `DriveMonitor-win-x64.zip.sha256`. In that folder, check that the two hashes match (letter case does not
   matter), then extract the inner zip:

   ```powershell
   Get-FileHash .\DriveMonitor-win-x64.zip -Algorithm SHA256
   Get-Content .\DriveMonitor-win-x64.zip.sha256
   Expand-Archive .\DriveMonitor-win-x64.zip -DestinationPath .\DriveMonitor
   ```

4. Install the .NET 9 Desktop Runtime (x64) if you do not have it. Then start
   `DriveMonitor\DriveMonitor.exe`, or run `DriveMonitor\drivemon.exe` from an administrator terminal.

The binaries are not code-signed, so Windows SmartScreen may warn the first time you start them.

**Releases.** Pushing a version tag such as `v1.0.0` runs the
[Release workflow](.github/workflows/release.yml), which runs the tests and the self-test and then publishes a
GitHub release with `DriveMonitor-<version>-win-x64.zip` (the same app and CLI, stamped with that version),
its `.sha256` checksum and the self-test report. Once the repository has releases, you can download the
latest one from its **Releases** page instead of a CI artifact.

### Build from source

```powershell
git clone https://github.com/iamironman-01/HISTORY-DRIVE.git
cd HISTORY-DRIVE
dotnet build DriveMonitor.sln -c Release
dotnet test DriveMonitor.sln -c Release --no-build
```

The app is then `src\DriveMonitor.App\bin\Release\net9.0-windows\DriveMonitor.exe` and the CLI
`src\DriveMonitor.Cli\bin\Release\net9.0-windows\drivemon.exe`. To put both in one folder, as CI does:

```powershell
dotnet publish src/DriveMonitor.App/DriveMonitor.App.csproj -c Release -r win-x64 --self-contained false -o publish/DriveMonitor
dotnet publish src/DriveMonitor.Cli/DriveMonitor.Cli.csproj -c Release -r win-x64 --self-contained false -o publish/DriveMonitor
```

#### Self-contained builds (no .NET runtime needed)

Publish profiles produce a single executable that carries its own .NET runtime, for machines where the
.NET 9 Desktop Runtime is not installed:

```powershell
dotnet publish src/DriveMonitor.App/DriveMonitor.App.csproj -p:PublishProfile=win-x64-selfcontained
dotnet publish src/DriveMonitor.Cli/DriveMonitor.Cli.csproj -p:PublishProfile=win-x64-selfcontained
```

The results are in each project's `bin\publish\win-x64-selfcontained\` folder: `DriveMonitor.exe` (about
120 MB) or `drivemon.exe` (about 70 MB), plus an `amd64\` folder with TraceEvent's native DLLs. Keep that
folder next to the executable; TraceEvent loads the DLLs from there to start the kernel session. The profiles
are:

| Profile | Result |
| --- | --- |
| `win-x64` | Framework-dependent folder, like the CI build; needs the .NET 9 Desktop Runtime (x64). |
| `win-x64-selfcontained` | Single executable for x64, runtime included. |
| `win-arm64-selfcontained` | Single executable for ARM64 Windows, runtime included (native DLLs in `arm64\`). |

[CONTRIBUTING.md](CONTRIBUTING.md) has the full developer workflow.

## Using the app

Start `DriveMonitor.exe` and approve the administrator prompt. Monitoring starts by itself (you can turn
that off). Only one DriveMonitor window runs per Windows session.

### Left panel

- **Drives**: one card per drive letter with its bus, model, size, free space and file system, the current
  read and write rate, and the totals so far. Tick a drive to watch it; the change takes effect at once.
  By default every drive except the Windows drive and network drives is ticked. DriveMonitor remembers
  which fixed disks you untick and whether you watch the Windows drive. A drive that is removed stays in
  the list as *Disconnected*, with its totals.
- When a pendrive, memory card or other external drive is plugged in while DriveMonitor runs, a notice
  appears. With **Monitor new USB drives automatically** on, the drive is ticked right away, and its activity
  is captured from the moment Windows reports the new volume, before the drive appears in the list; otherwise
  the notice offers **Monitor it**.
- **Show** chooses which operations appear in Live activity. Reads, Writes, Created, Deleted and Renamed
  are on by default; Opens, Folder listings, Other changes (attributes, timestamps, size changes and
  flushes), Paging I/O and Failed operations are off. These choices only filter the Live activity list,
  not the totals in Files and Processes.
- **Options**: Hide DriveMonitor's own activity (for example an export saved onto a watched drive), Monitor
  new USB drives automatically, and Start monitoring when DriveMonitor opens. All three are on by default.

### Toolbar

- **Start** / **Stop** monitoring.
- **Pause view** freezes the Live activity list so you can read it. Events are still captured and appear
  when you resume; if more arrive than the list can hold, the oldest of them are not kept. Files and
  Processes keep updating.
- **Auto-scroll** keeps the newest row in view (not while the list is sorted by a column).
- **Group repeated operations**: see [Grouping](#grouping).
- **Clear** empties all tables and totals.
- **Export** saves Live activity (the rows currently visible), Files or Processes as a CSV file.
- **Record log** writes every event to a CSV file in `%LOCALAPPDATA%\DriveMonitor\Logs` until you turn it
  off, or until the drive runs short of space (see [Record log and export](#record-log-and-export)). The
  arrow next to it has **Open logs folder**.
- The **search box** (Ctrl+F, Esc to clear) shows only the rows whose path or process name contains the
  text, in all three tabs. In Files it also matches the new name of a renamed file and every program that
  used the file, not only the three in the Processes column.

### Tabs

- **Live activity**: one row per operation (or group of repeated operations), with time, operation, count,
  size, file, process, PID and details such as `created`, `size → 1,000 bytes`, `failed: access denied`,
  `paging` or `unbuffered`. Hover over a file name for the full path, the new name of a renamed file, the
  range of bytes read or written, and the NTSTATUS code. `folder` marks an operation that DriveMonitor knows
  to be on a folder. Windows reports a folder's delete or rename without saying that it is a folder, so
  DriveMonitor marks one only when it already knows the path as a folder: opened or created as one, listed,
  or the new name of a renamed folder, since DriveMonitor started or since the last **Clear**.
- **Files**: totals per file since DriveMonitor started or since the last **Clear**: reads, bytes read,
  writes, bytes written, opens, a status (Created, Modified, Read only, Resized, Renamed → new name, Deleted, Moved to the
  Recycle Bin, ...), the last activity and operation, and up to three of the programs that used the file.
  Folders get a row only when they are created, deleted or renamed. Paging I/O is not in these columns; the
  row's tooltip shows it.
- **Processes**: totals per program: reads, writes, bytes, files created, deleted and renamed, and how many
  files it touched. Paging I/O is not attributed to programs.

Click a column header to sort, click again to reverse, and a third time to return to arrival order. In a
sorted table, rows whose values change move to their new place about once a second.
Right-click rows to copy their paths, open the containing folder, or filter to that file or process.
Double-click a row to open its folder in File Explorer (in Processes: to filter to that program).

### Grouping

With **Group repeated operations** on (the default), repeated reads, writes, opens, folder listings,
metadata changes and flushes share one row when they have the same file, process, paging flag, outcome,
NTSTATUS code and detail, and each comes within a second of the previous one. Two different failures, or a
timestamp change and a hard link, therefore get rows of their own, while the size changes of a file share a
row whatever the new size, and the row shows the latest size. The Count column then shows how many there were
(`×24`). Creates, deletes and renames always get a row of their own.

### Status bar

The status bar shows the monitoring state, events per second, events since DriveMonitor started, **Lost**
(events the kernel dropped; red when not zero), **Dropped** (events DriveMonitor discarded because the
display could not keep up), **Unnamed** (I/O whose file name Windows did not report) and the number of files
tracked.
Hover over the state for diagnostics: kernel events received, operations waiting for their completion,
callback errors, session restarts, whether names of already open files were requested, whether Windows
reports new names of renamed files, failed operations and the log file.
[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) explains what to do about lost and unnamed events.

Banners above the tables report what needs attention: red for errors, blue for information, and amber when
administrator rights are missing or when monitoring started although another program holds the guard that
keeps two DriveMonitors off one kernel session (see
[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md#another-drivemonitor-window-or-command-is-already-monitoring)).
Screen readers read each new banner out.

### Record log and export

- **Record log** files are named `activity-YYYYMMDD-HHMMSS.csv` and have one row per event, with the
  columns Time, Operation, Path, Drive, Bytes, Offset, Process, PID, TID, Flags, Status, NtStatus, Detail
  and Target.
- A recording has limits, so that it cannot fill the Windows drive. When its file reaches 100 MB, it
  continues in `activity-YYYYMMDD-HHMMSS.002.csv`, then `.003.csv` and so on, each with its own header row.
  It keeps its 10 newest files and deletes the older ones of the same recording. It does not start, or
  stops, when less than 512 MB is free on the drive: a banner says *Could not start the activity log: ...*
  or *The activity log stopped: ...*, and **Record log** turns off. DriveMonitor never deletes earlier
  recordings. What to do when a log stops:
  [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md#the-activity-log-stops-or-is-split-into-several-files).
- DriveMonitor writes the log as administrator, so it refuses links at the log's folder and files: when the
  `Logs` folder is a junction or a symbolic link, or one of the log's file names is taken by a link, a folder
  or a file with more than one name (a hard link), the log does not start, or stops, with a banner that names
  the path. While a recording runs, its folder cannot be renamed or deleted.
- Closing the window while recording saves the log first: the window stays open with the banner *Saving the
  activity log… DriveMonitor closes when it is done.* and closes by itself once the log is saved, which can
  take up to 30 seconds on a slow drive. When Windows logs off or shuts down, DriveMonitor waits at most 3
  seconds, so the last events can be missing from the log.
- **Export → Live activity** saves the visible rows as they are grouped, with first and last time, count,
  total bytes and the range of offsets. **Files** and **Processes** exports save every row, whatever the
  search box contains.
- CSV files are UTF-8 with a byte-order mark, so Excel shows non-ASCII file names correctly. Text that
  starts with `=`, `+`, `-` or `@` gets a leading apostrophe, so spreadsheet programs do not run it as a
  formula.

## Using the CLI

`drivemon.exe` needs an administrator terminal, except for `--list`, `--version` and `--help`. It monitors until you
press Ctrl+C or the `--duration` ends, then (unless `--no-summary` is given) prints totals per drive, the 20
busiest files, the kernel counters and, with `--csv`, a line for the CSV log.

```text
drivemon [options]              Monitor drives and print each file operation
drivemon --list                 List drives and whether they are monitored by default
drivemon --self-test [E:]       Verify capture on a drive (uses a temporary folder)
```

| Option | Meaning |
| --- | --- |
| `-d`, `--drives E,F` | Drives to monitor. Separate letters with commas, semicolons or spaces; `E`, `E:` and `E:\` all work. Default: the drives `--list` marks as monitored by default, which is every drive except the Windows drive and network drives, and such drives plugged in while `drivemon` runs. |
| `--system` | Also monitor the Windows drive. Ignored together with `--drives`; name the drive in that list instead. |
| `--opens` | Show file opens. |
| `--listings` | Show folder listings. Without this option they are not captured at all. |
| `--other` | Show metadata changes and flushes. |
| `--paging` | Show paging I/O from the cache and memory manager. |
| `--failed` | Show failed operations. |
| `--csv FILE` | Also write every event to a CSV file, including the ones the screen does not show. Same columns and limits as the app's Record log: `activity.csv` continues in `activity.002.csv` and so on every 100 MB, the 10 newest files are kept, and the log stops when less than 512 MB is free on its drive. |
| `-t`, `--duration SEC` | Stop after SEC seconds, for example `60` or `2.5`: more than 0 and at most 31536000 (365 days). Default: until Ctrl+C. |
| `-q`, `--quiet` | Do not print individual events. |
| `--no-summary` | Do not print per-file totals on exit. |
| `--report FILE` | Self-test: also save the report to FILE. |
| `-h`, `--help`, `/?` | Show help. |
| `--version` | Print the version and the commit it was built from, for example `drivemon 0.1.0+<commit>`, and exit. |

Options are not case-sensitive. The exit code is 0 on success; 1 when monitoring could not start (including
a `--csv` file that cannot be written), when the `--csv` log stopped on an error during the run, or when the
self-test failed or was cancelled; and 2 for invalid options, such as a `--duration` out of range.

Without `--drives`, `drivemon` keeps its selection up to date while it runs. Every 200 ms it checks whether a
drive letter came or went, and every 10 seconds it reads the drive list anyway. It adds each new drive of the
default selection and prints `Now monitoring F: STICK (SanDisk Ultra)`, also with `--quiet`. The start line
then reads `Monitoring E: and drives plugged in later`, or `Waiting for a drive to be plugged in` when there
is none yet. A drive that is pulled out stays selected, so it is monitored again when it comes back.

If the `--csv` file cannot be created, for example because another program has it open or its drive has less
than 512 MB free, `drivemon` prints `Cannot write the CSV file: <reason>` and exits with code 1 before
monitoring starts. If the log stops later, because its drive runs short of space or is removed or a write
fails, `drivemon` says so on stderr within a moment and goes on monitoring; the CSV file ends at that point:

```text
The CSV log stopped: Only 300 MB is free on E:\; the activity log stops when less than 512 MB is left. 1,234 rows were written to E:\logs\activity.csv; later events are not logged.
```

At the end, `drivemon` writes the rows still queued and closes the log before the summary. When that takes
more than a second, it prints `Finishing the CSV log...` first; on a slow drive it can take up to 30 seconds,
and Ctrl+C does not cut it short. A failure that was not reported yet is reported then, also with
`--no-summary`, in a line that ends `... and <n> were dropped.` The summary ends with a line such as
`CSV log: E:\logs\activity.csv through activity.002.csv  rows written: 1,234,567  rows dropped: 0`, which
adds `error:` and the reason when the log failed. A log that failed makes the exit code 1.

When another program holds the guard that keeps two DriveMonitors off one session name, but no session of
that name is running, `drivemon` starts monitoring anyway, about 2 seconds later, and says so on stderr
before the `Monitoring` line (the self-test, after `Session started in N ms`):

```text
Another program holds Global\DriveMonitor.Session.DriveMonitor-CLI, which keeps two DriveMonitors from sharing the session "DriveMonitor-CLI", but no such session is running, so monitoring started anyway.
```

```powershell
drivemon --list
drivemon --drives E,F --csv activity.csv
drivemon -d E --opens --failed --duration 60
drivemon --self-test E: --report selftest.txt
```

`--list` prints one line per drive letter; the Default column says `yes`, `no` or `(system)`:

```text
Drive Type       Bus      FS           Size      Free  Default  Label / model / device
C:    Fixed      Nvme     NTFS       476 GB    120 GB  (system) Samsung SSD 980 | \Device\HarddiskVolume3
E:    Removable  Usb      FAT32     28.9 GB   12.1 GB  yes      KINGSTON | Kingston DataTraveler 3.0 | \Device\HarddiskVolume5
```

While monitoring, each event is one line: time, operation, size (reads and writes), path, offset, process
and PID, then `[paging]` and the detail when they apply. The operations are READ, WRITE, CREATE, OPEN,
DELETE, RENAME, SETINFO (metadata changes), FLUSH and LIST. For example:

```text
14:03:12.300  CREATE             E:\photos\img_0042.jpg  explorer.exe (5312)  created
14:03:12.310  WRITE        1 MB  E:\photos\img_0042.jpg  @0  explorer.exe (5312)
14:03:13.020  READ        64 KB  E:\photos\img_0042.jpg  @65,536  MsMpEng.exe (4100)
14:03:15.500  RENAME             E:\photos\img_0042.jpg  explorer.exe (5312)  → E:\photos\holiday.jpg
```

By default `drivemon` prints reads, writes, creates, deletes and renames; the options above add the rest.
Control characters and Unicode bidirectional formatting characters in paths, process names and details are
printed as `?`, so that a file or program name cannot overwrite or fake a line. `--list` and the
`Now monitoring` line do the same for volume labels and drive models, and so do the start warning, the CSV
log's lines, the reason live monitoring could not start, and the kernel session's fault and restart
messages. The engine already replaces these characters in process names, so the app and the CSV files show
them as `?` too.

## The self-test

`drivemon --self-test E:` checks, on a real kernel trace, that DriveMonitor captures file operations on
drive E: correctly. Run it from an administrator terminal. Without a drive letter it picks a ready fixed or
removable drive that is monitored by default, preferring external drives, and falls back to the Windows
drive.

It:

1. creates a new folder at the root of the drive, named `.drivemonitor-selftest-` followed by 16 random
   hexadecimal digits, with an access list that lets only administrators and SYSTEM change it (FAT and exFAT
   drives have no access lists), and in it a file that it opens before monitoring starts;
2. starts a kernel session named `DriveMonitor-SelfTest` and waits 2 seconds for it to settle;
3. reads and writes the file opened before the start, writes a 1 MiB file with write-through, reads it back
   cached and then unbuffered, renames it, truncates it to 1,000 bytes and deletes it, writes a
   delete-on-close file, lists the folder, and tries to open a file that does not exist;
4. waits up to 10 seconds for the events, stops the session, deletes the files it created and then the folder
   if it is empty, and prints a report: the checks below, every event its own process caused in the test
   folder, the raw kernel fields, and diagnostics.

| Check | Passes when | Otherwise |
| --- | --- | --- |
| New file reported as created | the new file's create event succeeded and says `created` | FAIL without a create event, else WARN |
| 1 MiB of writes seen | at least 1 MiB of writes was reported | FAIL |
| 1 MiB of cached reads seen | at least 1 MiB of cached reads was reported | FAIL |
| 1 MiB of unbuffered reads seen | at least 1 MiB of unbuffered reads was reported | FAIL |
| Read bytes not over-counted | the two 1 MiB reads add up to at most 2 MiB | WARN |
| Rename seen | a rename of the old name that did not fail was reported | FAIL |
| Rename target reported | the rename carries the new name | WARN |
| Truncate to 1,000 bytes seen | a size change to 1,000 bytes was reported | FAIL without a size change, else WARN |
| Delete seen | a delete that did not fail was reported | FAIL |
| File opened before start is named | reads or writes of the file opened before the start carry its name | WARN |
| Delete-on-close seen | the delete-on-close flag was reported | FAIL |
| Folder listing seen | a listing of the test folder was reported | WARN |
| Failed open reported as failed | opening the missing file was reported as failed | FAIL without an event, else WARN |
| Completions matched | at least 90% of the events have a matched completion | WARN |
| Process attributed | every event names the self-test's own process | WARN |
| Kernel did not drop events | the kernel lost no events | WARN |
| No errors inside trace callbacks | no exception was caught inside a trace callback | FAIL |

The report ends with `RESULT: PASS (0 warning(s))` or `RESULT: FAIL (...)`. The exit code is 1 when a check
failed, the test could not run or it was cancelled, and 0 otherwise; warnings do not fail the test. They point
at things that depend on the Windows build or on the moment, such as the new name of a renamed file or lost
events.

The self-test does not run when the path of its folder leads through a junction, symbolic link or SUBST
drive, and it never deletes anything it did not create: anything else it finds in its folder is left in place
and reported in a `note:` line (see [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md#the-self-tests-folder)).
Ctrl+C stops it cleanly: it stops the kernel session, removes the test files, prints
`Cancelled: the kernel session is stopped and the test files are removed.` and exits with code 1. Closing the
console window, or a crash, also stops the session and removes the files.

From a clone, `./scripts/Invoke-SelfTest.ps1 -Drive E` builds `drivemon` and runs the self-test in one step.
When PowerShell is not elevated, Windows asks for permission and the test runs in its own window; the report
is printed afterwards and saved in your temporary folder.

CI runs `drivemon --self-test D: --report selftest-report.txt` (C: when the runner has no D: drive) on
Windows Server 2022 and 2025. It shows every check as an annotation and as a table on the run's summary page,
and uploads each report as an artifact (`selftest-report-windows-2022`, `selftest-report-windows-2025`).

## Smart App Control

> If **Smart App Control** is on (Windows Security → App & browser control), Windows blocks programs that
> its cloud service does not know and that carry no valid signature from a trusted publisher.
> DriveMonitor's binaries are not signed: not your own builds, and not the zips from CI or from releases.
> They then fail to start with error `0x800711C7` ("An Application Control policy has blocked this file"), and `dotnet run`
> or `dotnet test` can be blocked the same way. Either turn Smart App Control off, or sign the binaries
> with a code-signing certificate from a publicly trusted certificate authority (self-signed certificates
> are not accepted). Do not try to work around it.
> [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md#smart-app-control-blocks-the-program) has the details.

## Limitations

- **Administrator rights are required.** The app asks for elevation every time it starts; `drivemon` must
  run in an administrator terminal.
- **About one second of delay.** Windows hands over kernel trace events in batches, each processor's on its
  own. DriveMonitor asks for a batch every 500 ms, holds each event back for up to about 750 ms so that it
  can match the events in the order the kernel logged them, and refreshes the window four times a second
  (`drivemon` prints five times a second). Events therefore usually appear 0.75 to 1.6 seconds after they
  happen. Events are published in the order the operations started, so an operation still waiting for its
  completion can hold back the ones after it for up to 2 seconds more.
- **Unnamed I/O.** The kernel names a file when it is opened. For files that were already open when
  monitoring started, DriveMonitor asks Windows for their names. I/O whose file name is still unknown cannot
  be tied to a drive, so it is not shown; it is only counted (**Unnamed** in the status bar, for all drives
  together).
- **Paging I/O is counted separately.** The Windows cache and memory manager read ahead, write cached data
  back later and page memory-mapped files. That I/O repeats bytes a program already read or wrote through
  the cache, so it is hidden from Live activity by default, kept in separate paging totals per file and per
  drive, and never added to a program's totals.
- **Reads served from the file cache count as reads.** The trace shows the requests programs make, including
  those Windows answers from memory without touching the device.
- **Network drives are not monitored by default.**
- **FAT pendrives can show a failed and then a successful delete or rename.** FAT rejects the newer form of
  these requests, so Windows retries with the older one. Failed operations are hidden unless you show them.
- **Antivirus and indexer reads show up.** Antivirus programs such as Microsoft Defender (`MsMpEng.exe`)
  and the Windows Search indexer read files, for example right after they were written. These are real
  reads, shown under the program that made them.
- **The new name of a renamed file** comes from an undocumented Windows event. When Windows does not send
  it, or the new name it reports is on another drive (a rename cannot leave its drive, so that name belongs
  to another operation), a rename shows only the old name ("renamed or moved").
- **The first moment of a new drive is not seen.** With **Monitor new USB drives automatically** on, the app
  watches a new pendrive from the moment Windows reports the new volume; `drivemon` watches it from its next
  check for new drive letters, every 200 ms. What happens on the drive before that is not captured.
- **The activity log has limits.** Each file of a Record log or a `--csv` log holds up to 100 MB, a log keeps
  its 10 newest files, and it stops when less than 512 MB is free on its drive. The app keeps every earlier
  recording until you delete it. A log that stops early is reported: by a banner in the app, and by
  `drivemon` on stderr and with exit code 1.
- **Bounded memory.** Live activity keeps the newest 20,000 rows by default, and statistics track up to
  200,000 files and 50,000 processes; [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#data-bounding-caps) lists
  the limits.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): the pipeline, threading, completion matching, name
  resolution, path mapping, session lifecycle and limits.
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md): Smart App Control, administrator rights, error
  messages, lost and unnamed events, and where logs and settings live.
- [CONTRIBUTING.md](CONTRIBUTING.md): building, testing, the self-test, branches, commits and CI.
- [SECURITY.md](SECURITY.md): reporting a vulnerability, what DriveMonitor sees, the rights it needs and
  the files it writes.
- [CHANGELOG.md](CHANGELOG.md): notable changes.

## Project layout

| Path | Contents |
| --- | --- |
| `src/DriveMonitor.Core` | Kernel trace session, completion matching, classification, statistics, drive detection, CSV |
| `src/DriveMonitor.App` | WPF desktop app (`DriveMonitor.exe`) |
| `src/DriveMonitor.Cli` | `drivemon.exe` console tool and self-test |
| `tests/DriveMonitor.Tests` | xUnit tests for the engine parts that do not need a kernel session, and for parts of the app and the CLI |
| `scripts` | `Invoke-SelfTest.ps1` (build and run the self-test) and `Stop-DriveMonitorSessions.ps1` (stop leftover trace sessions) |
| `.github/workflows` | `ci.yml` (build and test, kernel-trace self-test, the published zip), `codeql.yml` (code scanning), `release.yml` (releases from version tags) |
