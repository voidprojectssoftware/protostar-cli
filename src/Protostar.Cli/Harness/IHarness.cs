namespace Protostar.Cli.Harness;

/// <summary>
/// A coding harness protostar can wire capture hooks into (Claude Code, and others in future).
/// Each integration lives behind this boundary so harness-specific knowledge — config locations,
/// settings schema, hook event names — stays isolated. Supporting a new harness means adding one
/// implementation and registering it in <see cref="HarnessRegistry"/>; the command and
/// orchestration layers never change.
/// </summary>
internal interface IHarness
{
    /// <summary>Stable slug used as the <c>--harness</c> selector value, e.g. <c>claude-code</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable name, e.g. <c>Claude Code</c>.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Resolve where this harness keeps its config and decide whether it is present.
    /// <paramref name="rootOverride"/> (from <c>--harness-home</c>) takes precedence over the
    /// harness's own environment variables and defaults. Returns <c>false</c> when the harness is
    /// not detected (and was not explicitly pointed at via an override).
    /// </summary>
    bool TryLocate(string? rootOverride, out HarnessLocation location);

    /// <summary>
    /// Idempotently add protostar's capture hooks, preserving all other settings.
    /// <paramref name="exePath"/> is the absolute protostar binary the hooks should invoke.
    /// </summary>
    HookChangeSet InstallHooks(HarnessLocation location, string exePath, bool dryRun);

    /// <summary>Remove protostar's capture hooks, leaving all other settings untouched.</summary>
    HookChangeSet RemoveHooks(HarnessLocation location, bool dryRun);
}
