using Protostar.Cli.Agents;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Xunit;
using static Protostar.Cli.Unit.SkillPushServiceTests;

namespace Protostar.Cli.Unit;

/// <summary>
/// Contract tests for <see cref="IAgentChangeService"/>: two machines (A and B), each a temp Claude Code home with its own
/// ledger, sharing one in-memory registry. A renames or deletes; B pulls and pushes afterwards.
/// </summary>
public sealed class AgentChangeTests : IDisposable
{
    private const string RegistryUrl = "https://registry.test";
    private static readonly Uri Registry = new(RegistryUrl);
    private readonly string _root = Path.GetFullPath(Directory.CreateTempSubdirectory("pstar-agent-change-").FullName);
    private readonly FakeAgentApi _api = new();
    private readonly MetaOnlyRegistry _meta = new();
    private readonly FakeProjects _projects = new() { ByFolder = true };
    private readonly Machine _a;
    private readonly Machine _b;

    public AgentChangeTests()
    {
        _a = new Machine(Path.Combine(_root, "a", ".claude"));
        _b = new Machine(Path.Combine(_root, "b", ".claude"));
        Directory.CreateDirectory(_a.Home);
        Directory.CreateDirectory(_b.Home);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Repo => Path.Combine(_root, "repo");

    private Guid RepoProject => FakeRegistryApi.ProjectIdOf(ProjectFacts.NotGit(Repo, Repo));

    private static readonly StoredToken Token = new() { Registry = RegistryUrl, AccessToken = "tok", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };

    private static HarnessCatalog Catalog => new([new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper())]);

    private AgentChangeService Changes(Machine m) => new(new Tokens(), _api, new RegistryCapabilities(_meta), m.Ledger, new FakeFacts(), _projects, new FakeMachine());

    private Task<AgentPullRunResult> Pull(Machine m) =>
        new AgentPullService(Catalog, new Tokens(), _api, new RegistryCapabilities(_meta), m.Ledger, new FakeFacts(), _projects, new FakeMachine())
            .PullAsync(new AgentPullRequest([], null, m.Home, RegistryUrl), CancellationToken.None);

    private Task<AgentPushRunResult> Push(Machine m, bool fromHook = false) =>
        new AgentPushService(Catalog, new Tokens(), _api, new RegistryCapabilities(_meta), m.Ledger, new FakeFacts(), new FakeMachine())
            .PushAsync(new AgentPushRequest([], null, m.Home, null, RegistryUrl, FromHook: fromHook), CancellationToken.None);

    private async Task<AgentTarget> Find(Machine m, string name, string? projectStart = null, bool projectOnly = false)
    {
        var found = await Changes(m).FindAsync(new AgentTargetRequest(name, RegistryUrl, projectStart, projectOnly), CancellationToken.None);
        Assert.Equal(AgentPullRunFailure.None, found.Failure);
        return found.Target!;
    }

    private async Task<IReadOnlyList<RegistryAgent>> Listed() => (await _api.ListAgentsAsync(Registry, "tok", CancellationToken.None)).Agents!;

    // Both machines have the personal agent `name` from one push on A and one pull on B.
    private async Task<(string OnA, string OnB)> OnBothMachines(string name)
    {
        var onA = Write(Path.Combine(_a.Agents, $"{name}.md"), FakeAgentApi.File(name, "v1"));
        await Push(_a);
        await Pull(_b);
        return (onA, Path.Combine(_b.Agents, $"{name}.md"));
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task A_rename_rewrites_the_name_line_here_in_place_and_an_unedited_copy_elsewhere_follows_on_pull()
    {
        var (onA, onB) = await OnBothMachines("reviewer");

        var result = await Changes(_a).RenameAsync(await Find(_a, "reviewer"), "code-reviewer", CancellationToken.None);

        Assert.Equal("code-reviewer", result.NewName);
        Assert.Equal(new LocalAgentRename(onA, true, null), Assert.Single(result.Copies));
        Assert.Equal(FakeAgentApi.File("code-reviewer", "v1"), File.ReadAllText(onA));
        Assert.Equal(("code-reviewer", 1), (_a.Ledger.Get(Registry, onA)!.Name, _a.Ledger.Get(Registry, onA)!.Version));
        Assert.Equal("code-reviewer", Assert.Single(await Listed()).Name);

        // A: nothing to push and nothing to pull, by hand or from the hook.
        Assert.Empty(new AgentPushService(Catalog, new Tokens(), _api, new RegistryCapabilities(_meta), _a.Ledger, new FakeFacts(), new FakeMachine()).Changed(_a.Home, null, RegistryUrl));
        Assert.Equal(AgentPushStatus.Unchanged, Assert.Single((await Push(_a)).Entries).Outcome.Status);
        Assert.Equal(AgentPullStatus.UpToDate, Assert.Single((await Pull(_a)).Entries).Status);

        // B: the hook sees an unchanged copy and sends nothing; a push by hand reports the rename; the pull follows it in place.
        Assert.Equal(AgentPushStatus.Unchanged, Assert.Single((await Push(_b, fromHook: true)).Entries).Outcome.Status);
        Assert.Equal(AgentPushStatus.Removed, Assert.Single((await Push(_b)).Entries).Outcome.Status);
        var pulled = await Pull(_b);
        Assert.Equal(AgentPullStatus.FollowedRename, Assert.Single(pulled.Entries, e => e.Name == "reviewer").Status);
        Assert.Equal(FakeAgentApi.File("code-reviewer", "v1"), File.ReadAllText(onB));
        Assert.Equal(AgentPushStatus.Unchanged, Assert.Single((await Push(_b)).Entries).Outcome.Status);

        Assert.Single(_api.Pushed); // only A's first backup
        Assert.Equal(["code-reviewer"], (await Listed()).Select(a => a.Name));
    }

    [Fact]
    public async Task An_edited_copy_elsewhere_is_kept_and_reported_and_its_push_does_not_bring_the_old_name_back()
    {
        var (_, onB) = await OnBothMachines("reviewer");
        Write(onB, FakeAgentApi.File("reviewer", "edited on B"));

        await Changes(_a).RenameAsync(await Find(_a, "reviewer"), "code-reviewer", CancellationToken.None);
        var pulled = await Pull(_b);
        var hook = await Push(_b, fromHook: true);
        var byHand = await Push(_b);

        var kept = Assert.Single(pulled.Entries, e => e.Name == "reviewer");
        Assert.Equal(AgentPullStatus.RemovedInRegistry, kept.Status);
        Assert.Contains("edited on B", File.ReadAllText(onB));
        Assert.Equal(AgentPushStatus.Removed, Assert.Single(hook.Entries).Outcome.Status);
        Assert.Equal(AgentPushStatus.Removed, Assert.Single(byHand.Entries).Outcome.Status);
        Assert.Equal(["code-reviewer"], (await Listed()).Select(a => a.Name));
    }

    [Fact]
    public async Task A_rename_is_refused_before_the_registry_is_asked_when_a_copy_here_has_unpushed_changes()
    {
        var (onA, _) = await OnBothMachines("reviewer");
        Write(onA, FakeAgentApi.File("reviewer", "edited on A"));

        var result = await Changes(_a).RenameAsync(await Find(_a, "reviewer"), "code-reviewer", CancellationToken.None);

        Assert.Null(result.NewName);
        Assert.Equal(onA, Assert.Single(result.Edited).Path);
        Assert.Empty(_api.Changes);
        Assert.Equal(FakeAgentApi.File("reviewer", "edited on A"), File.ReadAllText(onA));
        Assert.Equal("reviewer", Assert.Single(await Listed()).Name);
    }

    [Fact]
    public async Task A_rename_to_a_taken_name_changes_nothing_here()
    {
        var (onA, _) = await OnBothMachines("reviewer");
        await OnBothMachines("planner");

        var result = await Changes(_a).RenameAsync(await Find(_a, "reviewer"), "planner", CancellationToken.None);

        Assert.Contains("already have an agent named 'planner'", result.Error);
        Assert.Empty(result.Copies);
        Assert.Equal(FakeAgentApi.File("reviewer", "v1"), File.ReadAllText(onA));
        Assert.Equal("reviewer", _a.Ledger.Get(Registry, onA)!.Name);
    }

    [Fact]
    public async Task A_delete_touches_no_local_file_and_no_machine_pushes_the_agent_back()
    {
        var (onA, onB) = await OnBothMachines("migrator");
        Write(onB, FakeAgentApi.File("migrator", "edited on B"));
        var target = await Find(_a, "migrator");

        var result = await Changes(_a).DeleteAsync(target, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(onA, Assert.Single(target.Copies).Path);
        Assert.Empty(await Listed());
        Assert.Equal(FakeAgentApi.File("migrator", "v1"), File.ReadAllText(onA));
        Assert.NotNull(_a.Ledger.Get(Registry, onA)); // kept, so the hook still sees an unchanged copy as backed up

        Assert.Empty(new AgentPushService(Catalog, new Tokens(), _api, new RegistryCapabilities(_meta), _a.Ledger, new FakeFacts(), new FakeMachine()).Changed(_a.Home, null, RegistryUrl));
        Assert.Equal(AgentPushStatus.Removed, Assert.Single((await Push(_a)).Entries).Outcome.Status);
        Assert.Equal(AgentPushStatus.Removed, Assert.Single((await Push(_b, fromHook: true)).Entries).Outcome.Status);
        Assert.Equal(AgentPushStatus.Removed, Assert.Single((await Push(_b)).Entries).Outcome.Status);
        Assert.Equal(AgentPullStatus.RemovedInRegistry, Assert.Single((await Pull(_b)).Entries).Status);
        Assert.Equal(FakeAgentApi.File("migrator", "edited on B"), File.ReadAllText(onB));
        Assert.Empty(await Listed());
    }

    [Fact]
    public async Task A_name_that_is_personal_and_this_projects_is_refused_with_both_and_each_option_picks_one()
    {
        Directory.CreateDirectory(Repo);
        _api.Publish(null, "linter.md", "linter", "personal");
        _api.Publish(RepoProject, "linter.md", "linter", "project");

        var ambiguous = await Changes(_a).FindAsync(new AgentTargetRequest("linter", RegistryUrl, Repo), CancellationToken.None);
        var personal = await Find(_a, "linter");
        var project = await Find(_a, "linter", Repo, projectOnly: true);

        Assert.Equal(AgentPullRunFailure.Ambiguous, ambiguous.Failure);
        Assert.Contains("both a personal agent and an agent of project app", ambiguous.Detail);
        Assert.Contains("--personal-only", ambiguous.Detail);
        Assert.Null(personal.Agent.ProjectId);
        Assert.Equal(RepoProject, project.Agent.ProjectId);
    }

    [Fact]
    public async Task Only_the_project_agent_with_project_and_a_missing_one_says_where_it_is()
    {
        Directory.CreateDirectory(Repo);
        _api.Publish(null, "reviewer.md", "reviewer", "personal");

        var found = await Changes(_a).FindAsync(new AgentTargetRequest("reviewer", RegistryUrl, Repo, ProjectOnly: true), CancellationToken.None);

        Assert.Equal(AgentPullRunFailure.AgentNotFound, found.Failure);
        Assert.Contains("There is a personal agent of that name: pass --personal-only", found.Detail);
    }

    [Fact]
    public async Task A_registry_without_agents_is_named_too_old_before_anything_is_looked_up()
    {
        _meta.Meta = MetaOnlyRegistry.Current with { Capabilities = MetaOnlyRegistry.Current.Capabilities!.Where(c => c != RegistryCapability.Agents).ToArray() };

        var found = await Changes(_a).FindAsync(new AgentTargetRequest("reviewer", RegistryUrl, null), CancellationToken.None);

        Assert.Equal(AgentPullRunFailure.RegistryTooOld, found.Failure);
        Assert.Contains("too old", found.Detail);
        Assert.Equal(0, _api.ListCalls);
    }

    private sealed record Machine(string Home)
    {
        public AgentPushTests.FakeAgentLedger Ledger { get; } = new();

        public string Agents => Path.Combine(Home, "agents");
    }

    private sealed class Tokens : IAccessTokenProvider
    {
        public Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(SessionLookups.Of(Token));
    }
}
