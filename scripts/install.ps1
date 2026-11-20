#Requires -Version 5.1
<#
.SYNOPSIS
  protostar installer (Windows). Downloads the latest release binary and self-installs it.

.DESCRIPTION
  Resolves the right release asset for this machine, downloads it from the latest GitHub release,
  and runs `protostar install`. Works in Windows PowerShell 5.1 and PowerShell 7. Run via the one-liner:

    irm https://raw.githubusercontent.com/voidprojectssoftware/protostar-cli/main/scripts/install.ps1 | iex

  To pick a channel from the one-liner, create a script block so parameters can be passed:

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/voidprojectssoftware/protostar-cli/main/scripts/install.ps1))) -Channel edge

  Any other arguments (e.g. --dir C:\Tools\protostar, --no-modify-path, --no-hooks) are forwarded
  to `protostar install`, from the script block form or when the script is downloaded and run directly.

.PARAMETER Channel
  Release track: `stable` (default, the latest tagged release) or `edge` (the rolling prerelease
  built from the tip of main).
#>
# PositionalBinding off: otherwise a forwarded argument such as `--dir` lands in -Channel by position.
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('stable', 'edge')]
    [string]$Channel = 'stable',
    [Parameter(ValueFromRemainingArguments = $true)] [string[]]$InstallArgs
)

$ErrorActionPreference = 'Stop'
$repo = 'voidprojectssoftware/protostar-cli'

# Windows sets these variables for every process, whatever the PowerShell or .NET version.
# PROCESSOR_ARCHITEW6432 holds the OS architecture when a 32-bit PowerShell runs on 64-bit Windows
# (where PROCESSOR_ARCHITECTURE reports x86). RuntimeInformation is only a fallback: under Windows
# PowerShell 5.1 on .NET Framework it is not reliably available (issue #90).
$osArch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
if (-not $osArch) {
    # The type may be missing entirely (TypeNotFound); that just leaves $osArch empty for the throw below.
    try { $osArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture } catch { }
}
switch ("$osArch") {
    { $_ -in 'AMD64', 'X64' } { $rid = 'win-x64'; break }
    'ARM64'                   { $rid = 'win-arm64'; break }
    default { throw "Unsupported architecture: '$osArch'. protostar ships Windows binaries for x64 and arm64 only." }
}

$asset = "protostar-$rid.exe"
$base = if ($Channel -eq 'edge') { "https://github.com/$repo/releases/download/edge" } else { "https://github.com/$repo/releases/latest/download" }
$url = "$base/$asset"

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("protostar-download-" + [System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$exe = Join-Path $tmp 'protostar.exe'

try {
    Write-Host "Downloading $asset ($Channel) ..." -ForegroundColor Cyan
    # Windows PowerShell 5.1 can default to TLS 1.0/1.1, which GitHub refuses.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing
    & $exe install @InstallArgs
    if ($LASTEXITCODE -ne 0) { throw "protostar install exited with code $LASTEXITCODE." }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
