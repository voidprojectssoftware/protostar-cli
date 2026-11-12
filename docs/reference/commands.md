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

## `protostar skills`

List the skills protostar finds on this machine, in global scope (for example
`~/.claude/skills`) and project scope (walking up from the current directory),
across every supported harness.

| Option | Description |
|---|---|
| `-H, --harness <ID>` | Only discover skills for this harness id. |
| `--harness-home <DIR>` | Override the harness config root (testing or a non-default location). |
| `--project <DIR>` | Where to start looking for project-scoped skills. Defaults to the current directory. |
| `--global-only` | Discover only global skills; skip the project scope. |

### `protostar skills push [NAME...]`

Back up local skills to the registry. With no names, every skill `protostar skills`
lists is pushed; otherwise only the named ones. You must be
[signed in](../get-started/authenticate.md) first.

Each skill is uploaded on its own, so one refused skill does not stop the rest. For
each skill you see one of:

- **new version vN**: the registry stored a new version.
- **unchanged (vN)**: the registry already had exactly this content.
- **refused**: the registry would not store it (for example, it is over the size
  limit or its SKILL.md is missing), with the reason.
- **skipped**: another skill with the same name was already pushed in this run
  (global wins over project, matching which one the harness loads).
- **failed**: the upload did not complete (for example, the registry was
  unreachable).

Every file in the skill's directory is uploaded, except version control and
dependency folders (`.git`, `node_modules`, `__pycache__`, `.venv`) and symbolic
links. The exit code is `0` when every skill reached the registry, otherwise `1`.

Takes the same options as `protostar skills`, plus:

| Option | Description |
|---|---|
| `--registry <URL>` | Registry to back up to. Defaults to `PROTOSTAR_REGISTRY_URL`, then `https://localhost:7443`. |
| `--force` | Upload every selected skill even if it has not changed since its last backup. |

protostar remembers what it last backed up to each registry in
`~/.protostar/skill-ledger.json`, so a skill that has not changed is reported as
unchanged without contacting the registry. Use `--force` if the registry has lost
something the ledger says it has.

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
protostar installs in your harness. It is hidden from `--help` for that reason and
is documented here only for completeness. You should not need to run it by hand.
:::

When Claude Code uses one of your skills, `capture` backs that skill up to the
registry if it changed since its last backup, the same way
[`skills push`](#protostar-skills-push-name) does for one skill. An unchanged skill
costs no network call. Plugin skills (`plugin:name`) are not backed up. Other hook
events are acknowledged and ignored for now.

The hook never blocks or fails your session: it gives up after 15 seconds and
always exits `0`. It writes one line to the session transcript, and problems you
may need to act on (not signed in, registry unreachable, skill refused) are also
appended to `~/.protostar/capture.log`.

| Option | Description |
|---|---|
| `--hook <EVENT>` | The harness event that triggered capture (e.g. `PostToolUse`, `SessionStart`). |
