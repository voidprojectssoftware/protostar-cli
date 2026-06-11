---
sidebar_position: 2
title: Update, move, or uninstall
---

# Update, move, or uninstall

Everything you need after the first install: keeping current, choosing where the
binary lives, and removing it cleanly.

## Update protostar

There is no separate "update" command — you re-run the installer for your
[channel](./choose-a-channel.md), and it replaces the binary in place.

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

On **edge**, re-run the `--channel edge` form to pull the newest rolling build.
Confirm with `protostar --version`.

## Install to a custom directory

By default `protostar install` picks a per-user directory and adds it to your
`PATH`. Override either behavior:

```console
$ protostar install --dir <DIR>        # install somewhere specific
$ protostar install --no-modify-path   # don't touch PATH (you manage it yourself)
```

`--no-modify-path` is useful when your `PATH` is managed by a dotfiles repo or a
configuration tool and you do not want the installer editing your shell profile.

## Uninstall

```console
$ protostar uninstall
```

This removes the installed binary. If you also
[connected protostar to a harness](../get-started/connect-your-harness.md), remove
those hooks too — uninstalling the binary does not touch your harness settings:

```console
$ protostar install-hooks --remove --yes
```

To fully clean up, you can also delete the config directory that holds your saved
sessions (`~/.protostar`, or wherever `PROTOSTAR_CONFIG_DIR` points). See
[Configuration](../reference/configuration.md) for what lives there.

:::tip Order doesn't matter, but do both
The binary and the harness hooks are independent. Removing one leaves the other in
place. For a complete teardown, run `install-hooks --remove` **and** `uninstall`.
:::
