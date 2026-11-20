using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Results;

namespace Protostar.Cli.Auth;

/// <summary>
/// Production <see cref="ISignInService"/>: OidcClient's Authorization Code + PKCE flow over a loopback redirect
/// (RFC 8252), with tokens kept in the <see cref="ITokenStore"/>.
/// </summary>
internal sealed class SignInService : ISignInService
{
    private readonly ITokenStore _tokens;
    private readonly IAccessTokenProvider _sessions;
    private readonly IDefaultRegistryStore _defaultRegistry;

    public SignInService(ITokenStore tokens, IAccessTokenProvider sessions, IDefaultRegistryStore defaultRegistry)
    {
        _tokens = tokens;
        _sessions = sessions;
        _defaultRegistry = defaultRegistry;
    }

    /// <inheritdoc />
    public async Task<SessionCheck> CheckAsync(Uri registry, CancellationToken cancellationToken)
    {
        var stored = await _sessions.GetSessionAsync(registry, cancellationToken);
        if (stored is null)
            return new SessionCheck(registry, null, SessionVerification.NoSession, null);

        UserInfoResult info;
        try
        {
            info = await OidcClientFactory.Create(registry).GetUserInfoAsync(stored.AccessToken, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new SessionCheck(registry, stored, SessionVerification.Unreachable, stored.Login);
        }

        if (info.IsError)
            return new SessionCheck(registry, stored, SessionVerification.Rejected, stored.Login);

        var login = info.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value
            ?? info.Claims.FirstOrDefault(c => c.Type == "github_login")?.Value
            ?? stored.Login;
        return new SessionCheck(registry, stored, SessionVerification.Verified, login);
    }

    /// <inheritdoc />
    public async Task<SignInResult> SignInAsync(Uri registry, SignInOptions options, CancellationToken cancellationToken)
    {
        using (var client = new RegistryClient(registry))
        {
            RegistryMeta? meta;
            try
            {
                meta = await client.GetMetaAsync(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                return Fail(SignInFailure.RegistryUnreachable, ex.Message);
            }

            if (meta is null)
                return Fail(SignInFailure.NotARegistry, null);
            if (ApiCompatibility.Check(meta) is { } incompatibility)
                return Fail(SignInFailure.Incompatible, incompatibility);
        }

        var browser = new LoopbackBrowser(openBrowser: !options.NoBrowser);
        var oidc = OidcClientFactory.Create(registry, browser);

        var request = new LoginRequest();
        // A provider hint lets the registry skip its chooser and forward straight to that provider.
        if (!string.IsNullOrWhiteSpace(options.Provider))
        {
            request.FrontChannelExtraParameters = new Parameters
            {
                new KeyValuePair<string, string>("identity_provider", options.Provider.Trim()),
            };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : options.Timeout);

        LoginResult result;
        try
        {
            result = await oidc.LoginAsync(request, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return Fail(SignInFailure.TimedOut, null);
        }
        catch (HttpRequestException ex)
        {
            return Fail(SignInFailure.RegistryUnreachable, ex.Message);
        }

        if (result.IsError)
            return Fail(SignInFailure.Rejected, result.ErrorDescription is { Length: > 0 } d ? d : result.Error ?? "the registry rejected the sign-in.");

        // With LoadProfile enabled, OidcClient has already merged the userinfo claims (including the
        // custom github_login) onto the principal, so no extra round trip is needed.
        var user = result.User;
        var login = user?.FindFirst("preferred_username")?.Value ?? user?.FindFirst("github_login")?.Value;

        var saved = _tokens.Save(new StoredToken
        {
            Registry = RegistryEndpoint.CredentialKey(registry),
            AccessToken = result.AccessToken!,
            RefreshToken = result.RefreshToken,
            ExpiresAtUtc = result.AccessTokenExpiration,
            Subject = user?.FindFirst("sub")?.Value,
            Login = login,
            Name = user?.FindFirst("name")?.Value,
        }) && _defaultRegistry.Save(registry);

        return saved
            ? new SignInResult(SignInFailure.None, null, registry, login)
            : Fail(SignInFailure.SaveFailed, null);

        SignInResult Fail(SignInFailure failure, string? detail) => new(failure, detail, registry, null);
    }
}
