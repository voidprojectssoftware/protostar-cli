using System.ComponentModel;
using Protostar.Cli.Harness;
using Protostar.Cli.Skills;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// <c>protostar skills pull [NAME...]</c>: installs or updates skills from the registry on this machine.
/// </summary>
/// <remarks>
/// With no names it pulls every skill in the registry. The work is in <see cref="SkillPullService"/> and
/// the output in <see cref="SkillPullPresenter"/>.
/// </remarks>
internal sealed class SkillsPullCommand : AsyncCommand<SkillsPullCommand.Settings>
{
    private readonly ISkillPullService _pull;
    private readonly IHarnessCatalog _catalog;

    public SkillsPullCommand(ISkillPullService pull, IHarnessCatalog catalog)
    {
        _pull = pull;
        _catalog = catalog;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[NAME]")]
        [Description("Registry skill name(s) to pull. Omit to pull every skill in the registry.")]
        public string[] Names { get; init; } = [];

        [CommandOption("-H|--harness <ID>")]
        [Description("Only install into this harness id.")]
        public string? Harness { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root (testing or a non-default location).")]
        public string? HarnessHome { get; init; }

        [CommandOption("--registry <URL>")]
        [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the built-in dev default.")]
        public string? Registry { get; init; }

        [CommandOption("--force")]
        [Description("Overwrite skills that have local changes.")]
        public bool Force { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var request = new SkillPullRequest(settings.Names, settings.Harness, settings.HarnessHome, settings.Registry, settings.Force);
        var result = await _pull.PullAsync(request, cancellation);
        return SkillPullPresenter.RenderPull(result, _catalog);
    }
}
