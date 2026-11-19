using Protostar.Cli.Harness;
using Spectre.Console;

namespace Protostar.Cli.Skills;

/// <summary>The console face of <see cref="ISkillChangeService"/>: what a delete or rename found, the delete confirmation, and what happened.</summary>
internal static class SkillChangePresenter
{
    /// <summary>Prints why the skill could not be found and returns the exit code, or returns <c>null</c> when it was found.</summary>
    public static int? RenderFindFailure(SkillTargetResult result, IHarnessCatalog catalog)
    {
        if (result.Failure != SkillPullRunFailure.SkillNotFound)
            return SkillPullPresenter.Failure(result.Failure, result.Detail, result.Registry, catalog);

        AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Detail ?? "No such skill.")}[/]");
        return 1;
    }

    /// <summary>Shows what a delete would remove and asks to go ahead; returns <c>null</c> to go ahead, otherwise the exit code.</summary>
    /// <param name="target">The skill to delete.</param>
    /// <param name="yes"><c>--yes</c>: go ahead without asking.</param>
    /// <param name="inputRedirected">Standard input is not a terminal, so there is nobody to ask.</param>
    /// <remarks>Without <c>--yes</c> and with no terminal to ask on, it refuses rather than waiting for an answer that never comes.</remarks>
    public static int? ConfirmDelete(SkillTarget target, bool yes, bool inputRedirected)
    {
        AnsiConsole.MarkupLine($"This deletes the {Describe(target)} from [grey]{Markup.Escape(Authority(target.Registry))}[/], {Versions(target.Skill.Version)}. It cannot be undone.");
        foreach (var copy in target.Copies)
        {
            AnsiConsole.MarkupLine(copy.HasLocalChanges
                ? $"[yellow]This machine has a copy with changes that were never pushed:[/] {Markup.Escape(copy.Path)}"
                : $"[grey]This machine has a copy at {Markup.Escape(copy.Path)}.[/]");
        }
        if (target.Copies.Count > 0)
            AnsiConsole.MarkupLine("[grey]Local copies are not deleted: only the registry's copy goes.[/]");

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

    /// <summary>Renders a delete and returns the exit code: 0 when the skill is gone, 1 when the registry refused.</summary>
    public static int RenderDelete(SkillDeleteResult result)
    {
        if (result.Error is { } error)
        {
            AnsiConsole.MarkupLine($"[red]Not deleted: {Markup.Escape(error)}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"Deleted the {Describe(result.Target)} and its versions from [grey]{Markup.Escape(Authority(result.Target.Registry))}[/].");
        AnsiConsole.MarkupLine(result.Target.Copies.Count == 0
            ? "[grey]No local copy was touched.[/]"
            : $"Local {(result.Target.Copies.Count == 1 ? "copy is" : "copies are")} untouched: {Markup.Escape(string.Join(", ", result.Target.Copies.Select(c => c.Path)))}. " +
              "`protostar skills remote` lists it as only on this machine. Once it differs from what was last pushed, the next push (or the capture hook) backs it up again as a new skill.");
        return 0;
    }

    /// <summary>Renders a rename and returns the exit code: 0 when renamed, 1 when the registry refused or the follow-up push failed.</summary>
    public static int RenderRename(SkillRenameResult result)
    {
        var target = result.Target;
        if (result.Error is { } error)
        {
            AnsiConsole.MarkupLine($"[red]Not renamed: {Markup.Escape(error)}[/]");
            return 1;
        }

        if (result.NewName == target.Skill.Name)
        {
            AnsiConsole.MarkupLine($"The {Describe(target)} already has that name. Nothing changed.");
            return 0;
        }

        AnsiConsole.MarkupLine($"Renamed the {Describe(target)} to [bold]{Markup.Escape(result.NewName!)}[/] in [grey]{Markup.Escape(Authority(target.Registry))}[/]. Its version history came with it.");

        var code = 0;
        if (result.Push is { } push)
        {
            if (push.Succeeded)
            {
                AnsiConsole.MarkupLine($"Its SKILL.md now names it [bold]{Markup.Escape(result.NewName!)}[/]: pushed as v{push.Version}.");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]Its SKILL.md still names it '{Markup.Escape(target.Skill.Name)}': pushing the renamed SKILL.md failed ({Markup.Escape(push.Message ?? "no reason given")}). Push a copy with the new name to finish.[/]");
                code = 1;
            }
        }

        foreach (var copy in result.Copies)
        {
            var moved = copy.Path != copy.OldPath ? $"{copy.OldPath} -> {copy.Path}" : copy.Path;
            var what = copy.ManifestRenamed ? "renamed here" : "kept its SKILL.md (it declares no name)";
            AnsiConsole.MarkupLine($"Local copy {what}: {Markup.Escape(moved)}" + (copy.Note is { } note ? $" [yellow]({Markup.Escape(note)})[/]" : ""));
        }
        if (result.Copies.Count == 0)
            AnsiConsole.MarkupLine("[grey]This machine has no copy of it.[/]");
        AnsiConsole.MarkupLine("[grey]Other machines still have it under the old name: there it shows as only on that machine, and the new name as not installed, until they pull it and remove the old folder.[/]");
        return code;
    }

    private static string Describe(SkillTarget target) =>
        target.Skill.ProjectId is null
            ? $"global skill [bold]{Markup.Escape(target.Skill.Name)}[/]"
            : $"skill [bold]{Markup.Escape(target.Skill.Name)}[/] of project [bold]{Markup.Escape(target.Skill.ProjectName ?? target.Skill.ProjectId.ToString()!)}[/]";

    private static string Versions(int count) => count == 1 ? "with its only version" : $"with all {count} of its versions";

    private static string Authority(Uri registry) => registry.GetLeftPart(UriPartial.Authority);
}
