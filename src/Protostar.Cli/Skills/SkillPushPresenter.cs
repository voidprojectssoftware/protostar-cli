using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Spectre.Console;

namespace Protostar.Cli.Skills;

/// <summary>
/// The console face of <see cref="ISkillPushService"/>: a per-skill table and a summary line.
/// </summary>
internal static class SkillPushPresenter
{
    /// <summary>Render a push run and return the exit code (0 when every skill reached the registry, otherwise 1).</summary>
    public static int Render(SkillPushRunResult result, IHarnessCatalog catalog)
    {
        var authority = result.Registry?.GetLeftPart(UriPartial.Authority) ?? string.Empty;
        switch (result.Failure)
        {
            case SkillPushRunFailure.InvalidRegistry:
                return Error(result.Detail!);
            case SkillPushRunFailure.UnknownHarness:
                return Error($"Unknown harness '{result.Detail}'. Known: {catalog.KnownIds}");
            case SkillPushRunFailure.Unsupported:
                return Error($"Harness '{result.Detail}' does not support skill discovery.");
            case SkillPushRunFailure.SkillNotFound:
                return Error($"No skill named '{result.Detail}' was found. Run `protostar skills` to see what is on this machine.");
            case SkillPushRunFailure.NotLoggedIn:
                return Error($"Not logged in to {authority}. Run `protostar auth login` first.");
            case SkillPushRunFailure.SessionExpired:
                return Error($"Your session for {authority} has expired. Run `protostar auth login` again.");
            case SkillPushRunFailure.RegistryUnreachable:
                return Error($"Could not reach the registry at {authority}.");
            case SkillPushRunFailure.Incompatible:
                return Error(result.Detail!);
        }

        if (result.Entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No skills found. Nothing to back up.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Scope");
        table.AddColumn("Name");
        table.AddColumn("Result");
        foreach (var entry in result.Entries)
        {
            table.AddRow(
                Markup.Escape(entry.Skill.Scope.ToString().ToLowerInvariant()),
                Markup.Escape(entry.Skill.Name),
                Describe(entry.Outcome));
        }
        AnsiConsole.Write(table);

        var created = result.Entries.Count(e => e.Outcome.Status == SkillPushStatus.Created);
        var unchanged = result.Entries.Count(e => e.Outcome.Status == SkillPushStatus.Unchanged);
        var problems = result.Entries.Count(e => e.Outcome.Status is SkillPushStatus.Refused or SkillPushStatus.Failed or SkillPushStatus.Conflict);

        if (created == 0 && problems == 0)
            AnsiConsole.MarkupLine($"Everything was already up to date in [grey]{Markup.Escape(authority)}[/].");
        else
            AnsiConsole.MarkupLine(
                $"Backed up to [grey]{Markup.Escape(authority)}[/]: {created} new, {unchanged} unchanged" +
                (problems > 0 ? $", [red]{problems} not backed up[/]." : "."));

        return result.Succeeded ? 0 : 1;
    }

    // Markup for one skill's outcome. The registry's reason is escaped; the colour tags are not.
    internal static string Describe(SkillPushOutcome outcome) => outcome.Status switch
    {
        SkillPushStatus.Created => $"[green]new version v{outcome.Version}[/]",
        SkillPushStatus.Unchanged => $"[grey]unchanged (v{outcome.Version})[/]",
        SkillPushStatus.Skipped => $"[yellow]skipped:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        SkillPushStatus.Refused => $"[red]refused:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        SkillPushStatus.Conflict => $"[red]conflict:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        _ => $"[red]failed:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
    };

    private static int Error(string message)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return 1;
    }
}
