#!/usr/bin/env pwsh
# Code coverage runner for the Protostar CLI.
#
#   .\scripts\coverage.ps1            # collect coverage + write an HTML report
#   .\scripts\coverage.ps1 -Open      # ...and open the HTML report when done
#
# Coverage uses the dotnet-coverage tool (pinned in .config/dotnet-tools.json), not coverlet,
# because the acceptance suite drives the built binary as a CHILD PROCESS. dotnet-coverage's engine
# captures child processes via shared memory; coverlet's in-process instrumentation would miss them
# and under-report coverage of every command exercised only through the binary.
#
# Output (all under the gitignored coverage/ dir):
#   coverage/coverage.cobertura.xml   raw Cobertura data
#   coverage/report/index.html        human-readable HTML report
param([switch]$Open)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    # Pinned local tools (dotnet-coverage, reportgenerator); no global install needed.
    dotnet tool restore

    $outDir = Join-Path $root 'coverage'
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $cobertura = Join-Path $outDir 'coverage.cobertura.xml'

    # Wrap the whole test run so coverage is collected across every spawned process.
    dotnet tool run dotnet-coverage collect -f cobertura -o $cobertura "dotnet test $root/protostar.sln -c Debug --nologo"

    # Render an HTML report plus a console summary, scoped to our own assembly (the CLI is built as
    # "protostar"); this drops Spectre.Console and the test assemblies from the numbers.
    $reportDir = Join-Path $outDir 'report'
    dotnet tool run reportgenerator "-reports:$cobertura" "-targetdir:$reportDir" "-reporttypes:Html;TextSummary" "-assemblyfilters:+protostar"

    $summary = Join-Path $reportDir 'Summary.txt'
    if (Test-Path $summary) { Get-Content $summary }

    Write-Host ""
    Write-Host "HTML report: $(Join-Path $reportDir 'index.html')"
    if ($Open) { Invoke-Item (Join-Path $reportDir 'index.html') }
}
finally {
    Pop-Location
}
