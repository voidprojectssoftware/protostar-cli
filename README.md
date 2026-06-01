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
