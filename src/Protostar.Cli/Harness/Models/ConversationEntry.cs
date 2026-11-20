using System.Text.Json;

namespace Protostar.Cli.Harness;

/// <summary>One thing that happened in a session, as <c>protostar sessions show</c> reads it from a transcript line.</summary>
/// <param name="At">The line's timestamp, when it has one.</param>
/// <param name="Sidechain">
/// Whether the line belongs to a subagent's run written into the session's own file, as older Claude Code versions did.
/// Newer versions write each subagent to a transcript of its own.
/// </param>
internal abstract record ConversationEntry(DateTimeOffset? At, bool Sidechain);

/// <summary>Something the user typed.</summary>
internal sealed record PromptEntry(DateTimeOffset? At, bool Sidechain, string Text) : ConversationEntry(At, Sidechain);

/// <summary>Text the model replied with.</summary>
internal sealed record ReplyEntry(DateTimeOffset? At, bool Sidechain, string Text) : ConversationEntry(At, Sidechain);

/// <summary>A tool the model called.</summary>
/// <param name="Id">The call's id, which its result names.</param>
/// <param name="Name">The tool, for example <c>Bash</c>.</param>
/// <param name="Summary">The input in one short line: the command, path, pattern or description that says what the call did.</param>
/// <param name="Input">The full input.</param>
/// <param name="Subagent">For a call that starts a subagent, its type and description; <c>null</c> otherwise.</param>
internal sealed record ToolCallEntry(DateTimeOffset? At, bool Sidechain, string Id, string Name, string Summary, JsonElement Input, SubagentCall? Subagent)
    : ConversationEntry(At, Sidechain);

/// <summary>What a tool call returned.</summary>
/// <param name="ToolUseId">The id of the call it answers.</param>
/// <param name="Text">The result's text.</param>
/// <param name="IsError">Whether the tool reported an error.</param>
/// <param name="AgentId">
/// For a call that ran a subagent, the subagent's agent id, which names its transcript; <c>null</c> otherwise. A harness
/// reads one line at a time and cannot always tell which call a result answers, so it may set this on any result that
/// names an agent id; <see cref="Sessions.ISessionReader.ReadAsync"/> clears it unless the call it answers ran a subagent.
/// </param>
internal sealed record ToolResultEntry(DateTimeOffset? At, bool Sidechain, string ToolUseId, string Text, bool IsError, string? AgentId)
    : ConversationEntry(At, Sidechain);

/// <summary>What a call that starts a subagent asked for.</summary>
/// <param name="AgentType">The kind of subagent, for example <c>general-purpose</c>.</param>
/// <param name="Description">The short description the model gave it.</param>
internal sealed record SubagentCall(string? AgentType, string? Description);
