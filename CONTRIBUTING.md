# Contributing to DriveMonitor

Thank you for helping. This guide covers the tools you need, how to build and test, how branches and
commits are named, what CI checks, and the code conventions. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
explains how the engine works.

## Prerequisites

- Windows 10 or Windows 11 (x64).
- The .NET 9 SDK, 9.0.300 or a later 9.0 SDK. [global.json](global.json) pins the 9.0 SDK line
  (`rollForward: latestFeature`), so a newer major version on its own does not build the solution.
- Git.
- An administrator account to run the app, `drivemon` and the self-test. Building and the unit tests need
  no administrator rights.
- If Smart App Control is on, Windows may refuse to run what you build; see
  [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md#smart-app-control-blocks-the-program). CI still builds,
  tests and runs the self-test for every branch you push.

## Build

```powershell
dotnet build DriveMonitor.sln -nologo -v q -nodeReuse:false
```

The build must end with `0 Warning(s)` and `0 Error(s)`: CI builds in Release with `-warnaserror`, so any
warning fails it. If a build looks stale, add `--no-incremental`.

## Test

```powershell
dotnet test tests/DriveMonitor.Tests/DriveMonitor.Tests.csproj --no-build -nologo
```

- The xUnit tests in `tests/DriveMonitor.Tests` cover `DriveMonitor.Core` without a kernel session, so they
  need no elevation. `MonitorEngineSinkTests` drives the engine through `IFileIoSink`, the interface the
  ETW source calls. A few tests (`DriveCatalogLiveTests`, `StorageQueryLiveTests`, `NativeMethodsTests`)
  call the real Windows APIs against the test machine's own drives and process; they need no
  administrator rights either. The test project also references the app and the CLI, which expose their
  internals to it: the app's view models, settings and single-instance check, and the CLI's options, drive
  watcher, console text, CSV start and self-test folder have tests too.
- Test folders mirror the engine's namespaces (`Tracing`, `Volumes`, `Aggregation`, `Export`, `Filtering`,
  `Models`, `IO`, `Interop`), with `App` and `Cli` for the front ends. `TestSupport` has `TempDirectory` (a
  temporary folder deleted on dispose), `CultureScope` (runs code under a given culture, including one that
  formats visibly differently from the invariant culture), `Rfc4180` (a strict CSV reader for checking what
  the CSV writer produced) and `FileLinks` (plants junctions, hard links and, where Windows allows it,
  symbolic links).
- Test classes are `public sealed class <Subject>Tests` with a `/// <summary>` saying what they cover, and
  test names usually read `Method_Condition_Result`, for example
  `Complete_LoggedBeforeTheBegin_DoesNotMatch`.
- Add or update tests for every behaviour change in the engine.

## Self-test

The self-test checks capture against a real kernel trace session, which unit tests cannot do. It needs a
drive you can write to. The script builds `drivemon` (Release, into `artifacts/selftest`) and runs it; when
PowerShell is not elevated, Windows asks for permission and the test runs in its own window, and the report
is printed afterwards and saved in your temporary folder:

```powershell
./scripts/Invoke-SelfTest.ps1 -Drive E
```

Or, after a build, from an administrator terminal:

```powershell
src\DriveMonitor.Cli\bin\Debug\net9.0-windows\drivemon.exe --self-test E: --report selftest.txt
```

Expect `RESULT: PASS (0 warning(s))`. Run it for every change to `src/DriveMonitor.Core` or
`src/DriveMonitor.Cli`, and paste the result into the pull request. The README explains
[each check](README.md#the-self-test) and what a warning means. The **Kernel trace self-test** jobs in CI run
it on every push as well.

If a killed `drivemon` or app leaves a kernel session behind, `./scripts/Stop-DriveMonitorSessions.ps1`
(elevated; `-WhatIf` lists what it would stop) stops every session whose name starts with `DriveMonitor`.

## Branches

- Branch from `main` and name the branch after the kind of change: `fix/`, `feat/`, `docs/`, `ci/` or
  `build/` (`test/` has been used for changes to tests only), followed by a short topic in lower-case words
  joined by hyphens. Examples from this repository: `fix/engine-lifecycle-hardening`, `feat/ops-scripts`,
  `ci/github-actions`, `test/live-windows-apis`, `docs/project-documentation`.
- Do not commit to `main` directly, and do not force-push a branch that someone else may have fetched.
  Branches are merged into `main` once CI is green.

## Commits

- Make one commit per logical change, and keep the build and the tests passing at every commit.
- Write the subject in the [Conventional Commits](https://www.conventionalcommits.org/) style,
  `type(scope): summary`, in the imperative and in lower case, without a closing period. Common types are
  `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `ci`, `build` and `chore`; the scope is optional and
  names the area, such as `engine`, `tracing`, `stats`, `cli` or `scripts`. Examples from this repository:
  - `fix(tracing): report session construction failures as MonitorStartException`
  - `feat(scripts): stop leftover DriveMonitor trace sessions`
  - `perf(stats): cache each file's process list text`
  - `test: cover drive enumeration against the machine's real drives`
  - `ci: run the kernel-trace self-test on elevated runners`
  - `build: pin the .NET SDK with global.json`
- Give every commit a body that explains why the change is needed, not only what it does.
- Add user-visible changes to the `Unreleased` section of [CHANGELOG.md](CHANGELOG.md).

## Pull requests

Open the pull request against `main` and fill in the template: a summary, the build and test result lines,
the self-test result, and the checklist. A pull request is ready to merge when every CI job is green.

## Releases

A release is made by pushing a version tag such as `v1.2.3` (see the Release workflow below). The
published binaries take their version from the tag, not from `Directory.Build.props`. Before tagging, move
the `Unreleased` entries of CHANGELOG.md under a heading for the new version.

## CI checks

[.github/workflows/ci.yml](.github/workflows/ci.yml) runs on every push, on pull requests to `main`, and on
demand. A newer run on the same branch cancels the one still in progress.

| Job | What it does | Fails when |
| --- | --- | --- |
| **Build and test** (`windows-latest`) | Restores, builds the solution in Release with `-warnaserror`, runs the tests, writes the test counts and the code coverage per assembly to the run's summary page, and uploads the results (TRX and coverage) as `test-results` | There is a warning, an error or a failing test, or `DriveMonitor.Core` line coverage falls below 60 % |
| **Kernel trace self-test** (`windows-2022` and `windows-2025`) | After Build and test, builds `drivemon` and runs `drivemon --self-test D: --report selftest-report.txt` (C: when the runner has no D: drive) on GitHub's elevated runners. Shows each check as an annotation and in a table on the summary page, uploads the report as `selftest-report-<runner>`, then kills a running `drivemon` and checks that `scripts/Stop-DriveMonitorSessions.ps1` stops the session it left behind | A check fails or the self-test cannot run (warnings only add annotations), or the cleanup check fails |
| **Publish** (`windows-latest`) | Only for pushes to `main`, after the other jobs: publishes the app and the CLI for win-x64 (framework-dependent) into one folder, zips it with a SHA-256 checksum and uploads `DriveMonitor-win-x64` | Publishing fails |

All jobs use the SDK from `global.json`. Two more workflows run next to it:

- [codeql.yml](.github/workflows/codeql.yml) builds the solution and scans the C# code with CodeQL's
  `security-and-quality` queries on every push, on pull requests to `main`, and every Monday.
- [release.yml](.github/workflows/release.yml) runs when a tag such as `v1.2.3` is pushed: it runs the tests
  and the self-test, publishes the app and the CLI with that version, and creates a GitHub release with
  `DriveMonitor-<version>-win-x64.zip`, its checksum, the self-test report and generated release notes.

Dependabot checks the NuGet packages and the GitHub Actions every week and opens pull requests for updates,
which CI checks like any other change.

## Code style

There is no `.editorconfig`; match the code around your change. The conventions used throughout:

- [Directory.Build.props](Directory.Build.props) sets the target framework once (`DriveMonitorTargetFramework`)
  and turns on nullable reference types, implicit usings and the latest C# version for every project.
- File-scoped namespaces that match the folder, such as `namespace DriveMonitor.Core.Tracing;`.
- Concrete classes are `sealed`, apart from the XAML code-behind classes `App` and `MainWindow`. What the
  front ends do not need stays `internal`; the engine exposes its internals only to the tests and to
  `drivemon` (`InternalsVisibleTo`).
- Private fields are `_camelCase`; types, members and constants are PascalCase. Braces go on their own
  lines, and indentation is four spaces.
- `var` mainly where the right-hand side shows the type; otherwise the type is usually written out.
- XML documentation comments (`/// <summary>`) on types and on members whose purpose is not obvious.
  Comments explain why, including the TraceEvent and kernel behaviour the code relies on; see
  `src/DriveMonitor.Core/Tracing/EtwFileIoSource.cs` and `IoCorrelator.cs`.
- Files (CSV, logs, reports) use `CultureInfo.InvariantCulture`; numbers shown to people use the current
  culture.
- The ETW callbacks run for every file event on the PC. Keep them cheap, catch every exception inside them
  (one that escapes ends the kernel session), and never make them wait for the UI.
- The collections that grow with file activity have limits, listed in
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#data-bounding-caps). Give any new one a limit too.
- WPF code stays in `DriveMonitor.App`; the engine has no UI dependency.
- [.gitattributes](.gitattributes) normalises line endings.

## Documentation

README.md, the files in `docs/` and CHANGELOG.md must stay true to the code. When you change behaviour, an
option, a message or a file location, update them in the same pull request. The CLI options are defined in
`src/DriveMonitor.Cli/Program.cs`, and the app's settings and file locations in
`src/DriveMonitor.App/Services/SettingsStore.cs` and `ErrorLog.cs`.

To report a security problem, follow [SECURITY.md](SECURITY.md) instead of opening an issue.
