using System.ComponentModel;
using Protostar.Cli.Agents;
using Protostar.Cli.Harness;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>Options shared by <c>protostar agents delete</c> and <c>agents rename</c>: which registry, and which scope the name is in.</summary>
internal class AgentChangeSettings : CommandSettings
{
    [CommandOption("--project <DIR>")]
    [Description("Only the agent of this folder's project. Without it, the current directory's project agent or the personal agent of that name, whichever exists.")]
    public string? Project { get; init; }

    [CommandOption("--personal-only")]
    [Description("Only the personal agent of that name, even when this folder's project has one.")]
    public bool PersonalOnly { get; init; }

    [CommandOption("--registry <URL>")]
    [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the registry saved by setup or auth login, then the built-in dev default.")]
    public string? Registry { get; init; }

    public AgentTargetRequest Target(string name) =>
        PersonalOnly
            ? new(name, Registry, null)
            : new(name, Registry, Project ?? Directory.GetCurrentDirectory(), ProjectOnly: Project is not null);
}

/// <summary><c>protostar agents delete &lt;name&gt;</c>: deletes an agent and all its versions from the registry, never a local file.</summary>
internal sealed class AgentsDeleteCommand : AsyncCommand<AgentsDeleteCommand.Settings>
{
    private readonly IAgentChangeService _changes;
    private readonly IHarnessCatalog _catalog;

    public AgentsDeleteCommand(IAgentChangeService changes, IHarnessCatalog catalog)
    {
        _changes = changes;
        _catalog = catalog;
    }

    public sealed class Settings : AgentChangeSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("The registry agent's exact name.")]
        public string Name { get; init; } = "";

        [CommandOption("-y|--yes")]
        [Description("Delete without asking. Needed when input is not a terminal.")]
        public bool Yes { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var found = await _changes.FindAsync(settings.Target(settings.Name), cancellation);
        if (AgentChangePresenter.RenderFindFailure(found, _catalog) is { } failed)
            return failed;
        if (AgentChangePresenter.ConfirmDelete(found.Target!, settings.Yes, Console.IsInputRedirected) is { } declined)
            return declined;
        return AgentChangePresenter.RenderDelete(await _changes.DeleteAsync(found.Target!, cancellation));
    }
}

/// <summary><c>protostar agents rename &lt;old&gt; &lt;new&gt;</c>: renames a registry agent, keeping its history, and rewrites this machine's copies.</summary>
internal sealed class AgentsRenameCommand : AsyncCommand<AgentsRenameCommand.Settings>
{
    private readonly IAgentChangeService _changes;
    private readonly IHarnessCatalog _catalog;

    public AgentsRenameCommand(IAgentChangeService changes, IHarnessCatalog catalog)
    {
        _changes = changes;
        _catalog = catalog;
    }

    public sealed class Settings : AgentChangeSettings
    {
        [CommandArgument(0, "<OLD>")]
        [Description("The registry agent's exact current name.")]
        public string OldName { get; init; } = "";

        [CommandArgument(1, "<NEW>")]
        [Description("The name it takes. Its files keep their paths; only the `name:` line changes.")]
        public string NewName { get; init; } = "";
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var found = await _changes.FindAsync(settings.Target(settings.OldName), cancellation);
        if (AgentChangePresenter.RenderFindFailure(found, _catalog) is { } failed)
            return failed;
        return AgentChangePresenter.RenderRename(await _changes.RenameAsync(found.Target!, settings.NewName, cancellation));
    }
}
