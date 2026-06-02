using System.Text.Json;
using GitCredentialManager;

namespace Protostar.Cli.Auth;

/// <summary>
/// Reads and writes <see cref="StoredToken"/> sessions in the OS credential store (Windows
/// Credential Manager / macOS Keychain / Linux Secret Service) via Devlooped.CredentialManager.
/// Degrades gracefully when no backend is available (e.g. a headless Linux box with no Secret
/// Service): reads report "no session" rather than throwing, and <see cref="Save"/> returns false.
/// </summary>
internal sealed class TokenStore
{
    private readonly Lazy<ICredentialStore?> _store = new(CreateStore);

    private static ICredentialStore? CreateStore()
    {
        try { return CredentialManager.Create(AuthConstants.CredentialService); }
        catch { return null; }
    }

    /// <summary>Persists the session. Returns false if the credential backend is unavailable.</summary>
    public bool Save(StoredToken token)
    {
        if (_store.Value is not { } store)
            return false;

        try
        {
            store.AddOrUpdate(
                RegistryEndpoint.CredentialKey(new Uri(token.Registry)),
                AuthConstants.CredentialAccount,
                JsonSerializer.Serialize(token, AuthJson.Default));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public StoredToken? Load(Uri registry)
    {
        if (_store.Value is not { } store)
            return null;

        try
        {
            var secret = store.Get(RegistryEndpoint.CredentialKey(registry), AuthConstants.CredentialAccount)?.Password;
            return string.IsNullOrEmpty(secret) ? null : JsonSerializer.Deserialize<StoredToken>(secret, AuthJson.Default);
        }
        catch
        {
            // Backend unavailable or unreadable payload: treat as no stored session.
            return null;
        }
    }

    public void Delete(Uri registry)
    {
        if (_store.Value is not { } store)
            return;

        try { store.Remove(RegistryEndpoint.CredentialKey(registry), AuthConstants.CredentialAccount); }
        catch { /* backend unavailable or nothing to remove */ }
    }
}
