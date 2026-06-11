---
sidebar_position: 1
title: Command reference
---

# Command reference

Every `protostar` command and its options. Run `protostar --help` or
`protostar <command> --help` for the same information at the terminal.

## Global

```console
$ protostar                 # print the banner and a hint to --help
$ protostar --help          # list commands
$ protostar --version, -v   # print the version (e.g. 0.1.0)
```

## `protostar install`

Install protostar to a per-user directory and add it to `PATH`.

| Option | Description |
|---|---|
| `--dir <DIR>` | Install to a specific directory instead of the default per-user location. |
| `--no-modify-path` | Do not edit `PATH`; you manage it yourself. |

See [Installation](../get-started/installation.md) and
[Update, move, or uninstall](../guides/manage-your-installation.md).

## `protostar uninstall`

Remove an installed protostar binary. Does not remove harness hooks — use
`install-hooks --remove` for those.

## `protostar install-hooks`

Detect supported harnesses and install protostar's capture hooks into the selected
ones, idempotently. With no flags and a terminal attached, it prompts you to pick
harnesses; otherwise it runs non-interactively.

| Option | Description |
|---|---|
| `-H, --harness <ID>` | Target a specific harness by id (repeatable). Implies non-interactive. |
| `--all` | Select all detected harnesses without prompting. |
| `-y, --yes` | Non-interactive: skip the prompt and use all detected harnesses. |
| `--harness-home <DIR>` | Override the harness config root (testing or a non-default location). |
| `--exe-path <PATH>` | Path to the protostar binary the hooks should invoke. Defaults to the running binary. |
| `--dry-run` | Show what would change without writing. |
| `--remove` | Remove protostar's capture hooks instead of installing them. |

See [Connect your harness](../get-started/connect-your-harness.md).

## `protostar auth`

Authenticate to the protostar registry.

### `protostar auth login`

Sign in via your browser and store the session.

| Option | Description |
|---|---|
| `--registry <URL>` | Registry to sign in to. Defaults to `https://localhost:7443` or `PROTOSTAR_REGISTRY_URL`. |
| `--provider <NAME>` | Skip the registry's sign-in chooser and go straight to this provider (e.g. `github`). |
| `--no-browser` | Print the sign-in URL instead of opening a browser automatically. |
| `--timeout <SECONDS>` | How long to wait for the browser sign-in to complete (default `300`). |

### `protostar auth status`

Show whether you are signed in to the registry (honors `--registry` /
`PROTOSTAR_REGISTRY_URL`).

### `protostar auth logout`

Remove the stored session for the registry.

See [Authenticate to a registry](../get-started/authenticate.md).

## `protostar capture`

:::note Invoked by hooks, not by you
`capture` is called automatically by the [hooks](../get-started/connect-your-harness.md)
protostar installs in your harness. It reads a hook event from stdin and
acknowledges it; it is hidden from `--help` for that reason and is documented here
only for completeness. You should not need to run it by hand.
:::

| Option | Description |
|---|---|
| `--hook <EVENT>` | The harness event that triggered capture (e.g. `PostToolUse`, `SessionStart`). |
