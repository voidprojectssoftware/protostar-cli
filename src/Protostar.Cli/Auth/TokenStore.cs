using System.Text.Json;
using GitCredentialManager;

namespace Protostar.Cli.Auth;

/// <summary>
/// Reads and writes <see cref="StoredToken"/> sessions in the OS credential store (Windows
/// Credential Manager / macOS Keychain / Linux Secret Service) via Devlooped.CredentialManager.
/// </summary>
internal sealed class TokenStore
{
    private readonly ICredentialStore _store = CredentialManager.Create(AuthConstants.CredentialService);

    public void Save(StoredToken token)
    {
        var json = JsonSerializer.Serialize(token, AuthJson.Default);
        _store.AddOrUpdate(
            RegistryEndpoint.CredentialKey(new Uri(token.Registry)),
            AuthConstants.CredentialAccount,
            json);
    }

    public StoredToken? Load(Uri registry)
    {
        var secret = _store.Get(RegistryEndpoint.CredentialKey(registry), AuthConstants.CredentialAccount)?.Password;
        if (string.IsNullOrEmpty(secret))
            return null;

        try { return JsonSerializer.Deserialize<StoredToken>(secret, AuthJson.Default); }
        catch (JsonException) { return null; }
    }

    public void Delete(Uri registry) =>
        _store.Remove(RegistryEndpoint.CredentialKey(registry), AuthConstants.CredentialAccount);
}
