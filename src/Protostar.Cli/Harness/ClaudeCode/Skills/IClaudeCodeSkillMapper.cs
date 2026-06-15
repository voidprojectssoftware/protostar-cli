namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>Projects a harness-native <see cref="ClaudeCodeSkill"/> onto the agnostic <see cref="DiscoveredSkill"/>.</summary>
/// <remarks>
/// Kept out of <see cref="ClaudeCodeSkill"/> so the model stays a pure description of a Claude skill
/// with no dependency on the discovery contract. This is the single place to enrich
/// <see cref="DiscoveredSkill"/> as it grows.
/// </remarks>
internal interface IClaudeCodeSkillMapper
{
    /// <summary>Translate a parsed skill into the discovery result the harness returns.</summary>
    DiscoveredSkill ToDiscoveredSkill(ClaudeCodeSkill skill);
}
