---
sidebar_position: 3
title: Troubleshooting
---

# Troubleshooting

Fixes for the issues you are most likely to hit, grouped by where they show up.

## Install & PATH

### `protostar: command not found` right after installing

`PATH` changes only apply to **new** shell sessions. Close and reopen your
terminal, then try again. If you installed with `--no-modify-path`, add the
install directory to your `PATH` yourself.

### The install script can't download a binary

The one-liner pulls from the latest
[GitHub release](https://github.com/voidprojectssoftware/protostar/releases).
Check that you are online and not behind a proxy that blocks
`raw.githubusercontent.com` or `github.com`. As a fallback, download the binary
for your platform from the releases page and run `protostar install` directly —
see [Installation](../get-started/installation.md).

### First run is slow

Expected for now. The binary is self-contained and untrimmed, so startup is JIT.
A leaner, faster build is tracked as separate performance work.

## Connecting your harness

### `install-hooks` reports "already up to date" and I expected a change

That is the idempotency guarantee working: protostar found its managed entries
already present and identical, so it wrote nothing. If you changed the binary
location, pass `--exe-path` (or just re-run after moving it) to update the path
the hooks call.

### protostar refuses to edit my `settings.json`

If the file is not valid JSON, protostar will not touch it — it surfaces the error
instead of risking your settings. Fix the JSON (a trailing comma is the usual
culprit) and re-run.

### Nothing seems to happen when I use a skill

Capture currently **acknowledges** events but does not upload them yet — syncing
is not built yet. To confirm the hook is firing, check your harness transcript for
a line like `protostar capture: PostToolUse (… bytes)`.

## Authentication

### `'<' is an invalid start of a value`, or "not a protostar registry"

You pointed `--registry` at a URL that returns a **web page (HTML)** instead of
the registry API. The fix is to use the API's address. If you are running the
registry locally under .NET Aspire, the most common mistake is using the **Aspire
dashboard** port instead of the `api` resource — use the `api` URL (e.g.
`https://localhost:7443`). protostar even hints at this when it detects an Aspire
dashboard response.

### "Could not reach the registry"

The registry URL is wrong or the service is down. Verify it responds:

```bash
curl -k https://localhost:7443/v1/meta
# {"service":"protostar-registry","version":"…","apiMajors":[1]}
```

### Sign-in says the registry's API major is unsupported

The CLI and registry version independently and agree on an **API contract**. This
message means the registry only speaks a major your CLI doesn't.
[Update protostar](../guides/manage-your-installation.md) (or point at a compatible
registry). The model is explained in [Registry concepts](/registry/concepts).

### The browser never returns / sign-in times out

On a headless machine, the browser can't open. Use `--no-browser` to print the URL
and complete it elsewhere, and raise `--timeout` if you need more time. See
[Authenticate](../get-started/authenticate.md).

### HTTPS / certificate errors against a local registry

Trust the ASP.NET Core dev certificate once: `dotnet dev-certs https --trust`.
This is required because the registry refuses plain HTTP.

## Still stuck?

Open an issue in the relevant repo — the **"Edit this page"** link at the bottom
of any doc takes you to its source, and the repo's issues are linked from there.
