---
sidebar_position: 3
title: Connect your harness
---

# Connect protostar to your harness

For protostar to refine your skills, it first has to **see you use them**. It does
that by installing small, well-behaved hooks into your AI harness (the tool you
run your agent in, such as Claude Code). Once the hooks are in place, every skill
use is captured — the first half of the `use → sync → refine` loop.

`protostar setup` does this for you (see [Set up a machine](./set-up-a-machine.md)).
To do it on its own, it is one command:

```console
$ protostar install-hooks
```

:::note Supported harnesses
Today protostar supports **Claude Code** (`claude-code`). The set is designed to
grow — when more harnesses are supported, `install-hooks` will detect them too.
:::

## What "hooks" are

A harness like Claude Code can run a command when certain events happen. Protostar
registers four of them in your harness's `settings.json`:

| Hook event | When it fires | What protostar does |
|---|---|---|
| `PostToolUse` (matching the `Skill` tool) | Right after you use a skill | Backs the skill up if it changed. The harness says only the skill's name, so a global skill and the current project's skill of that name are both checked, each backed up as its own skill |
| `Stop` | After each reply | Backs up the new part of the session, at most once every 30 seconds |
| `SessionEnd` | When the session ends | Backs up the rest of the session |
| `SessionStart` | When a new harness session begins | Reserved for the upcoming suggestion / push-back loop (does nothing yet) |

Every hook calls `protostar capture`. The hooks are designed to be invisible in
normal use: capture **never blocks the harness** and always exits cleanly, so it
cannot slow down or break your session. The session hooks do not upload anything
themselves: they start a background `protostar sessions push` for the session and
return at once.

If you are offline, nothing is lost: what has not reached the registry stays
pending on your machine, and the next sync (the next reply, or
`protostar sessions push`) sends it. Problems you may need to act on, such as not
being signed in, are written to `~/.protostar/capture.log`.

## Install the hooks

Run the command. With a terminal attached, protostar **detects the harnesses on
your machine** and lets you choose which to wire up (press space to toggle,
enter to confirm):

```console
$ protostar install-hooks
```

:::info 📷 Screenshot slot — `img/install-hooks-prompt.png`
The interactive harness picker (the space-to-toggle list protostar shows when it
detects more than one harness). Drop the image into `docs/img/` and replace this
admonition with: `![Harness selection prompt](../img/install-hooks-prompt.png)`
:::

Prefer a non-interactive run (CI, scripts, or you just know what you want)?

```console
$ protostar install-hooks --yes          # all detected harnesses, no prompt
$ protostar install-hooks --all          # same: select every detected harness
$ protostar install-hooks -H claude-code # target one harness by id (repeatable)
```

### Preview before you write

`--dry-run` shows exactly what would change without touching any file — a good
habit the first time:

```console
$ protostar install-hooks --dry-run
```

## Verify it worked

Re-running is **idempotent**: protostar recognizes its own managed entries and
updates them in place instead of duplicating. So the simplest check is to run it
twice — the second run should report nothing to change:

```console
$ protostar install-hooks --yes
updated capture hooks  (~/.claude/settings.json)

$ protostar install-hooks --yes
capture hooks already up to date  (~/.claude/settings.json)
```

You can also confirm a hook actually fires: use a skill in your harness and look
in its transcript for a line like

```text
protostar capture: PostToolUse (1234 bytes)
```

Or open your harness's `settings.json` and look under `hooks` for two entries
whose command contains `protostar ... capture --hook`.

:::tip Your other settings are safe
Every edit is surgical: protostar reads the existing JSON, adds or replaces only
its own hook entries, and leaves all your other settings and hooks untouched. If
the file cannot be parsed as JSON, protostar refuses to touch it rather than risk
clobbering it.
:::

## Remove the hooks

To disconnect protostar from a harness, tear the hooks back out (also idempotent):

```console
$ protostar install-hooks --remove --yes
```

## Pointing at a non-default harness location

By default protostar writes to the standard config directory for each harness
(for Claude Code, `~/.claude`). To target a different location — a test sandbox,
or a non-standard install — override the root:

```console
$ protostar install-hooks --harness-home <DIR>
```

The resolution order is `--harness-home` → `PROTOSTAR_HARNESS_ROOT` →
the harness's own env var (e.g. `CLAUDE_CONFIG_DIR`) → the default. See
[Configuration](../reference/configuration.md) for the full list.

## Next step

Hooks capture your usage locally. To tag that usage to **you** so the registry can
route refinements back, sign in next:
[Authenticate to a registry](./authenticate.md).
