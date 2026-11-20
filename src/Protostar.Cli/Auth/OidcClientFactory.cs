using Duende.IdentityModel.Client;
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
    /// Loads the registry's discovery document and creates a client for it. Pass a <paramref name="browser"/>
    /// for interactive login (it supplies the loopback redirect URI); omit it for refresh/userinfo-only use.
    /// </summary>
    /// <returns>The client, or <c>null</c> with the reason when the discovery document could not be loaded.</returns>
    /// <remarks>
    /// Left to itself, <see cref="OidcClient"/> loads the document on first use and throws
    /// <see cref="InvalidOperationException"/> when it cannot, which is how an unreachable registry used to surface as a
    /// raw "Error loading discovery document". Loading it here first makes that an ordinary result for callers.
    /// </remarks>
    public static async Task<(OidcClient? Client, string? Error)> CreateAsync(
        Uri registry, LoopbackBrowser? browser, CancellationToken cancellationToken)
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

            // The document was loaded just below; login would otherwise fetch it again.
            RefreshDiscoveryDocumentForLogin = false,
        };

        using var http = new HttpClient { Timeout = options.BackchannelTimeout };
        var discovery = await http.GetDiscoveryDocumentAsync(
            new DiscoveryDocumentRequest { Address = options.Authority, Policy = options.Policy.Discovery }, cancellationToken);
        if (discovery.IsError)
            return (null, discovery.Error);

        // The same fields OidcClient copies when it loads the document itself.
        options.ProviderInformation = new ProviderInformation
        {
            IssuerName = discovery.Issuer,
            KeySet = discovery.KeySet,
            AuthorizeEndpoint = discovery.AuthorizeEndpoint,
            TokenEndpoint = discovery.TokenEndpoint,
            EndSessionEndpoint = discovery.EndSessionEndpoint,
            UserInfoEndpoint = discovery.UserInfoEndpoint,
            PushedAuthorizationRequestEndpoint = discovery.PushedAuthorizationRequestEndpoint,
            TokenEndPointAuthenticationMethods = discovery.TokenEndpointAuthenticationMethodsSupported,
        };
        return (new OidcClient(options), null);
    }
}
