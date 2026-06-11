---
sidebar_position: 2
title: Releasing
---

:::warning AI-generated and not yet reviewed
This page was drafted by AI and has not been reviewed by a human. Protostar is in
early development with limited maintainer bandwidth, so content may be incomplete
or inaccurate. Treat it as a starting point, verify anything important against the
source, and please report any problems you hit.
:::

# Releasing

Releases are automated with
[release-please](https://github.com/googleapis/release-please). You never tag by
hand — you write [Conventional Commits](https://www.conventionalcommits.org) and
merge a Release PR.

**The version flows like this:** Conventional Commit messages (`feat:`, `fix:`,
`feat!:` for breaking) tell release-please how to bump the version. release-please
keeps an open "Release PR" that bumps `version.txt` + `CHANGELOG.md`. Merging that
Release PR creates the `vMAJOR.MINOR.PATCH` tag and a GitHub Release;
[MinVer](https://github.com/adamralph/minver) reads that tag at build time and
stamps it into the binaries, which the workflow attaches to the release.

## Day-to-day

1. Open a PR for your change. Give it a
   [Conventional Commit](https://www.conventionalcommits.org) title (e.g.
   `feat: add sync command`, `fix: handle missing config`).
2. **Squash-merge** it into `main`. The squash commit takes the PR title, so the
   title is what release-please reads — keep it conventional.
3. release-please opens or updates a **Release PR** ("chore: release X.Y.Z").
   Review it.
4. **Merge the Release PR** when you want to ship. That tags `main`, creates the
   GitHub Release, and the `release-please` workflow builds and attaches the
   `win-x64`, `win-arm64`, `linux-x64`, and `osx-arm64` binaries. The released
   binaries self-report their version via `protostar --version`.

:::note Never tag manually
Tags are created by release-please on `main`, so they are always reachable through
history — this is what makes MinVer reliable regardless of squash/rebase merges.
:::

## Two channels

protostar ships on two tracks (see [Choose a channel](../guides/choose-a-channel.md)
for how to install each):

- **stable** — the release-please flow above. Tagged `vX.Y.Z`, published as a
  normal GitHub Release (so it is the `releases/latest` the default installer
  pulls).
- **edge** — a rolling prerelease. The `edge` workflow rebuilds on every code
  change to `main` and replaces a single prerelease under the moving `edge` tag,
  versioned by MinVer (`0.X.Y-alpha.0.N`). Because the `edge` tag does not start
  with `v`, MinVer ignores it, so the two channels never collide.

While we are pre-1.0, stable releases stay in the `0.x` range
(`bump-minor-pre-major` keeps a breaking change from jumping to `1.0.0`); cutting
`1.0.0` will be a deliberate choice.
