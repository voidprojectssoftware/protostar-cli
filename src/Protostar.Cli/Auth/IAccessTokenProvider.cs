namespace Protostar.Cli.Auth;

/// <summary>
/// Supplies the stored registry session, refreshed first when its access token has expired.
/// </summary>
/// <remarks>
/// The one place that knows how to turn a stored session into a usable bearer token, so every command
/// that calls the registry (status, skill push, the capture hook) refreshes the same way and persists
/// the refreshed token for the next run.
/// </remarks>
internal interface IAccessTokenProvider
{
    /// <summary>
    /// Loads the session for <paramref name="registry"/>, refreshing and re-saving it when the access
    /// token has expired and a refresh token is available.
    /// </summary>
    /// <remarks>
    /// Never throws for an unreachable registry: a refresh that cannot reach it, including its OIDC discovery
    /// document, comes back as <see cref="SessionState.RegistryUnreachable"/>.
    /// </remarks>
    Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken);
}

/// <summary>Whether a usable session exists for a registry.</summary>
internal enum SessionState
{
    /// <summary>No session is stored for the registry.</summary>
    None,

    /// <summary>The access token has not expired, as stored or just refreshed.</summary>
    Current,

    /// <summary>The access token expired and the registry did not renew it (no refresh token, or it refused the refresh).</summary>
    Expired,

    /// <summary>The access token expired and the registry could not be reached to renew it.</summary>
    RegistryUnreachable,
}

/// <summary>The stored session for a registry, after any refresh.</summary>
/// <param name="State">Whether the session can be used.</param>
/// <param name="Session">The session, set for every state but <see cref="SessionState.None"/>; its token is only usable when <see cref="State"/> is <see cref="SessionState.Current"/>.</param>
internal sealed record SessionLookup(SessionState State, StoredToken? Session);
