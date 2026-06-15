namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>
/// Claude Code's skill-discovery capability. Skills are directories under a <c>skills/</c> folder,
/// each holding a <c>SKILL.md</c> manifest: globally at <c>&lt;config&gt;/skills</c> and per-project at
/// <c>&lt;root&gt;/.claude/skills</c>. A directory counts as a skill only if it contains the manifest,
/// so loose files and work-in-progress folders are ignored. The manifest's YAML front matter supplies
/// the display name and description; both fall back gracefully when absent.
/// </summary>
internal sealed partial class ClaudeCodeHarness : ISkillCapability
{
    // Claude Code's per-project config dir: both where project skills live and the marker that
    // identifies a Claude Code project, so it drives both the project-root walk-up and the skills path.
    private const string ProjectConfigDir = ".claude";
    private const string SkillsDirName = "skills";
    private const string SkillManifest = "SKILL.md";

    public IReadOnlyList<DiscoveredSkill> DiscoverSkills(HarnessLocation location, string? projectStart)
    {
        var skills = new List<DiscoveredSkill>();

        // Global: the harness's own config dir.
        skills.AddRange(Scan(Path.Combine(location.ConfigDir, SkillsDirName), SkillScope.Global));

        // Project: walk up from where the operator is to the nearest tree holding our project config
        // dir, then scan its skills. The marker is the provider's; the traversal is the generic helper's.
        if (!string.IsNullOrWhiteSpace(projectStart))
        {
            var projectRoot = ProjectLocator.FindAncestorContaining(projectStart, ProjectConfigDir);
            if (projectRoot is not null)
                skills.AddRange(Scan(Path.Combine(projectRoot, ProjectConfigDir, SkillsDirName), SkillScope.Project));
        }

        return skills;
    }

    // Each immediate subdirectory holding a SKILL.md is one skill. Ordered by name for stable output.
    private static IEnumerable<DiscoveredSkill> Scan(string skillsDir, SkillScope scope)
    {
        if (!Directory.Exists(skillsDir))
            yield break;

        var dirs = Directory.EnumerateDirectories(skillsDir)
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            var manifest = Path.Combine(dir, SkillManifest);
            if (!File.Exists(manifest))
                continue;

            var (name, description) = ReadFrontMatter(manifest, fallbackName: Path.GetFileName(dir));
            yield return new DiscoveredSkill(name, scope, dir, description);
        }
    }

    // Minimal YAML front-matter read: the leading `---` block, then `key: value` lines. We only need
    // `name` and `description`, so there's no YAML dependency; a missing or malformed block falls back
    // to the directory name rather than failing discovery. Block scalars (`description: >-` and friends)
    // are supported because a multi-line description is the common case for a skill manifest; their
    // indented continuation lines are gathered rather than left showing the bare `>-` indicator.
    private static (string name, string? description) ReadFrontMatter(string manifestPath, string fallbackName)
    {
        string? name = null;
        string? description = null;

        using var reader = new StreamReader(manifestPath);
        if (reader.ReadLine()?.Trim() != "---")
            return (fallbackName, null);

        // Buffer the front-matter body so a block scalar can look ahead at its continuation lines.
        var lines = new List<string>();
        for (string? line = reader.ReadLine(); line is not null && line.Trim() != "---"; line = reader.ReadLine())
            lines.Add(line);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // Indented lines belong to a preceding block scalar and are consumed there, not here.
            if (line.Length > 0 && char.IsWhiteSpace(line[0]))
                continue;

            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            var rawValue = line[(separator + 1)..].Trim();
            var value = IsBlockScalar(rawValue, out var literal)
                ? ReadBlockScalar(lines, ref i, literal)
                : Unquote(rawValue);

            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                name = value;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                description = value;
        }

        return (
            string.IsNullOrWhiteSpace(name) ? fallbackName : name,
            string.IsNullOrWhiteSpace(description) ? null : description);
    }

    // A YAML block scalar header: `|`/`>` with an optional chomping indicator (`-`/`+`). `literal` is
    // true for `|` (newlines kept) and false for `>` (lines folded into spaces).
    private static bool IsBlockScalar(string rawValue, out bool literal)
    {
        literal = rawValue.StartsWith('|');
        return rawValue is "|" or "|-" or "|+" or ">" or ">-" or ">+";
    }

    // Consume the indented continuation lines following a block-scalar header, advancing the loop
    // index past them. Folded scalars join their lines with spaces; literal scalars keep the breaks.
    private static string ReadBlockScalar(List<string> lines, ref int i, bool literal)
    {
        var block = new List<string>();
        while (i + 1 < lines.Count && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
            block.Add(lines[++i].Trim());

        return literal
            ? string.Join('\n', block)
            : string.Join(' ', block.Where(l => l.Length > 0));
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }
}
