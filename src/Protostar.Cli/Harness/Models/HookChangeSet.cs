namespace Protostar.Cli.Harness;

/// <summary>How an install/remove changed a harness's settings.</summary>
internal enum HookChange
{
    /// <summary>No change was needed; settings already matched the desired state.</summary>
    Unchanged,

    /// <summary>Hooks were written to a settings file that did not previously exist.</summary>
    Added,

    /// <summary>Hooks were written to a settings file that already existed.</summary>
    Updated,

    /// <summary>Existing protostar hooks were removed.</summary>
    Removed,
}

/// <summary>Outcome of installing or removing capture hooks against one harness.</summary>
internal sealed record HookChangeSet(HookChange Change, string SettingsPath, string Detail);
