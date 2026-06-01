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

Requires the .NET 10 SDK to build. From the repo root:

```powershell
./install.ps1
```

This publishes a self-contained, single-file `protostar.exe`, installs it to
`%LOCALAPPDATA%\Programs\protostar`, and adds that directory to your user PATH. Restart your shell,
then:

```console
$ protostar
protostar v0.1.0
Live, continuous refinement of agent skills.

Run protostar --help to see available commands.

$ protostar --version
0.1.0
```

The installer takes `-Rid` (e.g. `win-arm64`, `linux-x64`), `-Configuration`, and `-InstallDir`.
The published binary needs no .NET runtime on the target machine.

> Startup is currently JIT (self-contained, untrimmed); making the binary lean and fast is tracked
> as a separate performance-tuning unit of work.

## Build from source

```bash
dotnet build                              # build the solution
dotnet run --project src/Protostar.Cli    # run the CLI in place
```

## Repository layout

```text
protostar/
├─ src/
│  └─ Protostar.Cli/    # the `protostar` CLI (Spectre.Console.Cli)
├─ install.ps1          # self-contained build + install
└─ protostar.sln
```
