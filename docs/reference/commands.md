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
machine's skills and then pull the registry's, do the same for your personal
custom agents, bring your agents' memory to the
projects checked out here (as [`memory pull`](#protostar-memory-pull) does), and
offer to back up existing sessions. Prints one line per step (done, already
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

## `protostar status`

Answer "is this machine syncing?" in one place: the registry in use and where it
came from, whether you are signed in, whether each harness has the capture hooks,
when the hooks last backed up skills, sessions and memory in the background, the
failures since then with the newest error, the newest note about a backup that
worked but needs telling (such as a skill filed in a different project after
`project unlink`), sessions whose backup failed and wait
for a retry, and the session bytes not backed up yet.

It works offline. Everything comes from this machine except one quick check that
the stored sign-in is still good, which gives up after 3 seconds and is skipped
with `--offline`. With no stored sign-in the registry is not contacted at all.

Exits `0` when the machine is syncing and `1` when something needs attention: not
signed in, the sign-in refused, the registry not answering, no harness or hooks
missing, the newest background backup of a kind failed, or sessions waiting to be
retried. Pending bytes alone are not a problem, since a live session always has
some until its next push.

```console
$ protostar status
Registry  https://registry.example (from ~/.protostar/config.json (saved by setup or auth login))
Sign-in   signed in as octocat
Hooks     claude-code: installed (~/.claude/settings.json)
Skills    last backed up 3 min ago (2026-11-20 10:40)
Sessions  2 failures since the last backup 1 h ago (2026-11-20 09:12); newest 3 min ago (2026-11-20 10:40): session not backed up: the registry could not be reached. ...
Memory    last backed up 1 h ago (2026-11-20 09:12)
Pending   130 B in 1 transcript (goes with the next hook, or run `protostar sessions push`)

Needs attention:
  - Session backups are failing: the registry could not be reached. They resume on their own once it can.
```

| Option | Description |
|---|---|
| `--registry <URL>` | Registry base URL. Defaults to `$PROTOSTAR_REGISTRY_URL`, then the saved default, then the built-in dev default. |
| `--harness-home <DIR>` | Override the harness config root. |
| `--offline` | Do not ask the registry whether the sign-in is still good. |
| `--json` | Print one JSON object instead of the report. |

With `--json`, standard output holds one object: `ok` (boolean), `registry`,
`registrySource` (`flag`, `environment`, `configFile`, `default`), `signIn`
(`state`: `signedOut`, `signedIn`, `notChecked`, `rejected`, `unreachable`; and
`login`), `hooks` (`harness`, `settingsPath`, `installed`, `error`), `backups` (one
per `kind`: `skills`, `sessions`, `memory`, with `failing`, `lastSuccessAt`,
`lastFailureAt`, `failuresSinceSuccess`, `lastReason`, `lastError`, `lastNoteAt`, `lastNote`),
`failedSessions` (`id`, `harness`, `path`, `failedAt`), `pending` (`bytes`,
`transcripts`) and `problems`, the stable reasons for a non-zero exit:
`notSignedIn`, `sessionRejected`, `registryUnreachable`, `noHarness`,
`hooksMissing`, `skillsFailing`, `sessionsFailing`, `memoryFailing`,
`sessionsAwaitingRetry`. Names are camelCase and absent values are left out. An
unusable registry URL prints `{"error":{"code":"invalid_registry","message":...}}`
to standard error and exits `1`.

The backup history comes from `~/.protostar/capture-records.jsonl`, which the
hooks append to (see [`capture`](#protostar-capture)). Records for another
registry are ignored.

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
| `--force` | Upload every selected skill even if it has not changed since its last backup. When the registry has a newer version than the one this copy was based on, replace it (keep yours): the output names the version replaced, which stays in the registry's history. Also needed to push to a registry that cannot detect overwrites (it lists capabilities without `stale-skill-push`); the push then warns. See [Troubleshooting](troubleshooting.md#registry-too-old). |
| `--as <NEW-NAME>` | Push the one named skill as a new registry skill called `NEW-NAME`, and rename the copy here to match (its `SKILL.md` name, and its folder when the folder has the old name). Refused before anything is pushed or changed when `NEW-NAME` is already taken in the skill's scope (global, or its project): by another skill here (its folder or its `SKILL.md` name, in any harness), by any folder of that name beside it, or by a registry skill, or one deleted or renamed away from the registry. The refusal names what holds the name and suggests a free one. |

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

A project skill's base version is a version of the project its copy last synced
in, so it is only ever sent to that project. When the folder has since moved to
another project (an unlink, then a link back, say), the copy is pushed as one that
has no base there: if that project has a skill of the same name, the push is refused
as [changed here and in the registry](#changed-here-and-in-the-registry), with the
usual ways out. To check, push and the capture hook ask the registry which project
the folder is in, once per folder and only for an edited copy they are about to
upload; when the registry cannot say, that copy is not sent and the next push tries
again. `agents push` does the same for project agents.

A copy this machine has no sync record of (a restored backup, dotfiles, or a lost
`~/.protostar`) is recognised too, by content: when its files are byte-identical to a
version of a skill deleted or renamed in the registry, it is that skill, and it is
reported instead of uploaded as a new one. Only the hashes the registry already
keeps are compared (it lists each removed name's version hashes when it serves
`skill-tombstone-hashes`); nothing is uploaded to ask. Push and the hook check only
such a never-synced copy, which they were about to upload anyway:

```console
│ global │ greet-user │ renamed to hello in the registry: This copy has no sync record here, but it is identical to v1 of 'greet-user', which was renamed to 'hello' in the registry (at v1). It was not uploaded under the old name. `protostar skills pull` renames this copy to 'hello'. │
```

A copy with other content that only reuses the name is a new skill and is backed up
as usual. Against a registry without `skill-tombstone-hashes` such a copy is pushed
as a new skill, as before.

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
are never deleted. A copy with no sync record whose content equals a version of the
renamed skill follows the rename the same way.

For an edited copy of a renamed skill the row gives the exact steps to move your
changes into the new name: `protostar skills pull NEW` installs it beside your copy,
`diff -r` on the two folders shows your changes (the `name:` line differs on
purpose), you copy them across and `protostar skills push NEW`, then delete the old
folder. Until you do, or choose another way out, `protostar setup` ends with "needs a
decision". [`protostar skills dismiss`](#protostar-skills-dismiss-name) is the way
to say "keep it here, stop asking".

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

### `protostar skills dismiss <NAME>`

Keep this machine's copy of a skill deleted or renamed in the registry exactly as it
is, unsynced, and stop being asked about it. No file and nothing in the registry
changes: the ledger remembers the copy's content as dismissed, so `skills push` and
the capture hook skip it, `skills pull` never renames it, and `protostar setup` no
longer counts it as a decision. If you edit the copy again, the question comes back.

```console
$ protostar skills dismiss greet-user
Dismissed greet-user at ~/.claude/skills/greet-user: Renamed to 'hello' in the registry (at v1). Dismissed: kept here as it is, unsynced, and never uploaded or renamed. Delete the folder when you no longer want it, or run `protostar skills push greet-user --force` to back it up as a separate skill named 'greet-user'.
```

Only a copy that `protostar skills remote` lists as **gone from the registry** can be
dismissed; any other name exits `1` with "Nothing to dismiss". `push NAME --force`
still backs a dismissed copy up as a separate skill.

| Option | Description |
|---|---|
| `-H, --harness <ID>` | Only this harness's copies. |
| `--harness-home <DIR>` | Override the harness config root (testing or a non-default location). |
| `--project <DIR>` | The folder whose project skills count too. Defaults to the current directory. |
| `--global-only` | Only global skills. |
| `--registry <URL>` | Registry to use. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |

## `protostar agents`

Back up your Claude Code custom agents: the Markdown files in `~/.claude/agents/`
(personal) and in a project's `.claude/agents/`, subfolders included. An agent is
named by the `name` in its front matter, not by its file name, and is versioned in
the registry like a skill: personal agents belong to you, project agents to the
folder's project (the same one its sessions and skills go to).

### `protostar agents push [NAME...]`

Backs up every agent found (or the named ones) and uploads only what changed since
its last backup. Run it inside a project to include that project's agents.

| Option | Meaning |
|---|---|
| `--project <DIR>` | Where to look for project agents. Defaults to the current directory. |
| `--personal-only` | Skip the project's agents. |
| `--force` | Upload even if unchanged, replace the registry's version when it changed elsewhere since this copy was synced (keep mine; the replaced version stays in history), and restore an agent deleted or renamed in the registry. |
| `-H, --harness`, `--harness-home`, `--registry` | As for `skills push`. |

A file whose front matter has no `name` (or no `description`) is refused without
being sent: Claude Code does not load it either. When an agent changed here and in
the registry since this copy was synced, nothing is overwritten and the push says
which version the registry has, with the ways out: `protostar agents push NAME --force`
keeps your copy, `protostar agents pull NAME --force` takes the registry's. A copy of an
agent deleted or renamed in the registry is reported, not uploaded. A registry from before agents is
named as too old instead of failing. Exit code 1 when anything was not backed up.

You rarely need to run it: at the end of each session the capture hook backs up the
agents that changed (see [`capture`](#protostar-capture)).

### `protostar agents remote [NAME...]`

Lists the agents in the registry and how each compares with this machine: its scope
(personal, or the project it belongs to), the registry's latest version, what this
machine has (`not installed`, `up to date`, `update available`, `local changes`,
`changed here and in the registry`, `gone from the registry`, `conflict`, or `only
here`), and the file it lives in, relative to the agents folder. Another project's
agents are listed as theirs; they are only pulled from a checkout of that project.

| Option | Meaning |
|---|---|
| `--version <N>` | Compare the one named agent with its version N: what `agents pull NAME --version N` would do. |
| `--project <DIR>` | The folder whose project agents to compare. Defaults to the current directory. |
| `--personal-only` | Only personal agents. |
| `-H, --harness`, `--harness-home`, `--registry` | As for `skills remote`. |

### `protostar agents pull [NAME...]`

Installs the registry's agents that are missing here and updates copies that are
unedited since they last synced. **A copy edited here is never overwritten** without
`--force`, and a file that is not the agent is never overwritten at all. A missing
agent is written at the path it was pushed from (subfolders included), inside
`~/.claude/agents/` or, for this folder's project, `<repo>/.claude/agents/`; a
path that would leave that folder is refused. Run it again and nothing changes.

| Option | Meaning |
|---|---|
| `--force` | Overwrite copies with local changes, including ones changed here and in the registry (keep theirs). Never overwrites a conflict. |
| `--version <N>` | Install version N of the one named agent instead of its current version. Local changes are still kept unless `--force`. |
| `--project <DIR>` | The folder whose project agents to install. Defaults to the current directory. |
| `--personal-only` | Only personal agents. |
| `-H, --harness`, `--harness-home`, `--registry` | As for `skills pull`. |

When an agent was renamed in the registry, a pull renames an unedited copy here to
follow: it sets the `name` line in the file, which stays where it is (the registry
keeps the path the agent was pushed from, so every machine has it in the same
place). The same applies when a fresh machine pulls a renamed agent, whose stored
file still says its old name. An edited copy of a renamed agent, and any copy of a
deleted agent, is kept and reported; local files are never deleted.

Project agents follow the folder's project exactly as project skills do,
including after `protostar project link`. Like project skills, they are written
into the checkout, where git sees them; whether to commit them is yours to decide.
Exit code 1 when an agent failed or conflicted.

### `protostar agents rename <OLD> <NEW>`

Rename an agent in the registry. Its version history comes with it. This
machine's copies get the new name in their `name:` line and nothing else: **the
file is not renamed or moved**. Claude Code names an agent by that line, not by
its file name, the registry keeps the path the agent was pushed from, and a pull
on another machine follows the rename in place too, so every machine keeps the
agent in the same file. No new version is pushed: the registry's stored file
still says the old name, and a pull writes the new one as it installs. You must
be [signed in](../get-started/authenticate.md) first.

```console
$ protostar agents rename reviewer code-reviewer
Renamed the personal agent reviewer to code-reviewer in https://localhost:7727.
Its version history came with it.
Local copy renamed here (its `name:` line; the file stays where it is):
~/.claude/agents/review/reviewer.md
The registry remembers the old name, so other machines do not push it back.
Their next `protostar agents pull` (or setup) renames an unedited copy to match;
a copy edited there is kept and reported.
```

**A copy here with changes that were never pushed stops the rename** before the
registry is asked, and nothing changes. Push the changes first
(`protostar agents push OLD`), or drop them (`protostar agents pull OLD --force`),
then rename again.

`<OLD>` must be the registry agent's exact current name. Without options, the
personal agent of that name or the current folder's project agent of that name
is meant, whichever exists; when both do, the command refuses and names them, so
pick one with `--personal-only` or `--project <DIR>`. A name the registry does
not have, a taken new name, and unpushed changes are errors (exit `1`).

| Option | Description |
|---|---|
| `--project <DIR>` | Only the agent of this folder's project. |
| `--personal-only` | Only the personal agent of that name, even when this folder's project has one. |
| `--registry <URL>` | Registry to change. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |

### `protostar agents delete <NAME>`

Delete an agent and every one of its versions from the registry. It cannot be
undone. **No local file is deleted**, on this machine or any other: the command
lists this machine's copies, and afterwards `protostar agents remote` shows them
as **gone from the registry**. The registry remembers the delete, so neither
`protostar agents push`, the capture hook, nor `protostar setup`, on this machine
or another, uploads a copy of it again, edited or not. Remove a copy you no
longer want; `protostar agents push NAME --force` restores the agent on purpose,
as a new agent.

The command says what it is about to delete and asks first. With no terminal to
ask on (a script, a pipe) it refuses unless you pass `--yes`:

```console
$ protostar agents delete migrator < /dev/null
This deletes the personal agent migrator from https://localhost:7727, with all 2
of its versions. It cannot be undone.
This machine has a copy at ~/.claude/agents/migrator.md.
Local files are not deleted: only the registry's copy goes.
Not deleted: confirmation is needed and there is no terminal to ask on. Pass
--yes to delete without asking.
```

Which agent a name means works as for `rename`. The exit code is `0` when the
agent was deleted or you answered no, and `1` when it was not found or
ambiguous, the registry refused, or confirmation was needed and not possible.

| Option | Description |
|---|---|
| `--project <DIR>` | Only the agent of this folder's project. |
| `--personal-only` | Only the personal agent of that name, even when this folder's project has one. |
| `--registry <URL>` | Registry to change. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |
| `-y`, `--yes` | Delete without asking. Needed when input is not a terminal. |

## `protostar sessions`

Back up your harness's session transcripts to the registry, see what is backed
up, and read a session back. Each session is filed under the [project](../guides/projects.md) of the folder
it ran in. `protostar sessions` on its own prints its help; use one of the commands
below.

These options are shared by `push`, `status` and `list` (`show` takes `--registry`
and `--harness`, since it reads only the registry):

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
| `--json` | Print one JSON record per transcript instead of progress and a table. See [JSON output](json-output.md#sessions-push). |

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
| `--json` | Print one JSON record per transcript instead of a table. See [JSON output](json-output.md#sessions-status). |

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
| `--json` | Print one JSON record per session instead of a table, for agents and scripts. |

With `--json`, each row is the registry's record of the session, with its stable
`id` first. A row for a session not fully backed up has a `pending` object added,
and a row the registry does not have at all (made from the file on this machine)
has no title and zero counts. A registry error goes to standard error as an error
object with a `code`, so standard output stays valid JSON. The fields and codes are
in [JSON output for agents and scripts](json-output.md).

```console
$ protostar sessions list --json -n 1
[{"id":"00000000-0000-0000-0000-000000001113","harness":"claude-code","key":"00000000-0000-0000-0000-000000001113","kind":"session","title":"hello 2","firstPrompt":"hello 2","projectId":"84ae7ab4-363a-4328-b80c-81c5f6495764","project":"github.com/example/demo-app","workingDirectory":"/home/alice/src/demo-app","machineId":"e43c5bac-e1ac-4493-b2fb-21abbca63a1b","machineName":"vm-dev-01","sourcePath":"/home/alice/.claude/projects/-home-alice-src-demo-app/00000000-0000-0000-0000-000000001113.jsonl","userMessages":1,"assistantMessages":1,"length":2511,"subagentCount":0}]
```

## `protostar memory`

Back up the memory your agents keep about each project, and bring it to the same
project on another machine. Claude Code keeps it in
`<config root>/projects/<folder>/memory/`, one markdown file per note plus a
`MEMORY.md` index. Each memory folder is filed under the
[project](../guides/projects.md) of the checkout it belongs to, found from the
working directory a session transcript in the same folder records (the folder's
name is never decoded), so memory and sessions share a project. `protostar memory`
on its own prints its help.

Every subcommand takes:

| Option | Description |
|---|---|
| `--registry <URL>` | Registry to use. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |
| `--harness-home <DIR>` | Override the harness config root. |

Some memory folders are reported as skipped rather than backed up:

- a folder whose transcripts record no working directory (or that has none), since
  its project cannot be known. A folder with no transcript of its own borrows one
  from a worktree or subfolder session only when that session's checkout is the
  one the folder is named after;
- a second memory folder of the same checkout: worktrees share their main
  worktree's memory, which is backed up once;
- any file over 256 KiB. Memory notes are a few KiB; the limit only stops something
  that is not a note.

### `protostar memory push`

Back up the memory files that are new, changed or deleted since the last backup.
An unchanged file costs a stat and no network call, so a second push sends
nothing. A changed file becomes a new version in the registry and the earlier
versions are kept. A file deleted on this machine is never deleted from the
registry: it is marked deleted on this machine and keeps its versions.

```console
$ protostar memory push
╭─────────┬────────────────────────┬──────────┬─────────┬───────────╮
│ Project │ Folder                 │ Uploaded │ Deleted │ Result    │
├─────────┼────────────────────────┼──────────┼─────────┼───────────┤
│ alpha   │ /home/alice/src/alpha  │        3 │       0 │ backed up │
│ beta    │ /home/alice/src/beta   │        2 │       0 │ backed up │
╰─────────┴────────────────────────┴──────────┴─────────┴───────────╯
Skipped memory folder -tmp-scratch: No transcript records the folder it belongs
to, so its project is unknown.
Backed up 5 files for 2 projects from vm-dev-01. Registry: https://localhost:7443.
```

The exit code is `0` when every memory folder that could be backed up was, and `1`
otherwise. Skipped folders and files are reported but do not fail the push.

The capture hooks' background push (see [`capture`](#protostar-capture)) also backs
up the memory of the session's project, so memory goes up as agents write it.

### `protostar memory status`

Show which memory files are not backed up, and which conflicts a pull left for you
to resolve. It works offline: it compares the files on disk with what this machine
last recorded as sent.

```console
$ protostar memory status
Memory for 2 projects on this machine, 5 files. All backed up.
```

A conflict stays listed while its copy (`<name>.from-<machine>.md`, see
[`memory pull`](#protostar-memory-pull)) is in the memory folder. Merge what is
worth keeping into the file it is beside, then delete the copy.

The exit code is `0` when nothing is pending and no conflict is left, and `1`
otherwise.

### `protostar memory pull`

Bring the registry's memory to each project checked out on this machine, so a new
agent session there starts with what was learned elsewhere, even at a different
path or on another OS.

The registry knows a project by its facts (root commit, remotes), not by where it
is on this machine, so pull looks for local checkouts in two places only:

- the working directories your harness's session transcripts record (every folder
  you have opened in Claude Code here);
- folders you name with `--scan <DIR>` (repeatable), and their direct subfolders
  that are git checkouts. No deeper, and the disk is never crawled.

Each checkout is matched to a project the same way its sessions are (see
[projects](../guides/projects.md)). Memory is written to the memory folder of the
checkout's main worktree, named by encoding its path the way Claude Code does, and
created when Claude Code has never opened the project here. Two checkouts of one
project at different paths each get the memory.

Pull never loses a change made on this machine. For each file it compares three
things: the file here, the registry's current version, and the version this machine
last synced (what it last pushed or pulled):

- **New on either side:** a file only the registry has is written; a file only
  this machine has is left for `memory push`.
- **Changed elsewhere only:** a file unchanged here since this machine last synced
  it is updated to the newer version, when that version was edited from the one
  this machine has (`memory push` tells the registry which version each file was
  edited from). The version it had stays in the registry. A version another
  machine made without ever having this one counts as changed on both sides.
- **Changed here only:** left as it is for the next `memory push`.
- **Changed on both sides:** this machine's file is kept, and the other machine's
  version is written beside it as `<name>.from-<machine>.md` (for example
  `notes/port.from-laptop.md`; the machine name keeps letters, digits, `-` and `_`,
  and `-2`, `-3` are added when a different copy already has the name). A pointer
  line to it goes at the top of `MEMORY.md`, so an agent reads it, and the pull
  reports `conflicted (here and on laptop)`. Nothing is merged by a model: merge
  it yourself, or ask an agent to, then delete the copy. Pulling again makes no
  second copy.
- **`MEMORY.md` changed on both sides:** merged line by line. Its lines are
  one-line pointers, so the result is every line this machine has, in its order,
  then each line from the other machine this one lacks. No line from either side
  is dropped. This rule applies to `MEMORY.md` only, by name; every other file
  conflicts as above. When the merged index is longer than Claude Code loads into
  a session (200 lines or 25 KB), the pull says so.
- **Deleted here, changed elsewhere since:** the change wins and the file comes
  back, with a message. Deleted here and not changed elsewhere: not brought back.
- **Deleted elsewhere, still here:** kept, and reported. Deleted on every machine:
  not written.

The merge happens on the machine that pulls. The other machine receives the merged
file the next time this one pushes (the capture hooks push at the end of each
session) and it pulls.

Conflict copies stay on this machine. `memory push` never sends them, and their
pointer lines are left out of the `MEMORY.md` it sends, so another machine is never
handed a pointer to a file it does not have. A note of your own whose name has the
same shape (`x.from-name.md`, no dot in the name) is treated as a copy too.

Files are written through a temporary file and a move, never through a symbolic
link, and only inside the memory folder. Each file written is recorded as backed
up, so the next `memory push` does not send it back. Projects with memory but no
checkout here are listed as skipped.

```console
$ protostar memory pull
Pulled memory for 1 project: 1 written, 1 updated, 0 already here, 1 conflicted, 1 project not on this machine.
  alpha /home/alice/work/alpha: 1 written, 1 updated, 0 already here (/home/alice/.claude/projects/-home-alice-work-alpha/memory)
    MEMORY.md: changed here and on laptop; merged line by line, 1 line added.
    notes/port.md: conflicted (here and on laptop). Kept this machine's; laptop's is beside it as notes/port.from-laptop.md, listed in MEMORY.md. Merge it by hand, then delete the copy.
  Skipped beta: not on this machine. Clone it, open it in your harness or name its folder with --scan, then pull again.
```

| Option | Description |
|---|---|
| `--scan <DIR>` | Also look for checkouts in this folder and its direct subfolders. Repeatable. |

The exit code is `0` unless a memory folder could not be written. Conflicts and
skipped projects do not fail the pull.

### `protostar sessions show`

Read a backed-up session as a conversation, from any machine you are signed in on.
Give its id from `protostar sessions list`, or enough of the start of it to name
one session (case does not matter); a subagent's full id works too. Prompts and
replies are shown in order. Each tool call is one line (the tool and what it acted
on), and so is each subagent, through the call that started it:

```console
$ protostar sessions show 7d3c9a52
List files and summarise README
github.com/example/demo-app · vm-dev-01 · claude-code · 2026-11-19 04:00 to 2026-11-19 04:01
7d3c9a52-1f4e-4b8a-9c0d-2e6f8a1b3c4d
Skills used
  code-review                   2 uses  v3?
  user-stories                   1 use  v1?  not installed here
  old-helper                     1 use       deleted from the registry, not installed here
  b-fac:orchestrated-delivery    1 use       plugin
  ? the version in use then, inferred from when the skill's versions were pushed

You 04:00
  List the files here, then ask a helper to summarise README.md. My token is [redacted:github-token]

Assistant 04:00
  I'll list the files first.
  ▸ Bash ls -la
  ▸ Task subagent general-purpose: Summarise README
  There are two entries, README.md and src. The helper says the README describes a demo app.
```

Secrets the registry recognises (tokens, keys, passwords) are shown as
`[redacted:<type>]`. The backup itself keeps the original bytes. A registry that
does not list the `redacted-read` capability, including one too old to list
capabilities at all, is refused rather than trusted to redact (see
[troubleshooting](troubleshooting.md#registry-too-old)).

**Skills used** lists the skills the session ran (its subagents' too), with how
many times. The version is the registry's guess, marked `?`: the newest version
pushed before the skill was last used. Each one is also marked when it applies:

- **not installed here**: this machine has no skill of that name where the
  session would load it. Your personal skills count (`~/.claude/skills`, or under
  `--harness-home`), and so do the project skills of the folder you run the command
  in (`<project>/.claude/skills`), but only when that folder is the session's
  project. Checking that asks the registry once, and only when a skill is found
  nowhere but there.
- **deleted from the registry**: the skill was deleted there since. A local copy
  can still be installed.
- **not in the registry**: it was never pushed, so it has no version.
- **plugin**: a plugin's skill (`plugin:skill`). Plugins are not checked, so
  these are never marked "not installed here".

**Agents used** lists your own custom agents the session delegated to (its
subagents' too), with how many times, the version marked `?`, and whether the
agent matched is `personal` or `project` (a project agent of that name wins, as
Claude Code loads them). The version is the newest pushed by the time the session
ended, since protostar backs agents up when a session ends. Built-in agent types
(`general-purpose`, `Explore`, `Plan`) and plugin agents are never listed.
**deleted from the registry** marks an agent deleted since. "Agents used: none of
yours" means the session delegated to none of your agents.

A registry that does not offer these (it does not list `session-skills`, or
`session-agents`) shows the session without that block. One too old to list its capabilities is asked
anyway, and a refusal is ignored the same way. Listing the skills costs one
request, made while the conversation downloads.

With `--full`, each tool call's input and result follow it, and each subagent's
own conversation is nested, marked with a bar, right after the call that ran it:

```console
$ protostar sessions show 7d3c9a52 --full
...
  ▸ Task subagent general-purpose: Summarise README
      {
        "description": "Summarise README",
        "prompt": "Read README.md and summarise it in one line.",
        "subagent_type": "general-purpose"
      }
    ◂ result
      The README describes a demo app.
┌ Subagent general-purpose: Summarise README 7d3c9a52-1f4e-4b8a-9c0d-2e6f8a1b3c4d.agent-a1b2c3d4e5
│
│ Task 04:00
│   Read README.md and summarise it in one line.
...
└
```

A session is shown as it downloads, so even a very large one starts at once and
uses little memory. When standard output is a file or a pipe rather than a
terminal, the conversation is written as plain text: no colour, and long lines
are left whole instead of wrapped at 80 columns. An id that matches nothing says so and suggests
`protostar sessions list`. A prefix that matches several sessions lists them,
and you give more of the id. Either way the exit code is `1`. A prefix of 4
characters or more is looked up in one request on a registry that lists
`transcript-prefix-lookup`; a shorter one, or any prefix on an older registry,
is matched by reading the session list a page at a time.

| Option | Description |
|---|---|
| `--registry <URL>` | Registry to use. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |
| `-H, --harness <ID>` | Only look in this harness's sessions. |
| `--full` | Also show tool call inputs and results, and subagents' conversations where they ran. |
| `--harness-home <DIR>` | Look for this machine's skills under this harness config root instead of the default (`~/.claude`). |
| `--json` | Print a session record, then one JSON record per message (JSON Lines), instead of the conversation. See [JSON output](json-output.md#sessions-show). |

## `protostar search <QUERY>`

Find a session or a memory file by what it says, across everything backed up from
every machine: "the session where we decided X", or "what did we learn about DNS
in any project". The registry does the searching. Each hit shows its kind, its
title (or the memory file's path), its project, machine and date, a session's
short id, and the text around the match with the matched words highlighted:

```console
$ protostar search '"parentUuid ordering"'
memory   replay.md
         github.com/example/demo-app · vm-dev-01 · 2026-11-20 01:08
         Replay Transcripts replay in parentUuid ordering, not file order

session  Fix transcript replay order
         github.com/example/demo-app · vm-dev-01 · 2026-11-19 04:03 · 7d3c9a52
         token [redacted:github-token] to fetch the branch, then fix it. We
         decided on parentUuid ordering: each line follows the line its
         parentUui...
```

Open a session it finds with `protostar sessions show <id>`. Hits come best match
first, then most recent; a session and its subagents each count once.

The query uses web search syntax: every word is required, `"quoted words"` must
appear as a phrase, `or` gives an alternative and `-word` leaves out what has it.
Words match by their English stem and ignore case. Quote the whole query for your
shell. Sessions are searched by what you typed, the assistant's replies and the
names of the tools it called (not tool inputs or results), and memory by each
file's current version.

Secrets the registry recognises are redacted before anything is searched, so a
token's value finds nothing and a snippet shows `[redacted:<type>]` in its place.

```console
$ protostar search kubernetes
No matches for 'kubernetes'.
```

No match exits `0`. A registry too old for search says so, and the command exits
`1`.

| Option | Description |
|---|---|
| `-k, --kind <KIND>` | Only `session` (sessions and their subagents) or `memory`, or `agent` to search your custom agents instead (the registry must list `agent-search`). Sessions and memory by default. |
| `-p, --project <ID\|NAME\|.>` | Only this project: its id, text in its name (which may match several projects, as in `sessions list`), or `.` for the project this folder belongs to. |
| `-n, --limit <N>` | How many to show, from 1 to 100. Defaults to 20. |
| `--json` | Print one JSON array of hits instead of the list. See [JSON output](json-output.md#search). |
| `--registry <URL>` | Registry to use. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |

`--project .` resolves this folder the way a backup files its sessions (see
`protostar project`), and prints which project that is first. A folder with
nothing backed up yet has no project, so it finds nothing.

```console
$ protostar search '"DNS records"' --kind memory --project homelab --json
[{"kind":"memory","path":"dns.md","projectId":"ecb56db5-bb0f-41d1-8a7f-4b476620727c","project":"github.com/example/homelab","machineId":"178aa838-787d-4a5c-82e3-009c277ccec5","machineName":"vm-dev-01","date":"2026-11-20T07:08:23.134151+00:00","snippet":"«DNS»\n\n«DNS» «records» are added with the Technitium API, never by editing zone files"}]
```

## `protostar mcp`

Serve your backed-up sessions, memory and custom agents to coding agents as MCP tools, over
standard input and output. Register it with your agent rather than running it
yourself:

```console
$ claude mcp add --scope user protostar -- protostar mcp
```

It serves six read-only tools, `search`, `list_sessions`, `get_session`,
`get_session_skills`, `get_memory` and `get_agent`, with no tool that changes anything. Everything they return is
redacted by the registry, and one result is at most 80,000 characters, with long
sessions paged. It uses the stored sign-in, resolved on every call, and keeps
standard output for the protocol alone. See
[Let your agents search your history](../guides/agents-search-history.md).

| Option | Description |
|---|---|
| `--registry <URL>` | Registry to use. Defaults to `PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. |

## `protostar project`

See which [project](../guides/projects.md) a folder belongs to, and link or unlink
it. A project is what sessions and project skills are filed under, the same on
every machine. With no subcommand, `protostar project` is `protostar project show`.

These options are shared by `push`, `status` and `list` (`show` takes `--registry`
and `--harness`, since it reads only the registry):

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

Project skills you pulled into the folder while it was linked stay the linked
project's skills after an unlink while you leave them as they are: `skills remote`
lists them under that project, `skills pull` keeps them up to date from it, and
`skills push` reports them unchanged. Once you change one here (or the linked
project no longer has it, or this folder's own project has a skill of that name),
it belongs to this folder's project instead: `skills remote` shows it as only here,
and `skills push` backs it up as a new skill of this folder's project.

`unlink` (and a `link` that changes the folder's project) lists these skills and
what becomes of each, in place of **Skills moved** when none moved:

```console
$ protostar project unlink
Unlinked this folder. It now belongs to notes.
Folder         /home/alice/notes
Project        notes
...
2 skills of project github.com/example/demo-app stay in this folder. They synced here as that project's and did not move with the folder:
  deploy  unchanged here, so it stays github.com/example/demo-app's skill and
          `protostar skills pull` keeps it up to date from there. If you change
          it here, the next backup (the capture hook, `protostar skills push` or
          `protostar setup`) backs it up as a new skill of notes instead.
  lint    changed here since it was pulled, so it now belongs to notes: the next
          backup (the capture hook, `protostar skills push` or `protostar
          setup`) backs it up there as a new skill.
```

Other cases say that this folder's project has its own skill of that name (run
`skills remote` to compare them), that the linked project no longer has it,
or that it was deleted or renamed in the registry. Only skills are listed; agents
follow their own rule (cli#133). When the capture hook later backs such a copy up
into this folder's project, its line names the project, and the line is also
written to `~/.protostar/capture.log` and shown as a note by `protostar status`.

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
the upload. The same background push then backs up the memory of the session's
project (its own folder and its main worktree's), sending only files that changed. A `Stop` push is skipped when the session's last push started less
than 30 seconds ago; `SessionEnd` always pushes. Only one push of a transcript
runs at a time. A background push that fails (registry unreachable, not signed in)
marks its session in the local ledger; the next background push of any session
that reaches the registry also sends up to three marked sessions, oldest failure
first, and clears each mark once that session is fully backed up. Sessions that
were never backed up but never failed are left to `protostar sessions push`.
`SessionEnd` also compares your custom agents (personal ones, and the session
folder's project's) with what was last backed up, without contacting the registry.
When one changed, it starts a background `protostar agents push` for them; when none
did, nothing more happens. Other hook events are acknowledged and ignored for now.

The hook never blocks or fails your session: it gives up after 15 seconds,
counted from when it starts reading the event from the harness, and always exits
`0`. A harness that never finishes sending the event gets a logged line saying so
and nothing backed up. It writes one line to the session transcript, and problems you
may need to act on (not signed in, registry unreachable, skill refused, a skill
backed up into a different project than it last synced in) are also appended to
`~/.protostar/capture.log`, by the hook or by the background push.

Each skill backup and each background push also appends one JSON line to
`~/.protostar/capture-records.jsonl`, which [`status`](#protostar-status) reads:
when it finished (`at`), the `kind` (`skills`, `sessions`, `memory`), whether it
worked (`ok`), the `registry`, how many `items` it handled, the session `bytes`
sent, and for a failure the `reason` (`notSignedIn`, `sessionExpired`,
`registryUnreachable`, `refused`, `other`) and the `error` message. A skill backup
that worked but filed a copy in a project other than the one it last synced in
(after a `project unlink`) also carries a `note`, the hook's line, which `status`
shows under the **Skills** row. Records hold no
transcript or memory content. A run that tried nothing (a throttled `Stop`, a
project with no memory, an ignored event) writes none. Writing a record is best
effort and takes no lock; at 256 KiB the file is renamed to
`capture-records.1.jsonl`, replacing the previous one, so the two stay under about
512 KiB.

| Option | Description |
|---|---|
| `--hook <EVENT>` | The harness event that triggered capture (e.g. `PostToolUse`, `SessionStart`). |
