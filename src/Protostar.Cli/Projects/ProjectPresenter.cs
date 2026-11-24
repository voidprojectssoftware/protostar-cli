using System.Text.Json;
using Protostar.Cli.Registry;
using Protostar.Cli.Skills;
using Spectre.Console;

namespace Protostar.Cli.Projects;

/// <summary>The console face of <see cref="IProjectService"/>: which project a folder is in, and what a link or unlink did.</summary>
internal static class ProjectPresenter
{
    /// <summary>Renders a folder's project and returns the exit code: 0 when the registry answered, 1 when only local facts could be shown.</summary>
    public static int RenderShow(ProjectShowResult result, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new { facts = result.Facts, project = result.Project, registryError = result.Project is null ? Problem(result.Failure, result.Detail, result.Registry) : null },
                TranscriptJson.Options));
            return result.Project is null ? 1 : 0;
        }

        var grid = Grid();
        grid.AddRow("[grey]Folder[/]", Markup.Escape(result.Facts.WorkingDirectory));
        if (result.Project is { } project)
            AddProject(grid, project, result.Facts);
        else
            AddLocalIdentity(grid, result.Facts);
        AddGit(grid, result.Facts);
        AnsiConsole.Write(grid);

        if (result.Project is null)
        {
            AnsiConsole.MarkupLine($"[yellow]The project could not be checked with the registry: {Markup.Escape(Problem(result.Failure, result.Detail, result.Registry))}[/]");
            AnsiConsole.MarkupLine("[grey]What is shown is what can be worked out on this machine; the project's name and id come from the registry.[/]");
            return 1;
        }

        RenderSuggestions(result.Project);
        return 0;
    }

    /// <summary>Renders a link or unlink and returns the exit code: 0 when it was done, 1 when it failed.</summary>
    public static int RenderChange(ProjectLinkResult result)
    {
        if (result.Change is not { } change)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(Problem(result.Failure, result.Detail, result.Registry))}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine(change.Outcome switch
        {
            "linked" => $"Linked this folder to [bold]{Markup.Escape(change.Project.Name)}[/]. Its sessions moved with it.",
            "unlinked" => $"Unlinked this folder. It now belongs to [bold]{Markup.Escape(change.Project.Name)}[/].",
            "separated" => $"This folder shared its project with other copies of the repo. It now has its own: [bold]{Markup.Escape(change.Project.Name)}[/]. Run `protostar project unlink` again to rejoin.",
            "nothingToUnlink" => "This folder is not linked and is already its own project. Nothing changed.",
            _ => Markup.Escape(change.Outcome),
        });

        var grid = Grid();
        grid.AddRow("[grey]Folder[/]", Markup.Escape(result.Facts.WorkingDirectory));
        AddProject(grid, change.Project, result.Facts);
        if (change.Outcome != "nothingToUnlink")
        {
            if (change.MovedSkills.Count > 0 || result.LeftSkills is null)
            {
                grid.AddRow("[grey]Skills moved[/]", change.MovedSkills.Count == 0
                    ? "[grey]none[/]"
                    : Markup.Escape(string.Join(", ", change.MovedSkills)));
            }
            if (change.MovedAgents.Count > 0)
                grid.AddRow("[grey]Agents moved[/]", Markup.Escape(string.Join(", ", change.MovedAgents)));
        }
        AnsiConsole.Write(grid);

        if (result.LeftSkills is { } left)
            RenderLeftSkills(left, result.LeftFrom ?? "the earlier project", change.Project.Name);
        return 0;
    }

    // The project skills that stay in the folder as the earlier project's, and what becomes of each (cli#129).
    private static void RenderLeftSkills(IReadOnlyList<SkillLeftBehind> left, string from, string here)
    {
        var skills = left.Count == 1 ? "1 skill" : $"{left.Count} skills";
        AnsiConsole.MarkupLine($"{skills} of project [bold]{Markup.Escape(from)}[/] stay in this folder. They were pulled from there and only skills pushed from here move with it:");
        var grid = Grid();
        foreach (var skill in left)
            grid.AddRow($"  [bold]{Markup.Escape(skill.Name)}[/]", Markup.Escape(Fate(skill, from, here)));
        AnsiConsole.Write(grid);
    }

    internal static string Fate(SkillLeftBehind skill, string from, string here)
    {
        const string next = "the next backup (the capture hook, `protostar skills push` or `protostar setup`)";
        return skill.Fate switch
        {
            LeftSkillFate.StillLinked =>
                $"unchanged here, so it stays {from}'s skill and `protostar skills pull` keeps it up to date from there. " +
                $"If you change it here, {next} backs it up as a new skill of {here} instead.",
            LeftSkillFate.ChangedHere => $"changed here since it was pulled, so it now belongs to {here}: {next} backs it up there as a new skill.",
            LeftSkillFate.FolderHasOwn =>
                $"{here} has its own '{skill.Name}', so this copy now counts as that one's. `protostar skills remote` compares them before anything is backed up.",
            LeftSkillFate.GoneFromProject => $"{from} no longer has it, so it now belongs to {here}: {next} backs it up there as a new skill.",
            LeftSkillFate.Removed => "deleted or renamed in the registry since it was pulled, so it is not backed up. `protostar skills remote` says what to do with it.",
            _ => "the registry did not list its skills, so this could not be worked out. `protostar skills remote` says what becomes of it.",
        };
    }

    private static Grid Grid()
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().PadRight(2));
        grid.AddColumn();
        return grid;
    }

    private static void AddProject(Grid grid, RemoteProject project, ProjectFacts facts)
    {
        grid.AddRow("[grey]Project[/]", $"[bold]{Markup.Escape(project.Name)}[/]");
        grid.AddRow("[grey]Id[/]", project.ProjectId is { } id ? id.ToString() : "[grey]none yet: the first backup from here creates it[/]");
        grid.AddRow("[grey]Recognised by[/]", Markup.Escape(Recognition(project.RecognisedBy, facts)));
        if (project.Subpath is { Length: > 0 } subpath)
            grid.AddRow("[grey]Subfolder[/]", Markup.Escape(subpath));
        if (!string.Equals(project.Folder, facts.WorkingDirectory, StringComparison.Ordinal))
            grid.AddRow("[grey]Filed by[/]", Markup.Escape(project.Folder) + (facts.IsGit ? " [grey](the repo's main worktree on this machine)[/]" : ""));
    }

    // Offline: what the registry would key the folder by, without its project name or id.
    private static void AddLocalIdentity(Grid grid, ProjectFacts facts)
    {
        var identity = facts switch
        {
            { IsGit: true, RootCommit: { } root } => $"the repo whose root commit is {Short(root)}",
            { IsGit: true, Shallow: true } => "a shallow clone: matched by its remote once the registry knows it, else by this machine and path",
            { IsGit: true } => "a repo with no commits yet: this machine and path",
            _ => "this machine and path (not a git repo)",
        };
        grid.AddRow("[grey]Identity[/]", Markup.Escape(identity));
        if (facts.Subpath is { Length: > 0 } subpath)
            grid.AddRow("[grey]Subfolder[/]", Markup.Escape(subpath.TrimEnd('/', '\\')));
    }

    private static void AddGit(Grid grid, ProjectFacts facts)
    {
        if (!facts.IsGit)
        {
            grid.AddRow("[grey]Git[/]", "[grey]not a git repo[/]");
            return;
        }

        var parts = new List<string>();
        if (facts.RootCommit is { } root)
            parts.Add($"root {Short(root)} on {facts.DefaultBranch}");
        if (facts.Shallow)
            parts.Add("shallow clone");
        if ((facts.Remotes.FirstOrDefault(r => r.Name == "origin") ?? facts.Remotes.FirstOrDefault()) is { } remote)
            parts.Add($"{remote.Name} {remote.Url}");
        if (facts.MainWorktree is { } main && facts.TopLevel is { } top && !string.Equals(main, top, StringComparison.Ordinal))
            parts.Add($"worktree of {main}");
        grid.AddRow("[grey]Git[/]", Markup.Escape(string.Join(", ", parts)));
    }

    private static void RenderSuggestions(RemoteProject project)
    {
        if (project.Suggestions.Count == 0)
            return;

        AnsiConsole.MarkupLine("This folder might belong to another project. Nothing is linked until you say so:");
        foreach (var suggestion in project.Suggestions)
        {
            AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(suggestion.Name)}[/] [grey]({suggestion.ProjectId}): {Markup.Escape(suggestion.Reason)}[/]");
            AnsiConsole.MarkupLine($"    protostar project link {suggestion.ProjectId}");
        }
    }

    private static string Recognition(string recognisedBy, ProjectFacts facts) => recognisedBy switch
    {
        "link" => "a link you made from this folder (`protostar project unlink` removes it)",
        "rootCommit" => $"the repo's root commit {Short(facts.RootCommit)}",
        "previousRootCommit" => "a root commit this checkout had before its history changed",
        "remote" => "its remote: a shallow clone's own root commit cannot be trusted",
        "folder" => facts.IsGit ? "this machine and path (a shallow clone of a remote no full clone has reported yet)" : "this machine and path (not a git repo)",
        "new" => facts.RootCommit is { } root
            ? $"the repo's root commit {Short(root)}, which no backup has reported yet"
            : "this machine and path, which no backup has reported yet",
        _ => recognisedBy,
    };

    private static string Short(string? commit) => commit is null ? "(none)" : commit[..Math.Min(12, commit.Length)];

    private static string Problem(ProjectFailure failure, string? detail, Uri? registry)
    {
        var authority = registry?.GetLeftPart(UriPartial.Authority) ?? "";
        return failure switch
        {
            ProjectFailure.NotLoggedIn => $"not logged in to {authority}. Run `protostar auth login` first.",
            ProjectFailure.SessionExpired => $"your session for {authority} has expired. Run `protostar auth login` again.",
            ProjectFailure.InvalidRegistry => detail ?? "invalid registry URL.",
            _ => detail ?? $"could not reach {authority}.",
        };
    }
}
