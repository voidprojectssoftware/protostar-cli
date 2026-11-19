using System.Text;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
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
        new FakeTokens(() => _token), _registry, _registry, _ledger, new FakeMachine(), TimeProvider.System);

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

    /// <summary>A registry in memory that applies the real position rules: current revision, offset equal to length, revision + 1 at 0.</summary>
    private sealed class FakeRegistry : IRegistryApi, ITranscriptApi
    {
        private readonly Dictionary<string, (int Revision, List<byte> Bytes)> _content = [];

        public Dictionary<string, LocalTranscript> Registered { get; } = [];
        public List<int> ChunkSizes { get; } = [];
        public int Calls { get; private set; }
        public int Appends { get; private set; }
        public int MachineRegistrations { get; private set; }

        public void ResetCounters() => (Calls, Appends) = (0, 0);

        public byte[] Content(string key) => [.. _content[key].Bytes];

        public int Revision(string key) => _content[key].Revision;

        public void ForceContent(string key, byte[] bytes) => _content[key] = (_content[key].Revision, [.. bytes]);

        public Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<RegistryMeta?>(new RegistryMeta { ApiMajors = [1] });
        }

        public Task<SkillPushOutcome> PushSkillAsync(Uri registry, string accessToken, string name, IReadOnlyList<SkillUploadFile> files, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RegistrySkillList> ListSkillsAsync(Uri registry, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RegistrySkillDownload> GetSkillFilesAsync(Uri registry, string accessToken, string name, int? version, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<bool>> RegisterMachineAsync(Uri registry, string accessToken, Guid id, string name, string operatingSystem, CancellationToken cancellationToken)
        {
            Calls++;
            MachineRegistrations++;
            return Task.FromResult(ApiResult<bool>.Success(true));
        }

        public Task<ApiResult<TranscriptPosition>> RegisterTranscriptAsync(Uri registry, string accessToken, LocalTranscript transcript, Guid machineId, CancellationToken cancellationToken)
        {
            Calls++;
            Registered[transcript.Key] = transcript;
            if (!_content.ContainsKey(transcript.Key))
                _content[transcript.Key] = (1, []);
            var (revision, bytes) = _content[transcript.Key];
            return Task.FromResult(ApiResult<TranscriptPosition>.Success(new TranscriptPosition(revision, bytes.Count)));
        }

        public Task<AppendResult> AppendAsync(Uri registry, string accessToken, string harness, string key, int revision, long offset, Guid machineId, ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            Calls++;
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
            return Task.FromResult(new AppendResult(AppendStatus.Appended, new TranscriptPosition(revision, bytes.Count), null));
        }

        public Task<ApiResult<IReadOnlyList<RemoteTranscript>>> ListAsync(Uri registry, string accessToken, TranscriptListQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(ApiResult<IReadOnlyList<RemoteTranscript>>.Success([]));
    }

    private sealed class FakeLedger : ISessionLedger
    {
        private readonly Dictionary<string, SessionLedgerEntry> _entries = [];

        public void Clear() => _entries.Clear();

        public SessionLedgerEntry? Get(Uri registry, string harness, string key) => _entries.GetValueOrDefault(harness + "/" + key);

        public void Save(Uri registry, string harness, string key, SessionLedgerEntry entry) => _entries[harness + "/" + key] = entry;

        public IDisposable? TryLock(Uri registry, string harness, string key) => new MemoryStream();
    }

    private sealed class FakeMachine : IMachineIdentity
    {
        private bool _registered;

        public MachineInfo Get() => new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "test", "linux");

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
