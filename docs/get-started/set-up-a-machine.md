---
sidebar_position: 2
title: Set up a machine
---

# Set up a machine in one command

Once the binary is [installed](./installation.md), one command gets a machine
ready to work:

```console
$ protostar setup
```

It runs these steps in order and prints one line for each: **done**,
**already done**, **skipped** (with the reason), or **failed**.

| Step | What it does | Same as |
|---|---|---|
| Sign-in | Signs you in to the registry in your browser, unless you already have a session the registry accepts | `protostar auth login` |
| Hooks | Installs capture hooks into every harness it finds, so your skill use and sessions are captured | `protostar install-hooks --yes` |
| Skills | Backs up this machine's skills, then installs or updates your skills from the registry | `protostar skills push`, then `protostar skills pull` |
| Configuration | Not available yet: syncing harness configuration is still to come, so this step always says so | |
| Sessions | Offers to back up the sessions already on this machine | `protostar sessions push` |

Hooks go in before skills are pulled, so a skill you use the moment it arrives
is already captured, and it is not pushed straight back as a change.

Within the skills step, this machine's skills are pushed before the registry's
are pulled. A skill you changed here is backed up first, so it reaches your
other machines instead of being held back as a local change. When the registry
refuses a push because that skill also changed on another machine since you
last synced it, nothing is overwritten on either side. The step reports the
skill as changed here and in the registry, keeps your copy, and setup still
finishes; `protostar skills remote` shows how to keep yours, theirs, or both.

```console
$ protostar setup --yes
Opening your browser to sign in. If it doesn't open, use the URL below, then return here.
https://localhost:7443/connect/authorize?...
Sign-in: done. Signed in to https://localhost:7443 as alice.
Hooks: done. Claude Code: wrote capture hooks (~/.claude/settings.json)
Skills: done. Pushed: 1 backed up, 0 already backed up. Pulled: 2 installed, 0 updated, 1 already up to date.
  brew: new version v1
  greet-user: installed v1
  tidy-imports: installed v1
Configuration: skipped. Not available yet: syncing harness configuration is still to come.
2 sessions, 817.6 KB not backed up yet. Backing them up now, newest first; Ctrl+C stops cleanly and the next push carries on.
...
Sessions: done. Backed up 2 sessions (817.6 KB).
  The capture hooks back up new activity from here on.
This machine is set up. Running `protostar setup` again changes nothing.
```

## Backing up your existing sessions

A machine you have used for a while can hold gigabytes of session history, so
setup never starts that upload without a yes. It shows how much is waiting, then:

- on a terminal, it asks `Back up now?` (the default is no);
- with `--yes`, it backs up straight away, with a progress bar. Ctrl+C stops it
  cleanly, and `protostar sessions push` carries on later;
- with no terminal (a script, a pipe) and no `--yes`, it skips the backup and
  prints the exact `protostar sessions push` command to run.

Whichever you choose, the installed hooks back up new activity from then on.

## Running it again

Every step is safe to repeat. A second run changes nothing and says so:

```console
$ protostar setup
Sign-in: already done. Signed in to https://localhost:7443 as alice.
Hooks: already done. Claude Code: capture hooks already up to date (~/.claude/settings.json)
Skills: already done. Pushed: 0 backed up, 3 already backed up. Pulled: 0 installed, 0 updated, 3 already up to date.
Configuration: skipped. Not available yet: syncing harness configuration is still to come.
Sessions: already done. All 3 transcripts backed up.
  The capture hooks back up new activity from here on.
This machine is already set up. Nothing changed.
```

Setup exits `0` when no step failed (a skipped step is not a failure) and `1`
otherwise. Steps that need the registry are skipped when sign-in fails, and the
rest still run, so one run tells you everything that needs attention.

## Options

| Option | Description |
|---|---|
| `--registry <URL>` | The registry to use. Defaults to `$PROTOSTAR_REGISTRY_URL`, then the saved default, then `https://localhost:7443`. Saved as the default once you are signed in, so hooks use it too. |
| `-y`, `--yes` | Back up this machine's sessions without asking. |
| `--no-browser` | Print the sign-in URL instead of opening a browser (for SSH or headless machines; see [Authenticate](./authenticate.md#headless-machines)). |
| `--provider <NAME>` | Skip the registry's sign-in chooser and go straight to this provider (for example `github`). |
| `--harness-home <DIR>` | Use a non-default harness config root, as [install-hooks](./connect-your-harness.md#pointing-at-a-non-default-harness-location) does. |

Project skills are pushed and pulled the same way `skills push` and `skills pull`
handle them: only those of the project the current folder belongs to. Run setup
from your home folder to sync global skills only.

The pages that follow explain each step on its own, for when you want to run one
by itself.
