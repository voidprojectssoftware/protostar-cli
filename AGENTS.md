# AGENTS.md

Guidance for AI coding agents working in protostar-cli. This is the canonical, tool-agnostic source of
conventions, read by Codex, Cursor, Copilot, Gemini CLI, and others. Claude Code reads it through a
one-line `CLAUDE.md` that imports this file. Human-facing onboarding lives in `README.md`.

## Project

`protostar` is a .NET 10 command-line tool built on Spectre.Console.Cli. It discovers (and will sync)
agent "skills" across coding harnesses and installs capture hooks into them.

- `src/Protostar.Cli/` — the CLI (`AssemblyName` is `protostar`).
- `test/Protostar.Cli.Unit/` — xUnit unit tests over library types.
- `test/Protostar.Cli.Acceptance/` — Reqnroll BDD tests that drive the built binary as a child process.

## Build and test

```bash
dotnet build protostar.sln                                       # build everything
dotnet test test/Protostar.Cli.Unit/Protostar.Cli.Unit.csproj    # fast unit tests
dotnet test protostar.sln                                        # unit + acceptance (acceptance builds and drives the real binary)
```

## Conventions

Mechanically enforceable rules live in `.editorconfig` (Roslyn analyzers) so every editor, agent, and CI
run applies them identically. The conventions below are the ones analyzers can't express.

### Dependency injection

Commands are thin and receive business-logic services by constructor injection; they do not `new` services
up. Register a service once in `Program.cs` (`services.AddSingleton<IFoo, Foo>()`), expose it behind an
interface, and inject the interface into the command. Spectre resolves commands through the
`TypeRegistrar`/`TypeResolver` bridge in `src/Protostar.Cli/Infrastructure/`. This keeps commands
unit-testable with fakes instead of real disk or network.

### Commands and presentation

A command's `Execute` maps its `Settings` to a service call and returns `SomePresenter.Render(result, ...)`.
Commands never build tables, call `AnsiConsole`, or emit markup; the presenter owns all Spectre.Console
output and the exit code, which keeps the service a console-free, testable data source. A presenter lives
next to the service it renders in that concern's folder, not under `Commands/` (`Skills/SkillsPresenter.cs`,
`Hooks/HookInstallPresenter.cs`), and output-shaping helpers (truncation, formatting) live on it too.

### Constructors

Primary constructors are reserved for records (positional) and small lightweight types (simple wrappers,
adapters, DTOs). Logic-bearing classes use an explicit `private readonly` field plus a constructor:

```csharp
internal sealed class SkillsCommand : Command<SkillsCommand.Settings>
{
    private readonly ISkillService _skills;

    public SkillsCommand(ISkillService skills) => _skills = skills;
}
```

Use the explicit form whenever you want `readonly` enforcement on injected dependencies (a
primary-constructor captured field is not readonly), a place for guard clauses, or clarity about where
fields come from. Primary constructors offer no performance benefit (equivalent IL). `.editorconfig`
disables the Roslyn "use primary constructor" suggestion (`IDE0290`) so tooling never nudges the other way.

### XML doc comments

`<summary>` is one short sentence describing *what* a thing is; rationale and design intent go in
`<remarks>`. Document the non-obvious (units, nullability meaning, side effects, ordering), not the
signature. Inherit in the right direction: the interface or base type owns the canonical docs, and the
implementation or override carries a bare `<inheritdoc />`, which auto-resolves to the member it implements
(no `cref` needed). Reserve `<inheritdoc cref="..."/>` for links the compiler can't infer, such as a
sync/async twin.
