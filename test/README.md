# Tests

Two projects, split by test kind:

- **Protostar.Cli.Unit** — fast in-process unit tests that call library types directly.
- **Protostar.Cli.Acceptance** — black-box BDD scenarios that drive the real built binary.

## Protostar.Cli.Unit

In-process xUnit tests that exercise individual types directly, with no binary launched. The project
has `InternalsVisibleTo` access (the CLI's types are `internal`), so tests can reach helpers like
`ProjectLocator`, `TokenStore`, and `SelfRemoval` without making them `public`. Each test isolates
its own state (a throwaway temp dir, or a redirected `PROTOSTAR_CONFIG_DIR`) and cleans up after
itself. Keep anything that launches the binary out of here; that belongs in the acceptance suite.

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

### Harness integrations

Hook install (PROT-8) follows the redirectable-root pattern PROT-41 planned for:

- `Support/HarnessSandbox.cs` — a throwaway fake harness layout (a `.claude/` config dir) in a temp
  dir, the harness analogue of `Sandbox`. Scenarios point the CLI at it with `--harness-home`, so
  hook scenarios assert on the produced `settings.json` without touching the real harness.
- `Features/InstallHooks.feature` — uses a `Scenario Outline` with one Examples row per harness
  (just `claude-code` today) so the same template validates every supported harness as more are
  added.
- The CLI resolves the harness config dir from `--harness-home` > `PROTOSTAR_HARNESS_ROOT` >
  `CLAUDE_CONFIG_DIR` > `~/.claude`. Tests use `--harness-home`; the env vars exist for real use.
- Binary install/uninstall scenarios pass `--no-hooks` so they stay pure binary tests; the
  hook-wiring done by `install`/`uninstall` is covered by its own scenarios pointed at the fixture.

Assertions check that the written config has the expected shape (schema conformance). Actually
launching Claude Code to confirm the hook fires is a separate manual smoke, out of scope here.
