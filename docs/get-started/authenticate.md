---
sidebar_position: 3
title: Authenticate to a registry
---

:::warning AI-generated and not yet reviewed
This page was drafted by AI and has not been reviewed by a human. Protostar is in
early development with limited maintainer bandwidth, so content may be incomplete
or inaccurate. Treat it as a starting point, verify anything important against the
source, and please report any problems you hit.
:::

# Authenticate to a registry

Signing in tags your synced skills to **you**, so the registry knows whose usage
it is refining and where to route suggestions back. Protostar uses a browser-based
sign-in: you run one command, finish in your browser, and the session is stored
securely on your machine.

:::info Which registry do I sign in to?
There is **no public hosted registry yet**. Today, `auth login` is for people
running a registry themselves — most often a local development instance on
`https://localhost:7443`. If you are evaluating Protostar,
[run the registry locally](/registry/getting-started) and sign in to it. A hosted
registry (and the sync that depends on it) lands as the loop fills in. The steps
below work against any registry you point at.
:::

```console
$ protostar auth login
Opening your browser to sign in. Complete the sign-in there, then return here.
Signed in to https://localhost:7443 as alice.
```

## How sign-in works

Protostar never asks for a password. Under the hood it uses the OAuth
**Authorization Code flow with PKCE** over a loopback redirect (RFC 8252) — the
same pattern the GitHub CLI and other modern tools use:

1. The CLI opens your browser to the registry's sign-in page.
2. The registry **federates the login to GitHub**, so you sign in with the GitHub
   account you already have.
3. GitHub returns you to a one-time `localhost` URL the CLI is listening on.
4. The CLI exchanges the result for tokens and stores them locally.

No secret is ever written to disk, and the registry never sees your GitHub
password.

:::info 📷 Screenshot slot — `img/auth-signin-chooser.png`
The registry's browser sign-in chooser (where you pick GitHub). Drop the image
into `docs/img/` and replace this admonition with:
`![Registry sign-in chooser](../img/auth-signin-chooser.png)`
:::

## Choosing the registry

By default `auth login` targets `https://localhost:7443` — the address the
[local development registry](/registry/getting-started) listens on. Point at a
different one with a flag or an environment variable:

```console
$ protostar auth login --registry https://your-registry.example
```

```bash
export PROTOSTAR_REGISTRY_URL=https://your-registry.example   # bash / zsh
protostar auth login
```

```powershell
$env:PROTOSTAR_REGISTRY_URL = "https://your-registry.example"   # PowerShell
protostar auth login
```

The flag always wins over the environment variable. `auth status` and
`auth logout` read the same setting, so they target whatever registry you signed
in to.

On connect, the CLI calls the registry's `/v1/meta` endpoint to check **API
compatibility**. If the registry speaks a major version the CLI doesn't support,
it stops with an upgrade hint instead of failing halfway through sign-in. See the
[Registry concepts](/registry/concepts) for how that contract works.

## Skip the chooser

If a registry offers more than one identity provider it shows a chooser. To go
straight to one, pass `--provider`:

```console
$ protostar auth login --provider github
```

## Headless machines

On a box with no browser (a server, a container, SSH), use `--no-browser`.
Protostar prints the sign-in URL instead of trying to open one — open it on
another device, finish there, and the CLI completes when the callback returns:

```console
$ protostar auth login --no-browser
Open this URL to sign in:
https://registry.example/connect/authorize?response_type=code&...
```

You can also extend how long the CLI waits for you to finish (default 300
seconds):

```console
$ protostar auth login --timeout 600
```

:::warning Signing in to a CLI on a *remote* server
Sign-in finishes by redirecting your browser to a one-time `http://localhost:<port>`
URL that the CLI is listening on. When the CLI runs on a **remote** box but you
open the link in a browser on your **laptop**, that `localhost` is your laptop, not
the server — so the callback can't reach the CLI. Forward the port over SSH first
(the port is shown in the printed URL):

```bash
ssh -L <port>:localhost:<port> you@server   # then open the printed URL locally
```
:::

## Check your status and sign out

```console
$ protostar auth status
Logged in to https://your-registry.example as alice.

$ protostar auth logout
Signed out of https://your-registry.example.
```

When you are not signed in, `auth status` says so and exits cleanly (it works
offline — it never needs to reach the registry to tell you that):

```console
$ protostar auth status
Not logged in to https://your-registry.example.
```

## Where your session is stored

Tokens are written to `~/.protostar/credentials.json` with owner-only
permissions (`0600` on Unix; the per-user profile ACL on Windows). Change the
directory with `PROTOSTAR_CONFIG_DIR` — handy for keeping separate sessions per
registry or per project. See [Configuration](../reference/configuration.md).

:::tip Pointing at a local registry under .NET Aspire?
If you run the registry locally, point the CLI at the **`api`** resource URL from
the Aspire dashboard (e.g. `https://localhost:7443`), **not** the dashboard URL.
The dashboard returns HTML, which the CLI will reject as "not a protostar
registry." See [Troubleshooting](../reference/troubleshooting.md).
:::
