using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>The <c>--json</c> records and error codes of the <c>sessions</c> commands (docs/reference/json-output.md).</summary>
public sealed class SessionsJsonTests
{
    private const string SessionId = "6f83525b-790f-4654-944d-ba3b0c96453e";

    private static readonly LocalTranscript Subagent =
        new("claude-code", $"{SessionId}.agent-a1", TranscriptKind.Subagent, SessionId, "/h/projects/p/s/subagents/agent-a1.jsonl", null);

    [Fact]
    public void Every_failure_has_its_own_documented_code()
    {
        var codes = Enum.GetValues<SessionSyncFailure>().Where(f => f != SessionSyncFailure.None).Select(SessionsJson.Code).ToList();

        Assert.Equal(
            ["invalid_registry", "unknown_harness", "not_signed_in", "session_expired", "registry_unreachable", "registry_incompatible"],
            codes);
        Assert.Null(SessionsJson.Code(SessionSyncFailure.None));
    }

    [Fact]
    public void A_status_record_names_the_transcript_by_its_key_and_its_session_by_parent_id()
    {
        var status = new TranscriptStatus(Subagent, 300, 100, 200, false, null, DateTimeOffset.Parse("2026-11-19T10:00:00Z"));

        var record = SessionsJson.StatusRecord(status).AsObject();

        Assert.Equal($"{SessionId}.agent-a1", (string?)record["id"]);
        Assert.Equal(SessionId, (string?)record["parentId"]);
        Assert.Equal("subagent", (string?)record["kind"]);
        Assert.Equal("newContent", (string?)record["state"]);
        Assert.Equal(200, (long?)record["bytesPending"]);
        // Absent values are left out, not written as null.
        Assert.False(record.ContainsKey("lastBackedUpAt"));
        Assert.False(record.ContainsKey("verified"));
    }

    [Fact]
    public void A_push_record_says_what_happened_with_a_camel_case_result()
    {
        var outcome = new TranscriptSyncOutcome(Subagent, TranscriptSyncStatus.UpToDate, 0, 0, false, null,
            new VerifiedBackup(BackupCheck.Confirmed, 1, 300));

        var record = SessionsJson.PushRecord(outcome).AsObject();

        Assert.Equal("upToDate", (string?)record["result"]);
        Assert.Equal("confirmed", (string?)record["verified"]!["check"]);
        Assert.False(record.ContainsKey("message"));
    }

    [Fact]
    public void A_list_record_puts_the_id_first_and_keeps_the_registry_fields()
    {
        var row = new ListedSession(new RemoteTranscript { Harness = "claude-code", Key = SessionId, Kind = "session", Title = "t" }, null);

        var record = SessionsJson.ListRecord(row).AsObject();

        Assert.Equal("id", record.First().Key);
        Assert.Equal(SessionId, (string?)record["id"]);
        Assert.Equal(SessionId, (string?)record["key"]);
        Assert.False(record.ContainsKey("pending"));
    }
}
