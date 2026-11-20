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
        if (FailureMessage(result, catalog) is { } failure)
            return Error(failure);

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
                Markup.Escape(Scope(entry)),
                Markup.Escape(entry.Skill.Name),
                Describe(entry.Outcome));
        }
        AnsiConsole.Write(table);

        var created = result.Entries.Count(e => e.Outcome.Status == SkillPushStatus.Created);
        var unchanged = result.Entries.Count(e => e.Outcome.Status == SkillPushStatus.Unchanged);
        var problems = result.Entries.Count(e => e.Outcome.Status is SkillPushStatus.Refused or SkillPushStatus.Failed or SkillPushStatus.Conflict or SkillPushStatus.Stale);

        if (created == 0 && problems == 0)
            AnsiConsole.MarkupLine($"Everything was already up to date in [grey]{Markup.Escape(authority)}[/].");
        else
            AnsiConsole.MarkupLine(
                $"Backed up to [grey]{Markup.Escape(authority)}[/]: {created} new, {unchanged} unchanged" +
                (problems > 0 ? $", [red]{problems} not backed up[/]." : "."));

        foreach (var entry in result.Entries.Where(e => e.Outcome.Status == SkillPushStatus.Stale))
            ResolutionHelp(entry.Skill.Name, entry.Outcome.Version);

        return result.Succeeded ? 0 : 1;
    }

    /// <summary>Why a run stopped before uploading anything, as plain text; <c>null</c> when it went ahead. Also used by <c>setup</c>.</summary>
    internal static string? FailureMessage(SkillPushRunResult result, IHarnessCatalog catalog)
    {
        var authority = result.Registry?.GetLeftPart(UriPartial.Authority) ?? string.Empty;
        return result.Failure switch
        {
            SkillPushRunFailure.None => null,
            SkillPushRunFailure.UnknownHarness => $"Unknown harness '{result.Detail}'. Known: {catalog.KnownIds}",
            SkillPushRunFailure.Unsupported => $"Harness '{result.Detail}' does not support skill discovery.",
            SkillPushRunFailure.SkillNotFound => $"No skill named '{result.Detail}' was found. Run `protostar skills` to see what is on this machine.",
            SkillPushRunFailure.NotLoggedIn => $"Not logged in to {authority}. Run `protostar auth login` first.",
            SkillPushRunFailure.SessionExpired => $"Your session for {authority} has expired. Run `protostar auth login` again.",
            SkillPushRunFailure.RegistryUnreachable => $"Could not reach the registry at {authority}.",
            SkillPushRunFailure.ListFailed => $"Could not list the skills in {authority} to confirm what is backed up: {result.Detail}",
            _ => result.Detail,
        };
    }

    /// <summary>The three ways out of "changed here and in the registry", for the skill <paramref name="name"/>.</summary>
    /// <remarks>Also printed by <c>skills remote</c> and <c>skills pull</c> for a copy in that state.</remarks>
    internal static void ResolutionHelp(string name, int? registryVersion)
    {
        var theirs = registryVersion is { } v ? $"v{v}" : "the registry's version";
        var quoted = Quote(name);
        AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(name)}' changed here and in the registry.[/] Choose one:");
        AnsiConsole.MarkupLine($"  keep yours:   [blue]protostar skills push {Markup.Escape(quoted)} --force[/] [grey](replaces {theirs}, which stays in history)[/]");
        AnsiConsole.MarkupLine($"  keep theirs:  [blue]protostar skills pull {Markup.Escape(quoted)} --force[/] [grey](overwrites your copy here)[/]");
        AnsiConsole.MarkupLine($"  keep both:    [blue]protostar skills push {Markup.Escape(quoted)} --as NEW-NAME[/] [grey](pushes yours as a new skill, then pull {Markup.Escape(quoted)})[/]");
    }

    private static string Quote(string name) => name.Any(char.IsWhiteSpace) ? $"\"{name}\"" : name;

    // Markup for one skill's outcome. The registry's reason is escaped; the colour tags are not.
    internal static string Describe(SkillPushOutcome outcome) => outcome.Status switch
    {
        SkillPushStatus.Created when outcome.Message is { } note => $"[green]new version v{outcome.Version}[/] {Markup.Escape(note)}",
        SkillPushStatus.Created => $"[green]new version v{outcome.Version}[/]",
        SkillPushStatus.Stale => $"[red]changed here and in the registry:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        SkillPushStatus.Unchanged => $"[grey]unchanged (v{outcome.Version})[/]",
        SkillPushStatus.Skipped => $"[yellow]skipped:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        SkillPushStatus.Refused => $"[red]refused:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        SkillPushStatus.Conflict => $"[red]conflict:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        _ => $"[red]failed:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
    };

    // "global", or "project" with the registry project it was filed under when the registry said.
    private static string Scope(SkillPushEntry entry) =>
        entry.Outcome.ProjectName is { } project ? $"project ({project})" : entry.Skill.Scope.ToString().ToLowerInvariant();

    private static int Error(string message)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return 1;
    }
}
