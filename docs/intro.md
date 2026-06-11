---
sidebar_position: 1
slug: /
title: Protostar CLI
---

:::warning AI-generated and not yet reviewed
This page was drafted by AI and has not been reviewed by a human. Protostar is in
early development with limited maintainer bandwidth, so content may be incomplete
or inaccurate. Treat it as a starting point, verify anything important against the
source, and please report any problems you hit.
:::

# Protostar CLI

`protostar` is the command-line entry point to **Protostar** — live, continuous
refinement of agent skills. It is a self-contained binary: no .NET runtime to
install, one file to run.

```console
$ protostar
protostar v0.1.0
Live, continuous refinement of agent skills.

Run protostar --help to see available commands.
```

## The idea in one minute

Most tools treat agent skills like packages: you install a versioned plugin, and
it sits frozen until you remember to upgrade it. Protostar treats skills as
**living organisms** instead. You use them in your harness, they sync to a
registry, an engine refines and merges them, and improvements are offered back to
you — without a version-control deploy in the loop.

The cycle:

```
use ──▶ sync ──▶ refine ──▶ suggest ──▶ adopt ──▶ use
```

The `protostar` CLI is the part of that loop that lives on your machine. It
hooks into your AI harness to **capture** how you use skills, **authenticates**
you to a registry so your usage is tagged to you, and (as the loop fills in) will
surface refinements back inside the harness you already work in.

### A few terms

| Term | What it means here |
|---|---|
| **Skill** | A reusable capability your agent invokes (for example, a slash-command or tool in your harness). Protostar's job is to refine these over time. |
| **Harness** | The tool you run your AI agent in. Protostar supports **Claude Code** today; more harnesses can be added. |
| **Registry** | The service the CLI syncs to and signs in against. See the [Registry docs](/registry). |

## What you can do today

Protostar is built incrementally. Here is what the CLI does right now:

| Task | Command | Guide |
|---|---|---|
| Install the binary and put it on your `PATH` | `protostar install` | [Installation](./get-started/installation.md) |
| Connect protostar to your AI harness | `protostar install-hooks` | [Connect your harness](./get-started/connect-your-harness.md) |
| Sign in to a registry | `protostar auth login` | [Authenticate](./get-started/authenticate.md) |
| Follow the stable or edge release track | install `--channel` | [Choose a channel](./guides/choose-a-channel.md) |

:::info Where the loop is today
**Capture** is live: once hooks are installed, protostar sees each skill use and
acknowledges it. **Syncing** those captures to the registry, the **refinement**
engine, and the **suggestion** push-back are not built yet. The docs call out what
is wired up versus what is coming, so you always know what to expect.
:::

## Where to go next

- **New here?** Start with [Installation](./get-started/installation.md), then
  [Connect your harness](./get-started/connect-your-harness.md).
- **Want the full command list?** See the
  [Command reference](./reference/commands.md).
- **Building or releasing the CLI?** See [Develop](./develop/build-from-source.md).

Protostar is one of several components, each developed in its own repo with its
docs next to the code. The [Registry](/registry) is the service the CLI syncs to.
