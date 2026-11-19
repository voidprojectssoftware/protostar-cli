using System.ComponentModel;
using Protostar.Cli.Harness;
using Protostar.Cli.Skills;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// <c>protostar skills remote</c>: lists the skills in the registry and how each compares with this machine.
/// </summary>
/// <remarks>
/// The comparison is in <see cref="SkillPullService"/> and the output in <see cref="SkillPullPresenter"/>.
/// </remarks>
internal sealed class SkillsRemoteCommand : AsyncCommand<SkillsRemoteCommand.Settings>
{
    private readonly ISkillPullService _pull;
    private readonly IHarnessCatalog _catalog;

    public SkillsRemoteCommand(ISkillPullService pull, IHarnessCatalog catalog)
    {
        _pull = pull;
        _catalog = catalog;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-H|--harness <ID>")]
        [Description("Only compare with this harness id.")]
        public string? Harness { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root (testing or a non-default location).")]
        public string? HarnessHome { get; init; }

        [CommandOption("--project <DIR>")]
        [Description("The folder whose project skills to compare. Defaults to the current directory.")]
        public string? Project { get; init; }

        [CommandOption("--global-only")]
        [Description("Only compare global skills; leave project skills alone.")]
        public bool GlobalOnly { get; init; }

        [CommandOption("--registry <URL>")]
        [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the built-in dev default.")]
        public string? Registry { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var result = await _pull.CompareAsync(new SkillRemoteRequest(settings.Harness, settings.HarnessHome, settings.Registry, ProjectStart(settings)), cancellation);
        return SkillPullPresenter.RenderRemote(result, _catalog);
    }

    private static string? ProjectStart(Settings settings) =>
        settings.GlobalOnly ? null : settings.Project ?? Directory.GetCurrentDirectory();
}
