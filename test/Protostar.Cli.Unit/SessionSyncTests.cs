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
        new FakeTokens(() => _token), _registry, _registry, _ledger, _machine, _projects, TimeProvider.System);

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

    // ── fakes ─────────────────────────────────────────────────────────────────────

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

        public Task<SessionListResult> ListAsync(string? registry, TranscriptListQuery query, CancellationToken cancellationToken) =>
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
    private sealed class FakeRegistry : IRegistryApi, ITranscriptApi
    {
        private readonly Dictionary<string, (int Revision, List<byte> Bytes)> _content = [];
        private readonly HashSet<Guid> _machines = [];

        public MachineRegistrationBehaviour MachineRegistration { get; set; }
        public List<Guid> MachineRegistrationIds { get; } = [];

        /// <summary>Stands in for a rebuilt or restored database: transcripts kept, machines gone.</summary>
        public void ForgetMachines() => _machines.Clear();

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

        public Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Offline ? null : new RegistryMeta { ApiMajors = [1] });
        }

        public Task<SkillPushOutcome> PushSkillAsync(Uri registry, string accessToken, string name, IReadOnlyList<SkillUploadFile> files, SkillProject? project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RegistrySkillList> ListSkillsAsync(Uri registry, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RegistrySkillDownload> GetSkillFilesAsync(Uri registry, string accessToken, string name, Guid? projectId, int? version, CancellationToken cancellationToken) =>
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
            return Task.FromResult(ApiResult<TranscriptPosition>.Success(new TranscriptPosition(revision, bytes.Count)));
        }

        public Task<AppendResult> AppendAsync(Uri registry, string accessToken, string harness, string key, int revision, long offset, Guid machineId, ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            Calls++;
            if (!_machines.Contains(machineId))
                return Task.FromResult(new AppendResult(AppendStatus.UnknownMachine, null, NotRegistered(machineId)));
            var (current, bytes) = _content[key];
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

        public Task<ApiResult<IReadOnlyList<RemoteTranscript>>> ListAsync(Uri registry, string accessToken, TranscriptListQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(ApiResult<IReadOnlyList<RemoteTranscript>>.Success([]));
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

        public IDisposable? TryLock(Uri registry, string harness, string key)
        {
            var onLock = OnLock;
            OnLock = null;
            onLock?.Invoke();
            return new MemoryStream();
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

    private sealed class FakeTokens(Func<StoredToken?> token) : IAccessTokenProvider
    {
        public Task<StoredToken?> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(token());
    }

    private sealed class FakeCatalog(params IHarness[] harnesses) : IHarnessCatalog
    {
        public IReadOnlyList<IHarness> All => harnesses;
        public string KnownIds => string.Join(", ", harnesses.Select(h => h.Id));
        public IHarness? ById(string id) => harnesses.FirstOrDefault(h => h.Id == id);
    }
}
