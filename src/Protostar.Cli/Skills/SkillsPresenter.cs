using Protostar.Cli.Harness;
using Spectre.Console;

namespace Protostar.Cli.Skills;

/// <summary>
/// The console face of <see cref="ISkillService"/>: renders a <see cref="SkillDiscoveryResult"/> as a
/// table, or the matching error/empty message, and returns the process exit code. Kept out of the
/// service so discovery stays presentation-free and testable, mirroring
/// <c>HookInstallPresenter</c> for the hooks concern.
/// </summary>
internal static class SkillsPresenter
{
    /// <summary>Render a discovery result and return the process exit code (0 success, 1 a query failure).</summary>
    public static int Render(SkillDiscoveryResult result, IHarnessCatalog catalog)
    {
        switch (result.Failure)
        {
            case SkillQueryFailure.UnknownHarness:
                AnsiConsole.MarkupLine(
                    $"[red]Unknown harness '{Markup.Escape(result.OffendingHarnessId!)}'.[/] " +
                    $"Known: {catalog.KnownIds}");
                return 1;
            case SkillQueryFailure.Unsupported:
                AnsiConsole.MarkupLine(
                    $"[red]Harness '{Markup.Escape(result.OffendingHarnessId!)}' does not support skill discovery.[/]");
                return 1;
        }

        if (result.Skills.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No skills found.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Scope");
        table.AddColumn("Name");
        table.AddColumn("Description");
        foreach (var skill in result.Skills)
        {
            // The placeholder is markup, so it must not be escaped; a real description must be.
            var description = skill.Description is null
                ? "[grey](no description)[/]"
                : Markup.Escape(Truncate(skill.Description));
            table.AddRow(
                Markup.Escape(skill.Scope.ToString().ToLowerInvariant()),
                Markup.Escape(skill.Name),
                description);
        }
        AnsiConsole.Write(table);
        return 0;
    }

    private const string Ellipsis = "...";

    // internal (not private) so the unit suite can exercise the truncation edge cases directly.
    internal static string Truncate(string text, int max = 80)
    {
        if (max <= 0) return string.Empty;
        if (text.Length <= max) return text;
        // Too narrow to fit any text plus the ellipsis: just return as many dots as fit.
        if (max <= Ellipsis.Length) return Ellipsis[..max];
        return text[..(max - Ellipsis.Length)] + Ellipsis;
    }
}
