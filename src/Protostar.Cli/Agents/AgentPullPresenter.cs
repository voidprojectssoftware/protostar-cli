using Protostar.Cli.Harness;
using Spectre.Console;

namespace Protostar.Cli.Agents;

/// <summary>The console face of <see cref="IAgentPullService"/>: the registry comparison table and the pull results.</summary>
internal static class AgentPullPresenter
{
    /// <summary>Renders a registry comparison and returns the exit code (0 on success, otherwise 1).</summary>
    public static int RenderRemote(AgentRemoteResult result, IHarnessCatalog catalog)
    {
        if (Failure(result.Failure, result.Detail, result.Registry, catalog) is { } code)
            return code;

        var authority = Authority(result.Registry);
        if (result.Agents.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No agents in {Markup.Escape(authority)} and none here.[/]");
            return 0;
        }

        var showHarness = result.Agents.Select(a => a.Harness.Id).Distinct().Count() > 1;
        var table = new Table().Border(TableBorder.Rounded);
        if (showHarness)
            table.AddColumn("Harness");
        table.AddColumn("Scope");
        table.AddColumn("Name");
        table.AddColumn("Registry");
        table.AddColumn("This machine");
        table.AddColumn("File");
        foreach (var agent in result.Agents)
        {
            var cells = new List<string>();
            if (showHarness)
                cells.Add(Markup.Escape(agent.Harness.Id));
            cells.Add(Scope(agent.Project));
            cells.Add(Markup.Escape(agent.Name));
            cells.Add(agent.Remote is { } remote ? $"v{remote.Version}" : "[grey]-[/]");
            cells.Add(Describe(agent, result.Version));
            // The registry's path relative to the agents folder; for a copy only here, its own file.
            cells.Add($"[grey]{Markup.Escape(agent.Remote?.Path ?? (agent.LocalPath is { } local ? Path.GetFileName(local) : ""))}[/]");
            table.AddRow(cells.ToArray());
        }
        AnsiConsole.Write(table);

        var pullable = result.Agents.Count(a => a.State is AgentSyncState.NotInstalled or AgentSyncState.UpdateAvailable);
        if (result.Version is { } version)
        {
            var name = result.Agents.FirstOrDefault(a => a.Remote is not null)?.Remote?.Name ?? string.Empty;
            AnsiConsole.MarkupLine(pullable > 0
                ? $"Run `protostar agents pull {Markup.Escape(Quote(name))} --version {version}` to install v{version}."
                : $"Nothing here would be replaced by v{version} without --force.");
            return 0;
        }

        var conflicts = result.Agents.Count(a => a.State == AgentSyncState.Conflict);
        var changedBoth = result.Agents.Count(a => a.State == AgentSyncState.ChangedBoth);
        var elsewhere = result.Agents.Count(a => a.State == AgentSyncState.OtherProject);
        AnsiConsole.MarkupLine(pullable > 0
            ? $"{pullable} can be pulled from [grey]{Markup.Escape(authority)}[/]. Run `protostar agents pull` to get {(pullable == 1 ? "it" : "them")}."
            : conflicts > 0 || changedBoth > 0
                ? $"Nothing else can be pulled from [grey]{Markup.Escape(authority)}[/]."
                : $"Everything in [grey]{Markup.Escape(authority)}[/] is on this machine.");
        if (conflicts > 0)
            AnsiConsole.MarkupLine($"[red]{conflicts} cannot be pulled until the conflict is resolved[/] [grey](--force does not override a conflict)[/].");
        if (elsewhere > 0)
            AnsiConsole.MarkupLine(elsewhere == 1
                ? "[grey]1 belongs to another project and is only pulled from a checkout of that project.[/]"
                : $"[grey]{elsewhere} belong to other projects and are only pulled from a checkout of their project.[/]");
        foreach (var both in result.Agents.Where(a => a.State == AgentSyncState.ChangedBoth))
            AgentPushPresenter.ResolutionHelp(both.Name, both.Remote?.Version);
        return 0;
    }

    /// <summary>Renders a pull run and returns the exit code (0 when no agent failed or conflicted, otherwise 1).</summary>
    public static int RenderPull(AgentPullRunResult result, IHarnessCatalog catalog)
    {
        if (Failure(result.Failure, result.Detail, result.Registry, catalog) is { } code)
            return code;

        var authority = Authority(result.Registry);
        if (result.Entries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No agents in {Markup.Escape(authority)}. Nothing to pull.[/]");
            return 0;
        }

        var showHarness = result.Entries.Select(e => e.Harness.Id).Distinct().Count() > 1;
        var table = new Table().Border(TableBorder.Rounded);
        if (showHarness)
            table.AddColumn("Harness");
        table.AddColumn("Scope");
        table.AddColumn("Name");
        table.AddColumn("Result");
        table.AddColumn("Path");
        foreach (var entry in result.Entries)
        {
            var cells = new List<string>();
            if (showHarness)
                cells.Add(Markup.Escape(entry.Harness.Id));
            cells.Add(Scope(entry.Project));
            cells.Add(Markup.Escape(entry.Name));
            cells.Add(Describe(entry));
            cells.Add($"[grey]{Markup.Escape(entry.Path)}[/]");
            table.AddRow(cells.ToArray());
        }
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"From [grey]{Markup.Escape(authority)}[/]: {Summary(result)}.");
        if (result.Entries.Any(e => e.Status == AgentPullStatus.LocalChanges))
            AnsiConsole.MarkupLine("[grey]Back up local changes with `protostar agents push`, or overwrite them with `protostar agents pull NAME --force`.[/]");
        foreach (var entry in result.Entries.Where(e => e.Status == AgentPullStatus.ChangedBoth))
            AgentPushPresenter.ResolutionHelp(entry.Name, result.Version is null ? entry.Version : null);

        return result.Succeeded ? 0 : 1;
    }

    /// <summary>The counts of a pull as one clause ("1 installed, 0 updated, ..."), shared with <c>setup</c>.</summary>
    internal static string Summary(AgentPullRunResult result)
    {
        int Of(AgentPullStatus status) => result.Entries.Count(e => e.Status == status);
        return $"{Of(AgentPullStatus.Installed)} installed, {Of(AgentPullStatus.Updated)} updated, {Of(AgentPullStatus.UpToDate)} already up to date" +
            (Of(AgentPullStatus.LocalChanges) is > 0 and var held ? $", [yellow]{held} kept because of local changes[/]" : "") +
            (Of(AgentPullStatus.ChangedBoth) is > 0 and var both ? $", [yellow]{both} kept because they changed here and in the registry[/]" : "") +
            (Of(AgentPullStatus.Conflict) is > 0 and var conflicts ? $", [red]{conflicts} skipped as conflicts[/]" : "") +
            (Of(AgentPullStatus.Failed) is > 0 and var failed ? $", [red]{failed} failed[/]" : "") +
            (Of(AgentPullStatus.OtherProject) is > 0 and var elsewhere ? $", [grey]{elsewhere} left for other projects' checkouts[/]" : "") +
            (Of(AgentPullStatus.FollowedRename) is > 0 and var followed ? $", {followed} renamed here to follow the registry" : "") +
            (Of(AgentPullStatus.RemovedInRegistry) is > 0 and var gone ? $", [yellow]{gone} kept although deleted or renamed in the registry[/]" : "");
    }

    // The states whose text does not already carry the note get it after, when there is one.
    internal static string Describe(AgentComparison agent, int? version = null) =>
        agent.Note is { } note && agent.State is AgentSyncState.UpToDate or AgentSyncState.UpdateAvailable or AgentSyncState.LocalOnly
            ? $"{State(agent, version)} {Markup.Escape(note)}"
            : State(agent, version);

    private static string State(AgentComparison agent, int? version) => agent.State switch
    {
        AgentSyncState.UpToDate when version is { } pinned => $"[green]matches v{pinned}[/]",
        AgentSyncState.UpToDate => "[green]up to date[/]",
        AgentSyncState.NotInstalled => "[blue]not installed[/]",
        AgentSyncState.UpdateAvailable when version is { } pinned => agent.LocalVersion is { } have
            ? $"[yellow]can be replaced with v{pinned} (have v{have}, unedited)[/]"
            : $"[yellow]can be replaced with v{pinned} (unedited)[/]",
        AgentSyncState.UpdateAvailable => agent.LocalVersion is { } v ? $"[yellow]update available (have v{v})[/]" : "[yellow]update available[/]",
        AgentSyncState.LocalChanges => "[red]local changes[/]",
        AgentSyncState.ChangedBoth => agent.LocalVersion is { } based and > 0
            ? $"[red]changed here and in the registry[/] (this copy is based on v{based})"
            : "[red]changed here and in the registry[/]",
        AgentSyncState.Conflict => $"[red]conflict:[/] {Markup.Escape(agent.Note ?? string.Empty)}",
        AgentSyncState.OtherProject => $"[grey]other project:[/] {Markup.Escape(agent.Note ?? string.Empty)}",
        AgentSyncState.RemovedInRegistry when agent.Remote is not null => $"[yellow]a new agent took this name:[/] {Markup.Escape(agent.Note ?? string.Empty)}",
        AgentSyncState.RemovedInRegistry => $"[yellow]gone from the registry:[/] {Markup.Escape(agent.Note ?? string.Empty)}",
        _ => "[grey]only here (not backed up)[/]",
    };

    /// <summary>Markup for what a pull did with one agent, with its message. Also used by <c>setup</c>.</summary>
    internal static string Describe(AgentPullEntry entry)
    {
        var text = entry.Status switch
        {
            AgentPullStatus.Installed => $"[green]installed v{entry.Version}[/]",
            AgentPullStatus.Updated => $"[green]updated to v{entry.Version}[/]",
            AgentPullStatus.UpToDate => $"[grey]up to date (v{entry.Version})[/]",
            AgentPullStatus.LocalChanges => "[yellow]kept: has local changes[/]",
            AgentPullStatus.ChangedBoth => "[yellow]kept: changed here and in the registry[/]",
            AgentPullStatus.Conflict => "[red]conflict:[/]",
            AgentPullStatus.OtherProject => "[grey]not here:[/]",
            AgentPullStatus.FollowedRename => "[green]followed the rename:[/]",
            AgentPullStatus.RemovedInRegistry => "[yellow]kept, removed from the registry:[/]",
            _ => "[red]failed[/]",
        };
        return entry.Message is { } message ? $"{text} {Markup.Escape(message)}" : text;
    }

    /// <summary>Why a run stopped early, as plain text; <c>null</c> when it went ahead. Also used by <c>setup</c>.</summary>
    internal static string? FailureMessage(AgentPullRunFailure failure, string? detail, Uri? registry, IHarnessCatalog catalog)
    {
        var authority = Authority(registry);
        return failure switch
        {
            AgentPullRunFailure.None => null,
            AgentPullRunFailure.UnknownHarness => $"Unknown harness '{detail}'. Known: {catalog.KnownIds}",
            AgentPullRunFailure.Unsupported => $"Harness '{detail}' has no custom agents.",
            AgentPullRunFailure.NoHarness => "No harness with custom agents was found on this machine.",
            AgentPullRunFailure.NotLoggedIn => $"Not logged in to {authority}. Run `protostar auth login` first.",
            AgentPullRunFailure.SessionExpired => $"Your session for {authority} has expired. Run `protostar auth login` again.",
            AgentPullRunFailure.RegistryUnreachable => $"Could not reach the registry at {authority}.",
            AgentPullRunFailure.ListFailed => $"Could not list agents in {authority}: {detail}",
            AgentPullRunFailure.AgentNotFound => $"There is no agent named '{detail}' in {authority}. Run `protostar agents remote` to see what is there.",
            AgentPullRunFailure.VersionNotFound => $"Could not fetch that version from {authority}: {detail}",
            _ => detail,
        };
    }

    private static int? Failure(AgentPullRunFailure failure, string? detail, Uri? registry, IHarnessCatalog catalog)
    {
        if (FailureMessage(failure, detail, registry, catalog) is not { } message)
            return null;
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return 1;
    }

    private static string Scope(string? project) => project is null ? "personal" : $"project ({Markup.Escape(project)})";

    private static string Authority(Uri? registry) => registry?.GetLeftPart(UriPartial.Authority) ?? string.Empty;

    private static string Quote(string name) => name.Any(char.IsWhiteSpace) ? $"\"{name}\"" : name;
}
