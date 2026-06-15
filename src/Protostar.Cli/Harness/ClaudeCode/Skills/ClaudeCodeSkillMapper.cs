namespace Protostar.Cli.Harness.ClaudeCode;

/// <inheritdoc />
internal sealed class ClaudeCodeSkillMapper : IClaudeCodeSkillMapper
{
    /// <inheritdoc />
    public DiscoveredSkill ToDiscoveredSkill(ClaudeCodeSkill skill) =>
        new()
        {
            Name = skill.Name,
            Scope = skill.Scope,
            Path = skill.Directory,
            Description = skill.Description,
            License = skill.License,
            Compatibility = skill.Compatibility,
            Metadata = skill.Metadata,
            AllowedTools = skill.AllowedTools,
        };
}
