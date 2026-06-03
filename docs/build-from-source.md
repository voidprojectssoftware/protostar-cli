---
sidebar_position: 4
title: Build from source
---

# Build from source

Requires the .NET 10 SDK.

```bash
dotnet build                              # build the solution
dotnet run --project src/Protostar.Cli    # run the CLI in place

# produce a self-contained binary and self-install it:
dotnet publish src/Protostar.Cli -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -o out
./out/protostar install
```

## Repository layout

```text
protostar/
├─ src/
│  └─ Protostar.Cli/       # the `protostar` CLI (Spectre.Console.Cli); install/uninstall commands
├─ docs/                   # these docs — lifted into the unified docs site
├─ scripts/
│  ├─ install.ps1          # curl-able release installer (Windows)
│  └─ install.sh           # curl-able release installer (Linux/macOS)
├─ .github/workflows/
│  ├─ release-please.yml   # stable: Release PR -> tag -> build + attach binaries
│  └─ edge.yml             # edge: rebuild tip of main -> replace rolling `edge` prerelease
├─ release-please-config.json      # release-please configuration
├─ .release-please-manifest.json   # release-please version tracker
├─ Directory.Build.props   # MinVer git-tag versioning
└─ protostar.sln
```
