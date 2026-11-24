using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Spectre.Console;

namespace Protostar.Cli.Agents;

/// <summary>The console face of <see cref="IAgentPushService"/>: a table per agent and a summary line.</summary>
internal static class AgentPushPresenter
{
    /// <summary>Renders a push run and returns the exit code: 0 when every agent reached the registry, otherwise 1.</summary>
    public static int Render(AgentPushRunResult result, IHarnessCatalog catalog)
    {
        var authority = result.Registry?.GetLeftPart(UriPartial.Authority) ?? string.Empty;
        if (FailureMessage(result, catalog) is { } failure)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(failure)}[/]");
            return 1;
        }

        if (result.Entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No agents found. Nothing to back up.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Scope");
        table.AddColumn("Name");
        table.AddColumn("File");
        table.AddColumn("Result");
        foreach (var entry in result.Entries)
        {
            table.AddRow(
                Markup.Escape(Scope(entry)),
                Markup.Escape(entry.Agent.Name ?? "(no name)"),
                Markup.Escape(entry.Agent.RelativePath),
                Describe(entry.Outcome));
        }
        AnsiConsole.Write(table);

        var created = result.Entries.Count(e => e.Outcome.Status == AgentPushStatus.Created);
        var unchanged = result.Entries.Count(e => e.Outcome.Status == AgentPushStatus.Unchanged);
        var removed = result.Entries.Count(e => e.Outcome.Status == AgentPushStatus.Removed);
        var problems = result.Entries.Count(e => e.Outcome.Status is AgentPushStatus.Refused or AgentPushStatus.Failed or AgentPushStatus.Conflict or AgentPushStatus.Stale);
        if (created == 0 && problems == 0 && removed == 0)
            AnsiConsole.MarkupLine($"Everything was already up to date in [grey]{Markup.Escape(authority)}[/].");
        else
            AnsiConsole.MarkupLine(
                $"Backed up to [grey]{Markup.Escape(authority)}[/]: {created} new, {unchanged} unchanged" +
                (removed > 0 ? $", [yellow]{removed} not uploaded because {(removed == 1 ? "it was" : "they were")} deleted or renamed in the registry[/]" : "") +
                (problems > 0 ? $", [red]{problems} not backed up[/]." : "."));

        foreach (var entry in result.Entries.Where(e => e.Outcome.Status == AgentPushStatus.Stale))
            ResolutionHelp(entry.Agent.Name!, entry.Outcome.Version);

        return result.Succeeded ? 0 : 1;
    }

    /// <summary>Why a run stopped before uploading anything, as plain text; <c>null</c> when it went ahead. Also used by the background push.</summary>
    internal static string? FailureMessage(AgentPushRunResult result, IHarnessCatalog? catalog)
    {
        var authority = result.Registry?.GetLeftPart(UriPartial.Authority) ?? string.Empty;
        return result.Failure switch
        {
            AgentPushRunFailure.None => null,
            AgentPushRunFailure.UnknownHarness => $"Unknown harness '{result.Detail}'. Known: {catalog?.KnownIds}",
            AgentPushRunFailure.Unsupported => $"Harness '{result.Detail}' has no custom agents.",
            AgentPushRunFailure.AgentNotFound => $"No agent named '{result.Detail}' was found. Agents are named by the 'name' in their front matter, not their file name.",
            AgentPushRunFailure.NotLoggedIn => $"Not logged in to {authority}. Run `protostar auth login` first.",
            AgentPushRunFailure.SessionExpired => $"Your session for {authority} has expired. Run `protostar auth login` again.",
            AgentPushRunFailure.RegistryUnreachable => $"Could not reach the registry at {authority}.",
            AgentPushRunFailure.ListFailed => $"Could not list the agents in {authority} to confirm what is backed up: {result.Detail}",
            _ => result.Detail,
        };
    }

    /// <summary>The ways out of "changed here and in the registry" for the agent <paramref name="name"/>.</summary>
    /// <remarks>Each way out is one command, as for skills: keep both pushes this copy as a new agent, after which the pull puts the registry's back.</remarks>
    internal static void ResolutionHelp(string name, int? registryVersion)
    {
        var theirs = registryVersion is { } v ? $"v{v}" : "the registry's version";
        var quoted = Markup.Escape(name.Any(char.IsWhiteSpace) ? $"\"{name}\"" : name);
        AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(name)}' changed here and in the registry.[/] Choose one:");
        AnsiConsole.MarkupLine($"  keep yours:   [blue]protostar agents push {quoted} --force[/] [grey](replaces {theirs}, which stays in history)[/]");
        AnsiConsole.MarkupLine($"  keep theirs:  [blue]protostar agents pull {quoted} --force[/] [grey](overwrites your copy here)[/]");
        AnsiConsole.MarkupLine($"  keep both:    [blue]protostar agents push {quoted} --as NEW-NAME[/] [grey](pushes yours as a new agent, then pull {quoted})[/]");
    }

    internal static string Describe(AgentPushOutcome outcome) => outcome.Status switch
    {
        AgentPushStatus.Created when outcome.Message is { } note => $"[green]new version v{outcome.Version}[/] {Markup.Escape(note)}",
        AgentPushStatus.Created => $"[green]new version v{outcome.Version}[/]",
        AgentPushStatus.Unchanged => $"[grey]unchanged (v{outcome.Version})[/]",
        AgentPushStatus.Stale => $"[red]changed here and in the registry:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        AgentPushStatus.Removed when outcome.RenamedTo is { } renamedTo => $"[yellow]renamed to {Markup.Escape(renamedTo)} in the registry:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        AgentPushStatus.Removed => $"[yellow]deleted in the registry:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        AgentPushStatus.Skipped => $"[yellow]skipped:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        AgentPushStatus.Refused => $"[red]refused:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        AgentPushStatus.Conflict => $"[red]conflict:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
        _ => $"[red]failed:[/] {Markup.Escape(outcome.Message ?? string.Empty)}",
    };

    private static string Scope(AgentPushEntry entry) =>
        entry.Outcome.ProjectName is { } project ? $"project ({project})" : AgentPushService.Describe(entry.Agent.Scope);
}
