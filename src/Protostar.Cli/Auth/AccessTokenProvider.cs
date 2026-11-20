namespace Protostar.Cli.Auth;

/// <summary>
/// Production <see cref="IAccessTokenProvider"/>: reads the <see cref="ITokenStore"/> and refreshes an
/// expired access token through the registry's OIDC token endpoint.
/// </summary>
internal sealed class AccessTokenProvider : IAccessTokenProvider
{
    private readonly ITokenStore _tokens;

    public AccessTokenProvider(ITokenStore tokens) => _tokens = tokens;

    /// <inheritdoc />
    public async Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken)
    {
        var stored = _tokens.Load(registry);
        if (stored is null)
            return new SessionLookup(SessionState.None, null);
        if (!stored.IsExpired)
            return new SessionLookup(SessionState.Current, stored);
        if (stored.RefreshToken is not { Length: > 0 } refreshToken)
            return new SessionLookup(SessionState.Expired, stored);

        // Discovery is the first request to the registry, so it is where an unreachable registry shows. A token
        // request that fails after it is not thrown either: OidcClient returns it as an error result.
        var (oidc, _) = await OidcClientFactory.CreateAsync(registry, browser: null, cancellationToken);
        if (oidc is null)
            return new SessionLookup(SessionState.RegistryUnreachable, stored);

        var refreshed = await oidc.RefreshTokenAsync(refreshToken, backChannelParameters: null, scope: null, cancellationToken);
        if (refreshed.IsError || string.IsNullOrEmpty(refreshed.AccessToken))
            return new SessionLookup(SessionState.Expired, stored);

        stored = stored with
        {
            AccessToken = refreshed.AccessToken,
            RefreshToken = string.IsNullOrEmpty(refreshed.RefreshToken) ? stored.RefreshToken : refreshed.RefreshToken,
            ExpiresAtUtc = refreshed.AccessTokenExpiration,
        };
        _tokens.Save(stored);
        return new SessionLookup(SessionState.Current, stored);
    }
}
