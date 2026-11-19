using System.Text.RegularExpressions;

namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>Claude Code's session capability: finds transcripts under <c>projects/</c>.</summary>
/// <remarks>
/// <para>
/// Layout: <c>projects/&lt;encoded cwd&gt;/&lt;session id&gt;.jsonl</c> for a session, and
/// <c>projects/&lt;encoded cwd&gt;/&lt;session id&gt;/subagents/agent-&lt;id&gt;.jsonl</c> (with an optional
/// <c>agent-&lt;id&gt;.meta.json</c>) for its subagents.
/// </para>
/// <para>
/// Both the resolved config root and <c>~/.claude</c> are searched: tools that set <c>CLAUDE_CONFIG_DIR</c>
/// for a profile do not always move where Claude Code writes transcripts. Files are de-duplicated by full path.
/// A pinned root (<c>--harness-home</c>, <c>PROTOSTAR_HARNESS_ROOT</c>) is searched alone, so pointing protostar
/// at a test or fixture folder can never pick up the real transcripts in <c>~/.claude</c>.
/// </para>
/// </remarks>
internal sealed partial class ClaudeCodeHarness : ISessionCapability
{
    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex("^agent-[A-Za-z0-9_-]{1,100}$")]
    private static partial Regex AgentIdPattern();

    public IReadOnlyList<LocalTranscript> LocateTranscripts(HarnessLocation location)
    {
        var found = new Dictionary<string, LocalTranscript>(StringComparer.Ordinal);
        foreach (var projects in ProjectRoots(location))
        {
            foreach (var projectDir in SafeDirectories(projects))
            {
                foreach (var file in SafeFiles(projectDir, "*.jsonl"))
                {
                    foreach (var transcript in LocateSession(file))
                        found.TryAdd(transcript.Path, transcript);
                }
            }
        }

        return [.. found.Values.OrderBy(t => t.Path, StringComparer.Ordinal)];
    }

    public IReadOnlyList<LocalTranscript> LocateSession(string transcriptPath)
    {
        var full = Path.GetFullPath(transcriptPath);
        var sessionId = Path.GetFileNameWithoutExtension(full);
        if (!File.Exists(full) || !full.EndsWith(".jsonl", StringComparison.Ordinal) || !SessionIdPattern().IsMatch(sessionId))
            return [];

        var result = new List<LocalTranscript> { new(Id, sessionId, TranscriptKind.Session, null, full, null) };

        var subagents = Path.Combine(Path.GetDirectoryName(full)!, sessionId, "subagents");
        foreach (var file in SafeFiles(subagents, "agent-*.jsonl").Order(StringComparer.Ordinal))
        {
            var agentId = Path.GetFileNameWithoutExtension(file);
            if (!AgentIdPattern().IsMatch(agentId))
                continue;

            var meta = Path.ChangeExtension(file, ".meta.json");
            result.Add(new LocalTranscript(Id, $"{sessionId}.{agentId}", TranscriptKind.Subagent, sessionId, file, ReadSmallJson(meta)));
        }

        return result;
    }

    private static IEnumerable<string> ProjectRoots(HarnessLocation location)
    {
        var roots = new List<string> { Path.Combine(location.ConfigDir, "projects") };
        if (!location.Pinned)
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"));
        return roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .Where(Directory.Exists);
    }

    // Metadata files are a few hundred bytes; anything large or unreadable is ignored rather than uploaded.
    private static string? ReadSmallJson(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024)
                return null;
            var text = File.ReadAllText(path);
            using var _ = System.Text.Json.JsonDocument.Parse(text);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeFiles(string path, string pattern)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
