using System.Diagnostics.CodeAnalysis;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Protostar.Cli.Harness.ClaudeCode;

/// <inheritdoc />
/// <remarks>
/// Front matter is deserialized with YamlDotNet into an untyped tree, then projected onto the typed
/// model. Going through the untyped tree (rather than a YAML POCO) is deliberate: the well-known
/// fields are claimed explicitly, list fields accept either a YAML sequence or a delimited scalar
/// (Claude accepts <c>allowed-tools</c> as space-, comma-, or list-form), and anything unclaimed is
/// preserved in <see cref="ClaudeCodeSkill.UnknownFields"/>. Field shapes follow Claude Code's
/// frontmatter reference (<see href="https://code.claude.com/docs/en/skills"/>) and the Agent Skills
/// standard (<see href="https://agentskills.io/specification"/>); see <see cref="ClaudeCodeSkill"/>.
/// </remarks>
internal sealed class ClaudeCodeSkillParser : IClaudeCodeSkillParser
{
    private const string SkillManifest = "SKILL.md";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    // Scalars in a delimited list field separate on whitespace or commas: "Read Write", "Read, Write".
    private static readonly char[] ListSeparators = [' ', '\t', '\r', '\n', ','];

    // Keys the typed model claims; every other front-matter key flows into UnknownFields.
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "when_to_use", "argument-hint", "arguments", "allowed-tools",
        "disallowed-tools", "disable-model-invocation", "user-invocable", "model", "effort",
        "context", "agent", "paths", "license", "compatibility", "metadata", "hooks",
        "intercept-tool", "intercept-after-tool",
    };

    /// <inheritdoc />
    public bool TryParse(string skillDirectory, SkillScope scope, [MaybeNullWhen(false)] out ClaudeCodeSkill skill)
    {
        var manifestPath = Path.Combine(skillDirectory, SkillManifest);
        if (!File.Exists(manifestPath))
        {
            skill = null;
            return false;
        }

        var fallbackName = Path.GetFileName(Path.TrimEndingDirectorySeparator(skillDirectory));
        SplitFrontMatter(File.ReadAllText(manifestPath), out var frontMatter, out var body);
        var fields = ParseFrontMatter(frontMatter);

        skill = Build(skillDirectory, scope, fallbackName, body, fields);
        return true;
    }

    // Separate the leading `---` … `---` block from the markdown body. A missing opening or closing
    // fence means there is no front matter: the whole file is the body.
    private static void SplitFrontMatter(string text, out string frontMatter, out string body)
    {
        // Strip a leading BOM so the opening "---" is recognized on the first line.
        var content = text.StartsWith('﻿') ? text[1..] : text;

        using var reader = new StringReader(content);
        if (reader.ReadLine()?.Trim() != "---")
        {
            frontMatter = string.Empty;
            body = text;
            return;
        }

        var block = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null && line.Trim() != "---")
            block.AppendLine(line);

        if (line is null)
        {
            // Opening fence with no closing fence: not valid front matter.
            frontMatter = string.Empty;
            body = text;
            return;
        }

        frontMatter = block.ToString();
        body = reader.ReadToEnd();
    }

    // Deserialize the front matter into a case-insensitive key/value map. Malformed YAML yields an
    // empty map rather than throwing, so one bad manifest cannot fail discovery for the rest.
    private static Dictionary<string, object?> ParseFrontMatter(string frontMatter)
    {
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(frontMatter))
            return fields;

        object? root;
        try
        {
            root = Yaml.Deserialize<object?>(new StringReader(frontMatter));
        }
        catch (YamlException)
        {
            return fields;
        }

        if (Normalize(root) is Dictionary<string, object?> map)
            foreach (var (key, value) in map)
                fields[key] = value;

        return fields;
    }

    // YamlDotNet's untyped tree uses non-generic IDictionary/IList with object keys. Reshape it into
    // string-keyed Dictionary / List<object?> so the rest of the parser sees one consistent shape.
    private static object? Normalize(object? node)
    {
        switch (node)
        {
            case System.Collections.IDictionary dict:
                var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Collections.DictionaryEntry entry in dict)
                    map[Convert.ToString(entry.Key) ?? string.Empty] = Normalize(entry.Value);
                return map;
            case System.Collections.IList list:
                var items = new List<object?>(list.Count);
                foreach (var item in list)
                    items.Add(Normalize(item));
                return items;
            default:
                return node; // scalar (string) or null
        }
    }

    private static ClaudeCodeSkill Build(
        string directory, SkillScope scope, string fallbackName, string body, Dictionary<string, object?> fields)
    {
        var unknown = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
            if (!KnownKeys.Contains(key))
                unknown[key] = value;

        var (effort, effortRaw) = AsEffort(Get(fields, "effort"));
        if (effortRaw is not null)
            unknown["effort"] = effortRaw;

        var (context, contextRaw) = AsContext(Get(fields, "context"));
        if (contextRaw is not null)
            unknown["context"] = contextRaw;

        var name = AsString(Get(fields, "name"));

        return new ClaudeCodeSkill
        {
            Directory = directory,
            Scope = scope,
            Name = string.IsNullOrWhiteSpace(name) ? fallbackName : name,
            Body = body,
            Description = AsString(Get(fields, "description")),
            WhenToUse = AsString(Get(fields, "when_to_use")),
            ArgumentHint = AsString(Get(fields, "argument-hint")),
            Arguments = AsStringList(Get(fields, "arguments")),
            AllowedTools = AsStringList(Get(fields, "allowed-tools")),
            DisallowedTools = AsStringList(Get(fields, "disallowed-tools")),
            DisableModelInvocation = AsBool(Get(fields, "disable-model-invocation"), fallback: false),
            UserInvocable = AsBool(Get(fields, "user-invocable"), fallback: true),
            Model = AsString(Get(fields, "model")),
            Effort = effort,
            Context = context,
            Agent = AsString(Get(fields, "agent")),
            Paths = AsStringList(Get(fields, "paths")),
            License = AsString(Get(fields, "license")),
            Compatibility = AsString(Get(fields, "compatibility")),
            Metadata = AsStringMap(Get(fields, "metadata")),
            Hooks = AsMap(Get(fields, "hooks")),
            InterceptTool = AsString(Get(fields, "intercept-tool")),
            InterceptAfterTool = AsString(Get(fields, "intercept-after-tool")),
            UnknownFields = unknown,
        };
    }

    private static object? Get(Dictionary<string, object?> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : null;

    private static string? AsString(object? node) =>
        node is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static bool AsBool(object? node, bool fallback) =>
        node is string s && bool.TryParse(s.Trim(), out var value) ? value : fallback;

    private static IReadOnlyList<string> AsStringList(object? node) => node switch
    {
        IReadOnlyList<object?> list => list.Select(AsString).OfType<string>().ToList(),
        string s => s.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    private static IReadOnlyDictionary<string, string> AsStringMap(object? node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is IReadOnlyDictionary<string, object?> map)
            foreach (var (key, value) in map)
                if (AsString(value) is { } s)
                    result[key] = s;
        return result;
    }

    private static IReadOnlyDictionary<string, object?> AsMap(object? node) =>
        node as Dictionary<string, object?> ?? new Dictionary<string, object?>();

    // Recognized effort/context values map to the enum; an unrecognized value returns null and the
    // raw string, which the caller stashes in UnknownFields so nothing is silently dropped.
    private static (SkillEffort? value, string? raw) AsEffort(object? node)
    {
        if (node is not string s || string.IsNullOrWhiteSpace(s))
            return (null, null);

        return s.Trim().ToLowerInvariant() switch
        {
            "low" => (SkillEffort.Low, null),
            "medium" => (SkillEffort.Medium, null),
            "high" => (SkillEffort.High, null),
            "xhigh" => (SkillEffort.XHigh, null),
            "max" => (SkillEffort.Max, null),
            _ => (null, s),
        };
    }

    private static (SkillContext? value, string? raw) AsContext(object? node)
    {
        if (node is not string s || string.IsNullOrWhiteSpace(s))
            return (null, null);

        return s.Trim().ToLowerInvariant() switch
        {
            "fork" => (SkillContext.Fork, null),
            "default" => (SkillContext.Default, null),
            _ => (null, s),
        };
    }
}
