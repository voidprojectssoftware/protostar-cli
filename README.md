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

> Requires a published release. (The CI that builds and publishes release binaries is upcoming work.)

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

## Repository layout

```text
protostar/
├─ src/
│  └─ Protostar.Cli/    # the `protostar` CLI (Spectre.Console.Cli); `install`/`uninstall` commands
├─ scripts/
│  ├─ install.ps1       # curl-able release installer (Windows)
│  └─ install.sh        # curl-able release installer (Linux/macOS)
└─ protostar.sln
```
