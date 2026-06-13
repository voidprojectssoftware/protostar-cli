namespace Protostar.Cli.Harness;

/// <summary>How an install/remove changed a harness's settings.</summary>
internal enum HookChange
{
    Unchanged,
    Added,
    Updated,
    Removed,
}

/// <summary>Outcome of installing or removing capture hooks against one harness.</summary>
internal sealed record HookChangeSet(HookChange Change, string SettingsPath, string Detail);
