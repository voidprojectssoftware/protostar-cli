# protostar

Live, continuous refinement of agent skills — the opposite of the "install a versioned
plugin" model. Skills are treated as evolving organisms, not packages: you use them, they sync
to a registry, an engine refines and merges them, and improvements are offered back to you
inside your harness — without the friction of version-control deploys.

The loop: **use → sync → refine → suggest → adopt → use.**

## Status

Built incrementally, one ticket at a time (Jira project `PROT`). The first component is the
**CLI** (`protostar`) — a self-contained binary you can run without installing the .NET runtime.

## Install the CLI

The CLI is a self-contained binary — no .NET runtime needed to run it. Install the latest release
with a one-liner:

**Windows (PowerShell)**

```powershell
irm https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.ps1 | iex
```

**Linux / macOS**

```bash
curl -fsSL https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.sh | sh
```

These download the right binary from the latest [GitHub release](https://github.com/voidprojectssoftware/protostar/releases)
and run `protostar install`, which places `protostar` in a per-user directory and adds it to PATH.
Restart your shell, then:

```console
$ protostar
protostar v0.1.0
Live, continuous refinement of agent skills.

Run protostar --help to see available commands.

$ protostar --version
0.1.0
```

> The version is derived from git tags at build time (via MinVer), not hardcoded. A binary built
> from a tagged commit reports that tag (e.g. `0.1.0`); a local build from an untagged commit reports
> a pre-release like `0.1.0-alpha.0.4`. See [Releasing](#releasing).

### Channels: stable vs edge

`stable` (the default above) installs the latest tagged release. `edge` installs the rolling
prerelease rebuilt from the tip of `main` on every code change — handy for trying unreleased fixes.

**Linux / macOS**

```bash
curl -fsSL https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.sh | sh -s -- --channel edge
```

**Windows (PowerShell)** — fetch then invoke with `-Channel` (the piped one-liner can't take params):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/voidprojectssoftware/protostar/main/scripts/install.ps1))) -Channel edge
```

Edge binaries report a prerelease version like `0.2.0-alpha.0.7`. Re-run the same command to update.

### Already have the binary?

If you downloaded `protostar` directly, it installs itself:

```console
$ protostar install              # copy into a per-user dir + add to PATH
$ protostar install --dir <DIR>  # custom location
$ protostar install --no-modify-path
$ protostar uninstall            # remove it
```

> Startup is currently JIT (self-contained, untrimmed); making the binary lean and fast is tracked
> as a separate performance-tuning unit of work.

## Authenticate to the registry

Sign in so your synced skills are tagged to you. `protostar auth login` opens your browser,
you sign in (the registry federates this to GitHub), and the resulting session is stored in your
OS credential store. The flow uses the OAuth Authorization Code grant with PKCE over a loopback
redirect — no secret is kept on disk.

```console
$ protostar auth login
Opening your browser to sign in. Complete the sign-in there, then return here.
Signed in to https://registry.example as alice.

$ protostar auth status
Logged in to https://registry.example as alice.

$ protostar auth logout
Signed out of https://registry.example.
```

By default the registry shows a sign-in chooser so you can pick how to authenticate. Pass
`--provider <name>` (e.g. `--provider github`) to skip the chooser and go straight to that provider.

Point the CLI at a registry with `--registry <url>` or the `PROTOSTAR_REGISTRY_URL` environment
variable. Use `--no-browser` on a headless machine to print the sign-in URL instead of opening a
browser. The CLI checks API compatibility with the registry on connect and refuses to proceed
against an unsupported major version.

Sessions are stored in `~/.protostar/credentials.json` (owner-only permissions: `0600` on
Unix, the per-user profile ACL on Windows). Override the directory with `PROTOSTAR_CONFIG_DIR`.

## Build from source

Requires the .NET 10 SDK.

```bash
dotnet build                              # build the solution
dotnet run --project src/Protostar.Cli    # run the CLI in place

# produce a self-contained binary and self-install it:
dotnet publish src/Protostar.Cli -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -o out
./out/protostar install
```

## Develop

For manual testing there is a thin dev runner so you do not retype the project path. `pstar <args>`
is equivalent to `protostar <args>`, building in place first:

```powershell
.\pstar.ps1 --help                          # Windows / PowerShell
.\pstar.ps1 install-hooks --yes --dry-run
```

```bash
./pstar.sh --help                           # Linux / macOS
./pstar.sh install-hooks --yes --dry-run
```

**Installing a dev build.** `protostar install` from a local build (what `pstar` runs) copies the
whole build output and produces a *framework-dependent* install: it works on any machine with the
.NET runtime (so, your dev box), but is not portable. For a standalone binary that needs no runtime,
publish a self-contained single file first (see "Build from source"), then `install` that.

**Testing hook/install commands safely.** `install-hooks` (and `install`) write into your real
`~/.claude` by default. To exercise them without touching it, point the harness at a throwaway
scratch dir — the CLI resolves every harness path from `PROTOSTAR_HARNESS_ROOT` (and you can also
pass `--harness-home <DIR>` per command):

```powershell
$env:PROTOSTAR_HARNESS_ROOT = "$PWD\.dev\harness"   # scratch; .dev/ is gitignored
.\pstar.ps1 install-hooks --yes
Get-Content .dev\harness\settings.json              # inspect what was written
.\pstar.ps1 install-hooks --yes --remove            # tear it back out
```

`.dev/` is gitignored, so scratch installs and harness fixtures never get committed. To run the
acceptance suite, `dotnet test` from the repo root.

> The `capture` command is invoked by an installed hook (the real binary) and reads its payload
> from stdin. Piping stdin through the dev runner can hang, because `dotnet run` does not forward
> stdin's end-of-input. To test `capture` by hand, run the built binary directly, e.g.
> `echo '{}' | ./src/Protostar.Cli/bin/Debug/net10.0/protostar capture --hook PostToolUse`.

## Releasing

Releases are automated with [release-please](https://github.com/googleapis/release-please). You never
tag by hand — you just write [Conventional Commits](https://www.conventionalcommits.org) and merge a
Release PR.

**The version flows like this:** Conventional Commit messages (`feat:`, `fix:`, `feat!:` for
breaking) tell release-please how to bump the version. release-please keeps an open "Release PR" that
bumps `version.txt` + `CHANGELOG.md`. Merging that Release PR creates the `vMAJOR.MINOR.PATCH` tag and
a GitHub Release; [MinVer](https://github.com/adamralph/minver) reads that tag at build time and
stamps it into the binaries, which the workflow attaches to the release.

**Day-to-day:**

1. Open a PR for your change. Give it a [Conventional Commit](https://www.conventionalcommits.org)
   title (e.g. `feat: add sync command`, `fix: handle missing config`).
2. **Squash-merge** it into `main`. The squash commit takes the PR title, so the title is what
   release-please reads — keep it conventional.
3. release-please opens or updates a **Release PR** ("chore: release X.Y.Z"). Review it.
4. **Merge the Release PR** when you want to ship. That tags `main`, creates the GitHub Release, and
   the `release-please` workflow builds and attaches the `win-x64`, `win-arm64`, `linux-x64`, and
   `osx-arm64` binaries. The released binaries self-report the version via `protostar --version`.

> Tags are created by release-please on `main`, so they are always reachable through history — this
> is what makes MinVer reliable regardless of squash/rebase merges. Do not tag manually.

### Two channels

protostar ships on two tracks (see [Channels](#channels-stable-vs-edge) for how to install each):

- **stable** — the release-please flow above. Tagged `vX.Y.Z`, published as a normal GitHub Release
  (so it is the `releases/latest` the default installer pulls).
- **edge** — a rolling prerelease. The `edge` workflow rebuilds on every code change to `main` and
  replaces a single prerelease under the moving `edge` tag, versioned by MinVer (`0.X.Y-alpha.0.N`).
  Because the `edge` tag does not start with `v`, MinVer ignores it, so the two channels never
  collide.

While we are pre-1.0, stable releases stay in the `0.x` range (`bump-minor-pre-major` keeps a
breaking change from jumping to `1.0.0`); cutting `1.0.0` will be a deliberate choice.

## Repository layout

```text
protostar/
├─ src/
│  └─ Protostar.Cli/       # the `protostar` CLI (Spectre.Console.Cli); `install`/`uninstall` commands
├─ scripts/
│  ├─ install.ps1          # curl-able release installer (Windows)
│  └─ install.sh           # curl-able release installer (Linux/macOS)
├─ .github/workflows/
│  ├─ release-please.yml   # stable: Release PR -> tag -> build + attach binaries
│  └─ edge.yml             # edge: rebuild tip of main -> replace rolling `edge` prerelease
├─ release-please-config.json      # release-please configuration
├─ .release-please-manifest.json   # release-please version tracker
├─ Directory.Build.props   # MinVer git-tag versioning
└─ protostar.sln
```
