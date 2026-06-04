using Duende.IdentityModel.OidcClient;

namespace Protostar.Cli.Auth;

/// <summary>
/// Builds the <see cref="OidcClient"/> the auth commands drive against a registry. The client speaks
/// OIDC against the registry's discovery document (<c>/.well-known/openid-configuration</c>), so the
/// authorize/token/userinfo endpoints are discovered rather than hand-built.
/// </summary>
internal static class OidcClientFactory
{
    /// <summary>
    /// Creates a client for the given registry. Pass a <paramref name="browser"/> for interactive
    /// login (it supplies the loopback redirect URI); omit it for refresh/userinfo-only use.
    /// </summary>
    public static OidcClient Create(Uri registry, LoopbackBrowser? browser = null)
    {
        var options = new OidcClientOptions
        {
            Authority = registry.GetLeftPart(UriPartial.Authority),
            ClientId = AuthConstants.ClientId,
            Scope = AuthConstants.Scopes,
            Browser = browser,
            RedirectUri = browser?.RedirectUri ?? string.Empty,

            // The registry advertises a plain authorize endpoint (no PAR), and a userinfo call after
            // login keeps the profile claims (incl. github_login) on the returned principal.
            DisablePushedAuthorization = true,
            LoadProfile = true,
        };

        return new OidcClient(options);
    }
}
