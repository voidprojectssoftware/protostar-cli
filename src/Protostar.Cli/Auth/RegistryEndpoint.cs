namespace Protostar.Cli.Auth;

/// <summary>
/// Resolves which registry the CLI talks to: an explicit <c>--registry</c> value wins, then the
/// <c>PROTOSTAR_REGISTRY_URL</c> environment variable, then the built-in dev default.
/// </summary>
internal static class RegistryEndpoint
{
    public static Uri Resolve(string? option)
    {
        var raw = !string.IsNullOrWhiteSpace(option)
            ? option!
            : Environment.GetEnvironmentVariable(AuthConstants.RegistryEnvVar) is { Length: > 0 } env
                ? env
                : AuthConstants.DefaultRegistryUrl;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException($"Invalid registry URL '{raw}'. Use an absolute http(s) URL.");
        }

        return uri;
    }

    /// <summary>Stable credential key for a registry: scheme://host:port, no trailing slash.</summary>
    public static string CredentialKey(Uri registry) => registry.GetLeftPart(UriPartial.Authority);
}
