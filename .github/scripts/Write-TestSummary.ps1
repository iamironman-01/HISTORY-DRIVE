<#
.SYNOPSIS
    Writes the test results and code coverage of a dotnet test run to the GitHub job summary.

.PARAMETER ResultsDirectory
    The folder passed to dotnet test --results-directory (holds the .trx file and coverage.cobertura.xml).

.PARAMETER MinimumLineCoverage
    Fails the step when DriveMonitor.Core's line coverage, in percent, is below this value. 0 disables the check.
#>
param(
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [double] $MinimumLineCoverage = 0
)

$ErrorActionPreference = 'Stop'

# Outside GitHub Actions (a local dry run) the summary goes next to the results.
$summaryPath = if ($env:GITHUB_STEP_SUMMARY) { $env:GITHUB_STEP_SUMMARY } else { Join-Path $ResultsDirectory 'summary.md' }
$summary = [System.Collections.Generic.List[string]]::new()
$belowMinimum = $false

$trx = Get-ChildItem $ResultsDirectory -Recurse -Filter *.trx -ErrorAction SilentlyContinue | Select-Object -First 1
if ($trx) {
    $counters = ([xml](Get-Content $trx.FullName -Raw)).TestRun.ResultSummary.Counters
    $summary.Add('### Tests')
    $summary.Add('')
    $summary.Add('| Total | Passed | Failed | Not executed |')
    $summary.Add('| ---: | ---: | ---: | ---: |')
    $summary.Add("| $($counters.total) | $($counters.passed) | $($counters.failed) | $($counters.notExecuted) |")
    $summary.Add('')
}

# The collector writes the same report twice (a GUID folder and the machine "In" folder); one is enough.
$cobertura = Get-ChildItem $ResultsDirectory -Recurse -Filter coverage.cobertura.xml -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cobertura) {
    $coverage = ([xml](Get-Content $cobertura.FullName -Raw)).coverage
    $summary.Add('### Code coverage')
    $summary.Add('')
    $summary.Add('| Assembly | Lines | Branches |')
    $summary.Add('| --- | ---: | ---: |')
    foreach ($package in $coverage.packages.package) {
        $lineRate = [double]$package.'line-rate' * 100
        $branchRate = [double]$package.'branch-rate' * 100
        $summary.Add(('| {0} | {1:N1} % | {2:N1} % |' -f $package.name, $lineRate, $branchRate))
        if ($MinimumLineCoverage -gt 0 -and $package.name -eq 'DriveMonitor.Core' -and $lineRate -lt $MinimumLineCoverage) {
            $belowMinimum = $true
            Write-Host ('::error title=Coverage::DriveMonitor.Core line coverage {0:N1} % is below the minimum of {1} %' -f $lineRate, $MinimumLineCoverage)
        }
    }

    $summary.Add('')
}

if ($summary.Count -gt 0) {
    Add-Content -Path $summaryPath -Value $summary -Encoding utf8
}

if ($belowMinimum) {
    exit 1
}
