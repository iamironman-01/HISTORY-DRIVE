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

### Fixed

- A kernel session is never started after the engine is disposed. A `Start` racing with or following
  `Dispose`, for example the window closing while auto-start was still creating the session, could create a
  kernel session that nothing would stop. `Start` now throws `ObjectDisposedException` after `Dispose`, and
  a faulted session is not restarted once the engine is disposed.
- A failure in the trace session's constructor is reported as a readable `MonitorStartException` instead of
  reaching the caller as a raw exception.
- Each file's process-list text is cached and rebuilt only when a new process name arrives, so statistics
  snapshots hold the lock that the trace consumer needs for less time during bursts.
- The status bar's **Unnamed** tooltip and the engine's documentation now say that unnamed I/O is counted for
  all drives, because without a file name the drive is unknown.

[Unreleased]: https://github.com/iamironman-01/HISTORY-DRIVE/commits/main
