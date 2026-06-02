namespace Protostar.Cli.Auth;

/// <summary>
/// The persisted session for one registry, stored as JSON in the OS credential store.
/// </summary>
internal sealed record StoredToken
{
    public required string Registry { get; init; }
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public string? Subject { get; init; }
    public string? Login { get; init; }
    public string? Name { get; init; }

    /// <summary>True once the access token is within 30s of expiry (treat as needing refresh).</summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAtUtc.AddSeconds(-30);
}
