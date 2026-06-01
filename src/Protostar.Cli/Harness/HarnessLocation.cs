namespace Protostar.Cli.Harness;

/// <summary>A resolved harness install: where its config lives and where hook settings are stored.</summary>
internal sealed record HarnessLocation(string ConfigDir, string SettingsPath);

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
