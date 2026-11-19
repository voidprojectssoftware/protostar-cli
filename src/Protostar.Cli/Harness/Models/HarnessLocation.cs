namespace Protostar.Cli.Harness;

/// <summary>A resolved harness install: where its config lives and where hook settings are stored.</summary>
/// <param name="ConfigDir">The harness config root.</param>
/// <param name="SettingsPath">The settings file hooks are written to.</param>
/// <param name="Pinned">
/// Whether the user named this root (<c>--harness-home</c> or <c>PROTOSTAR_HARNESS_ROOT</c>). A pinned root is
/// the only place a capability may read; it never falls back to the harness's default location.
/// </param>
internal sealed record HarnessLocation(string ConfigDir, string SettingsPath, bool Pinned = false);
