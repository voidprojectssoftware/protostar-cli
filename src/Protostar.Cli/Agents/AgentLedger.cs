using System.Text.Json;
using Protostar.Cli.Auth;

namespace Protostar.Cli.Agents;

/// <summary>
/// Production <see cref="IAgentLedger"/>: a JSON file at <c>~/.protostar/agent-ledger.json</c>, written as <see cref="Skills.SkillLedger"/> writes its own.
/// </summary>
/// <remarks>
/// Written to a temp file and moved into place, so a hook killed mid-write never leaves a torn file. Two
/// processes writing at once can lose one record; the cost is one redundant upload, so no lock is taken.
/// </remarks>
internal sealed class AgentLedger : IAgentLedger
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string LedgerPath => Path.Combine(ProtostarPaths.ConfigDir(), "agent-ledger.json");

    /// <inheritdoc />
    public AgentLedgerEntry? Get(Uri registry, string filePath) =>
        Load().TryGetValue(RegistryEndpoint.CredentialKey(registry), out var agents) &&
        agents.TryGetValue(Normalize(filePath), out var entry)
            ? entry
            : null;

    /// <inheritdoc />
    public void Record(Uri registry, string filePath, AgentLedgerEntry entry) =>
        Change(registry, agents => agents[Normalize(filePath)] = entry);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, AgentLedgerEntry> Entries(Uri registry) =>
        Load().TryGetValue(RegistryEndpoint.CredentialKey(registry), out var agents)
            ? agents
            : new Dictionary<string, AgentLedgerEntry>();

    private static void Change(Uri registry, Action<Dictionary<string, AgentLedgerEntry>> change)
    {
        try
        {
            var ledger = Load();
            var key = RegistryEndpoint.CredentialKey(registry);
            if (!ledger.TryGetValue(key, out var agents))
                ledger[key] = agents = new Dictionary<string, AgentLedgerEntry>(StringComparer.Ordinal);
            change(agents);

            Directory.CreateDirectory(ProtostarPaths.ConfigDir());
            var temp = LedgerPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(ledger, Options));
            File.Move(temp, LedgerPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: see the interface remarks.
        }
    }

    private static Dictionary<string, Dictionary<string, AgentLedgerEntry>> Load()
    {
        try
        {
            if (File.Exists(LedgerPath))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AgentLedgerEntry>>>(File.ReadAllText(LedgerPath))
                    ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing, corrupt, or unreadable ledger only means every agent looks changed.
        }

        return [];
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
