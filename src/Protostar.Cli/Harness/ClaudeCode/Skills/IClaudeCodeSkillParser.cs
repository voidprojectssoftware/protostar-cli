using System.Diagnostics.CodeAnalysis;

namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>Parses a Claude Code skill directory's <c>SKILL.md</c> into a <see cref="ClaudeCodeSkill"/>.</summary>
internal interface IClaudeCodeSkillParser
{
    /// <summary>
    /// Read <c><paramref name="skillDirectory"/>/SKILL.md</c> into a <see cref="ClaudeCodeSkill"/>.
    /// Returns <c>false</c> when the manifest is absent: the manifest's presence is the "is this a
    /// skill" gate, owned here rather than by the caller. Malformed front matter does not fail the
    /// parse; it falls back to the directory name and an empty model so discovery never throws.
    /// </summary>
    bool TryParse(string skillDirectory, SkillScope scope, [MaybeNullWhen(false)] out ClaudeCodeSkill skill);
}
