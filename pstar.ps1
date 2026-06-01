#!/usr/bin/env pwsh
# Dev runner: build-and-run the protostar CLI in place.
#   .\pstar.ps1 <args>   ==   protostar <args>
#
# Examples:
#   .\pstar.ps1 --help
#   .\pstar.ps1 install-hooks --yes --dry-run
#
# Safe manual testing of hook/install commands: point the harness at a throwaway scratch dir so you
# never edit your real ~/.claude. The CLI honors PROTOSTAR_HARNESS_ROOT for every harness path:
#   $env:PROTOSTAR_HARNESS_ROOT = "$PWD\.dev\harness"
#   .\pstar.ps1 install-hooks --yes
#   Get-Content .dev\harness\settings.json
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Protostar.Cli'
dotnet run --project $project -v quiet -- @args
exit $LASTEXITCODE
