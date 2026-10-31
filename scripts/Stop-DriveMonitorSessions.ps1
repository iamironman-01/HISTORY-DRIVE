<#
.SYNOPSIS
    Stops kernel trace sessions left behind by DriveMonitor.

.DESCRIPTION
    DriveMonitor's sessions are DriveMonitor-FileIO (the app), DriveMonitor-CLI and DriveMonitor-SelfTest.
    A kernel trace session outlives a process that is killed or crashes, and Windows runs only a few such
    sessions at once, so a leftover one can keep other tracing tools from starting. DriveMonitor replaces its
    own leftover session the next time it starts; this script cleans up without starting it.

    Needs an elevated (administrator) PowerShell.

.EXAMPLE
    ./scripts/Stop-DriveMonitorSessions.ps1 -WhatIf
    Lists the sessions that would be stopped.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Listing and stopping kernel trace sessions needs an elevated (administrator) PowerShell.'
}

# "logman query -ets" prints one session per line: name, type and status in columns of 2+ spaces.
$sessions = logman query -ets |
    ForEach-Object { ($_ -split '\s{2,}')[0].Trim() } |
    Where-Object { $_ -like 'DriveMonitor*' }

if (-not $sessions) {
    Write-Host 'No DriveMonitor trace sessions are running.'
    return
}

foreach ($name in $sessions) {
    if ($PSCmdlet.ShouldProcess($name, 'Stop kernel trace session')) {
        logman stop $name -ets | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Stopped $name"
        }
        else {
            Write-Warning "Could not stop $name (logman exit code $LASTEXITCODE)."
        }
    }
}
