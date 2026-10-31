<#
.SYNOPSIS
    Builds drivemon and runs its kernel-trace self-test on a drive.

.DESCRIPTION
    The self-test creates a temporary folder on the drive, performs known file operations in it (create, write,
    cached and unbuffered reads, rename, truncate, delete, delete-on-close, folder listing, a failed open),
    checks that the kernel trace reported each of them correctly, and removes the folder again.

    Kernel tracing needs administrator rights. When this PowerShell is not elevated, Windows asks for
    permission (UAC) and the test runs in its own window; its report is printed here afterwards.

.PARAMETER Drive
    Drive letter to test, for example E or E:. Without it drivemon picks the first fixed or removable drive
    that is not the Windows drive, preferring external ones.

.PARAMETER Configuration
    Build configuration, Debug or Release.

.EXAMPLE
    ./scripts/Invoke-SelfTest.ps1 -Drive E
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z]:?$')]
    [string] $Drive,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts/selftest/$Configuration"
$report = Join-Path ([IO.Path]::GetTempPath()) ('drivemon-selftest-{0:yyyyMMdd-HHmmss}.txt' -f (Get-Date))

dotnet build (Join-Path $root 'src/DriveMonitor.Cli/DriveMonitor.Cli.csproj') -c $Configuration -o $output -nologo -v q
if ($LASTEXITCODE -ne 0) {
    throw 'Building drivemon failed.'
}

$arguments = @('--self-test')
if ($Drive) {
    $arguments += $Drive.TrimEnd(':') + ':'
}
$arguments += @('--report', $report)
$exe = Join-Path $output 'drivemon.exe'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    & $exe @arguments
    $exitCode = $LASTEXITCODE
}
else {
    Write-Host 'Kernel tracing needs administrator rights; Windows will ask for permission (UAC).'
    $quoted = $arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    try {
        $process = Start-Process -FilePath $exe -ArgumentList $quoted -Verb RunAs -Wait -PassThru
    }
    catch {
        throw "The self-test did not start: $($_.Exception.Message)"
    }

    $exitCode = $process.ExitCode
    if (Test-Path $report) {
        Get-Content $report
    }
}

Write-Host "Report saved to $report"
exit $exitCode
