namespace Protostar.Cli.Harness;

/// <summary>A resolved harness install: where its config lives and where hook settings are stored.</summary>
internal sealed record HarnessLocation(string ConfigDir, string SettingsPath);
