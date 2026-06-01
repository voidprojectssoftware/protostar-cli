# Tests

## Protostar.Cli.Acceptance

Behaviour-driven acceptance tests for the protostar CLI, written with
[Reqnroll](https://reqnroll.net) (the maintained successor to SpecFlow) and run by xUnit. Each
scenario validates the CLI **the way a user runs it**: it executes the real built binary as a child
process (via [CliWrap](https://github.com/Tyrrrz/CliWrap)) and asserts on stdout/stderr, the exit
code, and filesystem side effects. There is no mocking of the command layer.

### Layout

- `Features/*.feature` — Gherkin scenarios in user language (version, default command, help,
  install, uninstall).
- `Steps/CliSteps.cs` — step definitions binding the Gherkin to `CliRunner` + `Sandbox`.
- `Support/CliRunner.cs` — locates the protostar binary and runs it.
- `Support/Sandbox.cs` — a throwaway temp directory per scenario, so install/uninstall never touch
  the real machine. Install/uninstall scenarios pass `--no-modify-path`, so the suite never edits
  your PATH.

### Running

From the repo root:

```bash
dotnet test
```

The acceptance project has a `ProjectReference` to `Protostar.Cli`, so the CLI is built first and
its binary is discovered automatically under `src/Protostar.Cli/bin/`. To point the suite at a
specific binary instead (for example a published self-contained build), set `PROTOSTAR_BIN`:

```bash
PROTOSTAR_BIN=/path/to/protostar dotnet test
```

### Extending to harness integrations

When the CLI gains harness integration (hook install, skill discovery), make every harness path
(config dir, settings file, skills dir) redirectable via an environment variable or flag, then add
a `Support/Harness` fixture beside `Sandbox` that builds a fake harness layout in a temp dir. Use a
Gherkin `Scenario Outline` with one Examples row per harness. See PROT-41 for the full plan.
