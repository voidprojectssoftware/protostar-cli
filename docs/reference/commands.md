---
sidebar_position: 1
title: Command reference
---

# Command reference

Every `protostar` command and its options. Run `protostar --help` or
`protostar <command> --help` for the same information at the terminal.

Options go after the command they belong to: `protostar sessions list --json`,
`protostar project show --dir ../other`. An option written before a subcommand
(`protostar skills --global-only push`) is refused, and the error gives the
command line with the option moved after it. A command with no subcommand keeps
its options where they are: `protostar skills --global-only` and
`protostar project --dir ../other` work.

An option the command does not have (`protostar skills pull --forse`) is refused
before anything runs, and the error names the closest option it does have
(`--force`). `--version` belongs to `protostar` itself: `protostar skills
--version` is refused and points at `protostar --version`.

## Global

```console
$ protostar                 # print the banner and a hint to --help
$ protostar --help          # list commands
$ protostar --version, -v   # print the version (e.g. 0.1.0)
```

## `protostar setup`

Set up this machine in one step: sign in, install capture hooks, back up this
machine's skills and then pull the registry's, and offer to back up existing
sessions. Prints one line per step (done, already
done, skipped, or failed) and is safe to run again. Exits `0` unless a step
failed.

| Option | Description |
|---|---|
| `--registry <URL>` | Registry base URL. Defaults to `$PROTOSTAR_REGISTRY_URL`, then the saved default, then the built-in dev default. Saved as the default once sign-in succeeds. |
| `-y`, `--yes` | Back up this machine's sessions without asking. Without it, setup asks on a terminal and skips the backup otherwise. |
| `--no-browser` | Print the sign-in URL instead of opening a browser. |
| `--provider <NAME>` | Skip the registry's sign-in chooser and go straight to this provider. |
| `--harness-home <DIR>` | Override the harness config root. |

See [Set up a machine](../get-started/set-up-a-machine.md).

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
- **conflict**: another skill of the same scope with the same name but different
  content was pushed in this run (two harnesses' global skills, say). The registry
  holds one skill per name in each scope, so this copy is not backed up; rename one
  of them to back up both.
- **failed**: the upload did not complete (for example, the registry was
  unreachable).
- **changed here and in the registry**: the registry refused the push because the
  skill changed there (pushed from another machine) after this copy was last pushed
  or pulled here. Nothing was overwritten on either side. Choose what to keep; the
  three ways out are printed below the table and listed under
  [Changed here and in the registry](#changed-here-and-in-the-registry).

A project skill (one in a project's `.claude/skills`) is backed up as that
project's skill: protostar works out the project from the folder the skill's
`.claude` is in, the same way it files sessions (see `protostar project`). So a
global `deploy` and a project's `deploy`, or two projects' `deploy`, are separate
skills in the registry with separate versions. The scope column shows which
project the registry filed it under.

Every file in the skill's directory is uploaded, except version control and
dependency folders (`.git`, `.hg`, `.svn`, `node_modules`, `__pycache__`, `.venv`),
files with those names, and symbolic links. A skill with a file or folder whose name
starts or ends with whitespace fails before upload, because the registry does not
accept such names; rename it and push again. The registry also refuses a skill whose
`SKILL.md` name contains `/` or `\`. The exit code is `0` when every skill reached the registry, otherwise `1`.

Takes the same options as `protostar skills`, plus:

| Option | Description |
|---|---|
| `--registry <URL>` | Registry to back up to. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |
| `--force` | Upload every selected skill even if it has not changed since its last backup. When the registry has a newer version than the one this copy was based on, replace it (keep yours): the output names the version replaced, which stays in the registry's history. |
| `--as <NEW-NAME>` | Push the one named skill as a new registry skill called `NEW-NAME`, and rename the copy here to match (its `SKILL.md` name, and its folder when the folder has the old name). Refused, with nothing changed, when the registry already has a different skill of that name. |

protostar remembers what it last backed up to each registry in
`~/.protostar/skill-ledger.json`, so a skill that has not changed is not uploaded
again. `skills push` still asks the registry for its list of skills, one request
however many skills there are, to confirm that each unchanged skill is still there
under the name and project it was backed up as.

A skill deleted or renamed in the registry since this copy last synced (from any
machine, this one included) is **not** uploaded again, because that would undo the
delete or rename. The registry remembers every name it deleted or renamed away, and
its row says what happened and the ways out:

```console
│ global │ tidy       │ deleted in the registry: 'tidy' was deleted from the registry (at v1) after this copy last synced, so it was not uploaded again. Remove this copy if you no longer want it, or run `protostar skills push tidy --force` to restore it. │
│ global │ greet-user │ renamed to hello in the registry: 'greet-user' was renamed to 'hello' in the registry (at v1) after this copy last synced, so it was not uploaded again under the old name. `protostar skills pull` renames this copy to 'hello'. │
```

A copy edited here since it synced is refused the same way, like a
[stale push](#changed-here-and-in-the-registry): nothing is uploaded and your
changes stay here. `--force` restores a deleted skill on purpose (it is pushed as a
new skill of that name), or keeps an edited copy of a renamed skill as a separate
skill under its old name. A name that was deleted can be used again: a new skill
that no machine has synced before (one created after the delete) is backed up as
usual, and its versions are numbered after the deleted skill's, so no old copy is
mistaken for it: a copy of the deleted skill is reported, not pushed over the new
one, and `pull` leaves it alone unless you pass `--force`. A skill that is gone from a registry that does not remember why
(an older registry) is backed up again as a new skill, and its row says so. A
project skill that `protostar project link` moved to another project is followed
there, not pushed again. The [capture hook](#protostar-capture) does not list, so an
unchanged skill still costs it no network call; an edited copy of a removed skill
is refused once, and the hook does not send that same content again. Use `--force`
to upload every selected skill regardless.

Each push tells the registry which version this copy was last pushed or pulled as.
When that is no longer the registry's latest version, the registry refuses the push
instead of stacking it on top, and the skill shows as **changed here and in the
registry**. A copy protostar has no record of is pushed as "based on no version", so
it can create a skill but never silently replace one. A registry from before this
check ignores it, and a CLI from before it sends nothing to check, so both keep the
old behaviour (the later push wins) until upgraded.

#### Changed here and in the registry

A skill is in this state when it was edited on this machine and the registry also
moved past the version this machine last had. `skills push` reports it,
`skills remote` shows it, and `skills pull` leaves the copy alone. Pick one:

| To keep | Run | What happens |
|---|---|---|
| Yours | `protostar skills push NAME --force` | Your copy becomes the next version. The registry's version it replaces stays in history. |
| Theirs | `protostar skills pull NAME --force` | The registry's version overwrites your copy here. |
| Both | `protostar skills push NAME --as NEW-NAME`, then `protostar skills pull NAME` | Your copy is pushed as the new skill `NEW-NAME` and renamed here; the pull installs the registry's `NAME` beside it. |

Any version replaced this way, or lost before this check existed, can be fetched
again with `protostar skills pull NAME --version N`.

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
- **changed here and in the registry (this copy is based on vN)**: edited here
  since vN while the registry moved past vN, or a push of exactly this content was
  refused for that reason. `pull` will not overwrite it without `--force`; the ways
  to keep yours, theirs or both are printed below the table (see
  [Changed here and in the registry](#changed-here-and-in-the-registry)).
- **only here (not backed up)**: the skill is on this machine but not in the
  registry. Back it up with `protostar skills push`.
- **gone from the registry**: this machine's copy was synced as a skill that has
  since been deleted or renamed in the registry. `push` does not upload it. For a
  rename, `pull` renames an unedited copy to the new name (its folder and its
  `SKILL.md` name) so the registry's skill pulls into it; an edited copy is kept
  as it is. A deleted skill's copy is kept too. Nothing local is ever deleted: the
  note says how to remove the copy or restore it with `push --force`.
- **a new skill took this name**: this machine's copy was synced from a skill that
  was deleted, and a new, unrelated skill of the same name is in the registry now.
  `push` does not upload the copy and `pull` does not overwrite it without
  `--force`; `push NAME --as NEW-NAME` keeps it as a separate skill.
- **conflict**: the skill cannot be pulled safely, and the reason is shown. Either
  a folder with the skill's name already exists and is not this skill (a skill with
  another name, a folder with no `SKILL.md`, or the same name in different case), or
  your registry holds two skills whose names differ only by case. `pull` skips it,
  even with `--force`. Move or rename the folder, or rename one of the skills.

- **other project**: a project skill of a project other than the one the current
  folder is in. It is never compared or pulled here; the note says which project it
  belongs to and where a pull from a checkout of that project would put it.

Global skills are compared with each harness's global skills folder (for Claude
Code, `~/.claude/skills`). Project skills are compared only when the current folder
is in their project, and then with that checkout's own project skills folder
(`<repo top level>/.claude/skills`; in a git worktree, the worktree's own top
level). A Project column appears when any project skill is listed.

Pass skill names to compare only those. With one name and `--version N`, the
comparison is with that skill's version N instead of its latest: **matches vN**,
**can be replaced with vN** (this copy is unedited, so `pull --version N` would
replace it), or one of the protected states above, which `pull --version N` would
leave alone.

| Option | Description |
|---|---|
| `--version <N>` | Compare the one named skill with its version N: what `skills pull NAME --version N` would do. |
| `-H, --harness <ID>` | Only compare with this harness. |
| `--harness-home <DIR>` | Override the harness config root. |
| `--project <DIR>` | The folder whose project skills to compare. Defaults to the current directory. |
| `--global-only` | Only compare global skills. |
| `--registry <URL>` | Registry to read. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |

### `protostar skills pull [NAME...]`

Install or update skills from your registry on this machine. With no names, every
skill in the registry is pulled, which is the quick way to set up a new machine;
otherwise only the named ones (an exact match first, then ignoring case).

A global skill that is not installed is written to the harness's global skills
folder (`~/.claude/skills/<name>` for Claude Code). A project skill is pulled only in
a checkout of its project, into that checkout's `.claude/skills/<name>`, and never
into any other repo; other projects' skills are reported as **not here** with the
project they belong to. A skill that is installed is updated in
place: files the new version dropped are deleted, and version control and
dependency folders inside it (such as `.git`) are left alone. A skill with **local
changes** is never overwritten unless you pass `--force`. Back those changes up with
`protostar skills push` instead if you want to keep them. A **conflict** is never
written, even with `--force`: that flag overwrites your edits to the skill, never an
unrelated folder.

A pull with no names also settles copies of skills deleted or renamed in the
registry since they synced. An unedited copy of a renamed skill follows the
rename: its folder and its `SKILL.md` name change to the new name, and the
registry's skill then pulls into it (**followed the rename**). A copy edited here,
a copy whose new name is already taken here, and any copy of a deleted skill are
kept as they are (**kept, removed from the registry**), with the ways out. Local files
are never deleted.

Before anything is written, every file is checked against the checksum the registry
sent. A path that would land outside the skill folder, pass through a symbolic
link, go inside a folder protostar never reads (such as `.git` or `node_modules`),
or have a name that starts or ends with whitespace fails the skill. So does a skill
folder that is itself a symbolic link: pull into the folder it points to instead. After a pull, the ledger records the installed
content, so the capture hook does not push it straight back.

The registry does not store file permissions. An updated file keeps its existing
permissions, and a new file that starts with `#!` is made executable for you.

The exit code is `0` unless a skill failed to download or write, or was skipped as a
conflict. Skills kept because of local changes, or because they changed here and in
the registry, do not count as failures.

With one name and `--version N`, version N is installed instead of the latest, to
recover earlier work (including work overwritten before protostar checked for
concurrent edits). Every rule above still applies: a copy with local changes is
kept unless you pass `--force`, and a conflict or another project's skill is never
written. The ledger then records the copy as version N, so a plain `skills pull`
would replace it with the latest again; run `protostar skills push NAME --force` to
make it the latest instead.

| Option | Description |
|---|---|
| `-H, --harness <ID>` | Only install into this harness. |
| `--harness-home <DIR>` | Override the harness config root. |
| `--project <DIR>` | The folder whose project skills to install. Defaults to the current directory. |
| `--global-only` | Only pull global skills. |
| `--registry <URL>` | Registry to pull from. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |
| `--force` | Overwrite skills that have local changes, including ones changed here and in the registry. Never overrides a conflict. |
| `--version <N>` | Install version N of the one named skill instead of its latest. |

### `protostar skills rename <OLD> <NEW>`

Rename a skill in the registry. Its version history comes with it, and this
machine's copies follow: the local folder is renamed, its `SKILL.md` gets the new
name, and that change is pushed as a new version. You must be
[signed in](../get-started/authenticate.md) first.

```console
$ protostar skills rename notes journal
Renamed the global skill notes to journal in https://localhost:7579. Its version
history came with it.
Its SKILL.md now names it journal: pushed as v2.
Local copy renamed here: ~/.claude/skills/notes -> ~/.claude/skills/journal
The registry remembers the old name, so other machines do not push it back. Their
next `protostar skills pull` (or setup) renames an unedited copy to match; a copy
edited there is kept and reported.
```

`<OLD>` must be the registry skill's exact current name. When the current folder
is in a project with a project skill of that name, that skill is meant;
otherwise the global skill is. A name the registry does not have is an error
(exit `1`).

| Option | Description |
|---|---|
| `--project <DIR>` | The folder whose project skill is meant. Defaults to the current directory; with no project skill of that name there, the global skill is meant. |
| `--global-only` | Only the global skill of that name, even when this folder's project has one. |
| `--registry <URL>` | Registry to change. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |

### `protostar skills delete <NAME>`

Delete a skill and every one of its versions from the registry. It cannot be
undone. Local copies are never deleted: the command lists them, and afterwards
`protostar skills remote` shows them as **gone from the registry**. The registry
remembers the delete, so neither `protostar skills push` nor `protostar setup`, on
this machine or another, uploads a copy of it again. Remove a copy you no longer
want; `protostar skills push NAME --force` restores the skill on purpose, as a new
skill.

The command says what it is about to delete and asks first. With no terminal to
ask on (a script, a pipe) it refuses unless you pass `--yes`:

```console
$ protostar skills delete journal < /dev/null
This deletes the global skill journal from https://localhost:7579, with all 2 of
its versions. It cannot be undone.
This machine has a copy at ~/.claude/skills/journal.
Local copies are not deleted: only the registry's copy goes.
Not deleted: confirmation is needed and there is no terminal to ask on. Pass
--yes to delete without asking.
```

Which skill a name means works as for `rename`. The exit code is `0` when the
skill was deleted or you answered no, and `1` when it was not found, the
registry refused, or confirmation was needed and not possible.

| Option | Description |
|---|---|
| `--project <DIR>` | The folder whose project skill is meant. Defaults to the current directory; with no project skill of that name there, the global skill is meant. |
| `--global-only` | Only the global skill of that name, even when this folder's project has one. |
| `--registry <URL>` | Registry to change. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |
| `-y`, `--yes` | Delete without asking. Needed when input is not a terminal. |

## `protostar sessions`

Back up your harness's session transcripts to the registry and see what is backed
up. Each session is filed under the [project](../guides/projects.md) of the folder
it ran in. `protostar sessions` on its own prints its help; use one of the commands
below.

These options are shared by all three commands:

| Option | Description |
|---|---|
| `--registry <URL>` | Registry to use. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |
| `-H, --harness <ID>` | Only this harness id (for example `claude-code`). |
| `--harness-home <DIR>` | Override the harness config root. |

### `protostar sessions push`

Back up every session transcript on this machine, newest first. Only what the
registry does not have yet is sent, so a second push sends nothing. You must be
[signed in](../get-started/authenticate.md) first.

On a terminal a progress bar shows the bytes sent. When output goes to a file or a
pipe, you get one line when the run starts and one each time another tenth is
done. A run reaches 100% only when every transcript in it was backed up; a
transcript that is skipped or fails is counted as not finished and its bytes never
count as done.

Ctrl+C stops cleanly between or during chunks. Nothing is lost, and the next push
carries on from where this one stopped:

```console
$ protostar sessions push > push.log    # Ctrl+C part way
$ cat push.log
Backing up 2 transcripts (160 MB), newest first.
10%: 24 MB of 160 MB, 0 of 2 transcripts done.
20%: 40 MB of 160 MB, 0 of 2 transcripts done.
30%: 56 MB of 160 MB, 1 of 2 transcripts done.
╭──────────┬─────────┬───────┬────────────────────────────────╮
│ Session  │ Kind    │  Sent │ Result                         │
├──────────┼─────────┼───────┼────────────────────────────────┤
│ 5f0c1d2e │ session │ 16 MB │ partly backed up, 104 MB to go │
│ 6a1b2c3d │ session │ 40 MB │ backed up                      │
╰──────────┴─────────┴───────┴────────────────────────────────╯
5 transcripts: 1 backed up (56 MB sent), 3 already up to date, 1 not finished.
Registry: https://localhost:7579.
Stopped part way. Nothing is lost: run `protostar sessions push` again to carry
on.
```

A transcript another protostar process is already uploading (a capture hook's
background push, say) is skipped with that reason and goes next time.

The exit code is `0` when everything is backed up, `130` when stopped with Ctrl+C,
and `1` otherwise.

| Option | Description |
|---|---|
| `--verify` | Ask the registry what it holds of every backed-up transcript, in batches, and back up again what it lost. A transcript the registry holds differently from this machine's record is left alone and reported. |

`--verify` costs requests even when nothing changed, so plain pushes and the
capture hooks never do it.

:::note `--session`, for hooks
The capture hooks run `protostar sessions push --session <PATH>` to back up one
session (and its subagents) in the background, with no progress display; problems
go to `~/.protostar/capture.log`. It is hidden from `--help` and cannot be combined
with `--verify`. You should not need to run it yourself.
:::

### `protostar sessions status`

Show what is and is not backed up on this machine. It works offline: it compares
the transcripts on disk with what this machine last recorded as sent. A table
lists the pending transcripts and why each is pending, followed by a summary:

```console
$ protostar sessions status
5 transcripts on this machine (5 sessions, 0 subagents). All backed up. Last
backup: 2 min ago.
```

The exit code is `0` when everything is backed up and `1` while anything is
pending.

| Option | Description |
|---|---|
| `-a, --all` | List every transcript, backed up ones too, not only the pending ones. |
| `--verify` | Also ask the registry what it holds, to confirm the backups are really there (before wiping a machine, say). Changes nothing; it needs you to be signed in. |

```console
$ protostar sessions status --verify
5 transcripts on this machine (5 sessions, 0 subagents). All backed up. Last
backup: 2 min ago.
Verified 5 transcripts against the registry: 5 confirmed, 0 missing, 0 short.
```

### `protostar sessions list`

List the sessions the registry holds from every machine, newest activity first,
plus this machine's sessions that are not fully backed up yet. Each row shows when
it was last active, its title, project, machine, harness, number of prompts, size,
backup state and a short id.

```console
$ protostar sessions list --project nomatch
No sessions match --project nomatch. Leave the filter out to see every session.
```

When the registry cannot be reached, or you are not signed in, it still lists
this machine's pending sessions, says why the rest are missing, and exits `1`.

| Option | Description |
|---|---|
| `-p, --project <ID\|NAME>` | Only sessions in this project: its id, or text in its name (for example the repo name, `demo-app`). |
| `--machine <ID>` | Only sessions from this machine id. |
| `--since <DATE>` | Only sessions active on or after this date or time (for example `2026-11-12`, read in your local time zone). |
| `-n, --limit <N>` | How many to show, from 1 to 200. Defaults to 25. |
| `--json` | Print JSON instead of a table, for agents and scripts. |

With `--json`, each row is the registry's record of the session. A row for a
session not fully backed up has a `pending` object added, and a row the registry
does not have at all (made from the file on this machine) has no title and zero
counts. A registry error goes to standard error, so standard output stays valid
JSON.

```console
$ protostar sessions list --json -n 1
[{"harness":"claude-code","key":"00000000-0000-0000-0000-000000001113","kind":"session","title":"hello 2","firstPrompt":"hello 2","projectId":"84ae7ab4-363a-4328-b80c-81c5f6495764","project":"github.com/example/demo-app","workingDirectory":"/home/alice/src/demo-app","machineId":"e43c5bac-e1ac-4493-b2fb-21abbca63a1b","machineName":"vm-dev-01","sourcePath":"/home/alice/.claude/projects/-home-alice-src-demo-app/00000000-0000-0000-0000-000000001113.jsonl","userMessages":1,"assistantMessages":1,"length":2511,"subagentCount":0}]
```

## `protostar project`

See which [project](../guides/projects.md) a folder belongs to, and link or unlink
it. A project is what sessions and project skills are filed under, the same on
every machine. With no subcommand, `protostar project` is `protostar project show`.

These options are shared by all three commands:

| Option | Description |
|---|---|
| `--registry <URL>` | Registry to ask. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |
| `-C, --dir <DIR>` | The folder to ask about. Defaults to the current directory. |

### `protostar project show`

Show the folder's project and how it was recognised:

```console
$ protostar project
Folder         /home/alice/src/demo-app
Project        github.com/example/demo-app
Id             84ae7ab4-363a-4328-b80c-81c5f6495764
Recognised by  the repo's root commit 720d3077c24b
Git            root 720d3077c24b on refs/heads/main, origin
               https://github.com/example/demo-app.git
```

A folder no backup has reported yet shows `none yet: the first backup from here
creates it` as its id. When the registry suggests the folder may belong to
another project, the suggestions are listed with the `protostar project link`
command for each; nothing is linked until you run it.

If the registry cannot be reached, or you are not signed in, it shows what can be
worked out on this machine (the identity it would use and the git facts), says
why, and exits `1`. Otherwise it exits `0`.

| Option | Description |
|---|---|
| `--json` | Print JSON instead of text, for agents and scripts: the local `facts`, the registry's `project`, and a `registryError` when the registry could not answer. |

### `protostar project link <PROJECT>`

Link the folder, on this machine, to a project. `<PROJECT>` is the project's id,
or its full name as `protostar project` or `protostar sessions list` shows it. The
folder's sessions move with it, and any project skills that move too are listed
under **Skills moved**:

```console
$ cd ~/notes
$ protostar project link github.com/example/demo-app
Linked this folder to github.com/example/demo-app. Its sessions moved with it.
Folder         /home/alice/notes
Project        github.com/example/demo-app
Id             84ae7ab4-363a-4328-b80c-81c5f6495764
Recognised by  a link you made from this folder (`protostar project unlink`
               removes it)
Skills moved   none
```

A project the registry does not know is an error (exit `1`).

### `protostar project unlink`

Remove the folder's link, so it is recognised the usual way again:

```console
$ protostar project unlink
Unlinked this folder. It now belongs to notes.
```

In a copy of a repo that shares its project with other copies (a fork, or a
second clone), with no link to remove, `unlink` gives this folder a project of its
own instead. Run `protostar project unlink` again to rejoin. A folder that is not
linked and is already its own project is left alone (`Nothing changed.`).

## `protostar auth`

Authenticate to the protostar registry.

### `protostar auth login`

Sign in via your browser and store the session.

| Option | Description |
|---|---|
| `--registry <URL>` | Registry to sign in to. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. Saved as the default once sign-in succeeds. |
| `--provider <NAME>` | Skip the registry's sign-in chooser and go straight to this provider (e.g. `github`). |
| `--no-browser` | Print the sign-in URL instead of opening a browser automatically. |
| `--timeout <SECONDS>` | How long to wait for the browser sign-in to complete (default `300`). |

### `protostar auth status`

Show whether you are signed in to the registry, and where that registry came from
(`--registry`, `PROTOSTAR_REGISTRY_URL`, the saved default, or the built-in default).

### `protostar auth logout`

Remove the stored session for the registry. The saved default registry is kept.

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
costs no network call. Plugin skills (`plugin:name`) are not backed up. When the
registry refuses the backup because the skill also changed there, the refusal is
logged once and remembered: later uses of the same content are not sent again, and
`skills remote` shows the skill as changed here and in the registry.

On `Stop` (after each reply) and `SessionEnd`, `capture` starts a background
`protostar sessions push` for that session and returns at once, without waiting for
the upload. A `Stop` push is skipped when the session's last push started less
than 30 seconds ago; `SessionEnd` always pushes. Only one push of a transcript
runs at a time. A background push that fails (registry unreachable, not signed in)
marks its session in the local ledger; the next background push of any session
that reaches the registry also sends up to three marked sessions, oldest failure
first, and clears each mark once that session is fully backed up. Sessions that
were never backed up but never failed are left to `protostar sessions push`.
Other hook events are acknowledged and ignored for now.

The hook never blocks or fails your session: it gives up after 15 seconds and
always exits `0`. It writes one line to the session transcript, and problems you
may need to act on (not signed in, registry unreachable, skill refused) are also
appended to `~/.protostar/capture.log`, by the hook or by the background push.

| Option | Description |
|---|---|
| `--hook <EVENT>` | The harness event that triggered capture (e.g. `PostToolUse`, `SessionStart`). |
