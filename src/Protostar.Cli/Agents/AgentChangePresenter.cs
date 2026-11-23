using Protostar.Cli.Harness;
using Spectre.Console;

namespace Protostar.Cli.Agents;

/// <summary>The console face of <see cref="IAgentChangeService"/>: what a delete or rename found, the delete confirmation, and what happened.</summary>
internal static class AgentChangePresenter
{
    /// <summary>Prints why the agent could not be found and returns the exit code, or returns <c>null</c> when it was found.</summary>
    public static int? RenderFindFailure(AgentTargetResult result, IHarnessCatalog catalog)
    {
        if (result.Failure == AgentPullRunFailure.None)
            return null;

        var message = result.Failure is AgentPullRunFailure.AgentNotFound or AgentPullRunFailure.Ambiguous
            ? result.Detail ?? "No such agent."
            : AgentPullPresenter.FailureMessage(result.Failure, result.Detail, result.Registry, catalog);
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message ?? "Failed.")}[/]");
        return 1;
    }

    /// <summary>Shows what a delete would remove and asks to go ahead; returns <c>null</c> to go ahead, otherwise the exit code.</summary>
    /// <param name="target">The agent to delete.</param>
    /// <param name="yes"><c>--yes</c>: go ahead without asking.</param>
    /// <param name="inputRedirected">Standard input is not a terminal, so there is nobody to ask.</param>
    /// <remarks>Without <c>--yes</c> and with no terminal to ask on, it refuses rather than waiting for an answer that never comes.</remarks>
    public static int? ConfirmDelete(AgentTarget target, bool yes, bool inputRedirected)
    {
        AnsiConsole.MarkupLine($"This deletes the {Describe(target)} from [grey]{Markup.Escape(Authority(target.Registry))}[/], {Versions(target.Agent.Version - target.Agent.FirstVersion + 1)}. It cannot be undone.");
        foreach (var copy in target.Copies)
        {
            AnsiConsole.MarkupLine(copy.HasLocalChanges
                ? $"[yellow]This machine has a copy with changes that were never pushed:[/] {Markup.Escape(copy.Path)}"
                : $"[grey]This machine has a copy at {Markup.Escape(copy.Path)}.[/]");
        }
        if (target.Copies.Count > 0)
            AnsiConsole.MarkupLine("[grey]Local files are not deleted: only the registry's copy goes.[/]");

        if (yes)
            return null;
        if (inputRedirected)
        {
            AnsiConsole.MarkupLine("[red]Not deleted: confirmation is needed and there is no terminal to ask on. Pass --yes to delete without asking.[/]");
            return 1;
        }

        if (AnsiConsole.Confirm($"Delete {Describe(target)}?", defaultValue: false))
            return null;
        AnsiConsole.MarkupLine("Not deleted.");
        return 0;
    }

    /// <summary>Renders a delete and returns the exit code: 0 when the agent is gone, 1 when the registry refused.</summary>
    public static int RenderDelete(AgentDeleteResult result)
    {
        if (result.Error is { } error)
        {
            AnsiConsole.MarkupLine($"[red]Not deleted: {Markup.Escape(error)}[/]");
            return 1;
        }

        var target = result.Target;
        AnsiConsole.MarkupLine($"Deleted the {Describe(target)} and its versions from [grey]{Markup.Escape(Authority(target.Registry))}[/].");
        AnsiConsole.MarkupLine(target.Copies.Count == 0
            ? "[grey]No local file was touched.[/]"
            : $"Local {(target.Copies.Count == 1 ? "file is" : "files are")} untouched: {Markup.Escape(string.Join(", ", target.Copies.Select(c => c.Path)))}. " +
              $"Remove {(target.Copies.Count == 1 ? "it" : "them")} if you no longer want the agent; `protostar agents push {Markup.Escape(Quote(target.Agent.Name))} --force` restores it on purpose.");
        AnsiConsole.MarkupLine("[grey]The registry remembers the delete, so a push or the capture hook here or on another machine does not upload it again, " +
            "and no other machine's file is deleted: `protostar agents remote` there lists its copy as gone from the registry.[/]");
        return 0;
    }

    /// <summary>Renders a rename and returns the exit code: 0 when renamed (or already so named), 1 when refused.</summary>
    public static int RenderRename(AgentRenameResult result)
    {
        var target = result.Target;
        var quoted = Markup.Escape(Quote(target.Agent.Name));
        if (result.Edited.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]Not renamed: this machine has {(result.Edited.Count == 1 ? "a copy" : "copies")} of the {Describe(target)} with changes that were never pushed:[/]");
            foreach (var copy in result.Edited)
                AnsiConsole.MarkupLine($"  {Markup.Escape(copy.Path)}");
            AnsiConsole.MarkupLine($"Push the changes first with `protostar agents push {quoted}` (or drop them with `protostar agents pull {quoted} --force`), then rename again.");
            return 1;
        }

        if (result.Error is { } error)
        {
            AnsiConsole.MarkupLine($"[red]Not renamed: {Markup.Escape(error)}[/]");
            return 1;
        }

        if (result.NewName == target.Agent.Name)
        {
            AnsiConsole.MarkupLine($"The {Describe(target)} already has that name. Nothing changed.");
            return 0;
        }

        AnsiConsole.MarkupLine($"Renamed the {Describe(target)} to [bold]{Markup.Escape(result.NewName!)}[/] in [grey]{Markup.Escape(Authority(target.Registry))}[/]. Its version history came with it.");
        foreach (var copy in result.Copies)
        {
            AnsiConsole.MarkupLine(copy.Renamed
                ? $"Local copy renamed here (its `name:` line; the file stays where it is): {Markup.Escape(copy.Path)}"
                : $"[yellow]Local copy not renamed: {Markup.Escape(copy.Path)} ({Markup.Escape(copy.Note ?? "no reason given")})[/]");
        }
        if (result.Copies.Count == 0)
            AnsiConsole.MarkupLine("[grey]This machine has no copy of it.[/]");
        AnsiConsole.MarkupLine("[grey]The registry remembers the old name, so other machines do not push it back. Their next `protostar agents pull` (or setup) " +
            "renames an unedited copy to match; a copy edited there is kept and reported.[/]");
        return result.Copies.All(c => c.Renamed) ? 0 : 1;
    }

    private static string Describe(AgentTarget target) =>
        target.Agent.ProjectId is null
            ? $"personal agent [bold]{Markup.Escape(target.Agent.Name)}[/]"
            : $"agent [bold]{Markup.Escape(target.Agent.Name)}[/] of project [bold]{Markup.Escape(target.Agent.ProjectName ?? target.Agent.ProjectId.ToString()!)}[/]";

    private static string Versions(int count) => count == 1 ? "with its only version" : $"with all {count} of its versions";

    private static string Authority(Uri registry) => registry.GetLeftPart(UriPartial.Authority);

    private static string Quote(string name) => name.Any(char.IsWhiteSpace) ? $"\"{name}\"" : name;
}
