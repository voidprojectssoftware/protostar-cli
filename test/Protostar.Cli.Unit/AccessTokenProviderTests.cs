using Protostar.Cli.Auth;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Tests for <see cref="AccessTokenProvider"/> and <see cref="SignInService.CheckAsync"/> against a registry that is not
/// there, so an outage is a result rather than a raw OIDC discovery exception.
/// </summary>
public sealed class AccessTokenProviderTests
{
    // Nothing listens on port 1, so every request to it is refused.
    private static readonly Uri Down = new("https://127.0.0.1:1");

    private static StoredToken Token(bool expired) => new()
    {
        Registry = RegistryEndpoint.CredentialKey(Down),
        AccessToken = "access",
        RefreshToken = "refresh",
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1),
        Login = "octocat",
    };

    [Fact]
    public async Task An_expired_session_the_registry_cannot_be_reached_to_refresh_is_unreachable_and_kept()
    {
        var store = new MemoryTokenStore(Token(expired: true));

        var lookup = await new AccessTokenProvider(store).GetSessionAsync(Down, CancellationToken.None);

        Assert.Equal(SessionState.RegistryUnreachable, lookup.State);
        Assert.Equal("access", lookup.Session!.AccessToken);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task A_current_session_is_returned_without_asking_the_registry()
    {
        var lookup = await new AccessTokenProvider(new MemoryTokenStore(Token(expired: false))).GetSessionAsync(Down, CancellationToken.None);

        Assert.Equal(SessionState.Current, lookup.State);
    }

    [Fact]
    public async Task An_expired_session_without_a_refresh_token_is_expired()
    {
        var store = new MemoryTokenStore(Token(expired: true) with { RefreshToken = null });

        var lookup = await new AccessTokenProvider(store).GetSessionAsync(Down, CancellationToken.None);

        Assert.Equal(SessionState.Expired, lookup.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checking_a_stored_session_while_the_registry_is_down_says_unreachable(bool expired)
    {
        var store = new MemoryTokenStore(Token(expired));
        var signIn = new SignInService(store, new AccessTokenProvider(store), new NoDefaultRegistry());

        var check = await signIn.CheckAsync(Down, CancellationToken.None);

        Assert.Equal(SessionVerification.Unreachable, check.Verification);
        Assert.Equal("octocat", check.Login);
    }

    private sealed class MemoryTokenStore(StoredToken token) : ITokenStore
    {
        public int Saves { get; private set; }

        public bool Save(StoredToken token)
        {
            Saves++;
            return true;
        }

        public StoredToken? Load(Uri registry) => token;

        public void Delete(Uri registry) { }
    }

    private sealed class NoDefaultRegistry : IDefaultRegistryStore
    {
        public bool Save(Uri registry) => throw new InvalidOperationException("Checking a session never changes the default registry.");
    }
}
