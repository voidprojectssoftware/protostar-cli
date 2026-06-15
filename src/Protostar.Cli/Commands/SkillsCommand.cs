using System.ComponentModel;
using Protostar.Cli.Harness;
using Protostar.Cli.Skills;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// Lists the skills protostar can see on disk, in both global and project scope, across every harness
/// that supports skill discovery. The read side of "push a local skill". Discovery lives in
/// <see cref="SkillService"/> and rendering in <see cref="SkillsPresenter"/>; this command only maps
/// its settings into a query and hands the result to the presenter.
/// </summary>
internal sealed class SkillsCommand : Command<SkillsCommand.Settings>
{
    private readonly ISkillService _skills;
    private readonly IHarnessCatalog _catalog;

    public SkillsCommand(ISkillService skills, IHarnessCatalog catalog)
    {
        _skills = skills;
        _catalog = catalog;
    }

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

        return SkillsPresenter.Render(result, _catalog);
    }

    // The starting point each provider walks up from to find its own project root.
    // null means "skip project scope"; otherwise honor an explicit --project or fall back to the current directory.
    private static string? ResolveProjectStart(Settings settings)
    {
        if (settings.GlobalOnly)
            return null;

        return settings.Project ?? Directory.GetCurrentDirectory();
    }
}
