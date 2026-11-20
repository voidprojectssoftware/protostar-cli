using System.ComponentModel;
using Protostar.Cli.Harness;
using Protostar.Cli.Infrastructure;
using Protostar.Cli.Memory;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>Options shared by the <c>protostar memory</c> commands.</summary>
internal class MemorySettings : SessionsSettings
{
    [CommandOption("--harness-home <DIR>")]
    [Description("Override the harness config root (testing or a non-default location).")]
    public string? HarnessHome { get; init; }
}

/// <summary><c>protostar memory push</c>: backs up the agent memory on this machine.</summary>
internal sealed class MemoryPushCommand : AsyncCommand<MemoryPushCommand.Settings>
{
    private readonly IMemorySyncService _memory;
    private readonly IHarnessCatalog _catalog;

    public MemoryPushCommand(IMemorySyncService memory, IHarnessCatalog catalog)
    {
        _memory = memory;
        _catalog = catalog;
    }

    public sealed class Settings : MemorySettings;

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        using var ctrlC = new CtrlCCancellation(cancellation);
        var result = await _memory.PushAsync(new MemoryPushRequest(settings.Registry, settings.HarnessHome), ctrlC.Token);
        return MemoryPresenter.RenderPush(result, _catalog);
    }
}

/// <summary><c>protostar memory status</c>: which memory files on this machine are not backed up. Works offline.</summary>
internal sealed class MemoryStatusCommand : Command<MemoryStatusCommand.Settings>
{
    private readonly IMemorySyncService _memory;
    private readonly IHarnessCatalog _catalog;

    public MemoryStatusCommand(IMemorySyncService memory, IHarnessCatalog catalog)
    {
        _memory = memory;
        _catalog = catalog;
    }

    public sealed class Settings : MemorySettings;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation) =>
        MemoryPresenter.RenderStatus(_memory.Status(settings.Registry, settings.HarnessHome), _catalog);
}

/// <summary><c>protostar memory pull</c>: brings the registry's memory to the local checkouts of each project.</summary>
internal sealed class MemoryPullCommand : AsyncCommand<MemoryPullCommand.Settings>
{
    private readonly IMemorySyncService _memory;
    private readonly IHarnessCatalog _catalog;

    public MemoryPullCommand(IMemorySyncService memory, IHarnessCatalog catalog)
    {
        _memory = memory;
        _catalog = catalog;
    }

    public sealed class Settings : MemorySettings
    {
        [CommandOption("--scan <DIR>")]
        [Description("Also look for checkouts in this folder and its direct subfolders (repeatable). Without it, only folders your harness has opened are found.")]
        public string[] Scan { get; init; } = [];
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        using var ctrlC = new CtrlCCancellation(cancellation);
        var result = await _memory.PullAsync(new MemoryPullRequest(settings.Registry, settings.HarnessHome, settings.Scan), ctrlC.Token);
        return MemoryPresenter.RenderPull(result, _catalog);
    }
}
