---
sidebar_position: 1
title: Install protostar
---

:::warning AI-generated and not yet reviewed
This page was drafted by AI and has not been reviewed by a human. Protostar is in
early development with limited maintainer bandwidth, so content may be incomplete
or inaccurate. Treat it as a starting point, verify anything important against the
source, and please report any problems you hit.
:::

# Install protostar

The CLI ships as a **self-contained binary**, so there is no .NET runtime to set
up first. The fastest way to install is a one-line script that downloads the
right binary for your platform and puts it on your `PATH`.

## Prerequisites

- A supported platform with a prebuilt binary: **Windows** (`x64`, `arm64`),
  **Linux** (`x64`), or **macOS** (Apple Silicon / `arm64`). Intel macOS and ARM
  Linux have no prebuilt release yet — [build from source](../develop/build-from-source.md)
  for those.
- Nothing else. The binary bundles its own runtime.

## Install with the one-liner

<details open>
<summary><strong>Windows (PowerShell)</strong></summary>

```powershell
irm https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.ps1 | iex
```

</details>

<details>
<summary><strong>Linux / macOS</strong></summary>

```bash
curl -fsSL https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.sh | sh
```

</details>

The script downloads the latest
[GitHub release](https://github.com/voidprojectssoftware/protostar/releases) for
your OS and CPU, then runs `protostar install` — which copies the binary into a
per-user directory (no admin rights needed) and adds that directory to your
`PATH`:

| OS | Default install directory |
|---|---|
| Windows | `%LOCALAPPDATA%\Programs\protostar` |
| Linux / macOS | `~/.local/bin` |

Choose a different location with `protostar install --dir <DIR>`.

:::note Releases vs source
The install scripts and binaries are published to the public
**`voidprojectssoftware/protostar`** release repo. The CLI **source** (and these
docs) live in `voidprojectssoftware/protostar-cli` — which is where the
"Edit this page" link sends you.
:::

:::tip Restart your shell
`PATH` changes only take effect in **new** shell sessions. Close and reopen your
terminal (or open a new tab) before running `protostar` for the first time.
:::

## Verify the install

Open a fresh terminal and run:

```console
$ protostar --version
0.1.0

$ protostar
protostar v0.1.0
Live, continuous refinement of agent skills.

Run protostar --help to see available commands.
```

If you see a version number, you are done. Next, head to
[Connect your harness](./connect-your-harness.md).

:::info 📷 Screenshot slot — `img/install-verify.png`
A terminal window showing `protostar --version` and the `protostar` banner right
after a successful install. Drop the image into `docs/img/` and replace this
admonition with: `![protostar version output](../img/install-verify.png)`
:::

## Already downloaded the binary?

If you grabbed `protostar` from a release page directly, it installs itself —
same `install` step the one-liner runs for you:

```console
$ protostar install              # copy into a per-user dir + add to PATH
$ protostar install --dir <DIR>  # install to a custom location
$ protostar install --no-modify-path   # skip the PATH edit (you manage it)
```

To remove it later, see [Manage your installation](../guides/manage-your-installation.md).

## A note on versions

Versions are stamped from git tags at build time, never hardcoded. A stable
release reports a plain version like `0.1.0`; an
[edge build](../guides/choose-a-channel.md) reports a prerelease like
`0.2.0-alpha.0.7`. If you build the CLI yourself, see
[Releasing](../develop/releasing.md) for the full version story.

:::note Startup performance
Startup is currently JIT (self-contained, untrimmed). Making the binary lean and
fast is tracked as a separate performance unit of work, so expect a noticeable
first-run delay for now.
:::
