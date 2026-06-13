using System.ComponentModel;
using Protostar.Cli.Harness;
using Protostar.Cli.Skills;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// Lists the skills protostar can see on disk, in both global and project scope, across every harness
/// that supports skill discovery. The read side of "push a local skill". Discovery itself lives in
/// <see cref="SkillService"/>; this command only maps its settings into a query and renders the result.
/// </summary>
internal sealed class SkillsCommand : Command<SkillsCommand.Settings>
{
    private readonly ISkillService _skills;

    public SkillsCommand(ISkillService skills) => _skills = skills;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-H|--harness <ID>")]
        [Description("Only discover skills for this harness id.")]
        public string? Harness { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root (testing or a non-default location).")]
        public string? HarnessHome { get; init; }

        [CommandOption("--project <DIR>")]
        [Description("Where to start looking for project-scoped skills. Defaults to the current directory.")]
        public string? Project { get; init; }

        [CommandOption("--global-only")]
        [Description("Discover only global skills; skip the project scope.")]
        public bool GlobalOnly { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var result = _skills.Discover(
            settings.Harness,
            settings.HarnessHome,
            ResolveProjectStart(settings));

        switch (result.Failure)
        {
            case SkillQueryFailure.UnknownHarness:
                AnsiConsole.MarkupLine(
                    $"[red]Unknown harness '{Markup.Escape(result.OffendingHarnessId!)}'.[/] " +
                    $"Known: {string.Join(", ", HarnessRegistry.All.Select(h => h.Id))}");
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

    // The starting point each provider walks up from to find its own project root.
    // null means "skip project scope"; otherwise honor an explicit --project or fall back to the current directory.
    private static string? ResolveProjectStart(Settings settings)
    {
        if (settings.GlobalOnly)
            return null;

        return settings.Project ?? Directory.GetCurrentDirectory();
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
