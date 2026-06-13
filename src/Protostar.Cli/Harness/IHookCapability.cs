namespace Protostar.Cli.Harness;

/// <summary>
/// Capability: a harness protostar can wire capture hooks into. Implemented by providers whose config
/// supports event hooks (Claude Code's <c>settings.json</c>). Kept separate from <see cref="IHarness"/>
/// so a harness without a hook mechanism simply does not implement it, and the hook orchestration asks
/// for the capability (<c>harness is IHookCapability</c>) rather than assuming every harness has one.
/// </summary>
internal interface IHookCapability
{
    /// <summary>
    /// Idempotently add protostar's capture hooks, preserving all other settings.
    /// <paramref name="exePath"/> is the absolute protostar binary the hooks should invoke.
    /// </summary>
    HookChangeSet InstallHooks(HarnessLocation location, string exePath, bool dryRun);

    /// <summary>Remove protostar's capture hooks, leaving all other settings untouched.</summary>
    HookChangeSet RemoveHooks(HarnessLocation location, bool dryRun);
}
