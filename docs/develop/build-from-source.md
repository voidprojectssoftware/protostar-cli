---
sidebar_position: 1
title: Build from source
---

:::warning AI-generated and not yet reviewed
This page was drafted by AI and has not been reviewed by a human. Protostar is in
early development with limited maintainer bandwidth, so content may be incomplete
or inaccurate. Treat it as a starting point, verify anything important against the
source, and please report any problems you hit.
:::

# Build from source

For contributors and anyone who wants to run the CLI from a local checkout.

## Prerequisites

- The **.NET 10 SDK**.

## Build and run

```bash
dotnet build                              # build the solution
dotnet run --project src/Protostar.Cli    # run the CLI in place
```

## Produce a self-contained binary

This is what ships in a release: one file, no runtime dependency. Build it and
self-install it:

```bash
dotnet publish src/Protostar.Cli -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -o out
./out/protostar install
```

Swap `-r win-x64` for your target runtime identifier (`linux-x64`,
`osx-arm64`, `win-arm64`).

:::info Dev build vs released binary
`protostar install` from a plain `dotnet build` output produces a
**framework-dependent** install: it works on any machine that has the .NET runtime
(your dev box), but is not portable. For a standalone binary that needs no runtime,
publish a self-contained single file first, as above, then `install` that.
:::

## The `pstar` dev runner

For manual testing there is a thin wrapper so you do not retype the project path.
`pstar <args>` is equivalent to `protostar <args>`, building in place first (it
runs `dotnet run` under the hood — see the `capture` note below for the one case
where that matters):

```powershell
.\pstar.ps1 --help                          # Windows / PowerShell
.\pstar.ps1 install-hooks --yes --dry-run
```

```bash
./pstar.sh --help                           # Linux / macOS
./pstar.sh install-hooks --yes --dry-run
```

## Testing install/hook commands safely

`install-hooks` (and `install`) write into your real `~/.claude` by default. To
exercise them without touching it, redirect the harness root at a scratch dir —
protostar resolves every harness path from `PROTOSTAR_HARNESS_ROOT` (or
`--harness-home <DIR>` per command):

```powershell
$env:PROTOSTAR_HARNESS_ROOT = "$PWD\.dev\harness"   # scratch; .dev/ is gitignored
.\pstar.ps1 install-hooks --yes
Get-Content .dev\harness\settings.json              # inspect what was written
.\pstar.ps1 install-hooks --yes --remove            # tear it back out
```

:::note Testing `capture` by hand
`capture` reads its payload from stdin and is normally invoked by an installed
hook. Piping stdin through `dotnet run` — and therefore through `pstar` — can hang
(it does not forward stdin's end-of-input), so test the **built binary** directly:

```bash
echo '{}' | ./src/Protostar.Cli/bin/Debug/net10.0/protostar capture --hook PostToolUse
```
:::

Run the acceptance suite with `dotnet test` from the repo root.

## Repository layout

```text
protostar/
├─ src/
│  └─ Protostar.Cli/       # the `protostar` CLI (Spectre.Console.Cli)
├─ docs/                   # these docs — lifted into the unified docs site
├─ scripts/
│  ├─ install.ps1          # curl-able release installer (Windows)
│  └─ install.sh           # curl-able release installer (Linux/macOS)
├─ .github/workflows/
│  ├─ release-please.yml   # stable: Release PR -> tag -> build + attach binaries
│  └─ edge.yml             # edge: rebuild tip of main -> replace rolling `edge` prerelease
├─ version.txt             # current version, bumped by release-please
├─ release-please-config.json      # release-please configuration
├─ .release-please-manifest.json   # release-please version tracker
├─ Directory.Build.props   # MinVer git-tag versioning
└─ protostar.sln
```
