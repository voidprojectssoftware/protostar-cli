namespace Protostar.Cli.Hooks;

/// <summary>
/// Inputs for a capture-hook install or remove run. A standalone type shared by every
/// <see cref="IHookInstallService"/> implementation and the commands that drive them, so the
/// abstraction never depends on a concrete service's nested types.
/// </summary>
internal sealed record HookInstallOptions
{
    /// <summary><c>--harness-home</c>: override the harness config root.</summary>
    public string? RootOverride { get; init; }

    /// <summary><c>--harness</c>: target these harness ids explicitly. Skips detection and selection.</summary>
    public IReadOnlyList<string>? HarnessIds { get; init; }

    /// <summary><c>--dry-run</c>: report intended changes without writing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Path to the protostar binary the hooks should invoke. Defaults to this process.</summary>
    public string? ExePathOverride { get; init; }
}
