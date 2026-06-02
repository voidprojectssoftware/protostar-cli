namespace Protostar.Cli.Auth;

/// <summary>
/// The CLI/registry compatibility contract. Versions are not lockstepped; instead the registry
/// advertises the API majors it supports at <c>/v1/meta</c> and the CLI checks that the major it
/// speaks is among them before doing anything else.
/// </summary>
internal static class ApiCompatibility
{
    /// <summary>Returns null when compatible, otherwise a human-readable explanation.</summary>
    public static string? Check(RegistryMeta? meta)
    {
        if (meta?.ApiMajors is not { Length: > 0 } majors)
            return "The registry did not report a supported API version.";

        if (!majors.Contains(AuthConstants.SupportedApiMajor))
        {
            var supported = string.Join(", ", majors.Select(m => $"v{m}"));
            return $"This protostar build speaks registry API v{AuthConstants.SupportedApiMajor}, " +
                   $"but the registry supports {supported}. Update protostar to a compatible version.";
        }

        return null;
    }
}
