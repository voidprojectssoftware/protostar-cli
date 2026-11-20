using System.Text;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <see cref="ISessionSyncService"/>, against an in-memory registry that enforces the real append rules.</summary>
public sealed class SessionSyncServiceTests : IDisposable
{
    private const string Registry = "https://registry.test";
    private const string SessionId = "6f83525b-790f-4654-944d-ba3b0c96453e";
    private readonly string _root = Directory.CreateTempSubdirectory("pstar-sessions-").FullName;
    private readonly FakeRegistry _registry = new();
    private readonly FakeLedger _ledger = new();
    private readonly FakeProjects _projects = new();
    private readonly FakeMachine _machine = new();
    // Overrides _token when set, for a lookup a stored token alone cannot give (an unreachable refresh).
    private SessionLookup? _session;
    private StoredToken? _token = new() { Registry = Registry, AccessToken = "tok", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ProjectDir => Path.Combine(_root, "projects", "-home-me-src-protostar");

    private string SessionFile => Path.Combine(ProjectDir, SessionId + ".jsonl");

    private string WriteSession(params string[] lines)
    {
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(SessionFile, string.Concat(lines.Select(l => l + "\n")));
        return SessionFile;
    }

    private void AppendRaw(string text) => File.AppendAllText(SessionFile, text);

    private ISessionSyncService Sut() => new SessionSyncService(
        new FakeCatalog(new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper())),
        new FakeTokens(() => _session ?? SessionLookups.Of(_token)), _registry, _registry, _ledger, _machine, _projects, _registry, TimeProvider.System);

    private SessionSyncRequest Request(TimeSpan? budget = null, TimeSpan? throttle = null, string? sessionPath = null) =>
        new(Registry, "claude-code", _root, sessionPath, budget, throttle);

    private Task<SessionSyncResult> Sync(TimeSpan? budget = null, TimeSpan? throttle = null) =>
        Sut().SyncAsync(Request(budget, throttle), null, CancellationToken.None);

    [Fact]
    public async Task First_sync_uploads_the_whole_session_and_registers_the_machine_once()
    {
        WriteSession("""{"type":"user"}""", """{"type":"assistant"}""");

        var result = await Sync();

        Assert.True(result.Complete);
        var outcome = Assert.Single(result.Transcripts);
        Assert.Equal(TranscriptSyncStatus.Uploaded, outcome.Status);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
        Assert.Equal(1, _registry.MachineRegistrations);
    }

    [Fact]
    public async Task Syncing_again_with_nothing_new_makes_no_network_call()
    {
        WriteSession("""{"type":"user"}""");
        await Sync();
        _registry.ResetCounters();
        _token = null;

        var result = await Sync();

        Assert.True(result.Complete);
        Assert.Equal(TranscriptSyncStatus.UpToDate, Assert.Single(result.Transcripts).Status);
        Assert.Equal(0, _registry.Calls);
    }

    [Fact]
    public async Task Only_new_lines_are_uploaded_after_the_session_grows()
    {
        WriteSession("""{"n":1}""");
        await Sync();
        _registry.ResetCounters();
        AppendRaw("{\"n\":2}\n{\"n\":3}\n");

        var result = await Sync();

        Assert.Equal(16, Assert.Single(result.Transcripts).BytesUploaded);
        Assert.Equal(1, _registry.Appends);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
    }

    [Fact]
    public async Task A_line_still_being_written_waits_until_it_is_complete()
    {
        WriteSession("""{"n":1}""");
        AppendRaw("{\"n\":2,\"partial");

        var first = await Sync();
        Assert.Equal(8, Assert.Single(first.Transcripts).BytesUploaded);
        _registry.ResetCounters();

        var meanwhile = await Sync();
        Assert.Equal(TranscriptSyncStatus.UpToDate, Assert.Single(meanwhile.Transcripts).Status);
        Assert.Equal(0, _registry.Calls);

        AppendRaw("\":true}\n");
        await Sync();
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
    }

    [Fact]
    public async Task A_rewritten_file_starts_a_new_revision_from_the_beginning()
    {
        WriteSession("""{"n":1}""", """{"n":2}""", """{"n":3}""");
        await Sync();
        WriteSession("""{"compacted":true}""");

        var result = await Sync();

        var outcome = Assert.Single(result.Transcripts);
        Assert.True(outcome.Rewritten);
        Assert.Equal(2, _registry.Revision(SessionId));
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
    }

    [Fact]
    public async Task A_lost_ledger_resumes_from_what_the_registry_already_has()
    {
        WriteSession("""{"n":1}""", """{"n":2}""");
        await Sync();
        _ledger.Clear();
        AppendRaw("{\"n\":3}\n");
        _registry.ResetCounters();

        await Sync();

        Assert.Equal(1, _registry.Appends);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
    }

    [Fact]
    public async Task A_conflict_adopts_the_registry_position_and_carries_on()
    {
        WriteSession("""{"n":1}""");
        await Sync();
        AppendRaw("{\"n\":2}\n");
        // Another process got the new line there first.
        _registry.ForceContent(SessionId, File.ReadAllBytes(SessionFile));
        AppendRaw("{\"n\":3}\n");

        var result = await Sync();

        Assert.True(result.Complete);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
    }

    [Fact]
    public async Task Subagents_are_uploaded_with_their_session_and_metadata()
    {
        WriteSession("""{"type":"user"}""");
        var subagents = Path.Combine(ProjectDir, SessionId, "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllText(Path.Combine(subagents, "agent-ae75b028226c93701.jsonl"), "{\"type\":\"user\"}\n");
        File.WriteAllText(Path.Combine(subagents, "agent-ae75b028226c93701.meta.json"), """{"agentType":"general-purpose"}""");

        var result = await Sync();

        Assert.Equal(2, result.Transcripts.Count);
        var registered = _registry.Registered[$"{SessionId}.agent-ae75b028226c93701"];
        Assert.Equal(TranscriptKind.Subagent, registered.Kind);
        Assert.Equal(SessionId, registered.ParentKey);
        Assert.Contains("general-purpose", registered.MetadataJson);
    }

    [Fact]
    public async Task Large_sessions_are_sent_in_chunks_of_whole_lines()
    {
        Directory.CreateDirectory(ProjectDir);
        var line = new string('x', 1023) + "\n";
        File.WriteAllText(SessionFile, string.Concat(Enumerable.Repeat(line, 20 * 1024)));

        await Sync();

        Assert.Equal(3, _registry.Appends);
        Assert.All(_registry.ChunkSizes, size => Assert.Equal(0, size % 1024));
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
    }

    [Fact]
    public async Task An_exhausted_budget_leaves_the_rest_for_next_time()
    {
        WriteSession("""{"n":1}""");

        var result = await Sync(budget: TimeSpan.Zero);

        Assert.False(result.Complete);
        Assert.Equal(TranscriptSyncStatus.Skipped, Assert.Single(result.Transcripts).Status);
        Assert.Equal(0, _registry.Appends);
    }

    [Fact]
    public async Task The_throttle_skips_a_session_synced_moments_ago()
    {
        WriteSession("""{"n":1}""");
        await Sync();
        AppendRaw("{\"n\":2}\n");

        var result = await Sync(throttle: TimeSpan.FromMinutes(5));

        Assert.Equal(TranscriptSyncStatus.Skipped, Assert.Single(result.Transcripts).Status);
    }

    [Fact]
    public async Task Offline_the_new_bytes_stay_pending_and_the_next_push_sends_them()
    {
        WriteSession("""{"n":1}""");
        await Sync();
        AppendRaw("{\"n\":2}\n");
        _registry.Offline = true;

        var offline = await Sut().SyncAsync(Request(sessionPath: SessionFile), null, CancellationToken.None);

        Assert.Equal(SessionSyncFailure.RegistryUnreachable, offline.Failure);
        Assert.Equal(8, Assert.Single(Sut().Status(Registry, "claude-code", _root).Transcripts).BytesPending);

        _registry.Offline = false;
        var online = await Sync();

        Assert.True(online.Complete);
        Assert.Equal(8, Assert.Single(online.Transcripts).BytesUploaded);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
        Assert.True(Sut().Status(Registry, "claude-code", _root).FullySynced);
    }

    [Fact]
    public async Task A_push_that_waited_for_the_lock_plans_again_from_what_the_holder_uploaded()
    {
        WriteSession("""{"n":1}""");
        await Sync();
        AppendRaw("{\"n\":2}\n");
        _ledger.OnLock = () => Sync().GetAwaiter().GetResult();
        _registry.ResetCounters();

        var result = await Sync();

        // The holder sent the new line; this push finds nothing left and writes no stale position back.
        Assert.Equal(TranscriptSyncStatus.UpToDate, Assert.Single(result.Transcripts).Status);
        Assert.Equal(1, _registry.Appends);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
        Assert.Equal(new FileInfo(SessionFile).Length, _ledger.Get(new Uri(Registry), "claude-code", SessionId)!.Length);
    }

    [Fact]
    public async Task A_push_that_waited_for_the_lock_respects_the_throttle_the_holder_set()
    {
        WriteSession("""{"n":1}""");
        await Sync();
        AppendRaw("{\"n\":2}\n");
        var registry = new Uri(Registry);
        _ledger.Save(registry, "claude-code", SessionId, _ledger.Get(registry, "claude-code", SessionId)! with { LastAttemptAt = DateTimeOffset.UtcNow.AddHours(-1) });
        _ledger.OnLock = () =>
        {
            Sync().GetAwaiter().GetResult();
            AppendRaw("{\"n\":3}\n");
        };

        var result = await Sync(throttle: TimeSpan.FromMinutes(5));

        Assert.Equal(TranscriptSyncStatus.Skipped, Assert.Single(result.Transcripts).Status);
    }

    [Fact]
    public async Task Without_a_session_new_content_reports_not_logged_in()
    {
        WriteSession("""{"n":1}""");
        _token = null;

        var result = await Sync();

        Assert.Equal(SessionSyncFailure.NotLoggedIn, result.Failure);
    }

    [Fact]
    public async Task A_session_the_registry_could_not_be_reached_to_refresh_reports_the_registry_unreachable()
    {
        WriteSession("""{"n":1}""");
        _session = SessionLookups.Unreachable(Registry);

        var result = await Sync();

        Assert.Equal(SessionSyncFailure.RegistryUnreachable, result.Failure);
    }

    [Fact]
    public async Task Status_reports_pending_and_rewritten_files_offline()
    {
        WriteSession("""{"n":1}""");
        await Sync();
        _registry.ResetCounters();
        _token = null;

        Assert.True(Sut().Status(Registry, "claude-code", _root).FullySynced);

        AppendRaw("{\"n\":2}\n");
        var grown = Assert.Single(Sut().Status(Registry, "claude-code", _root).Transcripts);
        Assert.Equal(8, grown.BytesPending);
        Assert.False(grown.Rewritten);

        WriteSession("""{"x":0}""");
        var rewritten = Assert.Single(Sut().Status(Registry, "claude-code", _root).Transcripts);
        Assert.True(rewritten.Rewritten);
        Assert.Equal(new FileInfo(SessionFile).Length, rewritten.BytesPending);
        Assert.Equal(0, _registry.Calls);
    }

    [Fact]
    public async Task A_single_session_can_be_synced_by_its_transcript_path()
    {
        WriteSession("""{"n":1}""");
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(Path.Combine(ProjectDir, "0b8f5c21-0000-4000-8000-000000000000.jsonl"), "{}\n");

        var result = await Sut().SyncAsync(Request(sessionPath: SessionFile), null, CancellationToken.None);

        Assert.Equal(SessionId, Assert.Single(result.Transcripts).Transcript.Key);
    }

    [Fact]
    public void A_harness_home_override_pins_the_root()
    {
        var harness = new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper());

        Assert.True(harness.TryLocate(_root, out var location));

        Assert.True(location.Pinned);
    }

    [Fact]
    public void A_pinned_root_never_reaches_outside_itself_for_transcripts()
    {
        // Regression: the locator used to add ~/.claude/projects to every search, so a test or fixture run
        // read (and the in-memory fake registry buffered) the real transcripts on the machine.
        WriteSession("""{"n":1}""");
        var harness = new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper());

        var found = harness.LocateTranscripts(new HarnessLocation(_root, Path.Combine(_root, "settings.json"), Pinned: true));

        Assert.Equal(SessionFile, Assert.Single(found).Path);
    }

    [Fact]
    public void Reading_whole_lines_never_cuts_a_line()
    {
        WriteSession("""{"a":1}""");
        AppendRaw("{\"b\":");

        Assert.Equal(Encoding.UTF8.GetBytes("{\"a\":1}\n"), SessionSyncService.ReadWholeLines(SessionFile, 0));
        Assert.Empty(SessionSyncService.ReadWholeLines(SessionFile, 8));
    }

    // ── a large first backup ──────────────────────────────────────────────────────

    private string WriteSessionIn(string project, string id, DateTime modifiedUtc, int bytes = 8)
    {
        var dir = Path.Combine(_root, "projects", project);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + ".jsonl");
        var line = new string('x', 1023) + "\n";
        File.WriteAllText(path, bytes < 1024 ? "{\"n\":1}\n" : string.Concat(Enumerable.Repeat(line, bytes / 1024)));
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return path;
    }

    private const string Older = "0b8f5c21-0000-4000-8000-000000000001";
    private const string Oldest = "0b8f5c21-0000-4000-8000-000000000002";

    [Fact]
    public async Task The_most_recently_written_transcripts_go_first()
    {
        var now = DateTime.UtcNow;
        WriteSessionIn("-a", Oldest, now.AddDays(-30));
        WriteSessionIn("-b", SessionId, now.AddMinutes(-1));
        WriteSessionIn("-c", Older, now.AddDays(-2));

        await Sync();

        Assert.Equal([SessionId, Older, Oldest], _registry.RegistrationOrder);
    }

    [Fact]
    public async Task Progress_counts_bytes_within_a_large_transcript_and_adds_up_to_the_plan()
    {
        var size = new FileInfo(WriteSessionIn("-big", SessionId, DateTime.UtcNow, 20 * 1024 * 1024)).Length;
        WriteSession("""{"n":1}""");
        File.SetLastWriteTimeUtc(SessionFile, DateTime.UtcNow.AddDays(-1));
        var reports = new List<SessionSyncProgress>();

        await Sut().SyncAsync(Request(), new Recorder(reports.Add), CancellationToken.None);

        Assert.Equal(new SessionSyncProgress(0, 2, 0, size + 8, null), reports[0]);
        Assert.Equal(new SessionSyncProgress(2, 2, size + 8, size + 8, null), reports[^1]);
        var big = reports.Where(r => r.Current == SessionId && r.TranscriptsDone == 0).Select(r => r.BytesDone).ToList();
        Assert.Equal([0, SessionSyncService.ChunkBytes, 2L * SessionSyncService.ChunkBytes, size], big);
        Assert.Equal(reports.Select(r => r.BytesDone).Order(), reports.Select(r => r.BytesDone));
    }

    [Fact]
    public async Task A_failed_transcript_counts_as_finished_with_but_its_bytes_never_count_as_done()
    {
        var size = new FileInfo(WriteSessionIn("-a", SessionId, DateTime.UtcNow, 4096)).Length;
        _registry.AppendAnswer = new AppendResult(AppendStatus.Failed, null, "The registry returned 500.");
        var reports = new List<SessionSyncProgress>();

        var result = await Sut().SyncAsync(Request(), new Recorder(reports.Add), CancellationToken.None);

        Assert.Equal(TranscriptSyncStatus.Failed, Assert.Single(result.Transcripts).Status);
        Assert.Equal(new SessionSyncProgress(1, 1, 0, size, null, TranscriptsUnfinished: 1), reports[^1]);
    }

    [Fact]
    public async Task Stopping_part_way_never_reports_the_run_as_done()
    {
        var size = new FileInfo(WriteSessionIn("-big", SessionId, DateTime.UtcNow, 20 * 1024 * 1024)).Length;
        WriteSessionIn("-small", Older, DateTime.UtcNow.AddDays(-1));
        using var stop = new CancellationTokenSource();
        _registry.AfterAppend = _ => stop.Cancel();
        var reports = new List<SessionSyncProgress>();

        await Sut().SyncAsync(Request(), new Recorder(reports.Add), stop.Token);

        Assert.All(reports, r => Assert.True(r.TranscriptsDone < r.TranscriptsTotal && r.BytesDone < r.BytesTotal));
        Assert.True(reports[^1].BytesDone < size);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_part_way_loses_nothing_and_the_next_push_sends_only_the_rest(bool responseLost)
    {
        var bigFile = WriteSessionIn("-big", SessionId, DateTime.UtcNow, 20 * 1024 * 1024);
        var smallFile = WriteSessionIn("-small", Older, DateTime.UtcNow.AddDays(-1));
        using var stop = new CancellationTokenSource();
        // Ctrl+C arrives while the first chunk is in flight; the registry keeps it either way.
        _registry.AfterAppend = token =>
        {
            stop.Cancel();
            if (responseLost)
                token.ThrowIfCancellationRequested();
        };

        var stopped = await Sut().SyncAsync(Request(), null, stop.Token);

        Assert.True(stopped.Stopped);
        Assert.False(stopped.Complete);
        Assert.Equal(SessionSyncService.ChunkBytes, _registry.Content(SessionId).Length);
        var entry = _ledger.Get(new Uri(Registry), "claude-code", SessionId)!;
        Assert.Equal(responseLost ? 0 : SessionSyncService.ChunkBytes, entry.Length);
        Assert.DoesNotContain(Older, _registry.RegistrationOrder);

        _registry.AfterAppend = null;
        _registry.ResetCounters();
        var resumed = await Sync();

        Assert.True(resumed.Complete);
        Assert.False(resumed.Stopped);
        Assert.Equal(new FileInfo(bigFile).Length - SessionSyncService.ChunkBytes, resumed.Transcripts.Single(t => t.Transcript.Key == SessionId).BytesUploaded);
        Assert.Equal(3, _registry.Appends);
        Assert.Equal(File.ReadAllBytes(bigFile), _registry.Content(SessionId));
        Assert.Equal(File.ReadAllBytes(smallFile), _registry.Content(Older));
    }

    private sealed class Recorder(Action<SessionSyncProgress> record) : IProgress<SessionSyncProgress>
    {
        public void Report(SessionSyncProgress value) => record(value);
    }

    // ── projects ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_session_is_registered_with_the_facts_of_the_directory_its_lines_record()
    {
        WriteSession("""{"type":"permission-mode"}""", """{"type":"user","cwd":"/home/me/src/widget"}""");

        await Sync();

        var facts = Assert.Single(_registry.Filed[SessionId]);
        Assert.Equal("/home/me/src/widget", facts!.WorkingDirectory);
    }

    [Fact]
    public async Task A_transcript_backed_up_before_projects_is_registered_again_once_with_facts()
    {
        WriteSession("""{"type":"user","cwd":"/home/me/src/widget"}""");
        await Sync();
        _ledger.Save(new Uri(Registry), "claude-code", SessionId, _ledger.Get(new Uri(Registry), "claude-code", SessionId)! with { Project = null });
        _registry.ResetCounters();

        var first = await Sync();
        _registry.ResetCounters();
        var second = await Sync();

        Assert.Equal(TranscriptSyncStatus.UpToDate, Assert.Single(first.Transcripts).Status);
        Assert.Equal(2, _registry.Filed[SessionId].Count);
        Assert.NotNull(_registry.Filed[SessionId][1]);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
        Assert.True(second.Complete);
        Assert.Equal(0, _registry.Calls);
    }

    [Fact]
    public async Task A_session_registered_before_it_recorded_a_directory_sends_facts_with_its_next_upload()
    {
        WriteSession("""{"type":"permission-mode"}""");
        await Sync();
        AppendRaw("""{"type":"user","cwd":"/home/me/notes"}""" + "\n");

        await Sync();

        Assert.Equal([null, "/home/me/notes"], _registry.Filed[SessionId].Select(f => f?.WorkingDirectory));
    }

    [Fact]
    public async Task A_session_with_no_directory_and_nothing_new_stays_offline()
    {
        WriteSession("""{"type":"permission-mode"}""");
        await Sync();
        _registry.ResetCounters();

        var result = await Sync();

        Assert.True(result.Complete);
        Assert.Equal(0, _registry.Calls);
    }

    [Fact]
    public async Task A_registry_that_forgot_the_machine_gets_it_back_under_the_same_id_and_the_append_goes_through()
    {
        WriteSession("""{"n":1}""");
        await Sync();
        _registry.ForgetMachines();
        AppendRaw("{\"n\":2}\n");

        var result = await Sync();

        Assert.Equal(TranscriptSyncStatus.Uploaded, Assert.Single(result.Transcripts).Status);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
        Assert.Equal([FakeMachine.Id, FakeMachine.Id], _registry.MachineRegistrationIds);
        Assert.True(_machine.IsRegisteredWith(new Uri(Registry)));
    }

    [Fact]
    public async Task A_registry_that_forgot_the_machine_gets_it_back_when_a_new_transcript_registers()
    {
        _machine.MarkRegistered(new Uri(Registry));
        WriteSession("""{"n":1}""");

        var result = await Sync();

        Assert.Equal(TranscriptSyncStatus.Uploaded, Assert.Single(result.Transcripts).Status);
        Assert.Equal([FakeMachine.Id], _registry.MachineRegistrationIds);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
    }

    [Fact]
    public async Task A_failed_re_registration_still_retries_once_because_another_process_may_have_registered_the_machine()
    {
        WriteSession("""{"n":1}""");
        await Sync();
        _registry.ForgetMachines();
        _registry.MachineRegistration = MachineRegistrationBehaviour.FailsAfterAnotherProcessRegisters;
        AppendRaw("{\"n\":2}\n");

        var result = await Sync();

        Assert.Equal(TranscriptSyncStatus.Uploaded, Assert.Single(result.Transcripts).Status);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
    }

    [Fact]
    public async Task A_registry_that_still_refuses_after_re_registration_fails_the_transcript_without_looping()
    {
        WriteSession("""{"n":1}""");
        await Sync();
        _registry.ForgetMachines();
        _registry.MachineRegistration = MachineRegistrationBehaviour.SucceedsButIsForgotten;
        AppendRaw("{\"n\":2}\n");
        _registry.ResetCounters();

        var result = await Sync();

        var outcome = Assert.Single(result.Transcripts);
        Assert.Equal(TranscriptSyncStatus.Failed, outcome.Status);
        Assert.Contains("is not registered", outcome.Message);
        Assert.Equal(2, _registry.MachineRegistrationIds.Count);
        Assert.Equal(0, _registry.Appends);
    }

    // A line recording a working directory, so the transcript's project facts are sent and an unchanged ledger entry
    // needs no registration: only the append can discover that the registry lost the transcript.
    private const string WithDirectory = """{"type":"user","cwd":"/home/me/src/protostar"}""";

    [Fact]
    public async Task A_registry_that_lost_the_transcript_gets_it_registered_again_and_uploaded_from_the_start()
    {
        WriteSession(WithDirectory, """{"n":2}""");
        await Sync();
        _registry.ForgetTranscript(SessionId);
        var backedUp = new FileInfo(SessionFile).Length;
        AppendRaw("{\"n\":3}\n");
        _registry.AppendRequests.Clear();

        var result = await Sync();

        Assert.Equal(TranscriptSyncStatus.Uploaded, Assert.Single(result.Transcripts).Status);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
        Assert.Equal(1, _registry.Revision(SessionId));
        // The first append went where the ledger said; after the unknown answer, the upload restarted at 0 as revision 1.
        Assert.Equal([(SessionId, 1, backedUp), (SessionId, 1, 0L)], _registry.AppendRequests);
        var entry = _ledger.Get(new Uri(Registry), "claude-code", SessionId)!;
        Assert.Equal((1, new FileInfo(SessionFile).Length), (entry.Revision, entry.Length));
    }

    [Fact]
    public async Task After_a_full_reset_the_machine_comes_back_first_then_the_transcript()
    {
        WriteSession(WithDirectory);
        await Sync();
        _registry.ForgetMachines();
        _registry.ForgetTranscript(SessionId);
        AppendRaw("{\"n\":2}\n");
        _registry.RegistrationOrder.Clear();

        var result = await Sync();

        Assert.Equal(TranscriptSyncStatus.Uploaded, Assert.Single(result.Transcripts).Status);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
        Assert.Equal([FakeMachine.Id, FakeMachine.Id], _registry.MachineRegistrationIds);
        Assert.Equal([SessionId], _registry.RegistrationOrder);
        Assert.Equal(new FileInfo(SessionFile).Length, _ledger.Get(new Uri(Registry), "claude-code", SessionId)!.Length);
    }

    [Fact]
    public async Task A_lost_subagent_is_registered_again_under_its_session()
    {
        WriteSession(WithDirectory);
        var subagents = Path.Combine(ProjectDir, SessionId, "subagents");
        Directory.CreateDirectory(subagents);
        var agentFile = Path.Combine(subagents, "agent-ae75b028226c93701.jsonl");
        File.WriteAllText(agentFile, WithDirectory + "\n");
        await Sync();
        var key = $"{SessionId}.agent-ae75b028226c93701";
        _registry.ForgetTranscript(SessionId);
        _registry.ForgetTranscript(key);
        File.AppendAllText(agentFile, "{\"type\":\"assistant\"}\n");

        var result = await Sync();

        Assert.True(result.Complete);
        Assert.Equal(TranscriptKind.Subagent, _registry.Registered[key].Kind);
        Assert.Equal(SessionId, _registry.Registered[key].ParentKey);
        Assert.Equal(File.ReadAllBytes(agentFile), _registry.Content(key));
    }

    [Theory]
    [InlineData(false, "The registry returned 500.")]
    [InlineData(false, "Could not reach the registry: connection refused")]
    [InlineData(true, "Refused by the registry (404).")]
    public async Task Any_other_failure_leaves_the_ledger_where_it_was(bool refused, string message)
    {
        WriteSession(WithDirectory, """{"n":2}""");
        await Sync();
        var before = _ledger.Get(new Uri(Registry), "claude-code", SessionId)!;
        AppendRaw("{\"n\":3}\n");
        _registry.AppendAnswer = new AppendResult(refused ? AppendStatus.Rejected : AppendStatus.Failed, null, message);
        _registry.AppendRequests.Clear();

        var result = await Sync();

        Assert.Equal(TranscriptSyncStatus.Failed, Assert.Single(result.Transcripts).Status);
        Assert.Equal([(SessionId, before.Revision, before.Length)], _registry.AppendRequests);
        var after = _ledger.Get(new Uri(Registry), "claude-code", SessionId)!;
        Assert.Equal((before.Revision, before.Length, before.TailSha256), (after.Revision, after.Length, after.TailSha256));
    }

    [Fact]
    public async Task A_transcript_still_unknown_after_registering_again_fails_without_looping()
    {
        WriteSession(WithDirectory);
        await Sync();
        _registry.ForgetTranscript(SessionId);
        _registry.ForgetsTranscriptsAfterRegistering = true;
        AppendRaw("{\"n\":2}\n");
        _registry.AppendRequests.Clear();
        _registry.RegistrationOrder.Clear();

        var result = await Sync();

        var outcome = Assert.Single(result.Transcripts);
        Assert.Equal(TranscriptSyncStatus.Failed, outcome.Status);
        Assert.Equal(2, _registry.AppendRequests.Count);
        Assert.Single(_registry.RegistrationOrder);
    }

    // ── listing ───────────────────────────────────────────────────────────────────

    private static readonly string[] BackedUpIds =
        ["aaaaaaaa-1111-4111-8111-000000000001", "aaaaaaaa-1111-4111-8111-000000000002", "aaaaaaaa-1111-4111-8111-000000000003"];

    private const string AppendedId = "bbbbbbbb-2222-4222-8222-000000000004";
    private const string NeverId = "cccccccc-3333-4333-8333-000000000005";

    private string WriteSessionAs(string id, string cwd = "/home/me/src/protostar")
    {
        Directory.CreateDirectory(ProjectDir);
        var path = Path.Combine(ProjectDir, id + ".jsonl");
        File.WriteAllText(path, $$"""{"type":"user","cwd":"{{cwd}}"}""" + "\n");
        return path;
    }

    // Three sessions fully backed up, one appended to after its backup, one never backed up; the registry lists the four it has.
    private async Task ArrangeHistoryAsync()
    {
        foreach (var id in BackedUpIds.Append(AppendedId))
            WriteSessionAs(id);
        await Sync();
        foreach (var id in BackedUpIds.Append(AppendedId))
        {
            _registry.Listed.Add(new RemoteTranscript
            {
                Harness = "claude-code", Key = id, Kind = "session", Title = "Session " + id[..8], Project = "github.com/me/protostar",
                ProjectId = _registry.Resolves.ProjectId, MachineId = FakeMachine.Id, MachineName = "test",
                LastActivityAt = DateTimeOffset.UtcNow.AddHours(-1), Length = 40,
            });
        }

        File.AppendAllText(Path.Combine(ProjectDir, AppendedId + ".jsonl"), "{\"n\":2}\n");
        WriteSessionAs(NeverId);
    }

    private Task<SessionListResult> List(string? project = null, Guid? machine = null, int limit = 25) =>
        Sut().ListAsync(Registry, _root, new TranscriptListQuery(project, machine, "claude-code", null, limit), CancellationToken.None);

    [Fact]
    public async Task Listing_shows_a_partly_backed_up_session_once_with_its_pending_bytes_and_a_new_one_as_pending()
    {
        await ArrangeHistoryAsync();
        File.SetLastWriteTimeUtc(Path.Combine(ProjectDir, NeverId + ".jsonl"), DateTime.UtcNow.AddMinutes(1));

        var result = await List();

        Assert.Equal(SessionSyncFailure.None, result.Failure);
        Assert.Equal(5, result.Sessions.Count);
        Assert.Equal(5, result.Sessions.Select(r => r.Session.Key).Distinct().Count());
        var appended = Assert.Single(result.Sessions, r => r.Session.Key == AppendedId);
        Assert.Equal("Session bbbbbbbb", appended.Session.Title);
        Assert.Equal(new PendingBackup(8, true, false, Path.Combine(ProjectDir, AppendedId + ".jsonl"), appended.Pending!.ChangedAt), appended.Pending);
        var never = Assert.Single(result.Sessions, r => r.Session.Key == NeverId);
        Assert.False(never.Pending!.InRegistry);
        Assert.True(never.Pending.FromFile);
        Assert.Equal("github.com/me/protostar", never.Session.Project);
        Assert.Equal(_registry.Resolves.ProjectId, never.Session.ProjectId);
        Assert.Equal(new FileInfo(never.Pending.Path).Length, never.Session.Length);
        Assert.Equal(3, result.Sessions.Count(r => r.Pending is null));
        Assert.Equal(NeverId, result.Sessions[0].Session.Key);
    }

    [Fact]
    public async Task The_project_filter_applies_to_pending_sessions_by_name_or_id()
    {
        await ArrangeHistoryAsync();

        Assert.Equal(5, (await List(project: "PROTOSTAR")).Sessions.Count);
        Assert.Equal(5, (await List(project: _registry.Resolves.ProjectId.ToString())).Sessions.Count);
        Assert.Empty((await List(project: "other")).Sessions);
    }

    [Fact]
    public async Task Another_machine_s_list_has_no_local_pending_rows()
    {
        await ArrangeHistoryAsync();

        Assert.Empty((await List(machine: Guid.NewGuid())).Sessions);
    }

    [Fact]
    public async Task A_folder_is_resolved_with_the_registry_once_and_only_until_the_limit_is_filled()
    {
        await ArrangeHistoryAsync();
        WriteSessionAs("dddddddd-4444-4444-8444-000000000006");
        WriteSessionAs("eeeeeeee-5555-4555-8555-000000000007");
        _registry.Listed.Clear();

        var result = await List(limit: 2);

        Assert.Equal(2, result.Sessions.Count);
        Assert.Equal(1, _registry.Resolutions);
    }

    [Fact]
    public async Task With_the_registry_unreachable_the_pending_sessions_are_still_listed_with_the_failure()
    {
        await ArrangeHistoryAsync();
        _registry.Offline = true;

        var result = await List();

        Assert.Equal(SessionSyncFailure.RegistryUnreachable, result.Failure);
        Assert.Equal([NeverId, AppendedId], result.Sessions.Select(r => r.Session.Key).OrderByDescending(k => k));
        Assert.All(result.Sessions, r => Assert.Equal("protostar", r.Session.Project));
        Assert.Equal(2, (await List(project: "protostar")).Sessions.Count);
        Assert.Empty((await List(project: _registry.Resolves.ProjectId.ToString())).Sessions);
    }

    [Fact]
    public async Task Logged_out_the_pending_sessions_are_listed_without_a_network_call()
    {
        await ArrangeHistoryAsync();
        _token = null;
        _registry.ResetCounters();

        var result = await List();

        Assert.Equal(SessionSyncFailure.NotLoggedIn, result.Failure);
        Assert.Equal(2, result.Sessions.Count);
        Assert.Equal(0, _registry.Calls);
    }

    [Fact]
    public async Task Offline_with_an_expired_session_the_pending_sessions_are_listed_and_the_registry_is_unreachable()
    {
        await ArrangeHistoryAsync();
        _session = SessionLookups.Unreachable(Registry);
        _registry.ResetCounters();

        var result = await List();

        Assert.Equal(SessionSyncFailure.RegistryUnreachable, result.Failure);
        Assert.Equal(2, result.Sessions.Count);
        Assert.Equal(0, _registry.Calls);
    }

    // ── verify ────────────────────────────────────────────────────────────────────

    private Task<SessionSyncResult> Verify() =>
        Sut().SyncAsync(Request() with { Verify = true }, null, CancellationToken.None);

    private Task<SessionStatusResult> VerifyStatus() => Sut().VerifyAsync(Registry, "claude-code", _root, CancellationToken.None);

    [Fact]
    public async Task Verify_restores_an_unchanged_transcript_the_registry_lost_which_a_plain_push_cannot_see()
    {
        WriteSession(WithDirectory, """{"n":2}""");
        await Sync();
        _registry.ForgetTranscript(SessionId);
        _registry.ResetCounters();

        var plain = await Sync();
        Assert.Equal(TranscriptSyncStatus.UpToDate, Assert.Single(plain.Transcripts).Status);
        Assert.Equal(0, _registry.Calls);

        var verified = Assert.Single((await Verify()).Transcripts);

        Assert.Equal(TranscriptSyncStatus.Uploaded, verified.Status);
        Assert.Equal(BackupCheck.Missing, verified.Verified!.Check);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
        Assert.Equal([1], _registry.Lookups);
    }

    [Fact]
    public async Task Verify_resumes_a_short_transcript_from_the_registry_length_not_from_zero_or_the_ledger()
    {
        WriteSession(WithDirectory);
        var firstLength = (int)new FileInfo(SessionFile).Length;
        await Sync();
        AppendRaw("{\"n\":2}\n{\"n\":3}\n");
        await Sync();
        _registry.Truncate(SessionId, firstLength);
        _registry.AppendRequests.Clear();

        var outcome = Assert.Single((await Verify()).Transcripts);

        Assert.Equal(TranscriptSyncStatus.Uploaded, outcome.Status);
        Assert.Equal(new VerifiedBackup(BackupCheck.Short, 1, firstLength), outcome.Verified);
        Assert.Equal([(SessionId, 1, (long)firstLength)], _registry.AppendRequests);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
        Assert.Equal(new FileInfo(SessionFile).Length, _ledger.Get(new Uri(Registry), "claude-code", SessionId)!.Length);
    }

    [Fact]
    public async Task Verify_leaves_alone_a_transcript_the_registry_holds_more_of_than_the_ledger_records()
    {
        WriteSession(WithDirectory);
        await Sync();
        var ahead = File.ReadAllBytes(SessionFile).Concat("{\"elsewhere\":true}\n"u8.ToArray()).ToArray();
        _registry.ForceContent(SessionId, ahead);
        AppendRaw("{\"n\":2}\n");
        _registry.AppendRequests.Clear();

        var result = await Verify();

        var outcome = Assert.Single(result.Transcripts);
        Assert.Equal(TranscriptSyncStatus.Skipped, outcome.Status);
        Assert.Equal(BackupCheck.Diverged, outcome.Verified!.Check);
        Assert.Contains("Left alone", outcome.Message);
        Assert.Empty(_registry.AppendRequests);
        Assert.Equal(ahead, _registry.Content(SessionId));
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task Verify_leaves_alone_a_transcript_the_registry_holds_at_another_revision()
    {
        WriteSession(WithDirectory);
        await Sync();
        _registry.ForceRevision(SessionId, 3);
        _registry.AppendRequests.Clear();

        var outcome = Assert.Single((await Verify()).Transcripts);

        Assert.Equal(new VerifiedBackup(BackupCheck.Diverged, 3, new FileInfo(SessionFile).Length), outcome.Verified);
        Assert.Empty(_registry.AppendRequests);
        Assert.Equal(3, _registry.Revision(SessionId));
    }

    [Fact]
    public async Task Verify_confirms_intact_backups_without_uploading()
    {
        WriteSession(WithDirectory);
        await Sync();
        _registry.ResetCounters();

        var result = await Verify();

        Assert.True(result.Complete);
        Assert.Equal(BackupCheck.Confirmed, Assert.Single(result.Transcripts).Verified!.Check);
        Assert.Equal(0, _registry.Appends);
    }

    [Fact]
    public async Task Verify_asks_in_batches_never_one_request_per_transcript()
    {
        Directory.CreateDirectory(ProjectDir);
        for (var i = 0; i < 1001; i++)
            File.WriteAllText(Path.Combine(ProjectDir, $"00000000-0000-0000-0000-{i:D12}.jsonl"), WithDirectory + "\n");
        await Sync();

        await Verify();

        Assert.Equal([500, 500, 1], _registry.Lookups);
    }

    [Fact]
    public async Task Status_verify_reports_missing_and_short_transcripts_and_changes_nothing()
    {
        WriteSession(WithDirectory);
        var firstLength = (int)new FileInfo(SessionFile).Length;
        await Sync();
        AppendRaw("{\"n\":2}\n");
        await Sync();
        var other = Path.Combine(ProjectDir, "7a000000-0000-0000-0000-000000000001.jsonl");
        File.WriteAllText(other, WithDirectory + "\n");
        await Sync();
        _registry.Truncate(SessionId, firstLength);
        _registry.ForgetTranscript("7a000000-0000-0000-0000-000000000001");
        _registry.ResetCounters();
        var registrations = _registry.MachineRegistrations;
        _registry.RegistrationOrder.Clear();

        var status = await VerifyStatus();

        Assert.False(status.FullySynced);
        var shortOne = status.Transcripts.Single(t => t.Transcript.Key == SessionId);
        Assert.Equal(BackupCheck.Short, shortOne.Verified!.Check);
        Assert.Equal(firstLength, shortOne.BytesBackedUp);
        Assert.Equal(new FileInfo(SessionFile).Length - firstLength, shortOne.BytesPending);
        var missing = status.Transcripts.Single(t => t.Transcript.Key != SessionId);
        Assert.Equal(BackupCheck.Missing, missing.Verified!.Check);
        Assert.Equal(new FileInfo(other).Length, missing.BytesPending);
        Assert.Equal(0, _registry.Appends);
        Assert.Empty(_registry.RegistrationOrder);
        Assert.Equal(registrations, _registry.MachineRegistrations);
        Assert.True(Sut().Status(Registry, "claude-code", _root).FullySynced);
    }

    [Fact]
    public async Task Verify_with_the_registry_unreachable_fails_and_uploads_nothing()
    {
        WriteSession(WithDirectory);
        await Sync();
        _registry.ForgetTranscript(SessionId);
        _registry.Offline = true;

        var result = await Verify();

        Assert.Equal(SessionSyncFailure.RegistryUnreachable, result.Failure);
    }

    [Fact]
    public void A_capture_hook_push_cannot_verify()
    {
        var settings = new Protostar.Cli.Commands.SessionsPushCommand.Settings { Session = SessionFile, Verify = true };

        Assert.False(settings.Validate().Successful);
    }

    // ── fakes ─────────────────────────────────────────────────────────────────────

    // ── retrying failed background backups ────────────────────────────────────────

    private static string Id(int n) => $"0b8f5c21-0000-4000-8000-{n:D12}";

    private Task<SessionSyncResult> HookPush(string sessionPath) =>
        Sut().SyncAsync(Request(sessionPath: sessionPath), null, CancellationToken.None);

    private FailedBackup? Mark(string key) => _ledger.FailedBackups(new Uri(Registry)).SingleOrDefault(m => m.Key == key);

    [Fact]
    public async Task A_hook_push_that_cannot_reach_the_registry_marks_its_session_failed_and_status_says_so()
    {
        WriteSession("""{"n":1}""");
        _registry.Offline = true;

        var result = await HookPush(SessionFile);

        Assert.Equal(SessionSyncFailure.RegistryUnreachable, result.Failure);
        Assert.Equal(SessionFile, Mark(SessionId)!.Path);
        Assert.NotNull(Assert.Single(Sut().Status(Registry, "claude-code", _root).Transcripts).BackupFailedAt);
    }

    [Fact]
    public async Task A_manual_push_that_cannot_reach_the_registry_marks_nothing()
    {
        WriteSession("""{"n":1}""");
        _registry.Offline = true;

        await Sync();

        Assert.Empty(_ledger.FailedBackups(new Uri(Registry)));
    }

    [Fact]
    public async Task The_next_hook_push_that_reaches_the_registry_sends_a_failed_session_and_clears_its_mark()
    {
        WriteSession("""{"n":1}""", """{"n":2}""");
        var other = WriteSessionIn("-home-me-src-other", Id(7), DateTime.UtcNow);
        _registry.Offline = true;
        await HookPush(SessionFile);
        _registry.Offline = false;

        var result = await HookPush(other);

        Assert.Equal(Id(7), Assert.Single(result.Transcripts).Transcript.Key);
        Assert.Equal(TranscriptSyncStatus.Uploaded, Assert.Single(result.Retried!).Status);
        Assert.Equal(File.ReadAllBytes(SessionFile), _registry.Content(SessionId));
        Assert.Null(Mark(SessionId));
        Assert.True(Sut().Status(Registry, "claude-code", _root).FullySynced);
    }

    [Fact]
    public async Task A_hook_push_never_touches_sessions_that_were_simply_never_backed_up()
    {
        for (var n = 1; n <= 50; n++)
            WriteSessionIn("-home-me-src-history", Id(n), DateTime.UtcNow.AddDays(-n));
        var current = WriteSessionIn("-home-me-src-now", Id(100), DateTime.UtcNow);

        var result = await HookPush(current);

        Assert.Equal(Id(100), Assert.Single(result.Transcripts).Transcript.Key);
        Assert.Null(result.Retried);
        Assert.Equal([Id(100)], _registry.Registered.Keys);
    }

    [Fact]
    public async Task A_hook_push_retries_at_most_the_limit_oldest_failure_first()
    {
        var registry = new Uri(Registry);
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var n = 1; n <= SessionSyncService.RetryLimit + 2; n++)
        {
            var path = WriteSessionIn("-home-me-src-failed", Id(n), DateTime.UtcNow);
            _ledger.MarkFailed(registry, new FailedBackup("claude-code", Id(n), path, start.AddMinutes(-n)));
        }

        var current = WriteSessionIn("-home-me-src-now", Id(100), DateTime.UtcNow);
        var result = await HookPush(current);

        var oldest = Enumerable.Range(1, SessionSyncService.RetryLimit + 2).Reverse().Take(SessionSyncService.RetryLimit).Select(Id);
        Assert.Equal(oldest, result.Retried!.Select(o => o.Transcript.Key));
        Assert.Equal(2, _ledger.FailedBackups(registry).Count);
        Assert.Equal([Id(1), Id(2)], _ledger.FailedBackups(registry).Select(m => m.Key).Order());
    }

    [Fact]
    public async Task A_hook_push_that_needs_no_network_retries_nothing_and_stays_offline()
    {
        var current = WriteSessionIn("-home-me-src-now", Id(100), DateTime.UtcNow);
        await HookPush(current);
        var failed = WriteSessionIn("-home-me-src-failed", Id(1), DateTime.UtcNow);
        _ledger.MarkFailed(new Uri(Registry), new FailedBackup("claude-code", Id(1), failed, DateTimeOffset.UtcNow.AddMinutes(-5)));
        _registry.ResetCounters();

        var result = await HookPush(current);

        Assert.Null(result.Retried);
        Assert.Equal(0, _registry.Calls);
        Assert.NotNull(Mark(Id(1)));
    }

    [Fact]
    public async Task A_retry_never_uploads_a_session_another_process_is_already_uploading()
    {
        WriteSession("""{"n":1}""");
        var other = WriteSessionIn("-home-me-src-other", Id(7), DateTime.UtcNow);
        _registry.Offline = true;
        await HookPush(SessionFile);
        _registry.Offline = false;
        _ledger.Held.Add(SessionId);

        var result = await HookPush(other);

        Assert.Equal(TranscriptSyncStatus.Skipped, Assert.Single(result.Retried!).Status);
        Assert.DoesNotContain(SessionId, _registry.Registered.Keys);
        Assert.NotNull(Mark(SessionId));
    }

    [Fact]
    public async Task A_retry_that_fails_again_is_marked_anew_so_it_goes_behind_the_others()
    {
        WriteSession("""{"n":1}""");
        var other = WriteSessionIn("-home-me-src-other", Id(7), DateTime.UtcNow);
        var before = DateTimeOffset.UtcNow.AddHours(-1);
        _ledger.MarkFailed(new Uri(Registry), new FailedBackup("claude-code", SessionId, SessionFile, before));
        await HookPush(other);
        AppendRaw("{\"n\":2}\n");
        _ledger.MarkFailed(new Uri(Registry), new FailedBackup("claude-code", SessionId, SessionFile, before));
        _registry.AppendAnswer = new AppendResult(AppendStatus.Failed, null, "The registry answered 503.");
        File.AppendAllText(other, "{\"n\":2}\n");

        var result = await HookPush(other);

        Assert.Contains(result.Retried!, o => o.Transcript.Key == SessionId && o.Status == TranscriptSyncStatus.Failed);
        Assert.True(Mark(SessionId)!.FailedAt > before);
    }

    [Fact]
    public async Task A_manual_push_clears_the_mark_of_a_session_it_backs_up()
    {
        WriteSession("""{"n":1}""");
        _registry.Offline = true;
        await HookPush(SessionFile);
        _registry.Offline = false;

        await Sync();

        Assert.Null(Mark(SessionId));
    }

    [Fact]
    public async Task A_failed_session_whose_file_is_gone_loses_its_mark()
    {
        var other = WriteSessionIn("-home-me-src-other", Id(7), DateTime.UtcNow);
        _ledger.MarkFailed(new Uri(Registry), new FailedBackup("claude-code", SessionId, SessionFile, DateTimeOffset.UtcNow.AddMinutes(-5)));

        await HookPush(other);

        Assert.Null(Mark(SessionId));
    }

    internal sealed class FakeSessions : ISessionSyncService
    {
        public List<SessionSyncRequest> Requests { get; } = [];

        public Task<SessionSyncResult> SyncAsync(SessionSyncRequest request, IProgress<SessionSyncProgress>? progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new SessionSyncResult(SessionSyncFailure.None, null, new Uri(Registry), []));
        }

        public SessionStatusResult Status(string? registry, string? harnessId, string? harnessHome) =>
            new(SessionSyncFailure.None, null, new Uri(Registry), []);

        public Task<SessionStatusResult> VerifyAsync(string? registry, string? harnessId, string? harnessHome, CancellationToken cancellationToken) =>
            Task.FromResult(Status(registry, harnessId, harnessHome));

        public Task<SessionListResult> ListAsync(string? registry, string? harnessHome, TranscriptListQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new SessionListResult(SessionSyncFailure.None, null, new Uri(Registry), []));
    }

    private enum MachineRegistrationBehaviour
    {
        Succeeds,

        /// <summary>The PUT fails (a lost race), but the machine is registered by the time the caller retries.</summary>
        FailsAfterAnotherProcessRegisters,

        /// <summary>The PUT succeeds, yet the registry still does not know the machine.</summary>
        SucceedsButIsForgotten,
    }

    /// <summary>A registry in memory that applies the real position rules: current revision, offset equal to length, revision + 1 at 0.</summary>
    private sealed class FakeRegistry : IRegistryApi, ITranscriptApi, IProjectApi
    {
        private readonly Dictionary<string, (int Revision, List<byte> Bytes)> _content = [];
        private readonly HashSet<Guid> _machines = [];

        public MachineRegistrationBehaviour MachineRegistration { get; set; }
        public List<Guid> MachineRegistrationIds { get; } = [];

        /// <summary>Stands in for a rebuilt or restored database: transcripts kept, machines gone.</summary>
        public void ForgetMachines() => _machines.Clear();

        /// <summary>Stands in for a transcript's rows being lost: its content and registration are gone.</summary>
        public void ForgetTranscript(string key)
        {
            _content.Remove(key);
            Registered.Remove(key);
        }

        /// <summary>The append answer for the next calls, in place of the real rules (a 5xx, the registry going away).</summary>
        public AppendResult? AppendAnswer { get; set; }

        /// <summary>Registration succeeds but the transcript is forgotten straight away, so appends keep answering unknown.</summary>
        public bool ForgetsTranscriptsAfterRegistering { get; set; }

        public List<(string Key, int Revision, long Offset)> AppendRequests { get; } = [];

        private static string NotRegistered(Guid machineId) => $"Machine {machineId} is not registered.";

        public Dictionary<string, LocalTranscript> Registered { get; } = [];
        public List<string> RegistrationOrder { get; } = [];

        /// <summary>Runs after a chunk is stored, before the reply: to cancel mid-request, or lose the reply.</summary>
        public Action<CancellationToken>? AfterAppend { get; set; }
        public List<int> ChunkSizes { get; } = [];
        public int Calls { get; private set; }
        public int Appends { get; private set; }
        public int MachineRegistrations { get; private set; }
        public bool Offline { get; set; }

        public void ResetCounters() => (Calls, Appends) = (0, 0);

        public byte[] Content(string key) => [.. _content[key].Bytes];

        public int Revision(string key) => _content[key].Revision;

        public void ForceContent(string key, byte[] bytes) => _content[key] = (_content[key].Revision, [.. bytes]);

        /// <summary>Stands in for a restored backup: the same revision, holding only its first <paramref name="length"/> bytes.</summary>
        public void Truncate(string key, int length) => _content[key] = (_content[key].Revision, _content[key].Bytes[..length]);

        public void ForceRevision(string key, int revision) => _content[key] = (revision, _content[key].Bytes);

        /// <summary>How many transcripts each lookup named, in order.</summary>
        public List<int> Lookups { get; } = [];

        public Task<ApiResult<IReadOnlyList<TranscriptLookup>>> LookupAsync(Uri registry, string accessToken, IReadOnlyList<LocalTranscript> transcripts, CancellationToken cancellationToken)
        {
            Calls++;
            Lookups.Add(transcripts.Count);
            if (Offline)
                return Task.FromResult(ApiResult<IReadOnlyList<TranscriptLookup>>.Fail("Could not reach the registry: connection refused"));
            if (transcripts.Count > ITranscriptApi.MaxLookup)
                return Task.FromResult(ApiResult<IReadOnlyList<TranscriptLookup>>.Fail("Too many."));
            IReadOnlyList<TranscriptLookup> answers = [.. transcripts.Select(t => _content.TryGetValue(t.Key, out var held)
                ? new TranscriptLookup(t.Harness, t.Key, true, held.Revision, held.Bytes.Count)
                : new TranscriptLookup(t.Harness, t.Key, false, null, null))];
            return Task.FromResult(ApiResult<IReadOnlyList<TranscriptLookup>>.Success(answers));
        }

        public Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Offline ? null : new RegistryMeta { ApiMajors = [1] });
        }

        public Task<SkillPushOutcome> PushSkillAsync(
            Uri registry, string accessToken, string name, IReadOnlyList<SkillUploadFile> files, SkillProject? project, int? baseVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RegistrySkillList> ListSkillsAsync(Uri registry, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RegistrySkillDownload> GetSkillFilesAsync(Uri registry, string accessToken, string name, Guid? projectId, int? version, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<string>> RenameSkillAsync(Uri registry, string accessToken, string name, Guid? projectId, string newName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<bool>> DeleteSkillAsync(Uri registry, string accessToken, string name, Guid? projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<bool>> RegisterMachineAsync(Uri registry, string accessToken, Guid id, string name, string operatingSystem, CancellationToken cancellationToken)
        {
            Calls++;
            MachineRegistrations++;
            MachineRegistrationIds.Add(id);
            switch (MachineRegistration)
            {
                case MachineRegistrationBehaviour.FailsAfterAnotherProcessRegisters:
                    _machines.Add(id);
                    return Task.FromResult(ApiResult<bool>.Fail("The registry returned 500."));
                case MachineRegistrationBehaviour.SucceedsButIsForgotten:
                    return Task.FromResult(ApiResult<bool>.Success(true));
                default:
                    _machines.Add(id);
                    return Task.FromResult(ApiResult<bool>.Success(true));
            }
        }

        public Dictionary<string, List<ProjectFacts?>> Filed { get; } = [];

        public Task<ApiResult<TranscriptPosition>> RegisterTranscriptAsync(Uri registry, string accessToken, LocalTranscript transcript, Guid machineId, ProjectFacts? project, CancellationToken cancellationToken)
        {
            Calls++;
            if (!_machines.Contains(machineId))
                return Task.FromResult(ApiResult<TranscriptPosition>.UnknownMachine(NotRegistered(machineId)));
            Registered[transcript.Key] = transcript;
            RegistrationOrder.Add(transcript.Key);
            (Filed.TryGetValue(transcript.Key, out var filed) ? filed : Filed[transcript.Key] = []).Add(project);
            if (!_content.ContainsKey(transcript.Key))
                _content[transcript.Key] = (1, []);
            var (revision, bytes) = _content[transcript.Key];
            if (ForgetsTranscriptsAfterRegistering)
                _content.Remove(transcript.Key);
            return Task.FromResult(ApiResult<TranscriptPosition>.Success(new TranscriptPosition(revision, bytes.Count)));
        }

        public Task<AppendResult> AppendAsync(Uri registry, string accessToken, string harness, string key, int revision, long offset, Guid machineId, ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            Calls++;
            AppendRequests.Add((key, revision, offset));
            if (AppendAnswer is { } answer)
                return Task.FromResult(answer);
            // The real registry looks the transcript up before the machine (TranscriptService.AppendAsync).
            if (!_content.TryGetValue(key, out var stored))
                return Task.FromResult(new AppendResult(AppendStatus.UnknownTranscript, null, "No transcript with that harness and key."));
            if (!_machines.Contains(machineId))
                return Task.FromResult(new AppendResult(AppendStatus.UnknownMachine, null, NotRegistered(machineId)));
            var (current, bytes) = stored;
            var newRevision = revision == current + 1 && offset == 0;
            if (!newRevision && (revision != current || offset != bytes.Count))
                return Task.FromResult(new AppendResult(AppendStatus.Mismatch, new TranscriptPosition(current, bytes.Count), "mismatch"));
            if (chunk.Span[^1] != (byte)'\n')
                return Task.FromResult(new AppendResult(AppendStatus.Rejected, null, "not a line boundary"));

            Appends++;
            ChunkSizes.Add(chunk.Length);
            if (newRevision)
                bytes = [];
            bytes.AddRange(chunk.ToArray());
            _content[key] = (revision, bytes);
            AfterAppend?.Invoke(cancellationToken);
            return Task.FromResult(new AppendResult(AppendStatus.Appended, new TranscriptPosition(revision, bytes.Count), null));
        }

        /// <summary>What <c>GET /v1/transcripts</c> answers, before the query's filters.</summary>
        public List<RemoteTranscript> Listed { get; } = [];
        public List<TranscriptListQuery> ListQueries { get; } = [];

        public Task<ApiResult<IReadOnlyList<RemoteTranscript>>> ListAsync(Uri registry, string accessToken, TranscriptListQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            ListQueries.Add(query);
            if (Offline)
                return Task.FromResult(ApiResult<IReadOnlyList<RemoteTranscript>>.Fail("Could not reach the registry: connection refused"));
            var page = Listed
                .Where(t => query.Project is null || (t.Project?.Contains(query.Project, StringComparison.OrdinalIgnoreCase) ?? false) || t.ProjectId?.ToString() == query.Project)
                .Where(t => query.Machine is null || t.MachineId == query.Machine)
                .Take(query.Limit)
                .ToList();
            return Task.FromResult(ApiResult<IReadOnlyList<RemoteTranscript>>.Success(page));
        }

        /// <summary>The project <c>POST /v1/projects/resolve</c> answers for every folder.</summary>
        public RemoteProject Resolves { get; set; } = new() { ProjectId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Name = "github.com/me/protostar", RecognisedBy = "remote" };
        public int Resolutions { get; private set; }

        public Task<ApiResult<RemoteProject>> ResolveProjectAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken)
        {
            Calls++;
            Resolutions++;
            return Task.FromResult(ApiResult<RemoteProject>.Success(Resolves));
        }

        public Task<ApiResult<ProjectLinkChange>> LinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, string project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<ProjectLinkChange>> UnlinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProjects : IProjectFactsResolver
    {
        public List<string> Asked { get; } = [];

        public ProjectFacts Resolve(string workingDirectory)
        {
            Asked.Add(workingDirectory);
            return ProjectFacts.NotGit(workingDirectory, workingDirectory);
        }
    }

    private sealed class FakeLedger : ISessionLedger
    {
        private readonly Dictionary<string, SessionLedgerEntry> _entries = [];

        public void Clear() => _entries.Clear();

        public SessionLedgerEntry? Get(Uri registry, string harness, string key) => _entries.GetValueOrDefault(harness + "/" + key);

        public void Save(Uri registry, string harness, string key, SessionLedgerEntry entry) => _entries[harness + "/" + key] = entry;

        /// <summary>Runs as the lock is taken, to stand in for another process that finished just before.</summary>
        public Action? OnLock { get; set; }

        /// <summary>Transcript keys another process is uploading: their lock cannot be taken.</summary>
        public HashSet<string> Held { get; } = [];

        public IDisposable? TryLock(Uri registry, string harness, string key)
        {
            if (Held.Contains(key))
                return null;
            var onLock = OnLock;
            OnLock = null;
            onLock?.Invoke();
            return new MemoryStream();
        }

        private readonly Dictionary<string, FailedBackup> _failed = [];

        public void MarkFailed(Uri registry, FailedBackup backup) => _failed[backup.Harness + "/" + backup.Key] = backup;

        public IReadOnlyList<FailedBackup> FailedBackups(Uri registry) => [.. _failed.Values.OrderBy(m => m.FailedAt)];

        public void ClearFailed(Uri registry, string harness, string key, DateTimeOffset before)
        {
            if (_failed.TryGetValue(harness + "/" + key, out var mark) && mark.FailedAt < before)
                _failed.Remove(harness + "/" + key);
        }
    }

    private sealed class FakeMachine : IMachineIdentity
    {
        public static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        private bool _registered;

        public MachineInfo Get() => new(Id, "test", "linux");

        public bool IsRegisteredWith(Uri registry) => _registered;

        public void MarkRegistered(Uri registry) => _registered = true;
    }

    private sealed class FakeTokens(Func<SessionLookup> session) : IAccessTokenProvider
    {
        public Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(session());
    }

    private sealed class FakeCatalog(params IHarness[] harnesses) : IHarnessCatalog
    {
        public IReadOnlyList<IHarness> All => harnesses;
        public string KnownIds => string.Join(", ", harnesses.Select(h => h.Id));
        public IHarness? ById(string id) => harnesses.FirstOrDefault(h => h.Id == id);
    }
}
