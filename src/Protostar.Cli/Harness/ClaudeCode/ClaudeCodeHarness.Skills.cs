namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>
/// Claude Code's skill-discovery capability. Skills are directories under a <c>skills/</c> folder,
/// each holding a <c>SKILL.md</c> manifest: globally at <c>&lt;config&gt;/skills</c> and per-project at
/// <c>&lt;root&gt;/.claude/skills</c>. Each manifest is parsed into a <see cref="ClaudeCodeSkill"/> by the
/// injected parser (which also gates on the manifest's presence), then translated to the
/// harness-agnostic <see cref="DiscoveredSkill"/> by the injected mapper.
/// </summary>
internal sealed partial class ClaudeCodeHarness : ISkillCapability
{
    // Claude Code's per-project config dir: both where project skills live and the marker that
    // identifies a Claude Code project, so it drives both the project-root walk-up and the skills path.
    private const string ProjectConfigDir = ".claude";
    private const string SkillsDirName = "skills";

    public IReadOnlyList<DiscoveredSkill> DiscoverSkills(HarnessLocation location, string? projectStart)
    {
        var skills = new List<DiscoveredSkill>();

        skills.AddRange(GetGlobalSkills(location.ConfigDir));
        skills.AddRange(GetProjectSkills(projectStart));

        return skills;
    }

    private IEnumerable<DiscoveredSkill> GetProjectSkills(string? projectStart)
    {
        // Project scope is opt-in: a null start means "global only".
        if (string.IsNullOrWhiteSpace(projectStart))
            return [];

        var projectRoot = ProjectLocator.FindAncestorContaining(projectStart, ProjectConfigDir);
        if (projectRoot is null)
            return [];

        return Scan(Path.Combine(projectRoot, ProjectConfigDir, SkillsDirName), SkillScope.Project);
    }

    private IEnumerable<DiscoveredSkill> GetGlobalSkills(string globalConfigPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalConfigPath);

        return Scan(Path.Combine(globalConfigPath, SkillsDirName), SkillScope.Global);
    }

    private IEnumerable<DiscoveredSkill> Scan(string skillsDir, SkillScope scope)
    {
        if (!Directory.Exists(skillsDir))
            yield break;

        var dirs = Directory.EnumerateDirectories(skillsDir)
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
            if (_parser.TryParse(dir, scope, out var skill))
                yield return _mapper.ToDiscoveredSkill(skill);
    }
}
