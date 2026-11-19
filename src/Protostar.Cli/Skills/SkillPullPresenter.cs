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
        var table = new Table().Border(TableBorder.Rounded);
        if (showHarness)
            table.AddColumn("Harness");
        table.AddColumn("Name");
        table.AddColumn("Registry");
        table.AddColumn("This machine");
        table.AddColumn("Description");
        foreach (var skill in result.Skills)
        {
            var cells = new List<string>();
            if (showHarness)
                cells.Add(Markup.Escape(skill.Harness.Id));
            cells.Add(Markup.Escape(skill.Name));
            cells.Add(skill.Remote is { } remote ? $"v{remote.Version}" : "[grey]-[/]");
            cells.Add(Describe(skill));
            cells.Add(Markup.Escape(SkillsPresenter.Truncate(skill.Remote?.Description ?? string.Empty, 60)));
            table.AddRow(cells.ToArray());
        }
        AnsiConsole.Write(table);

        var pullable = result.Skills.Count(s => s.State is SkillSyncState.NotInstalled or SkillSyncState.UpdateAvailable);
        AnsiConsole.MarkupLine(pullable > 0
            ? $"{pullable} can be pulled from [grey]{Markup.Escape(authority)}[/]. Run `protostar skills pull` to get them."
            : $"Everything in [grey]{Markup.Escape(authority)}[/] is on this machine.");
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
        var table = new Table().Border(TableBorder.Rounded);
        if (showHarness)
            table.AddColumn("Harness");
        table.AddColumn("Name");
        table.AddColumn("Result");
        table.AddColumn("Path");
        foreach (var entry in result.Entries)
        {
            var cells = new List<string>();
            if (showHarness)
                cells.Add(Markup.Escape(entry.Harness.Id));
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
        var failed = result.Entries.Count(e => e.Status == SkillPullStatus.Failed);

        AnsiConsole.MarkupLine(
            $"From [grey]{Markup.Escape(authority)}[/]: {installed} installed, {updated} updated, {current} already up to date" +
            (held > 0 ? $", [yellow]{held} kept because of local changes[/]" : string.Empty) +
            (failed > 0 ? $", [red]{failed} failed[/]" : string.Empty) + ".");
        if (held > 0)
            AnsiConsole.MarkupLine("[grey]Back up local changes with `protostar skills push`, or overwrite them with `protostar skills pull --force`.[/]");

        return result.Succeeded ? 0 : 1;
    }

    internal static string Describe(SkillComparison skill) => skill.State switch
    {
        SkillSyncState.UpToDate => "[green]up to date[/]",
        SkillSyncState.NotInstalled => "[blue]not installed[/]",
        SkillSyncState.UpdateAvailable => skill.LocalVersion is { } v ? $"[yellow]update available (have v{v})[/]" : "[yellow]update available[/]",
        SkillSyncState.LocalChanges => "[red]local changes[/]",
        _ => "[grey]only here (not backed up)[/]",
    };

    internal static string Describe(SkillPullEntry entry)
    {
        var text = entry.Status switch
        {
            SkillPullStatus.Installed => $"[green]installed v{entry.Version}[/]",
            SkillPullStatus.Updated => $"[green]updated to v{entry.Version}[/]",
            SkillPullStatus.UpToDate => $"[grey]up to date (v{entry.Version})[/]",
            SkillPullStatus.LocalChanges => "[yellow]kept: has local changes[/]",
            _ => "[red]failed[/]",
        };
        return entry.Message is { } message ? $"{text} {Markup.Escape(message)}" : text;
    }

    private static string Authority(Uri? registry) => registry?.GetLeftPart(UriPartial.Authority) ?? string.Empty;

    // The message and exit code for a run that stopped early, or null when it went ahead.
    private static int? Failure(SkillPullRunFailure failure, string? detail, Uri? registry, IHarnessCatalog catalog)
    {
        var authority = Authority(registry);
        string? message = failure switch
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
            _ => detail,
        };
        if (message is null)
            return null;

        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return 1;
    }
}
