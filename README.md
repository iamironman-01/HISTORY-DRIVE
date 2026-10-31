# DriveMonitor

See **every file read and write** on your pendrives, memory cards and secondary disks, live, together with
the program that did it.

DriveMonitor watches the drives you choose and shows each operation as it happens:

| What you see | Example |
| --- | --- |
| Reads and writes, with size and offset | `WRITE 1.0 MB  E:\photos\img_0042.jpg  explorer.exe` |
| Files and folders created, deleted, renamed or resized | `DELETE  F:\old\report.docx  WINWORD.EXE` |
| Which program touched which file | `MsMpEng.exe` scanning a newly inserted USB stick |
| Totals per file, per drive and per program | "E:\movie.mp4 — read 1.4 GB by vlc.exe" |

It also notices when a USB drive is plugged in and starts monitoring it automatically.

## How it works

Windows can already report file activity through **Event Tracing for Windows (ETW)**, the same kernel
instrumentation that Resource Monitor and Windows Performance Analyzer use. DriveMonitor starts a real-time
kernel trace session, keeps only the events for the drives you selected, matches each operation with its
completion (to learn real byte counts and whether it succeeded), and shows the result.

Consequences of this design:

- **No driver is installed** and nothing is written to the monitored drives.
- **Safe removal keeps working:** DriveMonitor holds no handles on the drives, so "Eject" is never blocked.
- **Administrator rights are required**, because Windows only lets administrators read the kernel trace.

## Requirements

- Windows 10 or 11 (x64 or ARM64)
- .NET 9 Desktop Runtime to run, .NET 9 SDK to build
- Administrator rights

> **Smart App Control.** If Smart App Control is on (Windows Security → App & browser control), Windows
> blocks every program that is not signed by a trusted publisher, including your own builds of DriveMonitor.
> Either turn Smart App Control off, or sign the binaries with a code-signing certificate from a publicly
> trusted authority (self-signed certificates are not accepted).

## Build

```bash
dotnet build DriveMonitor.sln -c Release
```

Run the tests (needs the binaries to be allowed to run; see Smart App Control above):

```bash
dotnet test DriveMonitor.sln -c Release
```

Make a single-folder copy of the app to put somewhere else:

```bash
dotnet publish src/DriveMonitor.App -c Release -r win-x64 --self-contained false -o publish/DriveMonitor
```

## Use the app

Start `DriveMonitor.exe` (Windows asks for administrator approval).

- **Left panel:** tick the drives to watch. Every drive except the Windows drive is ticked by default,
  and newly plugged-in USB drives are added automatically. Below are filters for what to show.
- **Live activity:** one row per operation. Repeated reads or writes of the same file by the same
  program within a second are grouped into one row (`×24`), which can be switched off.
- **Files:** totals per file — reads, writes, bytes, and whether it was created, deleted, renamed or resized.
- **Processes:** totals per program.
- **Record log** writes every event to a CSV file in `%LOCALAPPDATA%\DriveMonitor\Logs`.
  **Export** saves the current tables as CSV.

## Use the command line

`drivemon.exe` does the same from an administrator command prompt:

```bash
drivemon --list
```

```bash
drivemon --drives E,F --csv activity.csv
```

```bash
drivemon --self-test E:
```

`--self-test` creates a temporary folder on the drive, writes, reads, renames, truncates and deletes files
in it, and checks that every operation was captured correctly. Run `drivemon --help` for all options.

## Project layout

| Path | Contents |
| --- | --- |
| `src/DriveMonitor.Core` | Kernel trace session, completion matching, classification, statistics, drive detection, CSV |
| `src/DriveMonitor.App` | WPF desktop app |
| `src/DriveMonitor.Cli` | `drivemon` console tool and self-test |
| `tests/DriveMonitor.Tests` | Unit tests for the parts that do not need a kernel session |
