namespace Protostar.Cli.Auth;

/// <summary>Resolves which registry the CLI talks to, and from where that choice came.</summary>
/// <remarks>
/// Precedence, the same for every command (capture hooks included): <c>--registry</c>, then
/// <c>PROTOSTAR_REGISTRY_URL</c>, then the default saved in <see cref="ProtostarPaths.ConfigFile"/> by
/// <c>auth login</c> or <c>setup</c>, then <see cref="AuthConstants.DefaultRegistryUrl"/>.
/// </remarks>
internal static class RegistryEndpoint
{
    public static Uri Resolve(string? option) => ResolveWithSource(option).Url;

    /// <exception cref="FormatException">The chosen value is not an absolute http(s) URL; the message names its source.</exception>
    public static ResolvedRegistry ResolveWithSource(string? option)
    {
        var (raw, source) =
            !string.IsNullOrWhiteSpace(option) ? (option!, RegistrySource.Flag)
            : Environment.GetEnvironmentVariable(AuthConstants.RegistryEnvVar) is { Length: > 0 } env ? (env, RegistrySource.Environment)
            : DefaultRegistryStore.Load() is { } saved ? (saved, RegistrySource.ConfigFile)
            : (AuthConstants.DefaultRegistryUrl, RegistrySource.Default);

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            var where = source == RegistrySource.ConfigFile ? $" in {ProtostarPaths.ConfigFile()}" : string.Empty;
            throw new FormatException($"Invalid registry URL '{raw}'{where}. Use an absolute http(s) URL.");
        }

        return new ResolvedRegistry(uri, source);
    }

    /// <summary>Stable credential key for a registry: scheme://host:port, no trailing slash.</summary>
    public static string CredentialKey(Uri registry) => registry.GetLeftPart(UriPartial.Authority);
}

/// <summary>Where the registry in use was chosen.</summary>
internal enum RegistrySource
{
    /// <summary>The <c>--registry</c> option.</summary>
    Flag,

    /// <summary>The <c>PROTOSTAR_REGISTRY_URL</c> environment variable.</summary>
    Environment,

    /// <summary>The default saved by <c>auth login</c> or <c>setup</c> in <see cref="ProtostarPaths.ConfigFile"/>.</summary>
    ConfigFile,

    /// <summary>The built-in <see cref="AuthConstants.DefaultRegistryUrl"/>.</summary>
    Default,
}

/// <summary>The registry in use and where it was chosen.</summary>
internal sealed record ResolvedRegistry(Uri Url, RegistrySource Source);
