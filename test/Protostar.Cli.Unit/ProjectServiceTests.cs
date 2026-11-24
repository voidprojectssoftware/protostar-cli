using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;
using Xunit;
using static Protostar.Cli.Unit.SkillPushServiceTests;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <see cref="IProjectService"/>: <c>protostar project</c> show, link and unlink.</summary>
public sealed class ProjectServiceTests : IDisposable
{
    private const string Registry = "https://registry.test";
    private static readonly Guid MachineId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly string _folder = Path.GetFullPath(Directory.CreateTempSubdirectory("pstar-project-").FullName);
    private readonly FakeApi _api = new();
    private readonly RecordingResolver _resolver = new();
    private readonly FakeLedger _ledger = new();
    private readonly AgentPushTests.FakeAgentLedger _agentLedger = new();
    // Overrides _token when set, for a lookup a stored token alone cannot give (an unreachable refresh).
    private SessionLookup? _session;
    private StoredToken? _token = new() { Registry = Registry, AccessToken = "tok", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private readonly MetaOnlyRegistry _meta = new();

    private readonly List<DiscoveredSkill> _local = [];
    private FakeRegistryApi _skills = new();

    private ProjectService Sut() => new(
        _resolver, new FakeTokens(() => _session ?? SessionLookups.Of(_token)), new RegistryCapabilities(_meta), _api, new FakeMachine(), _ledger, _agentLedger,
        new FakeSkillService(_local), _skills);

    // A project skill in the folder whose record says it synced at v1 as a skill of project synced; edited when changedHere.
    private DiscoveredSkill Copy(string name, Guid synced, bool changedHere = false)
    {
        var path = Path.Combine(_folder, ".claude", "skills", name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), $"---\nname: {name}\ndescription: d\n---\nbody\n");
        var copy = new DiscoveredSkill { Name = name, Scope = SkillScope.Project, Path = path, ProjectRoot = _folder };
        _ledger.Record(new Uri(Registry), path, new SkillLedgerEntry(SkillFileReader.Hash(SkillFileReader.Read(path)), 1, name, synced));
        if (changedHere)
            File.AppendAllText(Path.Combine(path, "SKILL.md"), "changed here\n");
        _local.Add(copy);
        return copy;
    }

    [Fact]
    public async Task Show_asks_the_registry_with_the_facts_the_session_resolver_gives()
    {
        var result = await Sut().ShowAsync(Registry, _folder, CancellationToken.None);

        Assert.Equal(ProjectFailure.None, result.Failure);
        Assert.Equal("widget", result.Project!.Name);
        Assert.Equal([_folder], _resolver.Asked);
        Assert.Same(result.Facts, _api.LastFacts);
        Assert.Equal(MachineId, _api.LastMachine);
    }

    [Fact]
    public async Task A_registry_without_projects_is_refused_before_it_is_asked_and_local_facts_still_show()
    {
        _meta.Meta = new RegistryMeta { ApiMajors = [1], Capabilities = ["sessions"] };

        var result = await Sut().ShowAsync(Registry, _folder, CancellationToken.None);

        Assert.Equal(ProjectFailure.Incompatible, result.Failure);
        Assert.Equal("This registry is too old for projects. Update the registry.", result.Detail);
        Assert.NotNull(result.Facts);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task Offline_show_still_returns_what_can_be_worked_out_locally()
    {
        _api.Error = "Could not reach the registry: connection refused";

        var result = await Sut().ShowAsync(Registry, _folder, CancellationToken.None);

        Assert.Equal(ProjectFailure.Failed, result.Failure);
        Assert.Null(result.Project);
        Assert.Equal(_folder, result.Facts.WorkingDirectory);
        Assert.Contains("connection refused", result.Detail);
    }

    [Fact]
    public async Task Signed_out_show_returns_local_facts_without_calling_the_registry()
    {
        _token = null;

        var result = await Sut().ShowAsync(Registry, _folder, CancellationToken.None);

        Assert.Equal(ProjectFailure.NotLoggedIn, result.Failure);
        Assert.Equal(_folder, result.Facts.Directory);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task Show_with_a_session_the_registry_could_not_be_reached_to_refresh_fails_with_local_facts()
    {
        _session = SessionLookups.Unreachable(Registry);

        var result = await Sut().ShowAsync(Registry, _folder, CancellationToken.None);

        Assert.Equal(ProjectFailure.Failed, result.Failure);
        Assert.Equal(_folder, result.Facts.Directory);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task Link_sends_the_folders_facts_and_the_project_named()
    {
        var result = await Sut().LinkAsync(Registry, _folder, "github.com/acme/widget", CancellationToken.None);

        Assert.Equal("linked", result.Change!.Outcome);
        Assert.Equal("github.com/acme/widget", _api.LastTarget);
        Assert.Same(result.Facts, _api.LastFacts);
    }

    [Fact]
    public async Task A_refused_link_reports_why()
    {
        _api.Error = "No project 'nope'.";

        var result = await Sut().LinkAsync(Registry, _folder, "nope", CancellationToken.None);

        Assert.Null(result.Change);
        Assert.Equal(ProjectFailure.Failed, result.Failure);
        Assert.Equal("No project 'nope'.", result.Detail);
    }

    [Fact]
    public async Task Unlink_sends_the_folders_facts()
    {
        var result = await Sut().UnlinkAsync(Registry, _folder, CancellationToken.None);

        Assert.Equal("unlinked", result.Change!.Outcome);
        Assert.Same(result.Facts, _api.LastFacts);
    }

    [Fact]
    public async Task A_link_that_moves_skills_points_this_machines_records_of_them_at_the_new_project()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var registry = new Uri(Registry);
        _api.ProjectBefore = from;
        _api.Linked = new ProjectLinkChange { Outcome = "linked", Project = new RemoteProject { ProjectId = to, Name = "repo" }, MovedSkills = ["notes"] };
        var here = new SkillLedgerEntry("h1", 1, "notes", from);
        var otherCheckout = new SkillLedgerEntry("h2", 2, "notes", from);
        var otherSkill = new SkillLedgerEntry("h3", 1, "deploy", from);
        var otherProject = new SkillLedgerEntry("h4", 1, "notes", Guid.NewGuid());
        var global = new SkillLedgerEntry("h5", 1, "notes");
        _ledger.Record(registry, "/f/.claude/skills/notes", here);
        _ledger.Record(registry, "/g/.claude/skills/notes", otherCheckout);
        _ledger.Record(registry, "/f/.claude/skills/deploy", otherSkill);
        _ledger.Record(registry, "/h/.claude/skills/notes", otherProject);
        _ledger.Record(registry, "/home/.claude/skills/notes", global);

        await Sut().LinkAsync(Registry, _folder, "repo", CancellationToken.None);

        Assert.Equal(here with { ProjectId = to }, _ledger.Get(registry, "/f/.claude/skills/notes"));
        Assert.Equal(otherCheckout with { ProjectId = to }, _ledger.Get(registry, "/g/.claude/skills/notes"));
        Assert.Equal(otherSkill, _ledger.Get(registry, "/f/.claude/skills/deploy"));
        Assert.Equal(otherProject, _ledger.Get(registry, "/h/.claude/skills/notes"));
        Assert.Equal(global, _ledger.Get(registry, "/home/.claude/skills/notes"));
    }

    [Fact]
    public async Task An_unlink_that_moves_skills_back_points_the_records_back()
    {
        var linked = Guid.NewGuid();
        var own = Guid.NewGuid();
        var registry = new Uri(Registry);
        _api.ProjectBefore = linked;
        _api.Unlinked = new ProjectLinkChange { Outcome = "unlinked", Project = new RemoteProject { ProjectId = own, Name = "f" }, MovedSkills = ["notes"] };
        _ledger.Record(registry, "/f/.claude/skills/notes", new SkillLedgerEntry("h1", 3, "notes", linked));

        await Sut().UnlinkAsync(Registry, _folder, CancellationToken.None);

        Assert.Equal(own, _ledger.Get(registry, "/f/.claude/skills/notes")!.ProjectId);
    }

    [Fact]
    public async Task A_refused_link_leaves_the_ledger_alone()
    {
        var from = Guid.NewGuid();
        var registry = new Uri(Registry);
        _api.ProjectBefore = from;
        _ledger.Record(registry, "/f/.claude/skills/notes", new SkillLedgerEntry("h1", 1, "notes", from));
        _api.LinkError = "Nothing changed: 'repo' already has a skill named notes.";

        var result = await Sut().LinkAsync(Registry, _folder, "repo", CancellationToken.None);

        Assert.Null(result.Change);
        Assert.Equal(from, _ledger.Get(registry, "/f/.claude/skills/notes")!.ProjectId);
    }

    // ----- skills an unlink leaves behind (cli#129) -----

    [Fact]
    public async Task An_unlink_lists_the_skills_it_leaves_behind_and_what_becomes_of_each()
    {
        var linked = Guid.NewGuid();
        var own = Guid.NewGuid();
        _api.ProjectBefore = linked;
        _api.Unlinked = new ProjectLinkChange { Outcome = "unlinked", Project = new RemoteProject { ProjectId = own, Name = "notes" } };
        Copy("deploy", linked, changedHere: true);
        Copy("lint", linked);
        Copy("gone", linked);
        Copy("mine", linked);
        _skills.Skills.Add(new RegistrySkill("deploy", null, 1, "", DateTimeOffset.UnixEpoch, linked, "widget"));
        _skills.Skills.Add(new RegistrySkill("lint", null, 1, "", DateTimeOffset.UnixEpoch, linked, "widget"));
        _skills.Skills.Add(new RegistrySkill("mine", null, 1, "", DateTimeOffset.UnixEpoch, linked, "widget"));
        _skills.Skills.Add(new RegistrySkill("Mine", null, 2, "", DateTimeOffset.UnixEpoch, own, "notes"));
        _skills.Removed.Add(new RegistryRemovedSkill("gone", linked, 1, "gone-now"));

        var result = await Sut().UnlinkAsync(Registry, _folder, CancellationToken.None);

        Assert.Equal("widget", result.LeftFrom);
        Assert.Equal(
            [
                new SkillLeftBehind("deploy", LeftSkillFate.ChangedHere), new SkillLeftBehind("gone", LeftSkillFate.Removed),
                new SkillLeftBehind("lint", LeftSkillFate.StillLinked), new SkillLeftBehind("mine", LeftSkillFate.FolderHasOwn),
            ],
            result.LeftSkills);
        Assert.Equal(1, _skills.ListCalls);
    }

    [Fact]
    public async Task Skills_that_moved_or_synced_in_another_project_are_not_left_behind_and_cost_no_list()
    {
        var linked = Guid.NewGuid();
        var own = Guid.NewGuid();
        _api.ProjectBefore = linked;
        _api.Unlinked = new ProjectLinkChange { Outcome = "unlinked", Project = new RemoteProject { ProjectId = own, Name = "notes" }, MovedSkills = ["pushed"] };
        Copy("pushed", linked);
        Copy("elsewhere", Guid.NewGuid());

        var result = await Sut().UnlinkAsync(Registry, _folder, CancellationToken.None);

        Assert.Null(result.LeftSkills);
        Assert.Equal(0, _skills.ListCalls);
    }

    [Fact]
    public async Task A_list_the_registry_refuses_leaves_each_skill_unknown_but_still_listed()
    {
        var linked = Guid.NewGuid();
        _api.ProjectBefore = linked;
        _api.Unlinked = new ProjectLinkChange { Outcome = "unlinked", Project = new RemoteProject { Name = "notes" } };
        Copy("lint", linked);
        _skills = new FakeRegistryApi { ListError = "boom" };

        var result = await Sut().UnlinkAsync(Registry, _folder, CancellationToken.None);

        Assert.Equal([new SkillLeftBehind("lint", LeftSkillFate.Unknown)], result.LeftSkills);
    }

    [Fact]
    public void Each_fate_says_what_happens_next()
    {
        Assert.StartsWith("unchanged here, so it stays widget's skill and `protostar skills pull` keeps it up to date from there.",
            ProjectPresenter.Fate(new SkillLeftBehind("lint", LeftSkillFate.StillLinked), "widget", "notes"));
        Assert.Equal(
            "changed here since it was pulled, so it now belongs to notes: the next backup (the capture hook, `protostar skills push` or " +
            "`protostar setup`) backs it up there as a new skill.",
            ProjectPresenter.Fate(new SkillLeftBehind("deploy", LeftSkillFate.ChangedHere), "widget", "notes"));
        Assert.Contains("notes has its own 'mine'", ProjectPresenter.Fate(new SkillLeftBehind("mine", LeftSkillFate.FolderHasOwn), "widget", "notes"));
    }

    private sealed class RecordingResolver : IProjectFactsResolver
    {
        public List<string> Asked { get; } = [];

        public ProjectFacts Resolve(string workingDirectory)
        {
            Asked.Add(workingDirectory);
            return ProjectFacts.NotGit(workingDirectory, workingDirectory);
        }
    }

    private sealed class FakeApi : IProjectApi
    {
        public string? Error { get; set; }

        /// <summary>When set, only the link or unlink fails, with this message.</summary>
        public string? LinkError { get; set; }

        /// <summary>The project the folder resolves to before a link or unlink.</summary>
        public Guid? ProjectBefore { get; set; }

        public ProjectLinkChange Linked { get; set; } = new() { Outcome = "linked", Project = Widget };
        public ProjectLinkChange Unlinked { get; set; } = new() { Outcome = "unlinked", Project = Widget };
        public int Calls { get; private set; }
        public ProjectFacts? LastFacts { get; private set; }
        public Guid LastMachine { get; private set; }
        public string? LastTarget { get; private set; }

        private static readonly RemoteProject Widget = new() { Name = "widget", RecognisedBy = "folder" };

        public Task<ApiResult<RemoteProject>> ResolveProjectAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
            Task.FromResult(Record(machineId, facts, Widget with { ProjectId = ProjectBefore }));

        public Task<ApiResult<ProjectLinkChange>> LinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, string project, CancellationToken cancellationToken)
        {
            LastTarget = project;
            return Task.FromResult(LinkError is null ? Record(machineId, facts, Linked) : ApiResult<ProjectLinkChange>.Fail(LinkError));
        }

        public Task<ApiResult<ProjectLinkChange>> UnlinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
            Task.FromResult(Record(machineId, facts, Unlinked));

        private ApiResult<T> Record<T>(Guid machineId, ProjectFacts facts, T value)
        {
            Calls++;
            LastMachine = machineId;
            LastFacts = facts;
            return Error is null ? ApiResult<T>.Success(value) : ApiResult<T>.Fail(Error);
        }
    }

    private sealed class FakeMachine : IMachineIdentity
    {
        public MachineInfo Get() => new(MachineId, "test", "linux");

        public bool IsRegisteredWith(Uri registry) => true;

        public void MarkRegistered(Uri registry)
        {
        }
    }

    private sealed class FakeTokens(Func<SessionLookup> session) : IAccessTokenProvider
    {
        public Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(session());
    }
}
