---
sidebar_position: 2
title: Configuration & file locations
---

# Configuration & file locations

Where protostar stores things, and the environment variables that change its
behavior.

## Environment variables

| Variable | Affects | Default |
|---|---|---|
| `PROTOSTAR_REGISTRY_URL` | The registry every command (hooks included) talks to | the saved default, then `https://localhost:7443` |
| `PROTOSTAR_CONFIG_DIR` | Where your session/credentials are stored | `~/.protostar` |
| `PROTOSTAR_HARNESS_ROOT` | The harness config root `install-hooks` writes to | the harness default (e.g. `~/.claude`) |
| `CLAUDE_CONFIG_DIR` | Claude Code's own config root, honored when set | `~/.claude` |

A `--flag` always wins over the matching environment variable. For example,
`--registry` overrides `PROTOSTAR_REGISTRY_URL`, and `--harness-home` overrides
`PROTOSTAR_HARNESS_ROOT`.

### Registry resolution order

Every command picks its registry the same way, including the capture hooks that
Claude Code runs in the background:

1. `--registry <URL>` (command flag)
2. `PROTOSTAR_REGISTRY_URL`
3. The default saved in `~/.protostar/config.json`
4. The built-in default, `https://localhost:7443`

`protostar setup` and `protostar auth login` save the registry you signed in to as
the default once sign-in succeeds (setup also saves it when you were already
signed in). That is how hooks reach your registry: the hook commands written to
the harness settings carry no URL, and the shell Claude Code starts from usually
does not export `PROTOSTAR_REGISTRY_URL`.

`protostar auth logout` keeps the saved default. It says which registry to use,
not who is signed in, so after a logout the next `auth login` and every hook
still go to the same registry. Sign in to another registry, or edit the file, to
change it.

`protostar auth status` prints where the registry it checked came from:

```console
$ protostar auth status
Logged in to https://registry.example as alice.
Registry from /home/alice/.protostar/config.json (saved by setup or auth login).
```

### Harness root resolution order

When `install-hooks` decides which directory to write to, it checks, in order:

1. `--harness-home <DIR>` (command flag)
2. `PROTOSTAR_HARNESS_ROOT` (applies to any harness)
3. The harness's own variable (e.g. `CLAUDE_CONFIG_DIR` for Claude Code)
4. The harness default (e.g. `~/.claude`)

## Files protostar reads and writes

| Path | What it is |
|---|---|
| `~/.protostar/credentials.json` | Your saved registry session (tokens). Owner-only permissions: `0600` on Unix, the per-user profile ACL on Windows. Override the directory with `PROTOSTAR_CONFIG_DIR`. |
| `~/.protostar/config.json` | Your default registry, written by `setup` and `auth login`: `{ "registry": "https://registry.example" }`. Read leniently: a missing or malformed file means no default, and unknown fields are ignored and kept when protostar rewrites it. An invalid URL in `registry` is an error that names the file. Lives under `PROTOSTAR_CONFIG_DIR` when that is set. |
| `~/.protostar/skill-ledger.json` | What protostar last backed up or pulled for each skill, per registry, so an unchanged skill is not uploaded again. |
| `~/.protostar/sessions/` | How much of each session transcript has reached each registry (one file per transcript), so a push sends only what is new. |
| `~/.protostar/machine.json` | This machine's id and name, which sessions are filed under. The id is what `sessions list --machine` takes. |
| `~/.protostar/capture.log` | Problems from the capture hooks and their background pushes that you may need to act on. |
| `<harness>/settings.json` | Your harness settings. `install-hooks` surgically adds or removes only its own `hooks` entries here and leaves the rest untouched. For Claude Code this is `~/.claude/settings.json`. |
| The install directory | Where the `protostar` binary lives. Defaults to `%LOCALAPPDATA%\Programs\protostar` on Windows and `~/.local/bin` on Linux/macOS, added to `PATH`; override with `protostar install --dir`. |

:::tip Keep separate sessions
Because `PROTOSTAR_CONFIG_DIR` relocates the whole credential store and the saved
default registry, you can keep independent sign-ins per registry or per project by
pointing it at different directories.
:::

## Safety properties worth knowing

- **Non-destructive harness edits.** Hook installation parses your existing
  `settings.json`, changes only protostar's own entries, and refuses to write if
  the file is not valid JSON.
- **Idempotent.** Re-running `install-hooks` reproduces identical output rather
  than duplicating entries, so it is safe to run in setup scripts.
- **No secrets on disk during sign-in.** Auth uses PKCE; only the resulting
  session tokens are stored, under owner-only permissions.
