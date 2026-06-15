namespace Protostar.Cli.Auth;

/// <summary>
/// Reads and writes <see cref="StoredToken"/> sessions in the on-disk credential store, one per
/// registry. Injected into the auth commands so they can be tested without touching the real
/// <c>~/.protostar/credentials.json</c>. See <see cref="TokenStore"/> for the production implementation.
/// </summary>
internal interface ITokenStore
{
    /// <summary>Persists the session. Returns false if the store can't be written.</summary>
    bool Save(StoredToken token);

    /// <summary>Loads the stored session for the registry, or null if there is none.</summary>
    StoredToken? Load(Uri registry);

    /// <summary>Removes the stored session for the registry; a no-op when none is stored.</summary>
    void Delete(Uri registry);
}
