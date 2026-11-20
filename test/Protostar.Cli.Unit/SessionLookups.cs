using Protostar.Cli.Auth;

namespace Protostar.Cli.Unit;

/// <summary>Builds the <see cref="SessionLookup"/> a fake <see cref="IAccessTokenProvider"/> returns.</summary>
internal static class SessionLookups
{
    /// <summary>What the real provider returns for <paramref name="token"/> when no refresh succeeds: none, expired, or current.</summary>
    public static SessionLookup Of(StoredToken? token) => token is null
        ? new SessionLookup(SessionState.None, null)
        : new SessionLookup(token.IsExpired ? SessionState.Expired : SessionState.Current, token);

    /// <summary>An expired session the registry could not be reached to refresh.</summary>
    public static SessionLookup Unreachable(string registry) => new(
        SessionState.RegistryUnreachable,
        new StoredToken { Registry = registry, AccessToken = "stale", RefreshToken = "refresh", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(-1) });
}
