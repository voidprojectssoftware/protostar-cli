using System.Text.Json;
using Protostar.Cli.Auth;

namespace Protostar.Cli.Skills;

/// <summary>
/// Production <see cref="ISkillLedger"/>: a JSON file at <c>~/.protostar/skill-ledger.json</c>.
/// </summary>
/// <remarks>
/// Written to a temp file and moved into place, so a hook killed mid-write never leaves a torn file. Two
/// processes writing at once can lose one record; the cost is one redundant upload, so no lock is taken.
/// </remarks>
internal sealed class SkillLedger : ISkillLedger
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(ProtostarPaths.ConfigDir(), "skill-ledger.json");

    /// <inheritdoc />
    public SkillLedgerEntry? Get(Uri registry, string skillPath) =>
        Load().TryGetValue(RegistryEndpoint.CredentialKey(registry), out var skills) &&
        skills.TryGetValue(Normalize(skillPath), out var entry)
            ? entry
            : null;

    /// <inheritdoc />
    public void Record(Uri registry, string skillPath, SkillLedgerEntry entry)
    {
        try
        {
            var ledger = Load();
            var key = RegistryEndpoint.CredentialKey(registry);
            if (!ledger.TryGetValue(key, out var skills))
                ledger[key] = skills = new Dictionary<string, SkillLedgerEntry>(StringComparer.Ordinal);
            skills[Normalize(skillPath)] = entry;

            Directory.CreateDirectory(ProtostarPaths.ConfigDir());
            var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(ledger, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: see the interface remarks.
        }
    }

    private static Dictionary<string, Dictionary<string, SkillLedgerEntry>> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, SkillLedgerEntry>>>(File.ReadAllText(FilePath))
                    ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing, corrupt, or unreadable ledger only means every skill looks changed.
        }

        return [];
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
