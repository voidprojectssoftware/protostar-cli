namespace Protostar.Cli.Harness;

/// <summary>
/// Capability: a harness protostar can wire capture hooks into. Implemented by providers whose config
/// supports event hooks (Claude Code's <c>settings.json</c>). Kept separate from <see cref="IHarness"/>
/// so a harness without a hook mechanism simply does not implement it, and the hook orchestration asks
/// for the capability (<c>harness is IHookCapability</c>) rather than assuming every harness has one.
/// </summary>
/// <remarks>
/// Both methods are idempotent: running again against settings already in the desired state reports
/// <see cref="HookChange.Unchanged"/> and writes nothing. When <c>dryRun</c> is true the change is
/// computed and returned but not persisted. An existing settings file that is not valid JSON is left
/// untouched and the call throws rather than risk overwriting unrecognised content.
/// </remarks>
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
