---
sidebar_position: 1
title: Choose a channel — stable vs edge
---

:::warning AI-generated and not yet reviewed
This page was drafted by AI and has not been reviewed by a human. Protostar is in
early development with limited maintainer bandwidth, so content may be incomplete
or inaccurate. Treat it as a starting point, verify anything important against the
source, and please report any problems you hit.
:::

# Choose a channel: stable vs edge

Protostar ships on two tracks. Pick based on whether you want proven releases or
the very latest changes.

| Channel | What you get | Version looks like | Best for |
|---|---|---|---|
| **stable** (default) | The latest tagged release | `0.1.0` | Everyday use |
| **edge** | A rolling prerelease rebuilt from the tip of `main` on every change | `0.2.0-alpha.0.7` | Trying unreleased fixes early |

If you do nothing, you are on **stable** — that is what the
[install one-liner](../get-started/installation.md) gives you.

## Install or switch to edge

<details open>
<summary><strong>Linux / macOS</strong></summary>

```bash
curl -fsSL https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.sh | sh -s -- --channel edge
```

</details>

<details>
<summary><strong>Windows (PowerShell)</strong></summary>

The piped one-liner cannot take parameters, so fetch the script first, then invoke
it with `-Channel`:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.ps1))) -Channel edge
```

</details>

## Switch back to stable

Re-run the **default** installer (no `--channel`/`-Channel`). It installs the
latest tagged release over your edge build:

<details open>
<summary><strong>Linux / macOS</strong></summary>

```bash
curl -fsSL https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.sh | sh
```

</details>

<details>
<summary><strong>Windows (PowerShell)</strong></summary>

```powershell
irm https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.ps1 | iex
```

</details>

## How do I tell which channel I'm on?

Check the version. A plain `MAJOR.MINOR.PATCH` is a stable release; a
`-alpha.0.N` suffix is an edge build:

```console
$ protostar --version
0.2.0-alpha.0.7      # edge
```

## Why the two channels never collide

The edge build is published under a moving `edge` tag that does **not** start with
`v`, so [MinVer](https://github.com/adamralph/minver) ignores it when stamping
versions. Stable releases are `vX.Y.Z` tags. The two tracks are versioned
independently and never step on each other. The full mechanics are in
[Releasing](../develop/releasing.md).

## Updating

To update within your channel, just re-run the matching install command above.
For more on keeping current and cleaning up, see
[Manage your installation](./manage-your-installation.md).
