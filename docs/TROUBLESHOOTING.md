# Troubleshooting

When capture seems wrong, start with the self-test. From an administrator terminal:

```powershell
drivemon --self-test E: --report selftest.txt
```

It performs known file operations on drive E: and checks that each one is reported correctly (see
[The self-test](../README.md#the-self-test)). From a clone of the repository, this builds `drivemon` and runs
the same test, asking Windows for administrator rights when needed:

```powershell
./scripts/Invoke-SelfTest.ps1 -Drive E
```

In the app, hover over the monitoring state in the status bar for the engine's diagnostics.

## Smart App Control blocks the program

**Symptoms**

- `DriveMonitor.exe` or `drivemon.exe` does not start, with error `0x800711C7`: "An Application Control
  policy has blocked this file".
- `dotnet run`, `dotnet test` or another program that loads DriveMonitor's DLLs can fail with the same error.
- Event Viewer records the block under *Applications and Services Logs → Microsoft → Windows →
  CodeIntegrity → Operational*.

**Cause.** Smart App Control blocks programs that Microsoft's cloud service does not know and that carry no
valid signature from a trusted publisher. DriveMonitor's binaries are not signed: not your own builds, and
not the zips that the CI and Release workflows publish (neither workflow has a signing step).

**Check** the state in Windows Security → App & browser control → Smart App Control settings: On, Evaluation
or Off. Only On blocks programs.

**What to do**

- Building normally still works; running the binaries is what gets blocked. CI builds, tests and runs the
  self-test on GitHub's runners, so a pushed branch gets checked even when your PC cannot run the binaries.
- Turn Smart App Control off. It protects the whole PC, so decide deliberately, and read Microsoft's
  documentation on whether it can be turned on again later on your Windows version.
- Or sign the `.exe` files and DriveMonitor's own `.dll` files with a code-signing certificate from a
  publicly trusted certificate authority. Self-signed certificates are not accepted.
- Do not look for ways around the block.

## Administrator rights and UAC

- **The app** asks for administrator rights through its manifest, so with the default UAC settings Windows
  shows a prompt every time it starts. If you choose No, it does not start. On a standard account, the
  prompt asks for an administrator's credentials, and DriveMonitor then runs as that administrator, so its
  settings and logs go to that account's `%LOCALAPPDATA%`.
- **`drivemon`** does not ask for elevation. Start it from an administrator terminal (right-click Terminal or
  PowerShell, then *Run as administrator*). Otherwise it exits at once with this message (exit code 1):

  ```text
  drivemon needs an administrator command prompt: kernel file tracing requires elevation.
  ```

  and the self-test with:

  ```text
  FAIL  not running as administrator - kernel file tracing needs an elevated prompt.
  ```

  Only `--list`, `--version` and `--help` work without administrator rights.
- If the app runs without administrator rights anyway (for example started as `dotnet DriveMonitor.dll`), it
  shows *Administrator rights are required to watch file activity* with a **Restart as administrator**
  button, and Start stays unavailable. Declining the prompt that follows shows *DriveMonitor was not
  restarted as administrator.*
- The engine reports *DriveMonitor needs administrator rights to read the Windows kernel file trace. Run it
  as administrator.* when the process is not elevated, and *Administrator rights are required to start the
  kernel trace session.* when Windows refused access while the session was starting.

## "Another DriveMonitor window or command is already monitoring"

The full message is:

```text
Another DriveMonitor window or command is already monitoring with the session "DriveMonitor-FileIO". Close it first.
```

**Cause.** Each kind of DriveMonitor uses a fixed kernel session name, and only one process at a time may
use a name, because starting a session with a name that is in use would silently end the other program's
session. A machine-wide guard, the semaphore `Global\DriveMonitor.Session.<session name>`, enforces this
together with the list of running trace sessions: the message appears when the guard is taken and a kernel
session with that name is running.

| Session name | Used by |
| --- | --- |
| `DriveMonitor-FileIO` | The app |
| `DriveMonitor-CLI` | `drivemon` while monitoring |
| `DriveMonitor-SelfTest` | `drivemon --self-test` |

Typical cases: a second `drivemon` in another terminal, a self-test that is still running, or the app
running in another user's Windows session (the app allows one window per Windows session, but the guard
covers the whole machine).

**Fix.** Stop the other one: press Ctrl+C in its terminal, close its window, or end it in Task Manager. Then
start again (the app's banner has **Try again**). The guard is released as soon as that process ends, even if
it was killed. The app and `drivemon` use different names, so they can run side by side.

Another program can hold the guard while no DriveMonitor runs: a stale guard, or one created to keep
DriveMonitor from monitoring. Then no session with that name is running, so DriveMonitor waits up to 2
seconds for one to appear and starts monitoring without the guard. The app and `drivemon` do not say so.

Related messages:

- *DriveMonitor is already running.* Another DriveMonitor, running as administrator, is open in this Windows
  session; switch to it.
- *Could not check whether another DriveMonitor is running: ...* Windows refused to create or open the guard,
  and the list of running trace sessions could not be read either; the rest of the message says why.

## "Windows has no free kernel trace session" (0x800705AA)

The full message is:

```text
Windows has no free kernel trace session: other tracing tools (Windows Performance Recorder, profilers, security software) are using all of them. Close one of them and try again.
```

**Cause.** DriveMonitor's session is a kernel "system logger" session. Windows runs at most eight of them,
and two of the slots are reserved, so a few other tracing tools can use up the rest. Starting a session then
fails with `ERROR_NO_SYSTEM_RESOURCES` (`0x800705AA`). DriveMonitor sessions left behind by a DriveMonitor
that was killed count too: Windows keeps a trace session running after the process that started it ends.

**Inspect** the running sessions from an administrator terminal:

```powershell
logman query -ets
```

Look for `DriveMonitor-FileIO`, `DriveMonitor-CLI` and `DriveMonitor-SelfTest`, and for tools you recognise.

**Stop an orphaned DriveMonitor session**, one that is listed while no DriveMonitor is running:

```powershell
logman stop DriveMonitor-FileIO -ets
logman stop DriveMonitor-CLI -ets
logman stop DriveMonitor-SelfTest -ets
```

Run only the lines for the names that are listed. From a clone of the repository, the script
`scripts/Stop-DriveMonitorSessions.ps1` does the same in one step: it needs an elevated PowerShell, stops
every running session whose name starts with `DriveMonitor`, and with `-WhatIf` only lists them. CI checks
that it stops the session a killed `drivemon` leaves behind.

```powershell
./scripts/Stop-DriveMonitorSessions.ps1 -WhatIf
./scripts/Stop-DriveMonitorSessions.ps1
```

Close every DriveMonitor before you stop its sessions. A DriveMonitor that is still running would see its
session end and start it again, at most three times a minute, with a notice that events may be missing.
DriveMonitor also replaces its own leftover session when it next starts monitoring with the same name.

For other tools, close the tool that owns the session (for a Windows Performance Recorder trace,
`wpr -cancel`). Do not stop sessions you do not recognise; they may belong to security software. Restarting
Windows also ends every session that a program started.

Any other start failure appears as *The kernel trace session could not be started: ...* followed by the
reason from Windows.

## Lost, dropped and unnamed events

### Lost

**Lost** in the status bar (red when not zero), "lost by kernel" in the `drivemon` summary, and a WARN on
the self-test's "Kernel did not drop events" all mean the same thing: the kernel's buffers filled faster than
DriveMonitor read them, and the kernel threw events away.

- The kernel trace covers file activity on the whole PC, not only on the drives you watch. A burst anywhere,
  such as an antivirus scan, Windows Update, a build or a copy of many small files, can overflow the 64 MB of
  kernel buffers, more easily while the CPU is busy.
- Unticking drives does not reduce what the kernel sends; DriveMonitor filters by drive after it receives
  the events.
- Let the burst pass or close other heavy programs, and repeat what you wanted to observe. The buffer size
  is `MonitorOptions.BufferSizeMB` in code; the app and `drivemon` have no option for it.
- When the kernel session is restarted after stopping unexpectedly, the events in between are missing too;
  the app and `drivemon` say so when it happens. The count still includes what the stopped session lost,
  except for its last half second: DriveMonitor reads the count every 500 ms, and cannot read it once the
  session is gone.

### Dropped

**Dropped** means DriveMonitor's own queue (250,000 events) overflowed because the window or the console did
not take events out fast enough, so the oldest were discarded. Totals in Files and Processes, the Record log
and the `--csv` file still include them, because those are written before the queue; only Live activity and
the printed lines miss them. With `drivemon`, `--quiet` together with `--csv FILE` keeps every event without
printing it.

### Unnamed

**Unnamed** counts file I/O whose file name DriveMonitor did not know. Without a name the drive is unknown
too, so these events cannot be shown and are counted for all drives together.

- They usually come from files that were already open when monitoring started. DriveMonitor asks Windows for
  the names of open files when it starts; the diagnostics line *Names of already open files requested: yes*
  means Windows accepted that request. The self-test's "File opened before start is named" check shows
  whether it works on your Windows.
- Start monitoring before you plug in the drive or open the files. For a file that was already open, close
  it (or the program using it) and open it again.

## Antivirus and indexer noise

Antivirus programs such as Microsoft Defender (`MsMpEng.exe`) read files, for example right after they were
written, and the Windows Search indexer reads files in indexed locations. These reads are real, so
DriveMonitor shows them under the program that made them. When you show paging I/O, the cache's lazy writes
and read-ahead often appear under `System`.

To concentrate on one program, type its name in the search box or right-click a row and choose **Filter to
this process**; the Processes tab shows each program's totals. There is no setting that hides a particular
program. With `drivemon`, write the events to a CSV file with `--csv` and filter it afterwards.

## Events appear late, or not at all

**Late.** A delay of about a second is normal: Windows hands over kernel events in batches, DriveMonitor asks
for one every 500 ms, and the window refreshes every 250 ms (`drivemon` prints every 200 ms). Events are
published in the order the operations started, so an operation still waiting for its completion can hold
back the ones after it for up to 2 more seconds.

**Not at all.** Check that:

- the status bar says *Monitoring N drives*, not *Stopped* or *Monitoring, no drive selected*, and the drive
  is ticked (for `drivemon`: listed in `--drives`, or, without `--drives`, marked `yes` by `--list`; a drive
  plugged in later is announced with `Now monitoring`);
- it did not happen in the first moment after the drive was plugged in: the app watches a new drive from the
  moment Windows reports it, and `drivemon` from its next check for new drive letters, every 200 ms;
- the operation is shown: opens, folder listings, other changes, paging I/O and failed operations are hidden
  by default (for `drivemon`: `--opens`, `--listings`, `--other`, `--paging`, `--failed`);
- the search box is empty and **Pause view** is off;
- it is not DriveMonitor's own activity, which **Hide DriveMonitor's own activity** hides;
- it is not on a network drive: network drives are off by default, and DriveMonitor converts network paths
  to the `\\server\share` form rather than to drive letters, so they do not match a ticked network drive.

A rename that shows only the old name ("renamed or moved") means no event with the new name arrived in time.
If the diagnostics say *Windows reports new names of renamed files: not seen yet*, Windows has not sent any
such event since monitoring started.

## The activity log stops, or is split into several files

The app's **Record log** and `drivemon --csv` write the same activity log, with three limits. They are
`MonitorOptions` settings in code; the app and `drivemon` use the defaults and have no option for them.

| Limit | Default | What happens |
| --- | --- | --- |
| Size of one file | 100 MB (`MaxLogFileMB`) | The log continues in a new file with `.002`, `.003` and so on before the extension, for example `activity-20261031-140312.002.csv`. Each file starts with the header row. |
| Files kept | 10 (`MaxLogFiles`) | Starting a new file deletes the oldest files of the same log, so about 1 GB of one recording is kept. Only files that this log created are deleted; one that another program has open, such as a spreadsheet, is kept and tried again at the next file. |
| Free space on the log's drive | 512 MB (`MinLogFreeSpaceMB`) | The log does not start, or stops. The free space is checked when the log starts, at most once a second while it writes, and before every new file. A log on a network share (`\\server\share`) is not checked. |

**What you see**

- **The app:** starting **Record log** fails with a banner such as

  ```text
  Could not start the activity log: Only 300 MB is free on C:\; the activity log stops when less than 512 MB is left.
  ```

  While recording, a banner says *The activity log stopped:* with the reason, and **Record log** turns off.
- **`drivemon`:** when the file cannot be created at the start, it prints `Cannot write the CSV file:` with
  the reason and exits with code 1 before monitoring starts. A log that stops during the run is not
  reported: monitoring and the printed lines go on, and the CSV file ends at that point.

Other write errors stop the log in the same way: the drive was removed or is full, or the network share went
away.

**What to do**

- Free some space on the drive, then turn **Record log** on again; a new recording starts in a new file.
- The app always records to `%LOCALAPPDATA%\DriveMonitor\Logs`, normally on the Windows drive, and never
  deletes earlier recordings. Open the folder with **Open logs folder** and delete the recordings you no
  longer need.
- With `drivemon`, write the file to a drive with more room (`--csv D:\activity.csv`), and use a new file name
  for each run: the numbered files of an earlier run with the same name are not deleted, only replaced one by
  one as the new run reaches them.
- Turning **Record log** off, closing the app or the end of a `drivemon` run waits while a slow drive still
  takes the rest of the log, for up to 30 seconds. When the drive takes nothing for 5 seconds, the rest is
  counted as dropped.

## The self-test's folder

The self-test works in a new folder at the root of the drive it tests, named `.drivemonitor-selftest-`
followed by 16 random hexadecimal digits. Only administrators and SYSTEM can change the folder (FAT and exFAT
drives have no access lists, so there anyone can). At the end the self-test deletes the files it created,
then the folder if it is empty. It never deletes anything else.

- `FAIL  could not create the test folder on E: ...` ending in *the path leads through a junction, symbolic
  link or SUBST drive, so the self-test does not use it*: the drive letter is, for example, a SUBST drive.
  Test the drive it points to instead.
- `note: left <folder> in place: it still holds N item(s) that the self-test did not delete`: something
  other than the self-test put files into the folder while it ran. Look at them, then delete the folder.
  Only administrators can change it, so use an administrator terminal, or accept the administrator prompt
  in File Explorer.
- `note: could not delete ...` or `note: could not remove ...`: a test file or the folder was still in use,
  for example by an antivirus scan. Delete the folder in the same way.
- Ctrl+C stops the kernel session and removes the test files before the self-test ends; it prints
  `Cancelled: the kernel session is stopped and the test files are removed.` and exits with code 1. Closing
  the console window also stops the session and removes the files. A self-test that is killed, for example
  in Task Manager, leaves both behind: stop the `DriveMonitor-SelfTest` session as described in
  ["Windows has no free kernel trace session"](#windows-has-no-free-kernel-trace-session-0x800705aa) and
  delete the folder.
- A folder named `.drivemonitor-selftest-` followed by a process ID comes from an earlier build of
  `drivemon`. Check what is in it, then delete it.

## Where logs and settings live

| Path | Written by | Contents |
| --- | --- | --- |
| `%LOCALAPPDATA%\DriveMonitor\settings.json` | The app, when it exits and when Windows logs off or shuts down | Show filters, toolbar toggles, options, the Live activity row limit, your drive choices, window position |
| `%LOCALAPPDATA%\DriveMonitor\settings.bad.json` | The app, when it could not start | The settings it could not start with; the next start uses the defaults |
| `%LOCALAPPDATA%\DriveMonitor\errors.log` | The app, on unexpected errors | Time, context and exception details; emptied and started again once it exceeds 1 MiB |
| `%LOCALAPPDATA%\DriveMonitor\Logs\activity-YYYYMMDD-HHMMSS.csv`, then `.002.csv` and so on | The app, while **Record log** is on | Every event (see [The activity log stops, or is split into several files](#the-activity-log-stops-or-is-split-into-several-files)) |
| The file after `--csv`, and its numbered files, or after `--report` | `drivemon` | Every event, or the self-test report |

`%LOCALAPPDATA%` is normally `C:\Users\<name>\AppData\Local` of the account DriveMonitor runs as (see
[Administrator rights and UAC](#administrator-rights-and-uac)). Press Win+R and enter
`%LOCALAPPDATA%\DriveMonitor` to open the folder; **Open logs folder**, next to **Record log** in the app,
opens its `Logs` subfolder.

- **Reset the settings:** close DriveMonitor and delete `settings.json`. A damaged file is ignored, the
  defaults are used, and the file is rewritten when DriveMonitor exits. A saved window size or position that
  no screen can have is dropped. If a saved value still keeps the app from starting, the app moves
  `settings.json` to `settings.bad.json`, says so, and starts with the defaults the next time.
- **Settings are not saved, or nothing is written to `errors.log`:** the app runs as administrator but keeps
  these files in your profile, so it refuses links there. It does not write when
  `%LOCALAPPDATA%\DriveMonitor`, `settings.json`, `settings.json.tmp` or `errors.log` is a junction, a
  symbolic link or a file with more than one name (a hard link). Replace the link with a plain folder or
  file.
- **Live activity row limit:** `"MaxRows"` in `settings.json` (default 20,000, allowed 1,000 to 1,000,000).
  Edit it only while DriveMonitor is closed, because it rewrites the file when it exits.
- **The self-test** works in a folder of its own at the root of the drive it tests; see
  [The self-test's folder](#the-self-tests-folder).

## Reporting a problem

Open an issue with the **Bug report** form and include:

- your Windows edition, version and build (`winver`);
- the output of `drivemon --list`;
- the self-test report for the affected drive;
- the relevant part of `%LOCALAPPDATA%\DriveMonitor\errors.log`;
- what the diagnostics tooltip in the status bar says.

Report security problems privately instead, as described in [SECURITY.md](../SECURITY.md).
