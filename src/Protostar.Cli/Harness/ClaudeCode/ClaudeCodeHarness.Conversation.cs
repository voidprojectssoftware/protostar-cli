using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Protostar.Cli.Harness.ClaudeCode;

/// <summary>Reads Claude Code's JSONL lines as conversation entries, for <c>protostar sessions show</c>.</summary>
/// <remarks>
/// <para>
/// Read here: <c>user</c> lines (a typed prompt, or tool results) and <c>assistant</c> lines (text and tool calls).
/// A <c>user</c> line marked <c>isMeta</c> was injected by the harness, not typed, and thinking blocks are the
/// model's private reasoning, so neither is shown. Every other type (<c>system</c>, <c>summary</c>, <c>ai-title</c>,
/// and any added later) and any line that is not valid JSON yield nothing, so a new record type never breaks
/// reading; the registry's parser follows the same rule.
/// </para>
/// <para>
/// A subagent links to its parent through the result of the call that started it: the <c>user</c> line carrying that
/// <c>tool_result</c> has <c>toolUseResult.agentId</c>, and the subagent's transcript is
/// <c>subagents/agent-&lt;agentId&gt;.jsonl</c>, key <c>&lt;session id&gt;.agent-&lt;agentId&gt;</c>. Versions that do not
/// write <c>toolUseResult</c> still end the result text with <c>agentId: &lt;id&gt;</c>, which is used instead.
/// </para>
/// </remarks>
internal sealed partial class ClaudeCodeHarness
{
    // The input fields that say what a call did, most telling first; a tool with none of them shows its first string.
    private static readonly string[] SummaryFields = ["command", "file_path", "notebook_path", "path", "pattern", "url", "query", "description", "prompt"];

    [GeneratedRegex(@"agentId:\s*([A-Za-z0-9_-]{1,100})")]
    private static partial Regex AgentIdInText();

    public IEnumerable<ConversationEntry> ReadConversationLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return [];
            return String(root, "type") switch
            {
                "user" => ReadUser(root),
                "assistant" => ReadAssistant(root),
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<ConversationEntry> ReadUser(JsonElement root)
    {
        var entries = new List<ConversationEntry>();
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
            return entries;
        var (at, sidechain) = (Timestamp(root), IsSidechain(root));
        switch (Content(root))
        {
            case { ValueKind: JsonValueKind.String } text when !string.IsNullOrWhiteSpace(text.GetString()):
                entries.Add(new PromptEntry(at, sidechain, text.GetString()!));
                break;
            case { ValueKind: JsonValueKind.Array } blocks:
                foreach (var block in blocks.EnumerateArray())
                {
                    switch (String(block, "type"))
                    {
                        case "text" when String(block, "text") is { Length: > 0 } typed && !string.IsNullOrWhiteSpace(typed):
                            entries.Add(new PromptEntry(at, sidechain, typed));
                            break;
                        case "tool_result":
                            var resultText = Text(block.TryGetProperty("content", out var c) ? c : default);
                            var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                            entries.Add(new ToolResultEntry(at, sidechain, String(block, "tool_use_id") ?? "", resultText, isError, AgentId(root, resultText)));
                            break;
                    }
                }
                break;
        }

        return entries;
    }

    private static List<ConversationEntry> ReadAssistant(JsonElement root)
    {
        var entries = new List<ConversationEntry>();
        var (at, sidechain) = (Timestamp(root), IsSidechain(root));
        switch (Content(root))
        {
            case { ValueKind: JsonValueKind.String } text when !string.IsNullOrWhiteSpace(text.GetString()):
                entries.Add(new ReplyEntry(at, sidechain, text.GetString()!));
                break;
            case { ValueKind: JsonValueKind.Array } blocks:
                foreach (var block in blocks.EnumerateArray())
                {
                    switch (String(block, "type"))
                    {
                        case "text" when String(block, "text") is { } reply && !string.IsNullOrWhiteSpace(reply):
                            entries.Add(new ReplyEntry(at, sidechain, reply));
                            break;
                        case "tool_use":
                            var input = block.TryGetProperty("input", out var i) ? i.Clone() : default;
                            var name = String(block, "name") ?? "tool";
                            // Task is the subagent tool's older name, Agent its newer one; both carry subagent_type.
                            var subagent = name is "Task" or "Agent" || input.ValueKind == JsonValueKind.Object && input.TryGetProperty("subagent_type", out _)
                                ? new SubagentCall(String(input, "subagent_type"), String(input, "description"))
                                : null;
                            entries.Add(new ToolCallEntry(at, sidechain, String(block, "id") ?? "", name, Summary(input), input, subagent));
                            break;
                    }
                }
                break;
        }

        return entries;
    }

    private static JsonElement Content(JsonElement root) =>
        root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("content", out var content)
            ? content
            : default;

    // A result's content is a string, or blocks of which only the text ones are readable.
    private static string Text(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString()!;
        if (content.ValueKind != JsonValueKind.Array)
            return "";
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (String(block, "type") == "text" && String(block, "text") is { } part)
                text.Append(text.Length > 0 ? "\n" : "").Append(part);
        }

        return text.ToString();
    }

    private static string? AgentId(JsonElement root, string resultText)
    {
        if (root.TryGetProperty("toolUseResult", out var result) && String(result, "agentId") is { Length: > 0 } id)
            return id;
        var match = AgentIdInText().Match(resultText);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Summary(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return "";
        var value = SummaryFields.Select(field => String(input, field)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            ?? input.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()).FirstOrDefault()
            ?? "";
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static DateTimeOffset? Timestamp(JsonElement root) =>
        String(root, "timestamp") is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : null;

    private static bool IsSidechain(JsonElement root) =>
        root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
