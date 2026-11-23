using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Protostar.Cli.Agents;
using Protostar.Cli.Auth;
using Protostar.Cli.Capture;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Registry;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Contract tests for <see cref="IAgentPushService"/> over real Claude Code discovery in a temp harness home and an
/// in-memory registry that keeps the registry's versioning rules (base version, stale refusal, tombstones).
/// </summary>
public sealed class AgentPushTests : IDisposable
{
    private const string RegistryUrl = "https://registry.test";
    private static readonly Uri Registry = new(RegistryUrl);
    private readonly string _root = Directory.CreateTempSubdirectory("pstar-agents-").FullName;
    private readonly FakeAgentLedger _ledger = new();
    private readonly FakeAgentApi _api = new();
    private readonly MetaOnlyRegistry _meta = new();
    private SessionLookup _session = SessionLookups.Of(new StoredToken { Registry = RegistryUrl, AccessToken = "tok", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) });

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Home => Path.Combine(_root, "home", ".claude");

    private string Repo => Path.Combine(_root, "repo");

    private string Personal(string relativePath, string name, string body = "You review code.") =>
        Write(Path.Combine(Home, "agents", relativePath), name, body);

    private string Project(string relativePath, string name, string body = "You lint.") =>
        Write(Path.Combine(Repo, ".claude", "agents", relativePath), name, body);

    private static string Write(string path, string? name, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var nameLine = name is null ? "" : $"name: {name}\n";
        File.WriteAllText(path, $"---\n{nameLine}description: A test agent.\n---\n{body}\n");
        return path;
    }

    private AgentPushService Sut() => new(
        new HarnessCatalog([new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper())]),
        new FakeTokens(() => _session), _api, new RegistryCapabilities(_meta), _ledger, new SkillPushServiceTests.FakeFacts(), new SkillPushServiceTests.FakeMachine());

    private AgentPushRequest Request(bool fromHook = false, bool force = false, params string[] names) =>
        new(names, null, Home, Repo, RegistryUrl, force, fromHook);

    [Fact]
    public async Task Personal_and_project_agents_go_up_with_their_scope_and_a_second_run_uploads_nothing()
    {
        Personal("reviewer.md", "reviewer");
        Personal("review/nested.md", "deep-reviewer");
        Project("linter.md", "linter");

        var first = await Sut().PushAsync(Request(), CancellationToken.None);
        var second = await Sut().PushAsync(Request(), CancellationToken.None);

        Assert.Equal(AgentPushRunFailure.None, first.Failure);
        Assert.All(first.Entries, e => Assert.Equal(AgentPushStatus.Created, e.Outcome.Status));
        Assert.Equal(["linter.md", "review/nested.md", "reviewer.md"], _api.Pushed.Select(p => p.Path).Order(StringComparer.Ordinal));
        Assert.Null(_api.Pushed.Single(p => p.Path == "reviewer.md").Project);
        Assert.NotNull(_api.Pushed.Single(p => p.Path == "linter.md").Project);
        Assert.Equal(3, _api.Pushed.Count);
        Assert.All(second.Entries, e => Assert.Equal(AgentPushStatus.Unchanged, e.Outcome.Status));
        Assert.Equal(1, _api.ListCalls);
    }

    [Fact]
    public async Task An_agent_is_named_by_its_front_matter_not_its_file_name()
    {
        Personal("checks/style.md", "linter");

        var result = await Sut().PushAsync(Request(names: "linter"), CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("linter", entry.Agent.Name);
        Assert.Equal("checks/style.md", entry.Agent.RelativePath);
        Assert.Equal(AgentPushStatus.Created, entry.Outcome.Status);
    }

    [Fact]
    public async Task A_file_without_a_name_is_refused_with_a_clear_message_and_never_sent()
    {
        Write(Path.Combine(Home, "agents", "nameless.md"), null, "body");

        var result = await Sut().PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(AgentPushStatus.Refused, outcome.Status);
        Assert.Contains("nameless.md has no 'name' in its front matter", outcome.Message);
        Assert.Empty(_api.Pushed);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task The_hook_leaves_out_a_file_without_a_name_so_it_is_not_a_failed_backup_every_session()
    {
        Write(Path.Combine(Home, "agents", "nameless.md"), null, "body");
        Personal("reviewer.md", "reviewer");

        var result = await Sut().PushAsync(Request(fromHook: true), CancellationToken.None);

        Assert.Equal("reviewer", Assert.Single(result.Entries).Agent.Name);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task The_hook_takes_the_ledgers_word_for_an_unchanged_agent_with_no_request_at_all()
    {
        Personal("reviewer.md", "reviewer");
        await Sut().PushAsync(Request(), CancellationToken.None);
        _session = SessionLookups.Of(null);
        var metaBefore = _meta.MetaCalls;

        var result = await Sut().PushAsync(Request(fromHook: true), CancellationToken.None);

        Assert.Equal(AgentPushStatus.Unchanged, Assert.Single(result.Entries).Outcome.Status);
        Assert.Single(_api.Pushed);
        Assert.Equal(0, _api.ListCalls);
        Assert.Equal(metaBefore, _meta.MetaCalls);
    }

    [Fact]
    public async Task An_edit_on_a_stale_copy_is_refused_naming_the_newer_version_and_remembered()
    {
        var file = Personal("reviewer.md", "reviewer");
        await Sut().PushAsync(Request(), CancellationToken.None);
        _api.PushFromElsewhere(null, "reviewer", "changed on machine A");
        Write(file, "reviewer", "changed on machine B");

        var result = await Sut().PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(AgentPushStatus.Stale, outcome.Status);
        Assert.Equal(2, outcome.Version);
        Assert.Equal("Changed here and in the registry: the registry has v2, but this copy is based on v1. Nothing was overwritten.", outcome.Message);
        Assert.Equal(AgentPushService.Hash(File.ReadAllBytes(file)), _ledger.Get(Registry, file)!.RefusedHash);

        var pushes = _api.Pushed.Count;
        var fromHook = await Sut().PushAsync(Request(fromHook: true), CancellationToken.None);
        Assert.True(Assert.Single(fromHook.Entries).Outcome.Remembered);
        Assert.Equal(pushes, _api.Pushed.Count);
        Assert.Empty(Sut().Changed(Home, Repo, RegistryUrl));
    }

    [Fact]
    public async Task Force_replaces_a_newer_version_and_keeps_it_in_history()
    {
        var file = Personal("reviewer.md", "reviewer");
        await Sut().PushAsync(Request(), CancellationToken.None);
        _api.PushFromElsewhere(null, "reviewer", "changed on machine A");
        Write(file, "reviewer", "changed on machine B");

        var result = await Sut().PushAsync(Request(force: true), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(AgentPushStatus.Created, outcome.Status);
        Assert.Equal(3, outcome.Version);
        Assert.Contains("Replaced v2", outcome.Message);
        Assert.Equal([0, 1, 2], _api.Bases);
    }

    [Fact]
    public async Task A_copy_of_an_agent_deleted_in_the_registry_is_reported_and_not_uploaded()
    {
        var file = Personal("reviewer.md", "reviewer");
        await Sut().PushAsync(Request(), CancellationToken.None);
        _api.Delete(null, "reviewer");
        Write(file, "reviewer", "edited after the delete");

        var result = await Sut().PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(AgentPushStatus.Removed, outcome.Status);
        Assert.Contains("was deleted from the registry (at v1)", outcome.Message);
        Assert.Contains("protostar agents push reviewer --force", outcome.Message);
        Assert.Single(_api.Pushed);
    }

    [Fact]
    public async Task A_registry_without_agents_is_named_too_old_before_anything_is_sent()
    {
        Personal("reviewer.md", "reviewer");
        _meta.Meta = MetaOnlyRegistry.Current with { Capabilities = MetaOnlyRegistry.Current.Capabilities!.Where(c => c != RegistryCapability.Agents).ToArray() };

        var result = await Sut().PushAsync(Request(), CancellationToken.None);

        Assert.Equal(AgentPushRunFailure.Incompatible, result.Failure);
        Assert.Equal("This registry is too old for agent backups. Update the registry.", result.Detail);
        Assert.Empty(_api.Pushed);
    }

    [Fact]
    public async Task Changed_lists_only_agents_whose_bytes_differ_from_the_ledger()
    {
        var reviewer = Personal("reviewer.md", "reviewer");
        Project("linter.md", "linter");
        await Sut().PushAsync(Request(), CancellationToken.None);
        Assert.Empty(Sut().Changed(Home, Repo, RegistryUrl));

        Write(reviewer, "reviewer", "edited");

        Assert.Equal("reviewer", Assert.Single(Sut().Changed(Home, Repo, RegistryUrl)).Name);
    }

    [Fact]
    public async Task A_record_of_a_project_agent_that_a_link_moved_follows_it_when_the_list_shows_the_same_version()
    {
        var file = Project("linter.md", "linter");
        await Sut().PushAsync(Request(), CancellationToken.None);
        var before = _ledger.Get(Registry, file)!;
        var elsewhere = Guid.NewGuid();
        _api.MoveProject(before.ProjectId, elsewhere, "linter");

        var result = await Sut().PushAsync(Request(), CancellationToken.None);

        Assert.Equal(AgentPushStatus.Unchanged, Assert.Single(result.Entries).Outcome.Status);
        Assert.Equal(elsewhere, _ledger.Get(Registry, file)!.ProjectId);
    }

    [Fact]
    public void Moved_agent_records_follow_the_link_on_the_machine_that_ran_it()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        _ledger.Record(Registry, "/a.md", new AgentLedgerEntry("h", 1, "linter", from));
        _ledger.Record(Registry, "/b.md", new AgentLedgerEntry("h", 1, "other", from));

        MovedAgentLedger.Follow(_ledger, Registry, ["linter"], from, to);

        Assert.Equal(to, _ledger.Get(Registry, "/a.md")!.ProjectId);
        Assert.Equal(from, _ledger.Get(Registry, "/b.md")!.ProjectId);
    }

    [Fact]
    public async Task Session_end_hands_changed_agents_to_a_background_push_and_starts_nothing_otherwise()
    {
        var background = new SessionCaptureTests.FakeBackground();
        var changed = new FakeAgentPush { Answer = [new DiscoveredAgent { Name = "reviewer", Scope = AgentScope.Personal, Path = "/x.md", RelativePath = "x.md" }] };
        var payload = $"{{\"cwd\": {System.Text.Json.JsonSerializer.Serialize(_root)}}}";

        var handed = await new CaptureService(new NoSkills(), background, changed).HandleAsync("SessionEnd", payload, CancellationToken.None);
        var idle = await new CaptureService(new NoSkills(), background, new FakeAgentPush()).HandleAsync("SessionEnd", payload, CancellationToken.None);

        Assert.Equal(CaptureAction.AgentsHandedOff, handed.Action);
        Assert.Equal(1, handed.AgentsHandedOff);
        Assert.Equal([_root], background.AgentsStarted);
        Assert.Equal(CaptureAction.Ignored, idle.Action);
        Assert.Equal("protostar capture (SessionEnd): backing up 1 changed agent in the background.", CapturePresenter.Describe("SessionEnd", handed));
    }

    [Fact]
    public void The_background_agent_push_runs_agents_push_from_the_hook_in_the_sessions_folder()
    {
        var startInfo = BackgroundPush.AgentsStartInfo("protostar", "/home/me/src/widget", windows: false);

        Assert.Equal(["agents", "push", "--from-hook", "--project", "/home/me/src/widget"], startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.Equal(["agents", "push", "--from-hook", "--personal-only"], BackgroundPush.AgentsStartInfo("protostar", null, windows: false).ArgumentList);
    }

    [Fact]
    public async Task The_api_sends_one_file_part_named_by_its_path_and_maps_a_stale_problem()
    {
        string? body = null;
        using var api = new RegistryApi(new Stub(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("{\"type\":\"https://protostar.dev/problems/stale-agent-version\",\"title\":\"stale\",\"currentVersion\":4}", Encoding.UTF8, "application/problem+json"),
            };
        }));

        var outcome = await api.PushAgentAsync(Registry, "tok", "review/reviewer.md", "---\nname: r\n---\n"u8.ToArray(), null, 3, CancellationToken.None);

        Assert.Equal(new AgentPushOutcome(AgentPushStatus.Stale, 4, "stale"), outcome);
        Assert.Contains("filename=\"review/reviewer.md\"", body);
        Assert.Contains("name=baseVersion\r\n\r\n3", body);
        Assert.DoesNotContain("name=project", body);
    }

    [Fact]
    public async Task A_registry_without_the_route_is_reported_as_too_old_not_as_a_status_code()
    {
        using var api = new RegistryApi(new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

        var outcome = await api.PushAgentAsync(Registry, "tok", "r.md", [], null, 0, CancellationToken.None);

        Assert.Equal("This registry is too old for agent backups. Update the registry.", outcome.Message);
    }

    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class FakeTokens(Func<SessionLookup> session) : IAccessTokenProvider
    {
        public Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(session());
    }

    private sealed class NoSkills : Protostar.Cli.Skills.ISkillPushService
    {
        public Task<Protostar.Cli.Skills.SkillPushRunResult> PushAsync(Protostar.Cli.Skills.SkillPushRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>An <see cref="IAgentPushService"/> whose <see cref="Changed"/> answers <see cref="Answer"/>; pushing is not expected.</summary>
    internal sealed class FakeAgentPush : IAgentPushService
    {
        public IReadOnlyList<DiscoveredAgent> Answer { get; init; } = [];

        public Task<AgentPushRunResult> PushAsync(AgentPushRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyList<DiscoveredAgent> Changed(string? harnessHome, string? projectStart, string? registry) => Answer;
    }

    internal sealed class FakeAgentLedger : IAgentLedger
    {
        private readonly Dictionary<string, AgentLedgerEntry> _entries = new(StringComparer.Ordinal);

        public AgentLedgerEntry? Get(Uri registry, string filePath) => _entries.GetValueOrDefault(filePath);

        public void Record(Uri registry, string filePath, AgentLedgerEntry entry) => _entries[filePath] = entry;

        public IReadOnlyDictionary<string, AgentLedgerEntry> Entries(Uri registry) => _entries;
    }

    /// <summary>An in-memory registry with the registry's agent rules: unchanged by bytes, stale by base, tombstones by last version.</summary>
    private sealed class FakeAgentApi : IAgentApi
    {
        private readonly Dictionary<(Guid?, string), (int Version, string Hash, int First)> _agents = [];
        private readonly Dictionary<(Guid?, string), int> _removed = [];

        public List<(string Path, SkillProject? Project)> Pushed { get; } = [];

        public List<int> Bases { get; } = [];

        public int ListCalls { get; private set; }

        public void PushFromElsewhere(Guid? projectId, string name, string body)
        {
            var current = _agents[(projectId, name)];
            _agents[(projectId, name)] = (current.Version + 1, body, current.First);
        }

        public void Delete(Guid? projectId, string name)
        {
            _removed[(projectId, name)] = _agents[(projectId, name)].Version;
            _agents.Remove((projectId, name));
        }

        public void MoveProject(Guid? from, Guid to, string name)
        {
            _agents[(to, name)] = _agents[(from, name)];
            _agents.Remove((from, name));
        }

        public Task<AgentPushOutcome> PushAgentAsync(
            Uri registry, string accessToken, string relativePath, byte[] content, SkillProject? project, int baseVersion, CancellationToken cancellationToken)
        {
            Pushed.Add((relativePath, project));
            Bases.Add(baseVersion);
            var name = Regex.Match(Encoding.UTF8.GetString(content), "^name: (.+)$", RegexOptions.Multiline).Groups[1].Value;
            Guid? projectId = project is null ? null : SkillPushServiceTests.FakeRegistryApi.ProjectIdOf(project.Facts);
            var hash = AgentPushService.Hash(content).ToUpperInvariant();
            var key = (projectId, name);

            if (!_agents.TryGetValue(key, out var agent))
            {
                var last = _removed.GetValueOrDefault(key);
                if (baseVersion >= 1 && baseVersion <= last)
                    return Task.FromResult(new AgentPushOutcome(AgentPushStatus.Removed, last, "removed", projectId));
                _removed.Remove(key);
                _agents[key] = (last + 1, hash, last + 1);
                return Task.FromResult(new AgentPushOutcome(AgentPushStatus.Created, last + 1, null, projectId));
            }

            if (agent.Hash == hash)
                return Task.FromResult(new AgentPushOutcome(AgentPushStatus.Unchanged, agent.Version, null, projectId));
            if (baseVersion != agent.Version)
                return Task.FromResult(new AgentPushOutcome(AgentPushStatus.Stale, agent.Version, "stale", projectId));
            _agents[key] = (agent.Version + 1, hash, agent.First);
            return Task.FromResult(new AgentPushOutcome(AgentPushStatus.Created, agent.Version + 1, null, projectId));
        }

        public Task<RegistryAgentList> ListAgentsAsync(Uri registry, string accessToken, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(new RegistryAgentList(
                _agents.Select(a => new RegistryAgent(a.Key.Item2, a.Value.Version, a.Value.Hash, a.Key.Item1, null, a.Value.First)).ToList(),
                _removed.Select(r => new RegistryRemovedAgent(r.Key.Item2, r.Key.Item1, r.Value, null)).ToList(),
                null));
        }
    }
}
