#Requires -Version 5.1
<#
.SYNOPSIS
  Build and install the protostar CLI as a self-contained binary (no .NET runtime required to run).

.DESCRIPTION
  Publishes src/Protostar.Cli as a self-contained, single-file executable for the current (or
  specified) runtime identifier, copies it to a per-user install directory, and ensures that
  directory is on the user PATH. Re-running is idempotent.

.PARAMETER Configuration
  Build configuration. Default: Release.

.PARAMETER Rid
  Runtime identifier (e.g. win-x64, win-arm64, linux-x64, osx-arm64). Defaults to the current OS/arch.

.PARAMETER InstallDir
  Where to install protostar.exe. Default: %LOCALAPPDATA%\Programs\protostar.

.EXAMPLE
  ./install.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Rid,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\protostar')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src/Protostar.Cli/Protostar.Cli.csproj'

if (-not $Rid) {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $Rid = "win-$arch"
}

$publishDir = Join-Path $root "artifacts/cli/$Rid"

Write-Host "Publishing protostar ($Rid, self-contained single-file)..." -ForegroundColor Cyan
dotnet publish $project `
    -c $Configuration `
    -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $publishDir 'protostar.exe'
if (-not (Test-Path $exe)) { throw "expected binary not found: $exe" }

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $exe $InstallDir -Force
$installedExe = Join-Path $InstallDir 'protostar.exe'
Write-Host "Installed protostar -> $installedExe" -ForegroundColor Green

# Ensure the install dir is on the user PATH (idempotent).
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $InstallDir) {
    [Environment]::SetEnvironmentVariable('Path', "$userPath;$InstallDir", 'User')
    Write-Host "Added $InstallDir to your user PATH. Restart your shell to pick it up." -ForegroundColor Yellow
}

Write-Host ""
& $installedExe --version
