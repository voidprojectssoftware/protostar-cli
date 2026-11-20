using System.Diagnostics;
using Protostar.Cli.Auth;
using Protostar.Cli.Capture;
using Protostar.Cli.Harness;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Tests for the capture hooks that hand sessions to a background push.</summary>
public sealed class SessionCaptureTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");

    public SessionCaptureTests() => File.WriteAllText(_file, "{}\n");

    public void Dispose() => File.Delete(_file);

    private string Payload(string hook) =>
        $$"""{"session_id":"s","hook_event_name":"{{hook}}","transcript_path":{{System.Text.Json.JsonSerializer.Serialize(_file)}}}""";

    [Fact]
    public async Task Stop_hands_the_named_transcript_to_a_throttled_background_push()
    {
        var background = new FakeBackground();
        var sut = new CaptureService(new NoSkills(), background);

        var result = await sut.HandleAsync("Stop", Payload("Stop"), CancellationToken.None);

        Assert.Equal(CaptureAction.SessionHandedOff, result.Action);
        Assert.Equal((_file, (TimeSpan?)CaptureService.StopThrottle), Assert.Single(background.Started));
        Assert.Equal("protostar capture (Stop): backing up the session in the background.", CapturePresenter.Describe("Stop", result));
    }

    [Fact]
    public async Task SessionEnd_hands_off_without_a_throttle()
    {
        var background = new FakeBackground();
        var sut = new CaptureService(new NoSkills(), background);

        await sut.HandleAsync("SessionEnd", Payload("SessionEnd"), CancellationToken.None);

        Assert.Null(Assert.Single(background.Started).Throttle);
    }

    [Fact]
    public async Task A_payload_without_a_transcript_is_ignored()
    {
        var background = new FakeBackground();
        var sut = new CaptureService(new NoSkills(), background);

        var result = await sut.HandleAsync("Stop", """{"session_id":"s"}""", CancellationToken.None);

        Assert.Equal(CaptureAction.Ignored, result.Action);
        Assert.Empty(background.Started);
    }

    [Fact]
    public async Task A_push_that_cannot_start_is_logged_and_the_hook_still_exits_0()
    {
        var sut = new CaptureService(new NoSkills(), new FakeBackground { Failure = new System.ComponentModel.Win32Exception("no such file") });
        var logged = new List<string>();

        var result = await sut.HandleAsync("SessionEnd", Payload("SessionEnd"), CancellationToken.None);
        var exit = CapturePresenter.Render("SessionEnd", result, TextWriter.Null, logged.Add);

        Assert.Equal(CaptureAction.Failed, result.Action);
        Assert.Equal(0, exit);
        Assert.Contains("Could not start the session backup: no such file", Assert.Single(logged));
    }

    [Fact]
    public void A_throttled_session_is_reported_as_waiting_not_as_backed_up()
    {
        var sync = Sync(new TranscriptSyncOutcome(Transcript(), TranscriptSyncStatus.Skipped, 0, 252, false, "Synced moments ago; waiting for the throttle window."));
        var output = new StringWriter();
        var logged = new List<string>();

        CapturePresenter.RenderBackgroundPush(sync, output, logged.Add);

        Assert.DoesNotContain("already backed up", output.ToString());
        Assert.Contains("252 B of the session not backed up yet", output.ToString());
        Assert.Empty(logged);
    }

    [Fact]
    public void A_background_push_that_could_not_reach_the_registry_is_logged()
    {
        var logged = new List<string>();

        var exit = CapturePresenter.RenderBackgroundPush(
            SessionSyncResult.Fail(SessionSyncFailure.RegistryUnreachable, null), TextWriter.Null, logged.Add);

        Assert.Equal(0, exit);
        Assert.Contains("protostar sessions push (background): session not backed up: the registry could not be reached", Assert.Single(logged));
    }

    [Fact]
    public void The_unreachable_line_says_what_will_retry_the_session()
    {
        var line = CapturePresenter.DescribeSessions(SessionSyncResult.Fail(SessionSyncFailure.RegistryUnreachable, null));

        Assert.DoesNotContain("next sync", line);
        Assert.Contains("The next hook backup that reaches it retries this session, or run `protostar sessions push`.", line);
    }

    [Fact]
    public void Retried_sessions_are_counted_per_session_and_a_failed_retry_is_logged()
    {
        LocalTranscript Of(string key, string? parent = null) =>
            new("claude-code", key, parent is null ? TranscriptKind.Session : TranscriptKind.Subagent, parent, "/p/" + key + ".jsonl", null);
        var sync = Sync(new TranscriptSyncOutcome(Transcript(), TranscriptSyncStatus.Uploaded, 10, 0, false, null)) with
        {
            Retried =
            [
                new(Of("a"), TranscriptSyncStatus.Uploaded, 5, 0, false, null),
                new(Of("a.agent-1", "a"), TranscriptSyncStatus.UpToDate, 0, 0, false, null),
                new(Of("b"), TranscriptSyncStatus.Failed, 0, 9, false, "The registry answered 503."),
            ],
        };
        var output = new StringWriter();
        var logged = new List<string>();

        CapturePresenter.RenderBackgroundPush(sync, output, logged.Add);

        Assert.Contains("Retried 2 earlier sessions whose backup had failed: 1 backed up, 1 failed again (retried later).", output.ToString());
        Assert.Single(logged);
    }

    // ── how the child is started ─────────────────────────────────────────────────

    [Fact]
    public void The_child_is_the_same_executable_running_sessions_push_for_the_session()
    {
        var startInfo = BackgroundPush.StartInfo("/opt/protostar/protostar", "/home/me/.claude/projects/p/s.jsonl", TimeSpan.FromSeconds(30), windows: false);

        Assert.Equal("/opt/protostar/protostar", startInfo.FileName);
        Assert.Equal(["sessions", "push", "--session", "/home/me/.claude/projects/p/s.jsonl", "--throttle", "30"], startInfo.ArgumentList);
        Assert.Equal(Path.GetTempPath(), startInfo.WorkingDirectory);
    }

    [Fact]
    public void Without_a_throttle_no_throttle_is_passed()
    {
        var startInfo = BackgroundPush.StartInfo("protostar", "s.jsonl", null, windows: false);

        Assert.DoesNotContain("--throttle", startInfo.ArgumentList);
    }

    [Fact]
    public void On_unix_the_child_gets_its_own_pipes_for_every_standard_stream()
    {
        var startInfo = BackgroundPush.StartInfo("protostar", "s.jsonl", null, windows: false);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void On_windows_the_child_starts_through_the_shell_hidden_so_it_inherits_no_handles()
    {
        var startInfo = BackgroundPush.StartInfo(@"C:\Users\me\.protostar\bin\protostar.exe", @"C:\Users\me\.claude\projects\p\s.jsonl", null, windows: true);

        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.False(startInfo.RedirectStandardInput || startInfo.RedirectStandardOutput || startInfo.RedirectStandardError);
        Assert.Equal(@"C:\Users\me\.protostar\bin\protostar.exe", startInfo.FileName);
    }

    private LocalTranscript Transcript() => new("claude-code", "s", TranscriptKind.Session, null, _file, null);

    private static SessionSyncResult Sync(params TranscriptSyncOutcome[] outcomes) =>
        new(SessionSyncFailure.None, null, new Uri("https://registry.test"), outcomes);

    internal sealed class FakeBackground : IBackgroundPush
    {
        public List<(string Path, TimeSpan? Throttle)> Started { get; } = [];

        public Exception? Failure { get; init; }

        public void Start(string transcriptPath, TimeSpan? throttle)
        {
            if (Failure is not null)
                throw Failure;
            Started.Add((transcriptPath, throttle));
        }
    }

    private sealed class NoSkills : ISkillPushService
    {
        public Task<SkillPushRunResult> PushAsync(SkillPushRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

/// <summary>Tests for the file-backed <see cref="SessionLedger"/>'s upload lock, redirected to a temp config dir.</summary>
[Collection("ProtostarConfigDir")]
public sealed class SessionLedgerLockTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pstar-session-ledger-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable(ProtostarPaths.ConfigDirEnvVar);

    public SessionLedgerLockTests() => Environment.SetEnvironmentVariable(ProtostarPaths.ConfigDirEnvVar, _dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ProtostarPaths.ConfigDirEnvVar, _previous);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Only_one_holder_at_a_time_takes_a_transcripts_lock_and_it_frees_on_release()
    {
        var registry = new Uri("https://registry.test");
        var ledger = new SessionLedger();

        using (var first = ledger.TryLock(registry, "claude-code", "s"))
        {
            Assert.NotNull(first);
            Assert.Null(new SessionLedger().TryLock(registry, "claude-code", "s"));
            using var other = ledger.TryLock(registry, "claude-code", "t");
            Assert.NotNull(other);
        }

        using var again = ledger.TryLock(registry, "claude-code", "s");
        Assert.NotNull(again);
        Assert.StartsWith(_dir, Directory.EnumerateFiles(_dir, "*.lock", SearchOption.AllDirectories).Single());
    }

    [Fact]
    public void Failure_marks_list_oldest_first_and_a_clear_keeps_a_mark_made_after_the_run_began()
    {
        var registry = new Uri("https://registry.test");
        var ledger = new SessionLedger();
        var now = DateTimeOffset.UtcNow;
        ledger.MarkFailed(registry, new FailedBackup("claude-code", "new", "/p/new.jsonl", now));
        ledger.MarkFailed(registry, new FailedBackup("claude-code", "old", "/p/old.jsonl", now.AddMinutes(-5)));

        Assert.Equal(["old", "new"], ledger.FailedBackups(registry).Select(m => m.Key));
        Assert.Empty(ledger.FailedBackups(new Uri("https://other.test")));

        ledger.ClearFailed(registry, "claude-code", "old", before: now.AddMinutes(-1));
        ledger.ClearFailed(registry, "claude-code", "new", before: now.AddMinutes(-1));

        Assert.Equal("new", Assert.Single(ledger.FailedBackups(registry)).Key);
        Assert.Null(ledger.Get(registry, "claude-code", "new"));
    }
}
