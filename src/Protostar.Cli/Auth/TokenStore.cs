namespace Protostar.Cli.Auth;

/// <summary>
/// Reads and writes <see cref="StoredToken"/> sessions in the on-disk <see cref="CredentialFile"/>
/// (<c>~/.protostar/credentials.json</c>), one per registry. Degrades gracefully when the file
/// can't be written: <see cref="Save"/> returns false rather than throwing.
/// </summary>
internal sealed class TokenStore : ITokenStore
{
    /// <inheritdoc />
    public bool Save(StoredToken token)
    {
        try
        {
            var file = CredentialFile.Read();
            file.Registries[Key(new Uri(token.Registry))] = token;
            file.Write();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public StoredToken? Load(Uri registry) =>
        CredentialFile.Read().Registries.TryGetValue(Key(registry), out var token) ? token : null;

    /// <inheritdoc />
    public void Delete(Uri registry)
    {
        try
        {
            var file = CredentialFile.Read();
            if (file.Registries.Remove(Key(registry)))
                file.Write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing persisted / unwritable: treat as already gone.
        }
    }

    private static string Key(Uri registry) => RegistryEndpoint.CredentialKey(registry);
}
