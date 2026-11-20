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
        Console.Out.WriteLine(new JsonArray([.. records]).ToJsonString(Output));

    /// <summary>Writes <paramref name="record"/> to standard output as one JSON object on one line, for a command that reports one thing.</summary>
    public static void WriteObject(JsonNode record) => Console.Out.WriteLine(record.ToJsonString(Output));

    /// <summary>Writes the error object to standard error and returns <paramref name="exitCode"/>.</summary>
    /// <param name="extra">More fields for the error object, such as the candidates for an ambiguous id; <c>null</c> for none.</param>
    public static int Error(string code, string message, int exitCode = 1, JsonObject? extra = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        foreach (var (name, value) in extra ?? [])
            error[name] = value?.DeepClone();
        Console.Error.WriteLine(new JsonObject { ["error"] = error }.ToJsonString(Output));
        return exitCode;
    }

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

    private static string Kind(LocalTranscript transcript) => transcript.Kind == TranscriptKind.Session ? "session" : "subagent";

    private sealed record StatusRow(
        string Id, string Harness, string Kind, string? ParentId, string Path, TranscriptState State, long Size, long BytesBackedUp,
        long BytesPending, DateTimeOffset LastModifiedAt, DateTimeOffset? LastBackedUpAt, DateTimeOffset? BackupFailedAt,
        VerifiedBackup? Verified);

    private sealed record PushRow(
        string Id, string Harness, string Kind, string? ParentId, string Path, TranscriptSyncStatus Result, long BytesSent,
        long BytesPending, bool Rewritten, string? Message, VerifiedBackup? Verified);
}
