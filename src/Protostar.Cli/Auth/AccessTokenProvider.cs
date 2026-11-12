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
    public async Task<StoredToken?> GetSessionAsync(Uri registry, CancellationToken cancellationToken)
    {
        var stored = _tokens.Load(registry);
        if (stored is null || !stored.IsExpired || stored.RefreshToken is not { Length: > 0 } refreshToken)
            return stored;

        try
        {
            var oidc = OidcClientFactory.Create(registry);
            var refreshed = await oidc.RefreshTokenAsync(refreshToken, backChannelParameters: null, scope: null, cancellationToken);
            if (refreshed.IsError || string.IsNullOrEmpty(refreshed.AccessToken))
                return stored;

            stored = stored with
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = string.IsNullOrEmpty(refreshed.RefreshToken) ? stored.RefreshToken : refreshed.RefreshToken,
                ExpiresAtUtc = refreshed.AccessTokenExpiration,
            };
            _tokens.Save(stored);
        }
        catch (HttpRequestException)
        {
            // Registry unreachable: hand back the stale session and let the caller decide.
        }

        return stored;
    }
}
