#Requires -Version 5.1
<#
.SYNOPSIS
  protostar installer (Windows). Downloads the latest release binary and self-installs it.

.DESCRIPTION
  Resolves the right release asset for this machine, downloads it from the latest GitHub release,
  and runs `protostar install`. Run via the one-liner:

    irm https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.ps1 | iex

  Or download and run directly to pass options (e.g. -Dir), which are forwarded to `protostar install`.

.PARAMETER Channel
  Release track: `stable` (default, the latest tagged release) or `edge` (the rolling prerelease
  built from the tip of main).
#>
[CmdletBinding()]
param(
    [ValidateSet('stable', 'edge')]
    [string]$Channel = 'stable',
    [Parameter(ValueFromRemainingArguments = $true)] [string[]]$InstallArgs
)

$ErrorActionPreference = 'Stop'
$repo = 'voidprojectssoftware/protostar'

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
switch ($arch) {
    'x64'   { $rid = 'win-x64' }
    'arm64' { $rid = 'win-arm64' }
    default { throw "Unsupported architecture: $arch" }
}

$asset = "protostar-$rid.exe"
$base = if ($Channel -eq 'edge') { "https://github.com/$repo/releases/download/edge" } else { "https://github.com/$repo/releases/latest/download" }
$url = "$base/$asset"

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("protostar-download-" + [System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$exe = Join-Path $tmp 'protostar.exe'

try {
    Write-Host "Downloading $asset ..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing
    & $exe install @InstallArgs
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
