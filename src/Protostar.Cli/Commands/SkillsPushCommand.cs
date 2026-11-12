using System.ComponentModel;
using Protostar.Cli.Harness;
using Protostar.Cli.Skills;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// <c>protostar skills push [NAME...]</c>: backs up local skills to the registry.
/// </summary>
/// <remarks>
/// With no names it pushes every skill <c>protostar skills</c> would list. The work is in
/// <see cref="SkillPushService"/> and the output in <see cref="SkillPushPresenter"/>.
/// </remarks>
internal sealed class SkillsPushCommand : AsyncCommand<SkillsPushCommand.Settings>
{
    private readonly ISkillPushService _push;
    private readonly IHarnessCatalog _catalog;

    public SkillsPushCommand(ISkillPushService push, IHarnessCatalog catalog)
    {
        _push = push;
        _catalog = catalog;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[NAME]")]
        [Description("Skill name(s) to back up. Omit to back up every skill found.")]
        public string[] Names { get; init; } = [];

        [CommandOption("-H|--harness <ID>")]
        [Description("Only back up skills from this harness id.")]
        public string? Harness { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root (testing or a non-default location).")]
        public string? HarnessHome { get; init; }

        [CommandOption("--project <DIR>")]
        [Description("Where to start looking for project-scoped skills. Defaults to the current directory.")]
        public string? Project { get; init; }

        [CommandOption("--global-only")]
        [Description("Back up only global skills; skip the project scope.")]
        public bool GlobalOnly { get; init; }

        [CommandOption("--registry <URL>")]
        [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the built-in dev default.")]
        public string? Registry { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var request = new SkillPushRequest(
            settings.Names,
            settings.Harness,
            settings.HarnessHome,
            settings.GlobalOnly ? null : settings.Project ?? Directory.GetCurrentDirectory(),
            settings.Registry);

        var result = await _push.PushAsync(request, cancellation);
        return SkillPushPresenter.Render(result, _catalog);
    }
}
