# Security policy

## Reporting a vulnerability

Please report security problems privately, not in a public issue, pull request or discussion.

1. Open the repository's **Security** tab and choose **Report a vulnerability**. This uses GitHub's private
   vulnerability reporting: it creates a draft security advisory that only you and the maintainers can see.
2. Describe the problem, the affected part (the app, `drivemon`, the engine or the CI workflow), your Windows
   version, and the steps or a proof of concept that shows it.

If the **Report a vulnerability** button is missing, private reporting is turned off for the repository. In
that case, open an issue that only asks the maintainers for a private way to report a security problem,
without any details.

The discussion and the fix then happen in the private advisory, which is published once a fixed build is
available.

## Supported versions

DriveMonitor has no releases yet (the version in `Directory.Build.props` is 0.1.0). Releases are published
from version tags by the Release workflow; until the first one, fixes go to `main`, and the build that CI
publishes from `main` is the supported one.

## What DriveMonitor sees

- **File activity of every process, on every drive.** Windows cannot limit a kernel file trace to one drive,
  so DriveMonitor receives all file activity on the PC and discards the events for drives you did not select
  as they arrive.
- **For each operation on a selected drive:** the time, the operation, the full path (and the new path of a
  renamed file), the drive, the size and offset, the process name and ID, the thread ID, flags and the
  outcome (NTSTATUS).
- **No file contents.** The kernel trace carries no file data, and DriveMonitor does not read the files it
  reports on. (The self-test reads only the test files it wrote itself.)
- **In memory only:** to name later I/O, DriveMonitor keeps a bounded table of recently seen file names for
  all drives, and a table of process names. Neither is written to disk.

## Your data stays on your PC

- DriveMonitor's code makes no network connections: it sends no telemetry and does not check for updates.
- What it shows lives in memory until you click **Clear** or close the program. Events reach a file only
  when you save them: **Record log** or **Export** in the app, `--csv` in `drivemon` (and the self-test's
  `--report`). `errors.log`, written on unexpected errors, holds exception details, which can include paths.
- Those CSV files contain full paths and program names, which can be private. They are not encrypted; store
  and share them accordingly.
- File names on a pendrive are chosen by whoever wrote to it. When DriveMonitor writes CSV, text that starts
  with `=`, `+`, `-`, `@`, a tab or a carriage return gets a leading apostrophe, so that spreadsheet programs
  do not evaluate it as a formula, and fields are quoted as RFC 4180 requires.

## The privileges it needs, and why

- **Administrator rights.** Windows lets only administrators start a kernel trace session. The app's
  manifest requests elevation (`requireAdministrator`), so with the default UAC settings Windows asks every
  time it starts. `drivemon` checks for elevation and exits without it, except for `--list` and `--help`.
- Everything DriveMonitor does therefore runs with an administrator token. Run only binaries you trust:
  build them from source, or take a zip from this repository's CI runs or releases and check its SHA-256
  checksum. The binaries are not code-signed.
- **Nothing is installed.** There is no driver, service, scheduled task or startup entry. While it runs,
  DriveMonitor creates:
  - a kernel trace session (`DriveMonitor-FileIO` for the app, `DriveMonitor-CLI` for `drivemon`,
    `DriveMonitor-SelfTest` for the self-test), stopped when monitoring stops;
  - a named semaphore, `Global\DriveMonitor.Session.<session name>`, that keeps two DriveMonitors from using
    the same session;
  - for the app, a named mutex, `Local\DriveMonitor.App.SingleInstance`, that keeps to one window per
    Windows session.
- If the process is killed, Windows keeps its kernel session running until DriveMonitor starts again with
  the same session name, someone stops it with `logman stop <name> -ets`
  (see [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)), or Windows restarts.
- **Other calls into Windows** include `QueryDosDevice` to map volumes to drive letters; a volume handle opened with
  no access rights to read the bus type and model (`IOCTL_STORAGE_QUERY_PROPERTY`); `OpenProcess` with
  `PROCESS_QUERY_LIMITED_INFORMATION` to name a process the trace did not name; `explorer.exe` for **Open
  containing folder**; and, for **Restart as administrator**, starting itself again with the `runas` verb.

## Where it writes files

| Path | When | Contents |
| --- | --- | --- |
| `%LOCALAPPDATA%\DriveMonitor\settings.json` | The app exits, or Windows logs off or shuts down | Filters, options, drive choices, window position; written through `settings.json.tmp` |
| `%LOCALAPPDATA%\DriveMonitor\errors.log` | An unexpected error in the app | Time, context and exception details, which can include paths; deleted and started again once it exceeds 1 MiB |
| `%LOCALAPPDATA%\DriveMonitor\Logs\activity-YYYYMMDD-HHMMSS.csv` | While **Record log** is on | Every event |
| The file you choose | **Export** in the app | The exported table |
| The file after `--csv` | `drivemon` | Every event |
| The file after `--report` | `drivemon --self-test` | The self-test report |
| `X:\.drivemonitor-selftest-<process id>\` | `drivemon --self-test X:` | Test files, deleted when the test ends |

DriveMonitor's code writes no other files, on the drives it watches or anywhere else.

## Supply chain

- The engine has one package dependency, `Microsoft.Diagnostics.Tracing.TraceEvent`, pinned to 4.0.0. The
  test project adds xUnit, the .NET test SDK and coverlet.
- Dependabot checks the NuGet packages and the GitHub Actions weekly.
- CodeQL scans the C# code with the `security-and-quality` queries on every push, on pull requests to `main`
  and once a week.
- The workflows run with a read-only token (`permissions: contents: read`). Only the CodeQL job may also
  upload its findings (`security-events: write`), and only the Release job may create releases
  (`contents: write`).
- The zips published from `main` and from version tags come with a SHA-256 checksum file; a release also
  includes its self-test report.
