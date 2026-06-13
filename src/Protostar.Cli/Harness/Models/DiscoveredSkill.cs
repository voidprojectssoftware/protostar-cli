namespace Protostar.Cli.Harness;

/// <summary>Where a discovered skill was found, relative to the operator.</summary>
internal enum SkillScope
{
    /// <summary>Authored once per user, in the harness's config dir (e.g. <c>~/.claude/skills</c>).</summary>
    Global,

    /// <summary>Authored inside a project working tree (e.g. <c>&lt;repo&gt;/.claude/skills</c>).</summary>
    Project,
}

/// <summary>
/// One skill found on disk. <see cref="Path"/> is the skill's directory (the unit a push reads).
/// <see cref="Description"/> comes from the manifest front matter when present, so a caller can show
/// what would be published without re-reading the file.
/// </summary>
internal sealed record DiscoveredSkill(string Name, SkillScope Scope, string Path, string? Description);
