using Protostar.Cli.Harness;
using Spectre.Console;

namespace Protostar.Cli.Skills;

/// <summary>
/// The console face of <see cref="ISkillPullService"/>: the registry comparison table and the pull results.
/// </summary>
internal static class SkillPullPresenter
{
    /// <summary>Render a registry comparison and return the exit code (0 on success, otherwise 1).</summary>
    public static int RenderRemote(SkillRemoteResult result, IHarnessCatalog catalog)
    {
        if (Failure(result.Failure, result.Detail, result.Registry, catalog) is { } code)
            return code;

        var authority = Authority(result.Registry);
        if (result.Skills.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No skills in {Markup.Escape(authority)} and none installed here.[/]");
            return 0;
        }

        var showHarness = result.Skills.Select(s => s.Harness.Id).Distinct().Count() > 1;
        var showProject = result.Skills.Any(s => s.Project is not null);
        var table = new Table().Border(TableBorder.Rounded);
        if (showHarness)
            table.AddColumn("Harness");
        if (showProject)
            table.AddColumn("Project");
        table.AddColumn("Name");
        table.AddColumn("Registry");
        table.AddColumn("This machine");
        table.AddColumn("Description");
        foreach (var skill in result.Skills)
        {
            var cells = new List<string>();
            if (showHarness)
                cells.Add(Markup.Escape(skill.Harness.Id));
            if (showProject)
                cells.Add(Project(skill.Project));
            cells.Add(Markup.Escape(skill.Name));
            cells.Add(skill.Remote is { } remote ? $"v{remote.Version}" : "[grey]-[/]");
            cells.Add(Describe(skill, result.Version));
            cells.Add(Markup.Escape(SkillsPresenter.Truncate(skill.Remote?.Description ?? string.Empty, 60)));
            table.AddRow(cells.ToArray());
        }
        AnsiConsole.Write(table);

        var pullable = result.Skills.Count(s => s.State is SkillSyncState.NotInstalled or SkillSyncState.UpdateAvailable);
        if (result.Version is { } version)
        {
            // A single skill compared with an earlier version: say what `pull --version` would do.
            var name = result.Skills.FirstOrDefault(s => s.Remote is not null)?.Remote?.Name ?? string.Empty;
            AnsiConsole.MarkupLine(pullable > 0
                ? $"Run `protostar skills pull {Markup.Escape(name)} --version {version}` to install v{version}."
                : $"Nothing here would be replaced by v{version} without --force.");
            foreach (var both in result.Skills.Where(s => s.State == SkillSyncState.ChangedBoth))
                SkillPushPresenter.ResolutionHelp(both.Name, registryVersion: null);
            return 0;
        }

        var conflicts = result.Skills.Count(s => s.State == SkillSyncState.Conflict);
        var changedBoth = result.Skills.Count(s => s.State == SkillSyncState.ChangedBoth);
        var elsewhere = result.Skills.Count(s => s.State == SkillSyncState.OtherProject);
        AnsiConsole.MarkupLine(pullable > 0
            ? $"{pullable} can be pulled from [grey]{Markup.Escape(authority)}[/]. Run `protostar skills pull` to get them."
            : conflicts > 0 || changedBoth > 0
                ? $"Nothing else can be pulled from [grey]{Markup.Escape(authority)}[/]."
                : $"Everything in [grey]{Markup.Escape(authority)}[/] is on this machine.");
        if (conflicts > 0)
            AnsiConsole.MarkupLine($"[red]{conflicts} cannot be pulled until the conflict is resolved[/] [grey](--force does not override a conflict)[/].");
        if (elsewhere > 0)
            AnsiConsole.MarkupLine(elsewhere == 1
                ? "[grey]1 belongs to another project and is only pulled from a checkout of that project.[/]"
                : $"[grey]{elsewhere} belong to other projects and are only pulled from a checkout of their project.[/]");
        foreach (var both in result.Skills.Where(s => s.State == SkillSyncState.ChangedBoth))
            SkillPushPresenter.ResolutionHelp(both.Name, both.Remote?.Version);
        return 0;
    }

    /// <summary>Render a pull run and return the exit code (0 when no skill failed, otherwise 1).</summary>
    public static int RenderPull(SkillPullRunResult result, IHarnessCatalog catalog)
    {
        if (Failure(result.Failure, result.Detail, result.Registry, catalog) is { } code)
            return code;

        var authority = Authority(result.Registry);
        if (result.Entries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No skills in {Markup.Escape(authority)}. Nothing to pull.[/]");
            return 0;
        }

        var showHarness = result.Entries.Select(e => e.Harness.Id).Distinct().Count() > 1;
        var showProject = result.Entries.Any(e => e.Project is not null);
        var table = new Table().Border(TableBorder.Rounded);
        if (showHarness)
            table.AddColumn("Harness");
        if (showProject)
            table.AddColumn("Project");
        table.AddColumn("Name");
        table.AddColumn("Result");
        table.AddColumn("Path");
        foreach (var entry in result.Entries)
        {
            var cells = new List<string>();
            if (showHarness)
                cells.Add(Markup.Escape(entry.Harness.Id));
            if (showProject)
                cells.Add(Project(entry.Project));
            cells.Add(Markup.Escape(entry.Name));
            cells.Add(Describe(entry));
            cells.Add($"[grey]{Markup.Escape(entry.Path)}[/]");
            table.AddRow(cells.ToArray());
        }
        AnsiConsole.Write(table);

        var installed = result.Entries.Count(e => e.Status == SkillPullStatus.Installed);
        var updated = result.Entries.Count(e => e.Status == SkillPullStatus.Updated);
        var current = result.Entries.Count(e => e.Status == SkillPullStatus.UpToDate);
        var held = result.Entries.Count(e => e.Status == SkillPullStatus.LocalChanges);
        var both = result.Entries.Count(e => e.Status == SkillPullStatus.ChangedBoth);
        var failed = result.Entries.Count(e => e.Status == SkillPullStatus.Failed);
        var conflicted = result.Entries.Count(e => e.Status == SkillPullStatus.Conflict);
        var elsewhere = result.Entries.Count(e => e.Status == SkillPullStatus.OtherProject);
        var followed = result.Entries.Count(e => e.Status == SkillPullStatus.FollowedRename);
        var gone = result.Entries.Count(e => e.Status == SkillPullStatus.RemovedInRegistry);

        AnsiConsole.MarkupLine(
            $"From [grey]{Markup.Escape(authority)}[/]: {installed} installed, {updated} updated, {current} already up to date" +
            (held > 0 ? $", [yellow]{held} kept because of local changes[/]" : string.Empty) +
            (both > 0 ? $", [yellow]{both} kept because they changed here and in the registry[/]" : string.Empty) +
            (conflicted > 0 ? $", [red]{conflicted} skipped as conflicts[/]" : string.Empty) +
            (failed > 0 ? $", [red]{failed} failed[/]" : string.Empty) +
            (elsewhere > 0 ? $", [grey]{elsewhere} left for other projects' checkouts[/]" : string.Empty) +
            (followed > 0 ? $", {followed} renamed here to follow the registry" : string.Empty) +
            (gone > 0 ? $", [yellow]{gone} kept although deleted or renamed in the registry[/]" : string.Empty) + ".");
        if (held > 0)
            AnsiConsole.MarkupLine("[grey]Back up local changes with `protostar skills push`, or overwrite them with `protostar skills pull --force`.[/]");
        foreach (var entry in result.Entries.Where(e => e.Status == SkillPullStatus.ChangedBoth))
            SkillPushPresenter.ResolutionHelp(entry.Name, result.Version is null ? entry.Version : null);

        return result.Succeeded ? 0 : 1;
    }

    internal static string Describe(SkillComparison skill, int? version = null) => skill.State switch
    {
        SkillSyncState.UpToDate when version is { } pinned => $"[green]matches v{pinned}[/]",
        SkillSyncState.UpToDate => "[green]up to date[/]",
        SkillSyncState.NotInstalled => "[blue]not installed[/]",
        SkillSyncState.UpdateAvailable when version is { } pinned => skill.LocalVersion is { } have
            ? $"[yellow]can be replaced with v{pinned} (have v{have}, unedited)[/]"
            : $"[yellow]can be replaced with v{pinned} (unedited)[/]",
        SkillSyncState.UpdateAvailable => skill.LocalVersion is { } v ? $"[yellow]update available (have v{v})[/]" : "[yellow]update available[/]",
        SkillSyncState.LocalChanges => "[red]local changes[/]",
        SkillSyncState.ChangedBoth => skill.LocalVersion is { } based and > 0
            ? $"[red]changed here and in the registry[/] (this copy is based on v{based})"
            : "[red]changed here and in the registry[/]",
        SkillSyncState.Conflict => $"[red]conflict:[/] {Markup.Escape(skill.Note ?? string.Empty)}",
        SkillSyncState.OtherProject => $"[grey]other project:[/] {Markup.Escape(skill.Note ?? string.Empty)}",
        SkillSyncState.RemovedInRegistry => $"[yellow]gone from the registry:[/] {Markup.Escape(skill.Note ?? string.Empty)}",
        _ => "[grey]only here (not backed up)[/]",
    };

    private static string Project(string? project) => project is null ? "[grey]global[/]" : Markup.Escape(project);

    internal static string Describe(SkillPullEntry entry)
    {
        var text = entry.Status switch
        {
            SkillPullStatus.Installed => $"[green]installed v{entry.Version}[/]",
            SkillPullStatus.ChangedBoth => "[yellow]kept: changed here and in the registry[/]",
            SkillPullStatus.Updated => $"[green]updated to v{entry.Version}[/]",
            SkillPullStatus.UpToDate => $"[grey]up to date (v{entry.Version})[/]",
            SkillPullStatus.LocalChanges => "[yellow]kept: has local changes[/]",
            SkillPullStatus.Conflict => "[red]conflict:[/]",
            SkillPullStatus.OtherProject => "[grey]not here:[/]",
            SkillPullStatus.FollowedRename => "[green]followed the rename:[/]",
            SkillPullStatus.RemovedInRegistry => "[yellow]kept, gone from the registry:[/]",
            _ => "[red]failed[/]",
        };
        return entry.Message is { } message ? $"{text} {Markup.Escape(message)}" : text;
    }

    private static string Authority(Uri? registry) => registry?.GetLeftPart(UriPartial.Authority) ?? string.Empty;

    /// <summary>Prints why a run stopped early and returns its exit code, or returns <c>null</c> when it went ahead.</summary>
    /// <remarks>Also used by <see cref="SkillChangePresenter"/>, whose lookup stops for the same reasons.</remarks>
    internal static int? Failure(SkillPullRunFailure failure, string? detail, Uri? registry, IHarnessCatalog catalog)
    {
        var message = FailureMessage(failure, detail, registry, catalog);
        if (message is null)
            return null;

        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return 1;
    }

    /// <summary>Why a run stopped early, as plain text; <c>null</c> when it went ahead. Also used by <c>setup</c>.</summary>
    internal static string? FailureMessage(SkillPullRunFailure failure, string? detail, Uri? registry, IHarnessCatalog catalog)
    {
        var authority = Authority(registry);
        return failure switch
        {
            SkillPullRunFailure.None => null,
            SkillPullRunFailure.InvalidRegistry => detail,
            SkillPullRunFailure.UnknownHarness => $"Unknown harness '{detail}'. Known: {catalog.KnownIds}",
            SkillPullRunFailure.Unsupported => $"Harness '{detail}' does not support skills.",
            SkillPullRunFailure.NoHarness => "No harness that supports skills was found on this machine.",
            SkillPullRunFailure.NotLoggedIn => $"Not logged in to {authority}. Run `protostar auth login` first.",
            SkillPullRunFailure.SessionExpired => $"Your session for {authority} has expired. Run `protostar auth login` again.",
            SkillPullRunFailure.RegistryUnreachable => $"Could not reach the registry at {authority}.",
            SkillPullRunFailure.Incompatible => detail,
            SkillPullRunFailure.ListFailed => $"Could not list skills in {authority}: {detail}",
            SkillPullRunFailure.SkillNotFound => $"There is no skill named '{detail}' in {authority}. Run `protostar skills remote` to see what is there.",
            SkillPullRunFailure.InvalidVersion => detail,
            SkillPullRunFailure.VersionNotFound => $"Could not fetch that version from {authority}: {detail}",
            _ => detail,
        };
    }
}
