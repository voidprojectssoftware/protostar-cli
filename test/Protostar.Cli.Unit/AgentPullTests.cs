using System.Text;
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
/// Contract tests for <see cref="IAgentPullService"/> and <see cref="AgentInstaller"/>: real Claude Code discovery in a temp
/// harness home (this machine) against an in-memory registry that other machines push to.
/// </summary>
public sealed class AgentPullTests : IDisposable
{
    private const string RegistryUrl = "https://registry.test";
    private static readonly Uri Registry = new(RegistryUrl);
    private readonly string _root = Path.GetFullPath(Directory.CreateTempSubdirectory("pstar-agent-pull-").FullName);
    private readonly AgentPushTests.FakeAgentLedger _ledger = new();
    private readonly FakeAgentApi _api = new();
    private readonly MetaOnlyRegistry _meta = new();
    private readonly FakeProjects _projects = new() { ByFolder = true };

    public AgentPullTests() => Directory.CreateDirectory(Home);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Home => Path.Combine(_root, "home", ".claude");

    private string Agents => Path.Combine(Home, "agents");

    private string Repo => Path.Combine(_root, "repo");

    private Guid RepoProject => FakeRegistryApi.ProjectIdOf(ProjectFacts.NotGit(Repo, Repo));

    private static readonly StoredToken Token = new() { Registry = RegistryUrl, AccessToken = "tok", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };

    private HarnessCatalog Catalog => new([new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper())]);

    private AgentPullService Sut() => new(Catalog, new Tokens(), _api, new RegistryCapabilities(_meta), _ledger, new FakeFacts(), _projects, new FakeMachine());

    private AgentPushService Push() => new(Catalog, new Tokens(), _api, new RegistryCapabilities(_meta), _ledger, new FakeFacts(), new FakeMachine());

    private Task<AgentPullRunResult> Pull(bool force = false, string? project = null, int? version = null, params string[] names) =>
        Sut().PullAsync(new AgentPullRequest(names, null, Home, RegistryUrl, force, project, version), CancellationToken.None);

    private Task<AgentRemoteResult> Remote(string? project = null) =>
        Sut().CompareAsync(new AgentRemoteRequest(null, Home, RegistryUrl, project), CancellationToken.None);

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Missing_agents_are_installed_at_the_registrys_path_and_a_second_pull_changes_nothing()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "You review.");
        _api.Publish(null, "review/security.md", "security-reviewer", "You check security.");

        var first = await Pull();
        var written = File.GetLastWriteTimeUtc(Path.Combine(Agents, "review", "security.md"));
        var second = await Pull();

        Assert.Equal([AgentPullStatus.Installed, AgentPullStatus.Installed], first.Entries.Select(e => e.Status));
        Assert.Equal(FakeAgentApi.File("security-reviewer", "You check security."), File.ReadAllText(Path.Combine(Agents, "review", "security.md")));
        Assert.Equal(1, _ledger.Get(Registry, Path.Combine(Agents, "reviewer.md"))!.Version);
        Assert.All(second.Entries, e => Assert.Equal(AgentPullStatus.UpToDate, e.Status));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(Agents, "review", "security.md")));
        Assert.Equal(2, _api.Downloads.Count);
    }

    [Fact]
    public async Task Remote_shows_each_agents_scope_version_and_whether_this_machine_has_it()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "v1");
        _api.Publish(null, "planner.md", "planner", "v1");
        _api.Publish(RepoProject, "linter.md", "linter", "v1");
        await Pull();
        _api.PushFromElsewhere(null, "reviewer", "v2");
        _api.Publish(null, "new.md", "newcomer", "v1");
        Write(Path.Combine(Agents, "planner.md"), FakeAgentApi.File("planner", "edited here"));
        Write(Path.Combine(Agents, "mine.md"), FakeAgentApi.File("mine", "never pushed"));

        var result = await Remote();

        var states = result.Agents.ToDictionary(a => a.Name, a => (a.State, a.Remote?.Version, a.Project));
        Assert.Equal((AgentSyncState.UpdateAvailable, 2, (string?)null), states["reviewer"]);
        Assert.Equal((AgentSyncState.LocalChanges, 1, (string?)null), states["planner"]);
        Assert.Equal((AgentSyncState.NotInstalled, 1, (string?)null), states["newcomer"]);
        Assert.Equal((AgentSyncState.LocalOnly, (int?)null, (string?)null), states["mine"]);
        Assert.Equal((AgentSyncState.OtherProject, 1, "app"), states["linter"]);
    }

    [Fact]
    public async Task An_unedited_older_copy_is_updated_and_an_edited_one_is_never_overwritten_without_force()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "v1");
        _api.Publish(null, "planner.md", "planner", "v1");
        await Pull();
        _api.PushFromElsewhere(null, "reviewer", "v2");
        var planner = Write(Path.Combine(Agents, "planner.md"), FakeAgentApi.File("planner", "my edit"));

        var result = await Pull();

        var byName = result.Entries.ToDictionary(e => e.Name);
        Assert.Equal(AgentPullStatus.Updated, byName["reviewer"].Status);
        Assert.Equal(2, byName["reviewer"].Version);
        Assert.Contains("v2", File.ReadAllText(Path.Combine(Agents, "reviewer.md")));
        Assert.Equal(AgentPullStatus.LocalChanges, byName["planner"].Status);
        Assert.Contains("my edit", File.ReadAllText(planner));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task An_edit_on_an_older_copy_is_changed_both_ways_and_force_takes_the_registrys()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "v1");
        await Pull();
        _api.PushFromElsewhere(null, "reviewer", "v2 from A");
        var file = Write(Path.Combine(Agents, "reviewer.md"), FakeAgentApi.File("reviewer", "edit on B"));

        var kept = await Pull();
        var forced = await Pull(force: true, names: "reviewer");

        Assert.Equal(AgentPullStatus.ChangedBoth, Assert.Single(kept.Entries).Status);
        var entry = Assert.Single(forced.Entries);
        Assert.Equal(AgentPullStatus.Updated, entry.Status);
        Assert.Equal("Local changes were overwritten (--force).", entry.Message);
        Assert.Contains("v2 from A", File.ReadAllText(file));
        Assert.Equal(2, _ledger.Get(Registry, file)!.Version);
    }

    [Fact]
    public async Task A_stale_push_refusal_shows_as_changed_both_on_the_pull()
    {
        var file = Write(Path.Combine(Agents, "reviewer.md"), FakeAgentApi.File("reviewer", "v1"));
        await Push().PushAsync(new AgentPushRequest([], null, Home, null, RegistryUrl), CancellationToken.None);
        _api.PushFromElsewhere(null, "reviewer", "v2 from A");
        Write(file, FakeAgentApi.File("reviewer", "edit on B"));
        var push = await Push().PushAsync(new AgentPushRequest([], null, Home, null, RegistryUrl), CancellationToken.None);

        var result = await Pull();

        Assert.Equal(AgentPushStatus.Stale, Assert.Single(push.Entries).Outcome.Status);
        Assert.Equal(AgentPullStatus.ChangedBoth, Assert.Single(result.Entries).Status);
        Assert.Contains("edit on B", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_file_at_the_registrys_path_that_is_another_agent_is_a_conflict_even_with_force()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "v1");
        var other = Write(Path.Combine(Agents, "reviewer.md"), FakeAgentApi.File("something-else", "not this agent"));

        var result = await Pull(force: true);

        var entries = result.Entries.Where(e => e.Name == "reviewer").ToList();
        var entry = Assert.Single(entries);
        Assert.Equal(AgentPullStatus.Conflict, entry.Status);
        Assert.Contains("already exists and is not this agent", entry.Message);
        Assert.Contains("not this agent", File.ReadAllText(other));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task An_unedited_copy_follows_a_rename_in_place_and_stays_quiet_afterwards()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "v1");
        await Pull();
        _api.Rename(null, "reviewer", "code-reviewer");
        var file = Path.Combine(Agents, "reviewer.md");

        var remote = await Remote();
        var result = await Pull();
        var again = await Pull();
        var push = await Push().PushAsync(new AgentPushRequest([], null, Home, null, RegistryUrl), CancellationToken.None);

        Assert.Equal(AgentSyncState.RemovedInRegistry, Assert.Single(remote.Agents, a => a.Name == "reviewer").State);
        Assert.Contains("`protostar agents pull` renames this copy to match", Assert.Single(remote.Agents, a => a.Name == "reviewer").Note);
        Assert.Equal(AgentSyncState.UpdateAvailable, Assert.Single(remote.Agents, a => a.Name == "code-reviewer").State);
        Assert.Equal(AgentPullStatus.FollowedRename, Assert.Single(result.Entries, e => e.Name == "reviewer").Status);
        Assert.Equal(AgentPullStatus.UpToDate, Assert.Single(result.Entries, e => e.Name == "code-reviewer").Status);
        Assert.Equal(FakeAgentApi.File("code-reviewer", "v1"), File.ReadAllText(file));
        Assert.Equal("code-reviewer", Assert.Single(again.Entries).Name);
        Assert.Equal(AgentPullStatus.UpToDate, Assert.Single(again.Entries).Status);
        Assert.Equal(AgentPushStatus.Unchanged, Assert.Single(push.Entries).Outcome.Status);
        Assert.Empty(_api.Pushed);
    }

    [Fact]
    public async Task An_edited_copy_of_a_renamed_agent_is_kept_and_reported()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "v1");
        await Pull();
        _api.Rename(null, "reviewer", "code-reviewer");
        var file = Write(Path.Combine(Agents, "reviewer.md"), FakeAgentApi.File("reviewer", "my edit"));

        var result = await Pull();

        var kept = Assert.Single(result.Entries, e => e.Name == "reviewer");
        Assert.Equal(AgentPullStatus.RemovedInRegistry, kept.Status);
        Assert.Contains("this copy has changes made here, so it was not renamed", kept.Message);
        Assert.Contains("my edit", File.ReadAllText(file));
        Assert.Equal(AgentPullStatus.Conflict, Assert.Single(result.Entries, e => e.Name == "code-reviewer").Status);
    }

    [Fact]
    public async Task A_fresh_machine_installs_a_renamed_agent_under_its_new_name()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "v1");
        _api.Rename(null, "reviewer", "code-reviewer");

        var result = await Pull();
        var again = await Remote();

        Assert.Equal(AgentPullStatus.Installed, Assert.Single(result.Entries).Status);
        Assert.Equal(FakeAgentApi.File("code-reviewer", "v1"), File.ReadAllText(Path.Combine(Agents, "reviewer.md")));
        Assert.Equal(AgentSyncState.UpToDate, Assert.Single(again.Agents).State);
    }

    [Fact]
    public async Task A_copy_of_a_deleted_agent_is_kept_and_never_deleted()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "v1");
        await Pull();
        _api.Delete(null, "reviewer");

        var result = await Pull();

        var entry = Assert.Single(result.Entries);
        Assert.Equal(AgentPullStatus.RemovedInRegistry, entry.Status);
        Assert.Contains("Deleted from the registry (at v1)", entry.Message);
        Assert.True(File.Exists(Path.Combine(Agents, "reviewer.md")));
    }

    [Fact]
    public async Task Project_agents_go_into_this_folders_project_and_other_projects_are_never_written()
    {
        Directory.CreateDirectory(Repo);
        _api.Publish(RepoProject, "linter.md", "linter", "v1");
        _api.Publish(Guid.NewGuid(), "other.md", "other", "v1");

        var result = await Pull(project: Repo);

        Assert.Equal(AgentPullStatus.Installed, Assert.Single(result.Entries, e => e.Name == "linter").Status);
        Assert.True(File.Exists(Path.Combine(Repo, ".claude", "agents", "linter.md")));
        Assert.Equal(AgentPullStatus.OtherProject, Assert.Single(result.Entries, e => e.Name == "other").Status);
        Assert.False(File.Exists(Path.Combine(Repo, ".claude", "agents", "other.md")));
        Assert.False(File.Exists(Path.Combine(Agents, "linter.md")));
    }

    [Fact]
    public async Task A_registry_without_agent_downloads_is_named_too_old_before_anything_is_written()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "v1");
        _meta.Meta = MetaOnlyRegistry.Current with { Capabilities = MetaOnlyRegistry.Current.Capabilities!.Where(c => c != RegistryCapability.AgentContent).ToArray() };

        var result = await Pull();

        Assert.Equal(AgentPullRunFailure.RegistryTooOld, result.Failure);
        Assert.Equal("This registry is too old for reading agents. Update the registry.", result.Detail);
        Assert.False(Directory.Exists(Agents));
    }

    [Fact]
    public async Task Version_installs_an_earlier_version_and_says_how_to_make_it_current()
    {
        _api.Publish(null, "reviewer.md", "reviewer", "v1");
        _api.PushFromElsewhere(null, "reviewer", "v2");

        var result = await Pull(version: 1, names: "reviewer");

        var entry = Assert.Single(result.Entries);
        Assert.Equal((AgentPullStatus.Installed, 1), (entry.Status, entry.Version));
        Assert.Contains("`protostar agents push reviewer --force` makes it current", entry.Message);
        Assert.Contains("v1", File.ReadAllText(Path.Combine(Agents, "reviewer.md")));
        Assert.Equal(AgentSyncState.UpdateAvailable, Assert.Single((await Remote()).Agents).State);
    }

    [Fact]
    public async Task A_project_agent_moved_by_a_link_elsewhere_is_followed_even_after_it_moved_on()
    {
        Directory.CreateDirectory(Repo);
        var file = Write(Path.Combine(Repo, ".claude", "agents", "linter.md"), FakeAgentApi.File("linter", "v1"));
        await Push().PushAsync(new AgentPushRequest([], null, Home, Repo, RegistryUrl), CancellationToken.None);
        var elsewhere = Guid.NewGuid();
        _api.MoveProject(RepoProject, elsewhere, "linter");
        _api.PushFromElsewhere(elsewhere, "linter", "v2");

        var result = await Push().PushAsync(new AgentPushRequest([], null, Home, Repo, RegistryUrl), CancellationToken.None);

        Assert.Equal(AgentPushStatus.Unchanged, Assert.Single(result.Entries).Outcome.Status);
        Assert.Equal(elsewhere, _ledger.Get(Registry, file)!.ProjectId);
        Assert.Contains(("linter", (int?)1), _api.Downloads);
    }

    [Fact]
    public void The_installer_refuses_a_download_that_does_not_match_its_hash_and_writes_nothing()
    {
        var content = new RemoteAgentContent { Name = "reviewer", Version = 1, Path = "reviewer.md", ContentHash = new string('0', 64), Content = "---\nname: reviewer\n---\n"u8.ToArray() };

        var ex = Assert.Throws<AgentInstallException>(() => AgentInstaller.Install(Agents, Path.Combine(Agents, "reviewer.md"), content, "reviewer"));

        Assert.Contains("does not match its checksum", ex.Message);
        Assert.False(Directory.Exists(Agents));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("/etc/passwd.md")]
    [InlineData("a/../../b.md")]
    [InlineData("C:/x.md")]
    [InlineData("notes.txt")]
    public void An_unsafe_registry_path_is_never_turned_into_a_place_to_write(string path) =>
        Assert.Null(AgentInstaller.PathFor(Agents, path));

    [Fact]
    public void The_installer_never_writes_outside_the_agents_folder()
    {
        var bytes = "---\nname: x\n---\n"u8.ToArray();
        var content = new RemoteAgentContent { Name = "x", Version = 1, Path = "x.md", ContentHash = AgentPushService.Hash(bytes), Content = bytes };

        Assert.Throws<AgentInstallException>(() => AgentInstaller.Install(Agents, Path.Combine(_root, "x.md"), content, "x"));
        Assert.False(File.Exists(Path.Combine(_root, "x.md")));
    }

    [Fact]
    public void Naming_rewrites_only_the_name_line_and_keeps_a_matching_file_byte_for_byte()
    {
        var same = Encoding.UTF8.GetBytes("---\r\nname: reviewer\r\ndescription: d\r\n---\r\nbody\r\n");

        Assert.Same(same, AgentInstaller.Named(same, "reviewer"));
        Assert.Equal("---\r\nname: code-reviewer\r\ndescription: d\r\n---\r\nbody\r\n", Encoding.UTF8.GetString(AgentInstaller.Named(same, "code-reviewer")));
    }

    private sealed class Tokens : IAccessTokenProvider
    {
        public Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(SessionLookups.Of(Token));
    }
}
