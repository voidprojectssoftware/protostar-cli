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
- **skipped**: another skill with the same name and identical content was already
  pushed in this run, so the registry already has it.
- **conflict**: another skill with the same name but different content was pushed
  in this run (global wins over project, matching which one the harness loads).
  The registry holds one skill per name, so this copy is not backed up; rename one
  of them to back up both.
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

### `protostar skills remote`

List the skills in your registry and how each one compares with this machine. You
must be [signed in](../get-started/authenticate.md) first. Nothing is downloaded or
written. Each skill shows its latest registry version and one of:

- **up to date**: this machine has exactly the registry's latest version.
- **not installed**: the skill is in the registry but not on this machine.
- **update available (have vN)**: this machine has an older version that has not
  been edited here since it was last pushed or pulled.
- **local changes**: this machine's copy differs from the registry and may hold
  work done here (or protostar has no record of it). `pull` will not overwrite it
  without `--force`.
- **only here (not backed up)**: the skill is on this machine but not in the
  registry. Back it up with `protostar skills push`.

Skills are compared with each harness's global skills folder (for Claude Code,
`~/.claude/skills`). Project skills are not compared.

| Option | Description |
|---|---|
| `-H, --harness <ID>` | Only compare with this harness. |
| `--harness-home <DIR>` | Override the harness config root. |
| `--registry <URL>` | Registry to read. Defaults to `PROTOSTAR_REGISTRY_URL`, then `https://localhost:7443`. |

### `protostar skills pull [NAME...]`

Install or update skills from your registry on this machine. With no names, every
skill in the registry is pulled, which is the quick way to set up a new machine;
otherwise only the named ones (names ignore case).

A skill that is not installed is written to the harness's global skills folder
(`~/.claude/skills/<name>` for Claude Code). A skill that is installed is updated in
place: files the new version dropped are deleted, and version control and
dependency folders inside it (such as `.git`) are left alone. A skill with **local
changes** is never overwritten unless you pass `--force`. Back those changes up with
`protostar skills push` instead if you want to keep them.

Before anything is written, every file is checked against the checksum the registry
sent, and a path that would land outside the skill folder (or pass through a
symbolic link) fails the skill. After a pull, the ledger records the installed
content, so the capture hook does not push it straight back.

The registry does not store file permissions. An updated file keeps its existing
permissions, and a new file that starts with `#!` is made executable for you.

The exit code is `0` unless a skill failed to download or write. Skills kept
because of local changes do not count as failures.

| Option | Description |
|---|---|
| `-H, --harness <ID>` | Only install into this harness. |
| `--harness-home <DIR>` | Override the harness config root. |
| `--registry <URL>` | Registry to pull from. Defaults to `PROTOSTAR_REGISTRY_URL`, then `https://localhost:7443`. |
| `--force` | Overwrite skills that have local changes. |

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
