using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>
/// Claude Code's custom agents: Markdown files under <c>&lt;config&gt;/agents</c> (personal) and
/// <c>&lt;root&gt;/.claude/agents</c> (project), scanned recursively, each named by the <c>name</c> in its front matter.
/// </summary>
internal sealed partial class ClaudeCodeHarness : IAgentCapability
{
    private const string AgentsDirName = "agents";

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredAgent> DiscoverAgents(HarnessLocation location, string? projectStart)
    {
        var agents = ScanAgents(GlobalAgentsDirectory(location), AgentScope.Personal, null).ToList();
        if (string.IsNullOrWhiteSpace(projectStart))
            return agents;

        var projectRoot = ProjectLocator.FindAncestorContaining(projectStart, ProjectConfigDir);
        // ~/.claude found by walking up from the home folder is the personal config, not a project's (as for skills).
        if (projectRoot is not null && !SamePath(ProjectAgentsDirectory(projectRoot), GlobalAgentsDirectory(location)))
            agents.AddRange(ScanAgents(ProjectAgentsDirectory(projectRoot), AgentScope.Project, projectRoot));
        return agents;
    }

    /// <inheritdoc />
    public string GlobalAgentsDirectory(HarnessLocation location) => Path.Combine(location.ConfigDir, AgentsDirName);

    /// <inheritdoc />
    public string ProjectAgentsDirectory(string projectRoot) => Path.Combine(projectRoot, ProjectConfigDir, AgentsDirName);

    private static IEnumerable<DiscoveredAgent> ScanAgents(string agentsDir, AgentScope scope, string? projectRoot)
    {
        if (!Directory.Exists(agentsDir))
            return [];

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(agentsDir, "*.md", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return files.Select(file =>
        {
            var (name, description) = ReadAgentFrontMatter(file);
            return new DiscoveredAgent
            {
                Name = name,
                Description = description,
                Scope = scope,
                Path = file,
                RelativePath = Path.GetRelativePath(agentsDir, file).Replace(Path.DirectorySeparatorChar, '/'),
                ProjectRoot = projectRoot,
            };
        });
    }

    // The name and description from a file's leading `---` block; nulls when the file, its block or its YAML cannot be read.
    internal static (string? Name, string? Description) ReadAgentFrontMatter(string file)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }

        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (start < 0 || lines[start].Trim() != "---")
            return (null, null);
        var end = Array.FindIndex(lines, start + 1, l => l.Trim() is "---" or "...");
        if (end < 0)
            return (null, null);

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(string.Join('\n', lines[(start + 1)..end])));
        }
        catch (YamlException)
        {
            return (null, null);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return (null, null);
        return (Scalar(root, "name"), Scalar(root, "description"));

        static string? Scalar(YamlMappingNode root, string key) =>
            root.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode { Value: { } value } && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
    }
}
