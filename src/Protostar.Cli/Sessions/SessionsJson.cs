using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Sessions;

/// <summary>The machine-readable face of the <c>sessions</c> commands: the records and errors <c>--json</c> prints.</summary>
/// <remarks>
/// <para>
/// The contract, documented for agents in <c>docs/reference/json-output.md</c>: standard output carries data only, as
/// JSON with no colour or table codes; standard error carries at most one error object,
/// <c>{"error":{"code":...,"message":...}}</c>, on one line, whose <c>code</c> is one of the constants here. Every
/// record names its transcript by <c>id</c>, the harness's own transcript key, which is the same on every machine
/// because the harness wrote it (a Claude Code session id is the transcript's file name) and the registry stores it
/// verbatim; <c>harness</c> beside it completes the identity the registry addresses.
/// </para>
/// <para>Field names are camelCase, absent values are left out rather than written as <c>null</c>, and enums are camelCase strings.</para>
/// </remarks>
internal static class SessionsJson
{
    /// <summary>The <c>--registry</c> value, or the one it fell back to, is not a usable URL.</summary>
    public const string InvalidRegistry = "invalid_registry";

    /// <summary>The <c>--harness</c> id does not exist or does not keep sessions.</summary>
    public const string UnknownHarness = "unknown_harness";

    /// <summary>No stored sign-in for the registry.</summary>
    public const string NotSignedIn = "not_signed_in";

    /// <summary>The stored sign-in expired and could not be refreshed.</summary>
    public const string SessionExpired = "session_expired";

    /// <summary>The registry could not be reached, or refused or failed the request.</summary>
    public const string RegistryUnreachable = "registry_unreachable";

    /// <summary>The registry speaks an API version this CLI does not.</summary>
    public const string RegistryIncompatible = "registry_incompatible";

    /// <summary><c>sessions push</c> ran but some transcripts failed or were left part way; the records say which.</summary>
    public const string Incomplete = "incomplete";

    /// <summary>Stopped by Ctrl+C; what was not sent goes next time.</summary>
    public const string Interrupted = "interrupted";

    // The registry's options, but printing '+', '`', '<' and non-ASCII text as themselves: the output goes to a terminal or
    // a JSON parser, never into HTML, and "\u002B00:00" in every timestamp only makes it harder to read.
    private static readonly JsonSerializerOptions Output = new(TranscriptJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>No transcript has the id given, and no session id starts with it.</summary>
    public const string NotFound = "not_found";

    /// <summary>More than one session id starts with the id given; the error's <c>candidates</c> lists them.</summary>
    public const string AmbiguousId = "ambiguous_id";

    /// <summary>The error code for a failure that stopped a command early, or <c>null</c> for none.</summary>
    public static string? Code(SessionSyncFailure failure) => failure switch
    {
        SessionSyncFailure.None => null,
        SessionSyncFailure.InvalidRegistry => InvalidRegistry,
        SessionSyncFailure.UnknownHarness => UnknownHarness,
        SessionSyncFailure.NotLoggedIn => NotSignedIn,
        SessionSyncFailure.SessionExpired => SessionExpired,
        SessionSyncFailure.Incompatible => RegistryIncompatible,
        _ => RegistryUnreachable,
    };

    /// <summary>Writes <paramref name="records"/> to standard output as one JSON array on one line.</summary>
    public static void WriteArray(IEnumerable<JsonNode> records) =>
        Console.Out.WriteLine(Serialize(new JsonArray([.. records])));

    /// <summary>Writes <paramref name="record"/> to standard output as one JSON object on one line, for a command that reports one thing.</summary>
    public static void WriteObject(JsonNode record) => Console.Out.WriteLine(Serialize(record));

    /// <summary>Writes the error object to standard error and returns <paramref name="exitCode"/>.</summary>
    /// <param name="extra">More fields for the error object, such as the candidates for an ambiguous id; <c>null</c> for none.</param>
    public static int Error(string code, string message, int exitCode = 1, JsonObject? extra = null)
    {
        Console.Error.WriteLine(Serialize(ErrorObject(code, message, extra)));
        return exitCode;
    }

    /// <summary>The error object, <c>{"error":{"code":...,"message":...}}</c>, with <paramref name="extra"/>'s fields added to <c>error</c>.</summary>
    public static JsonObject ErrorObject(string code, string message, JsonObject? extra = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        foreach (var (name, value) in extra ?? [])
            error[name] = value?.DeepClone();
        return new JsonObject { ["error"] = error };
    }

    /// <summary><paramref name="node"/> as one line of JSON, written as every <c>--json</c> output is.</summary>
    public static string Serialize(JsonNode node) => node.ToJsonString(Output);

    /// <summary>A <c>sessions list</c> row: the registry's record with <c>id</c> added, and <c>pending</c> when not fully backed up.</summary>
    /// <remarks>The registry's own fields (<c>key</c> among them) stay as <c>sessions list --json</c> has always printed them.</remarks>
    public static JsonNode ListRecord(ListedSession row)
    {
        var node = JsonSerializer.SerializeToNode(row.Session, TranscriptJson.Options)!.AsObject();
        node.Insert(0, "id", row.Session.Key);
        if (row.Pending is { } pending)
            node["pending"] = JsonSerializer.SerializeToNode(pending, TranscriptJson.Options);
        return node;
    }

    /// <summary>A <c>sessions status</c> row: one transcript on this machine and how much of it is backed up.</summary>
    public static JsonNode StatusRecord(TranscriptStatus t) => JsonSerializer.SerializeToNode(
        new StatusRow(
            t.Transcript.Key, t.Transcript.Harness, Kind(t.Transcript), t.Transcript.ParentKey, t.Transcript.Path,
            SessionsPresenter.State(t), t.SizeOnDisk, t.BytesBackedUp, t.BytesPending, t.LastModifiedAt, t.LastSyncedAt,
            t.BackupFailedAt, t.Verified),
        TranscriptJson.Options)!;

    /// <summary>A <c>sessions push</c> row: what one transcript's backup did.</summary>
    public static JsonNode PushRecord(TranscriptSyncOutcome t) => JsonSerializer.SerializeToNode(
        new PushRow(
            t.Transcript.Key, t.Transcript.Harness, Kind(t.Transcript), t.Transcript.ParentKey, t.Transcript.Path,
            t.Status, t.BytesUploaded, t.BytesPending, t.Rewritten, t.Message, t.Verified),
        TranscriptJson.Options)!;

    /// <summary>The first <c>sessions show</c> record: the session itself, and the skills and custom agents it used when the registry lists them.</summary>
    /// <remarks>
    /// <c>session</c> is the <c>sessions list</c> record. <c>skills</c> is left out when the registry cannot list them
    /// (it lacks <c>session-skills</c>, or the request failed); each has the registry's fields plus <c>installedHere</c>,
    /// which is left out for a skill not looked for (a plugin's). <c>agents</c> is the same for <c>session-agents</c>,
    /// with the registry's fields as they are: the user's own agents only, never built-in or plugin agent types.
    /// </remarks>
    public static JsonNode SessionRecord(RemoteTranscript session, IReadOnlyList<UsedSkill>? skills, IReadOnlyList<SessionAgent>? agents = null)
    {
        var record = new JsonObject
        {
            ["type"] = "session", ["transcriptId"] = session.Key, ["harness"] = session.Harness,
            ["session"] = ListRecord(new ListedSession(session, null)),
        };
        if (skills is not null)
            record["skills"] = new JsonArray([.. skills.Select(SkillRecord)]);
        if (agents is not null)
            record["agents"] = new JsonArray([.. agents.Select(a => JsonSerializer.SerializeToNode(a, TranscriptJson.Options))]);
        return record;
    }

    private static JsonNode SkillRecord(UsedSkill used)
    {
        var node = JsonSerializer.SerializeToNode(used.Skill, TranscriptJson.Options)!.AsObject();
        if (used.InstalledHere is { } installed)
            node["installedHere"] = installed;
        return node;
    }

    /// <summary>A <c>sessions show</c> record: one message of the conversation, or where a subagent starts or ends.</summary>
    /// <remarks>
    /// <c>type</c> is <c>prompt</c>, <c>reply</c>, <c>toolCall</c>, <c>toolResult</c>, <c>subagentStart</c> or
    /// <c>subagentEnd</c>; <c>transcriptId</c> is the session's id, or a subagent's for what ran inside it. A tool call's
    /// full <c>input</c> is included only with <paramref name="full"/>, as are tool results and subagents.
    /// </remarks>
    public static JsonNode ShowRecord(ShownEntry shown, string harness, bool full)
    {
        var record = new JsonObject { ["type"] = null, ["transcriptId"] = shown.TranscriptId, ["harness"] = harness };
        if (shown.Entry.At is { } at)
            record["at"] = at;
        if (shown.Entry.Sidechain)
            record["sidechain"] = true;
        switch (shown.Entry)
        {
            case PromptEntry prompt:
                (record["type"], record["text"]) = ("prompt", prompt.Text);
                break;
            case ReplyEntry reply:
                (record["type"], record["text"]) = ("reply", reply.Text);
                break;
            case ToolCallEntry call:
                (record["type"], record["tool"], record["toolUseId"], record["summary"]) = ("toolCall", call.Name, call.Id, call.Summary);
                if (call.Subagent is { } subagent)
                {
                    if (subagent.AgentType is { } type)
                        record["agentType"] = type;
                    if (subagent.Description is { } description)
                        record["description"] = description;
                }
                if (full && call.Input.ValueKind != JsonValueKind.Undefined)
                    record["input"] = JsonNode.Parse(call.Input.GetRawText());
                break;
            case ToolResultEntry result:
                (record["type"], record["toolUseId"], record["text"], record["isError"]) = ("toolResult", result.ToolUseId, result.Text, result.IsError);
                if (result.AgentId is { } agentId)
                    record["agentId"] = agentId;
                break;
            case SubagentStartEntry start:
                (record["type"], record["linked"]) = ("subagentStart", start.Linked);
                if (start.AgentType is { } agentType)
                    record["agentType"] = agentType;
                if (start.Description is { } startDescription)
                    record["description"] = startDescription;
                break;
            case SubagentEndEntry:
                record["type"] = "subagentEnd";
                break;
        }

        return record;
    }

    /// <summary>A candidate for an ambiguous id, as the <c>ambiguous_id</c> error lists it.</summary>
    public static JsonNode Candidate(RemoteTranscript t)
    {
        var node = new JsonObject { ["id"] = t.Key, ["harness"] = t.Harness };
        if (t.Title is { } title)
            node["title"] = title;
        if (t.LastActivityAt is { } at)
            node["lastActivityAt"] = at;
        return node;
    }

    private static string Kind(LocalTranscript transcript) => transcript.Kind == TranscriptKind.Session ? "session" : "subagent";

    private sealed record StatusRow(
        string Id, string Harness, string Kind, string? ParentId, string Path, TranscriptState State, long Size, long BytesBackedUp,
        long BytesPending, DateTimeOffset LastModifiedAt, DateTimeOffset? LastBackedUpAt, DateTimeOffset? BackupFailedAt,
        VerifiedBackup? Verified);

    private sealed record PushRow(
        string Id, string Harness, string Kind, string? ParentId, string Path, TranscriptSyncStatus Result, long BytesSent,
        long BytesPending, bool Rewritten, string? Message, VerifiedBackup? Verified);
}
