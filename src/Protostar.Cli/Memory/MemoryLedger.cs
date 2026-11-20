using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Protostar.Cli.Auth;

namespace Protostar.Cli.Memory;

/// <summary>Remembers, per registry, what each memory file looked like when the registry last confirmed it.</summary>
/// <remarks>
/// One small file per memory folder, holding an entry per memory file, plus a lock file per folder so two processes
/// (a manual push and a hook's) never push the same folder at once. A memory folder is tens of small notes, so one
/// file per folder keeps a status of hundreds of projects to hundreds of reads, not thousands. The ledger is what lets
/// <c>memory status</c> work offline and lets an unchanged file cost a stat, not a read or a network call.
/// </remarks>
internal interface IMemoryLedger
{
    /// <summary>The entries recorded for <paramref name="memoryFolder"/>, by path within it; empty when none.</summary>
    IReadOnlyDictionary<string, MemoryLedgerEntry> Get(Uri registry, string memoryFolder);

    /// <summary>Replaces the entries recorded for <paramref name="memoryFolder"/>. Never throws.</summary>
    void Save(Uri registry, string memoryFolder, IReadOnlyDictionary<string, MemoryLedgerEntry> entries);

    /// <summary>Takes the folder's push lock, or returns <c>null</c> when another process holds it.</summary>
    IDisposable? TryLock(Uri registry, string memoryFolder);
}

/// <summary>One memory file as the registry last confirmed it.</summary>
/// <param name="Sha256">Lowercase hex SHA-256 of the content sent.</param>
/// <param name="Size">The file's size when it was sent, in bytes.</param>
/// <param name="ModifiedAt">The file's last write time when it was sent; with <paramref name="Size"/>, what lets an unchanged file go unread.</param>
/// <param name="Version">The registry's version number for that content.</param>
/// <param name="RecordedAt">When this entry was written; a write time this close to it cannot prove the file unchanged (see <see cref="MemorySyncService"/>).</param>
/// <param name="Deleted">Whether the registry has recorded the file as deleted on this machine.</param>
internal sealed record MemoryLedgerEntry(string Sha256, long Size, DateTimeOffset ModifiedAt, int Version, DateTimeOffset RecordedAt, bool Deleted = false);

/// <summary>Production <see cref="IMemoryLedger"/> under <c>~/.protostar/memory/</c>.</summary>
internal sealed class MemoryLedger : IMemoryLedger
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <inheritdoc />
    public IReadOnlyDictionary<string, MemoryLedgerEntry> Get(Uri registry, string memoryFolder)
    {
        try
        {
            var path = EntryPath(registry, memoryFolder);
            if (!File.Exists(path))
                return new Dictionary<string, MemoryLedgerEntry>();
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            // A hash collision on the file name would mix two folders' entries; the stored folder rules it out.
            return stored is not null && stored.Folder == memoryFolder ? stored.Files : new Dictionary<string, MemoryLedgerEntry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, MemoryLedgerEntry>();
        }
    }

    /// <inheritdoc />
    public void Save(Uri registry, string memoryFolder, IReadOnlyDictionary<string, MemoryLedgerEntry> entries)
    {
        try
        {
            var path = EntryPath(registry, memoryFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(memoryFolder, new Dictionary<string, MemoryLedgerEntry>(entries, StringComparer.Ordinal)), Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a lost entry only costs sending the file again, which the registry stores as unchanged.
        }
    }

    /// <inheritdoc />
    public IDisposable? TryLock(Uri registry, string memoryFolder)
    {
        try
        {
            var path = EntryPath(registry, memoryFolder) + ".lock";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed record Stored(string Folder, Dictionary<string, MemoryLedgerEntry> Files);

    private static string EntryPath(Uri registry, string memoryFolder) =>
        Path.Combine(ProtostarPaths.ConfigDir(), "memory", Hash(RegistryEndpoint.CredentialKey(registry)), Hash(memoryFolder) + ".json");

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
