---
sidebar_position: 2
title: Installation
---

# Installation

The CLI is a self-contained binary, so there is no .NET runtime to install
first. Install the latest release with a one-liner.

## Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.ps1 | iex
```

## Linux / macOS

```bash
curl -fsSL https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.sh | sh
```

These download the right binary from the latest
[GitHub release](https://github.com/voidprojectssoftware/protostar/releases) and
run `protostar install`, which places `protostar` in a per-user directory and
adds it to your `PATH`. Restart your shell afterward.

:::note
The version is derived from git tags at build time (via MinVer), not hardcoded.
A binary built from a tagged commit reports that tag (e.g. `0.1.0`); a local
build from an untagged commit reports a pre-release like `0.1.0-alpha.0.4`. See
[Releasing](./releasing.md).
:::

## Already have the binary?

If you downloaded `protostar` directly, it installs itself:

```console
$ protostar install              # copy into a per-user dir + add to PATH
$ protostar install --dir <DIR>  # custom location
$ protostar install --no-modify-path
$ protostar uninstall            # remove it
```

:::info
Startup is currently JIT (self-contained, untrimmed); making the binary lean and
fast is tracked as a separate performance-tuning unit of work.
:::
