using System.ComponentModel;
using Protostar.Cli.Projects;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>Options shared by the <c>protostar project</c> commands.</summary>
internal class ProjectSettings : CommandSettings
{
    [CommandOption("--registry <URL>")]
    [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the registry saved by setup or auth login, then the built-in dev default.")]
    public string? Registry { get; init; }

    [CommandOption("-C|--dir <DIR>")]
    [Description("The folder to ask about. Defaults to the current directory.")]
    public string? Directory { get; init; }

    public string Folder => Directory ?? Environment.CurrentDirectory;
}

/// <summary><c>protostar project</c>: which project this folder belongs to, and how it was recognised.</summary>
internal sealed class ProjectShowCommand : AsyncCommand<ProjectShowCommand.Settings>
{
    private readonly IProjectService _projects;

    public ProjectShowCommand(IProjectService projects) => _projects = projects;

    public sealed class Settings : ProjectSettings
    {
        [CommandOption("--json")]
        [Description("Print JSON instead of text, for agents and scripts.")]
        public bool Json { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation) =>
        ProjectPresenter.RenderShow(await _projects.ShowAsync(settings.Registry, settings.Folder, cancellation), settings.Json);
}

/// <summary><c>protostar project link &lt;project&gt;</c>: links this folder to a project.</summary>
internal sealed class ProjectLinkCommand : AsyncCommand<ProjectLinkCommand.Settings>
{
    private readonly IProjectService _projects;

    public ProjectLinkCommand(IProjectService projects) => _projects = projects;

    public sealed class Settings : ProjectSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("The project's id, or its full name as `protostar project` or `sessions list` shows it.")]
        public string Project { get; init; } = "";
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation) =>
        ProjectPresenter.RenderChange(await _projects.LinkAsync(settings.Registry, settings.Folder, settings.Project, cancellation));
}

/// <summary><c>protostar project unlink</c>: removes this folder's link, or gives a fork's folder a project of its own.</summary>
internal sealed class ProjectUnlinkCommand : AsyncCommand<ProjectSettings>
{
    private readonly IProjectService _projects;

    public ProjectUnlinkCommand(IProjectService projects) => _projects = projects;

    protected override async Task<int> ExecuteAsync(CommandContext context, ProjectSettings settings, CancellationToken cancellation) =>
        ProjectPresenter.RenderChange(await _projects.UnlinkAsync(settings.Registry, settings.Folder, cancellation));
}
