using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Protostar.Cli.Auth;

namespace Protostar.Cli.Sessions;

/// <summary>Remembers, per registry, how much of each transcript has reached it.</summary>
/// <remarks>
/// One small file per transcript, so concurrent hooks for different sessions never contend, plus a lock
/// file per transcript so two processes never upload the same one at once. The ledger is what lets status
/// work offline and lets an unchanged transcript cost no network call.
/// </remarks>
internal interface ISessionLedger
{
    /// <summary>What was last recorded for a transcript, or <c>null</c> if nothing.</summary>
    SessionLedgerEntry? Get(Uri registry, string harness, string key);

    /// <summary>Records progress for a transcript. Never throws.</summary>
    void Save(Uri registry, string harness, string key, SessionLedgerEntry entry);

    /// <summary>Takes the transcript's upload lock, or returns <c>null</c> when another process holds it.</summary>
    IDisposable? TryLock(Uri registry, string harness, string key);
}

/// <summary>What the registry holds of one transcript, as last confirmed.</summary>
/// <param name="Revision">The registry revision.</param>
/// <param name="Length">Bytes the registry has; the next chunk's offset.</param>
/// <param name="TailSha256">Hex SHA-256 of up to the last 4 KiB before <paramref name="Length"/>, to notice a rewritten file.</param>
/// <param name="Registered">Whether the transcript has been registered with the registry.</param>
/// <param name="LastSyncedAt">When the registry last confirmed this position.</param>
/// <param name="LastAttemptAt">When a sync last started for it, for throttling hooks.</param>
internal sealed record SessionLedgerEntry(
    int Revision, long Length, string? TailSha256, bool Registered, DateTimeOffset? LastSyncedAt, DateTimeOffset? LastAttemptAt);

/// <summary>Production <see cref="ISessionLedger"/> under <c>~/.protostar/sessions/</c>.</summary>
internal sealed class SessionLedger : ISessionLedger
{
    /// <summary>How many bytes before the stored length are fingerprinted.</summary>
    public const int TailBytes = 4096;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <inheritdoc />
    public SessionLedgerEntry? Get(Uri registry, string harness, string key)
    {
        try
        {
            var path = EntryPath(registry, harness, key);
            return File.Exists(path) ? JsonSerializer.Deserialize<SessionLedgerEntry>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(Uri registry, string harness, string key, SessionLedgerEntry entry)
    {
        try
        {
            var path = EntryPath(registry, harness, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(entry, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a lost entry only costs a re-check against the registry next time.
        }
    }

    /// <inheritdoc />
    public IDisposable? TryLock(Uri registry, string harness, string key)
    {
        try
        {
            var path = EntryPath(registry, harness, key) + ".lock";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The fingerprint of the bytes just before <paramref name="length"/> in <paramref name="path"/>, or <c>null</c> if unreadable.</summary>
    public static string? TailHash(string path, long length)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < length)
                return null;
            var start = Math.Max(0, length - TailBytes);
            var buffer = new byte[length - start];
            file.Position = start;
            file.ReadExactly(buffer);
            return Convert.ToHexStringLower(SHA256.HashData(buffer));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string EntryPath(Uri registry, string harness, string key)
    {
        var registryDir = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RegistryEndpoint.CredentialKey(registry))))[..16];
        return Path.Combine(ProtostarPaths.ConfigDir(), "sessions", registryDir, harness, key + ".json");
    }
}
