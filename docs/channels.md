---
sidebar_position: 3
title: Channels — stable vs edge
---

# Channels: stable vs edge

`stable` (the default) installs the latest tagged release. `edge` installs the
rolling prerelease rebuilt from the tip of `main` on every code change — handy
for trying unreleased fixes.

## Linux / macOS

```bash
curl -fsSL https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.sh | sh -s -- --channel edge
```

## Windows (PowerShell)

Fetch then invoke with `-Channel` (the piped one-liner can't take params):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.ps1))) -Channel edge
```

Edge binaries report a prerelease version like `0.2.0-alpha.0.7`. Re-run the same
command to update.
