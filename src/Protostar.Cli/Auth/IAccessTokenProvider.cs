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
    /// <returns>
    /// The session, or <c>null</c> when none is stored. The returned session can still be expired when
    /// the refresh failed or the registry was unreachable; callers check <see cref="StoredToken.IsExpired"/>.
    /// </returns>
    Task<StoredToken?> GetSessionAsync(Uri registry, CancellationToken cancellationToken);
}
