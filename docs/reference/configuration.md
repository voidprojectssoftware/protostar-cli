---
sidebar_position: 2
title: Configuration & file locations
---

:::warning AI-generated and not yet reviewed
This page was drafted by AI and has not been reviewed by a human. Protostar is in
early development with limited maintainer bandwidth, so content may be incomplete
or inaccurate. Treat it as a starting point, verify anything important against the
source, and please report any problems you hit.
:::

# Configuration & file locations

Where protostar stores things, and the environment variables that change its
behavior.

## Environment variables

| Variable | Affects | Default |
|---|---|---|
| `PROTOSTAR_REGISTRY_URL` | The registry `auth` commands target | `https://localhost:7443` |
| `PROTOSTAR_CONFIG_DIR` | Where your session/credentials are stored | `~/.protostar` |
| `PROTOSTAR_HARNESS_ROOT` | The harness config root `install-hooks` writes to | the harness default (e.g. `~/.claude`) |
| `CLAUDE_CONFIG_DIR` | Claude Code's own config root, honored when set | `~/.claude` |

A `--flag` always wins over the matching environment variable. For example,
`--registry` overrides `PROTOSTAR_REGISTRY_URL`, and `--harness-home` overrides
`PROTOSTAR_HARNESS_ROOT`.

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
| `<harness>/settings.json` | Your harness settings. `install-hooks` surgically adds or removes only its own `hooks` entries here and leaves the rest untouched. For Claude Code this is `~/.claude/settings.json`. |
| The install directory | Where the `protostar` binary lives. Defaults to `%LOCALAPPDATA%\Programs\protostar` on Windows and `~/.local/bin` on Linux/macOS, added to `PATH`; override with `protostar install --dir`. |

:::tip Keep separate sessions
Because `PROTOSTAR_CONFIG_DIR` relocates the whole credential store, you can keep
independent sign-ins per registry or per project by pointing it at different
directories.
:::

## Safety properties worth knowing

- **Non-destructive harness edits.** Hook installation parses your existing
  `settings.json`, changes only protostar's own entries, and refuses to write if
  the file is not valid JSON.
- **Idempotent.** Re-running `install-hooks` reproduces identical output rather
  than duplicating entries, so it is safe to run in setup scripts.
- **No secrets on disk during sign-in.** Auth uses PKCE; only the resulting
  session tokens are stored, under owner-only permissions.
