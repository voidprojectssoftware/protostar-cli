namespace Protostar.Cli.Auth;

/// <summary>
/// Resolves protostar's per-user config directory. Defaults to <c>~/.protostar</c> (the
/// <c>.aws</c>/<c>.kube</c> convention), overridable with <c>PROTOSTAR_CONFIG_DIR</c> so tests and
/// power users can redirect it away from the real home.
/// </summary>
internal static class ProtostarPaths
{
    public const string ConfigDirEnvVar = "PROTOSTAR_CONFIG_DIR";

    public static string ConfigDir()
    {
        var overridden = Environment.GetEnvironmentVariable(ConfigDirEnvVar);
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".protostar");
    }

    public static string CredentialsFile() => Path.Combine(ConfigDir(), "credentials.json");
}
