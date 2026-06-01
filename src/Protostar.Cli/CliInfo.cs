using System.Reflection;

namespace Protostar.Cli;

/// <summary>Static CLI metadata. Version is stamped at build time by MinVer from the latest
/// reachable git tag and surfaced via <c>protostar --version</c>.</summary>
internal static class CliInfo
{
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var info = typeof(CliInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrEmpty(info))
            return "0.0.0";
        // MinVer/SourceLink append "+<git-sha>" build metadata; trim it for display.
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
