using System.ComponentModel;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Skills;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>Options shared by <c>protostar skills delete</c> and <c>skills rename</c>: which registry, and which scope the name is in.</summary>
internal class SkillChangeSettings : CommandSettings
{
    [CommandOption("--project <DIR>")]
    [Description("The folder whose project skill is meant. Defaults to the current directory; with no project skill of that name there, the global skill is meant.")]
    public string? Project { get; init; }

    [CommandOption("--global-only")]
    [Description("Only the global skill of that name, even when this folder's project has one.")]
    public bool GlobalOnly { get; init; }

    [CommandOption("--registry <URL>")]
    [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the registry saved by setup or auth login, then the built-in dev default.")]
    public string? Registry { get; init; }

    public SkillTargetRequest Target(string name, string needs) =>
        new(name, Registry, GlobalOnly ? null : Project ?? Directory.GetCurrentDirectory(), needs);
}

/// <summary><c>protostar skills delete &lt;name&gt;</c>: deletes a skill and all its versions from the registry, never the local copy.</summary>
internal sealed class SkillsDeleteCommand : AsyncCommand<SkillsDeleteCommand.Settings>
{
    private readonly ISkillChangeService _changes;
    private readonly IHarnessCatalog _catalog;

    public SkillsDeleteCommand(ISkillChangeService changes, IHarnessCatalog catalog)
    {
        _changes = changes;
        _catalog = catalog;
    }

    public sealed class Settings : SkillChangeSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("The registry skill's exact name.")]
        public string Name { get; init; } = "";

        [CommandOption("-y|--yes")]
        [Description("Delete without asking. Needed when input is not a terminal.")]
        public bool Yes { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var found = await _changes.FindAsync(settings.Target(settings.Name, RegistryCapability.SkillDelete), cancellation);
        if (SkillChangePresenter.RenderFindFailure(found, _catalog) is { } failed)
            return failed;
        if (SkillChangePresenter.ConfirmDelete(found.Target!, settings.Yes, Console.IsInputRedirected) is { } declined)
            return declined;
        return SkillChangePresenter.RenderDelete(await _changes.DeleteAsync(found.Target!, cancellation));
    }
}

/// <summary><c>protostar skills rename &lt;old&gt; &lt;new&gt;</c>: renames a registry skill, keeping its history, and brings this machine's copies along.</summary>
internal sealed class SkillsRenameCommand : AsyncCommand<SkillsRenameCommand.Settings>
{
    private readonly ISkillChangeService _changes;
    private readonly IHarnessCatalog _catalog;

    public SkillsRenameCommand(ISkillChangeService changes, IHarnessCatalog catalog)
    {
        _changes = changes;
        _catalog = catalog;
    }

    public sealed class Settings : SkillChangeSettings
    {
        [CommandArgument(0, "<OLD>")]
        [Description("The registry skill's exact current name.")]
        public string OldName { get; init; } = "";

        [CommandArgument(1, "<NEW>")]
        [Description("The name it takes.")]
        public string NewName { get; init; } = "";
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var found = await _changes.FindAsync(settings.Target(settings.OldName, RegistryCapability.SkillRename), cancellation);
        if (SkillChangePresenter.RenderFindFailure(found, _catalog) is { } failed)
            return failed;
        return SkillChangePresenter.RenderRename(await _changes.RenameAsync(found.Target!, settings.NewName, cancellation));
    }
}

/// <summary>
/// <c>protostar skills dismiss &lt;name&gt;</c>: keeps this machine's copy of a skill deleted or renamed in the registry
/// here as it is, unsynced, and stops listing it as a decision. Changes no file and nothing in the registry.
/// </summary>
internal sealed class SkillsDismissCommand : AsyncCommand<SkillsDismissCommand.Settings>
{
    private readonly ISkillPullService _pull;
    private readonly IHarnessCatalog _catalog;

    public SkillsDismissCommand(ISkillPullService pull, IHarnessCatalog catalog)
    {
        _pull = pull;
        _catalog = catalog;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("The local copy's skill name, as `protostar skills remote` lists it under \"gone from the registry\".")]
        public string[] Names { get; init; } = [];

        [CommandOption("-H|--harness <ID>")]
        [Description("Only this harness's copies.")]
        public string? Harness { get; init; }

        [CommandOption("--harness-home <DIR>")]
        [Description("Override the harness config root (testing or a non-default location).")]
        public string? HarnessHome { get; init; }

        [CommandOption("--project <DIR>")]
        [Description("The folder whose project skills count too. Defaults to the current directory.")]
        public string? Project { get; init; }

        [CommandOption("--global-only")]
        [Description("Only global skills.")]
        public bool GlobalOnly { get; init; }

        [CommandOption("--registry <URL>")]
        [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the registry saved by setup or auth login, then the built-in dev default.")]
        public string? Registry { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var request = new SkillPullRequest(settings.Names, settings.Harness, settings.HarnessHome, settings.Registry,
            ProjectStart: settings.GlobalOnly ? null : settings.Project ?? Directory.GetCurrentDirectory());
        return SkillPullPresenter.RenderDismiss(await _pull.DismissAsync(request, cancellation), _catalog);
    }
}
