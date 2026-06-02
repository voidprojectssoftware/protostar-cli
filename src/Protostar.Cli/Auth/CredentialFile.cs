using System.Text.Json;

namespace Protostar.Cli.Auth;

/// <summary>
/// The on-disk credential store: a single JSON file under <see cref="ProtostarPaths.ConfigDir"/>,
/// holding one session per registry (keyed by registry authority). Written atomically (temp file +
/// move) and locked down to the owner (0600 file, 0700 dir) on Unix; on Windows it relies on the
/// per-user profile ACL. Tokens are plaintext at rest, protected by filesystem permissions.
/// </summary>
internal sealed class CredentialFile
{
    public Dictionary<string, StoredToken> Registries { get; set; } = new(StringComparer.Ordinal);

    public static CredentialFile Read()
    {
        var path = ProtostarPaths.CredentialsFile();
        if (!File.Exists(path))
            return new CredentialFile();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CredentialFile>(json, AuthJson.Default) ?? new CredentialFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing/corrupt/unreadable file is treated as no stored sessions.
            return new CredentialFile();
        }
    }

    public void Write()
    {
        var dir = ProtostarPaths.ConfigDir();
        Directory.CreateDirectory(dir);
        RestrictToOwner(dir, isDirectory: true);

        var path = ProtostarPaths.CredentialsFile();
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");

        File.WriteAllText(temp, JsonSerializer.Serialize(this, AuthJson.Default));
        RestrictToOwner(temp, isDirectory: false);

        // Atomic replace so a crash mid-write can never leave a corrupt store.
        File.Move(temp, path, overwrite: true);
    }

    private static void RestrictToOwner(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
            return; // Protected by the per-user profile ACL.

        var mode = isDirectory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, mode);
    }
}
