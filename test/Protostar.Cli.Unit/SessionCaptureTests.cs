using Protostar.Cli.Capture;
using Protostar.Cli.Harness;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Tests for the capture hooks that back up sessions.</summary>
public sealed class SessionCaptureTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");

    public SessionCaptureTests() => File.WriteAllText(_file, "{}\n");

    public void Dispose() => File.Delete(_file);

    private string Payload(string hook) =>
        $$"""{"session_id":"s","hook_event_name":"{{hook}}","transcript_path":{{System.Text.Json.JsonSerializer.Serialize(_file)}}}""";

    [Fact]
    public async Task Stop_syncs_the_named_transcript_with_a_small_budget_and_a_throttle()
    {
        var sessions = new SessionSyncServiceTests.FakeSessions();
        var sut = new CaptureService(new NoSkills(), sessions);

        var result = await sut.HandleAsync("Stop", Payload("Stop"), CancellationToken.None);

        Assert.Equal(CaptureAction.SessionBackedUp, result.Action);
        var request = Assert.Single(sessions.Requests);
        Assert.Equal(_file, request.SessionPath);
        Assert.Equal(CaptureService.StopBudget, request.Budget);
        Assert.Equal(CaptureService.StopThrottle, request.Throttle);
    }

    [Fact]
    public async Task SessionEnd_syncs_without_a_throttle()
    {
        var sessions = new SessionSyncServiceTests.FakeSessions();
        var sut = new CaptureService(new NoSkills(), sessions);

        await sut.HandleAsync("SessionEnd", Payload("SessionEnd"), CancellationToken.None);

        Assert.Null(Assert.Single(sessions.Requests).Throttle);
    }

    [Fact]
    public async Task A_payload_without_a_transcript_is_ignored()
    {
        var sessions = new SessionSyncServiceTests.FakeSessions();
        var sut = new CaptureService(new NoSkills(), sessions);

        var result = await sut.HandleAsync("Stop", """{"session_id":"s"}""", CancellationToken.None);

        Assert.Equal(CaptureAction.Ignored, result.Action);
        Assert.Empty(sessions.Requests);
    }

    [Fact]
    public void A_throttled_session_is_reported_as_waiting_not_as_backed_up()
    {
        var transcript = new LocalTranscript("claude-code", "s", TranscriptKind.Session, null, _file, null);
        var sync = new SessionSyncResult(SessionSyncFailure.None, null, new Uri("https://registry.test"),
            [new TranscriptSyncOutcome(transcript, TranscriptSyncStatus.Skipped, 0, 252, false, "Synced moments ago; waiting for the throttle window.")]);

        var line = CapturePresenter.Describe("Stop", new CaptureResult(CaptureAction.SessionBackedUp, null, null, null, sync));

        Assert.DoesNotContain("already backed up", line);
        Assert.Contains("252 B of the session not backed up yet", line);
    }

    private sealed class NoSkills : ISkillPushService
    {
        public Task<SkillPushRunResult> PushAsync(SkillPushRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
