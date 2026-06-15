---
sidebar_position: 1
title: Build from source
---

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

## Debug the CLI

Run under a debugger with breakpoints, against any command and arguments you
choose. The repo ships launch configs, so open the `protostar-cli` folder in your
editor and they appear — no setup.

**VS Code** (`.vscode/launch.json`):

- **protostar: prompt for args** — F5 asks for a command line each launch (e.g.
  `skills --global-only`), so you can break into any command without editing a
  file. Multi-word input is tokenized into separate arguments.
- **protostar: --help** — a no-prompt baseline.

**Visual Studio / Rider / the CLI** read the profiles in
`src/Protostar.Cli/Properties/launchSettings.json`: pick `help`, `skills`,
`install-hooks (dry-run, scratch harness)`, or `custom` (edit its
`commandLineArgs` for an arbitrary command) from the run dropdown and start
debugging. Same profiles from the terminal:

```bash
dotnet run --project src/Protostar.Cli --launch-profile custom -- skills --global-only
```

Every launch config defaults `PROTOSTAR_HARNESS_ROOT` to a gitignored
`.dev/harness` scratch dir, so debugging hook/install commands never touches your
real `~/.claude` (see the next section for why that matters).

:::note Opening a parent folder instead of `protostar-cli`
The VS Code `prompt for args` config loads only when `protostar-cli` is a
workspace root, because `${workspaceFolder}` resolves to the folder holding
`.vscode`. If you open a parent folder that contains this repo, add `protostar-cli`
as a workspace root (File ▸ Add Folder to Workspace) so the config surfaces. The
`launchSettings.json` profiles show up either way, via C# Dev Kit.
:::

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

## Code coverage

The coverage tools, [`dotnet-coverage`](https://learn.microsoft.com/dotnet/core/additional-tools/dotnet-coverage)
and [ReportGenerator](https://github.com/danielpalme/ReportGenerator), are pinned as **local** tools
in `.config/dotnet-tools.json`. There is nothing to install globally; restore them once:

```bash
dotnet tool restore
```

Then run the coverage script, which collects coverage and writes an HTML report under the gitignored
`coverage/` directory:

```powershell
.\scripts\coverage.ps1          # add -Open to launch the HTML report when it finishes
```

:::note Why `dotnet-coverage` and not coverlet
coverlet is the more common pick, but it instruments the test process in-process and does not
reliably capture **child processes**. Our acceptance suite drives the built `protostar` binary as a
child process, so coverlet would report most command code as uncovered. `dotnet-coverage` uses
Microsoft's collector, which captures child-process coverage through shared memory, so the binary's
real exercise is counted.
:::

To run the steps by hand instead of the script (the CLI assembly is named `protostar`, which is what
the report filter targets):

```bash
dotnet tool run dotnet-coverage collect -f cobertura -o coverage/coverage.cobertura.xml "dotnet test"
dotnet tool run reportgenerator -reports:coverage/coverage.cobertura.xml -targetdir:coverage/report -reporttypes:Html -assemblyfilters:+protostar
```

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
