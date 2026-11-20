using System.Security.Cryptography;
using System.Text;
using Protostar.Cli.Auth;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Skills;
using Xunit;
using static Protostar.Cli.Unit.SkillPushServiceTests;

namespace Protostar.Cli.Unit;

/// <summary>
/// Contract tests for <see cref="ISkillChangeService"/>: real skill folders in a temporary directory, with in-memory
/// fakes for the session, the registry, the project lookup and the ledger.
/// </summary>
public sealed class SkillChangeServiceTests : IDisposable
{
    private const string Registry = "https://registry.test";
    private static readonly Uri RegistryUri = new(Registry);

    private readonly string _root = Path.GetFullPath(Directory.CreateTempSubdirectory("pstar-change-").FullName);
    private readonly FakeLedger _ledger = new();
    private readonly FakeRegistryApi _api = new();
    private readonly HereProjects _projects = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string SkillsDir => Path.Combine(_root, "skills");

    private ISkillChangeService Build() =>
        new SkillChangeService(new Tokens(), _api, _ledger, new FakeFacts(), _projects, new FakeMachine());

    private static string Manifest(string name, string body = "body") => $"---\nname: {name}\ndescription: d\n---\n{body}";

    // Puts a skill in the fake registry, as the registry would hash and serve it.
    private void Publish(string name, int version, string text, Guid? projectId = null, string? projectName = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var file = new RegistrySkillFile("SKILL.md", Convert.ToHexString(SHA256.HashData(bytes)), bytes);
        var hash = SkillFileReader.RegistryHash([new SkillUploadFile("SKILL.md", bytes)]);
        _api.Skills.Add(new RegistrySkill(name, "d", version, hash, DateTimeOffset.UtcNow, projectId, projectName));
        _api.Contents[FakeRegistryApi.Key(name, projectId)] = new RegistrySkillContent(name, version, hash, [file]);
    }

    // A local copy recorded in the ledger as synced with the registry skill `name` at `version`.
    private string SyncedCopy(string folder, string name, int version, string text, Guid? projectId = null)
    {
        var path = Path.Combine(SkillsDir, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), text);
        _ledger.Record(RegistryUri, path, new SkillLedgerEntry(SkillFileReader.Hash(SkillFileReader.Read(path)), version, name, projectId));
        return path;
    }

    private async Task<SkillTarget> FindAsync(string name, string? projectStart = null)
    {
        var found = await Build().FindAsync(new SkillTargetRequest(name, Registry, projectStart), CancellationToken.None);
        Assert.Equal(SkillPullRunFailure.None, found.Failure);
        return found.Target!;
    }

    // ----- RewriteName -----

    [Fact]
    public void RewriteName_sets_the_front_matter_name_and_keeps_everything_else()
    {
        var rewritten = SkillChangeService.RewriteName("---\r\nname: notes\r\ndescription: d\r\n---\r\nname: in the body\r\n", "journal");

        Assert.Equal("---\r\nname: journal\r\ndescription: d\r\n---\r\nname: in the body\r\n", rewritten);
    }

    [Fact]
    public void RewriteName_is_null_when_the_front_matter_declares_no_name()
    {
        Assert.Null(SkillChangeService.RewriteName("---\ndescription: d\n---\nname: body", "journal"));
        Assert.Null(SkillChangeService.RewriteName("no front matter\nname: x", "journal"));
    }

    [Fact]
    public void RewriteName_quotes_a_name_that_is_not_plainly_safe_in_yaml()
    {
        Assert.Equal("---\nname: \"a: b\"\n---\n", SkillChangeService.RewriteName("---\nname: x\n---\n", "a: b"));
    }

    // ----- FindAsync -----

    [Fact]
    public async Task Find_prefers_this_folders_project_skill_over_the_global_one()
    {
        var project = Guid.NewGuid();
        _projects.ProjectId = project;
        Publish("notes", 1, Manifest("notes"));
        Publish("notes", 2, Manifest("notes"), project, "repo");

        var target = await FindAsync("notes", _root);

        Assert.Equal(project, target.Skill.ProjectId);
        Assert.NotNull(target.ProjectFacts);
    }

    [Fact]
    public async Task Find_without_a_folder_means_the_global_skill()
    {
        _projects.ProjectId = Guid.NewGuid();
        Publish("notes", 1, Manifest("notes"));
        Publish("notes", 2, Manifest("notes"), _projects.ProjectId, "repo");

        var target = await FindAsync("notes");

        Assert.Null(target.Skill.ProjectId);
        Assert.Null(target.ProjectFacts);
    }

    [Fact]
    public async Task Find_of_another_projects_skill_fails_and_says_whose_it_is()
    {
        Publish("notes", 1, Manifest("notes"), Guid.NewGuid(), "elsewhere");

        var found = await Build().FindAsync(new SkillTargetRequest("notes", Registry, _root), CancellationToken.None);

        Assert.Equal(SkillPullRunFailure.SkillNotFound, found.Failure);
        Assert.Contains("elsewhere", found.Detail);
    }

    [Fact]
    public async Task Find_reports_this_machines_copies_and_which_have_unpushed_changes()
    {
        Publish("notes", 1, Manifest("notes"));
        var clean = SyncedCopy("notes", "notes", 1, Manifest("notes"));
        var edited = SyncedCopy("notes-copy", "notes", 1, Manifest("notes"));
        File.WriteAllText(Path.Combine(edited, "SKILL.md"), Manifest("notes", "edited"));
        SyncedCopy("other", "other", 1, Manifest("other"));

        var target = await FindAsync("notes");

        Assert.Equal([clean, edited], target.Copies.Select(c => c.Path));
        Assert.Equal([false, true], target.Copies.Select(c => c.HasLocalChanges));
    }

    // ----- DeleteAsync -----

    [Fact]
    public async Task Delete_removes_the_registry_skill_and_never_the_local_copy_or_its_record()
    {
        Publish("notes", 1, Manifest("notes"));
        var path = SyncedCopy("notes", "notes", 1, Manifest("notes"));
        var target = await FindAsync("notes");

        var result = await Build().DeleteAsync(target, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(["notes"], _api.Deleted);
        Assert.Equal(Manifest("notes"), File.ReadAllText(Path.Combine(path, "SKILL.md")));
        Assert.NotNull(_ledger.Get(RegistryUri, path));
    }

    // ----- RenameAsync -----

    [Fact]
    public async Task Rename_renames_in_the_registry_and_brings_an_unedited_copy_along_up_to_date()
    {
        Publish("notes", 2, Manifest("notes"));
        var path = SyncedCopy("notes", "notes", 2, Manifest("notes"));
        _api.Outcomes["journal"] = new SkillPushOutcome(SkillPushStatus.Created, 3, null);
        var target = await FindAsync("notes");

        var result = await Build().RenameAsync(target, "journal", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal([("notes", "journal")], _api.Renamed);
        var moved = Path.Combine(SkillsDir, "journal");
        Assert.Equal(moved, Assert.Single(result.Copies).Path);
        Assert.False(Directory.Exists(path));
        Assert.Equal(Manifest("journal"), File.ReadAllText(Path.Combine(moved, "SKILL.md")));

        // The registry's current SKILL.md is pushed with the new name, and is exactly what the copy now holds.
        var pushed = Assert.Single(_api.Pushed);
        Assert.Equal("journal", pushed.Name);
        Assert.Equal(Manifest("journal"), Encoding.UTF8.GetString(Assert.Single(pushed.Files).Content));

        Assert.Null(_ledger.Get(RegistryUri, path));
        var recorded = _ledger.Get(RegistryUri, moved)!;
        Assert.Equal("journal", recorded.Name);
        Assert.Equal(3, recorded.Version);
        Assert.Equal(SkillFileReader.Hash(SkillFileReader.Read(moved)), recorded.ContentHash);
    }

    [Fact]
    public async Task Rename_keeps_an_edited_copys_changes_protected()
    {
        Publish("notes", 1, Manifest("notes"));
        var path = SyncedCopy("notes", "notes", 1, Manifest("notes"));
        File.WriteAllText(Path.Combine(path, "SKILL.md"), Manifest("notes", "my edit"));
        var before = _ledger.Get(RegistryUri, path)!;
        var target = await FindAsync("notes");

        var result = await Build().RenameAsync(target, "journal", CancellationToken.None);

        var moved = Path.Combine(SkillsDir, "journal");
        Assert.Equal(Manifest("journal", "my edit"), File.ReadAllText(Path.Combine(moved, "SKILL.md")));
        Assert.Contains("not yet pushed", Assert.Single(result.Copies).Note);
        var recorded = _ledger.Get(RegistryUri, moved)!;
        Assert.Equal(before.ContentHash, recorded.ContentHash);
        Assert.Equal("journal", recorded.Name);
        Assert.DoesNotContain(_api.Pushed, p => Encoding.UTF8.GetString(p.Files[0].Content).Contains("my edit"));
    }

    [Fact]
    public async Task Rename_leaves_a_folder_that_does_not_carry_the_old_name_where_it_is()
    {
        Publish("notes", 1, Manifest("notes"));
        var path = SyncedCopy("my-notes", "notes", 1, Manifest("notes"));
        var target = await FindAsync("notes");

        var result = await Build().RenameAsync(target, "journal", CancellationToken.None);

        var copy = Assert.Single(result.Copies);
        Assert.Equal(path, copy.Path);
        Assert.True(copy.ManifestRenamed);
        Assert.Equal("journal", _ledger.Get(RegistryUri, path)!.Name);
    }

    [Fact]
    public async Task Rename_the_registry_refuses_changes_nothing_here()
    {
        Publish("notes", 1, Manifest("notes"));
        var path = SyncedCopy("notes", "notes", 1, Manifest("notes"));
        _api.RenameError = "There is already a skill named 'journal' here.";
        var target = await FindAsync("notes");

        var result = await Build().RenameAsync(target, "journal", CancellationToken.None);

        Assert.Equal(_api.RenameError, result.Error);
        Assert.Empty(result.Copies);
        Assert.Empty(_api.Pushed);
        Assert.Equal(Manifest("notes"), File.ReadAllText(Path.Combine(path, "SKILL.md")));
        Assert.Equal("notes", _ledger.Get(RegistryUri, path)!.Name);
    }

    [Fact]
    public async Task Rename_of_a_skill_whose_SKILL_md_declares_no_name_pushes_nothing()
    {
        const string text = "---\ndescription: d\n---\nbody";
        Publish("notes", 1, text);
        SyncedCopy("notes", "notes", 1, text);
        var target = await FindAsync("notes");

        var result = await Build().RenameAsync(target, "journal", CancellationToken.None);

        Assert.Null(result.Push);
        Assert.Empty(_api.Pushed);
        var moved = Path.Combine(SkillsDir, "journal");
        Assert.Equal(1, _ledger.Get(RegistryUri, moved)!.Version);
    }

    private sealed class Tokens : IAccessTokenProvider
    {
        public Task<StoredToken?> GetSessionAsync(Uri registry, CancellationToken cancellationToken) =>
            Task.FromResult<StoredToken?>(new StoredToken { Registry = Registry, AccessToken = "token", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) });
    }

    /// <summary>Resolves every folder to <see cref="ProjectId"/>.</summary>
    [Fact]
    public async Task Rename_finds_a_copy_recorded_under_the_project_a_link_moved_the_skill_out_of()
    {
        var old = Guid.NewGuid();
        var repo = Guid.NewGuid();
        _projects.ProjectId = repo;
        var text = Manifest("notes");
        Publish("notes", 1, text, repo, "repo");
        var path = SyncedCopy("notes", "notes", 1, text, old);

        var target = await FindAsync("notes", projectStart: _root);
        var renamed = await Build().RenameAsync(target, "journal", CancellationToken.None);

        Assert.Equal(path, Assert.Single(target.Copies).Path);
        Assert.Equal(Path.Combine(SkillsDir, "journal"), Assert.Single(renamed.Copies).Path);
        var recorded = _ledger.Get(RegistryUri, Path.Combine(SkillsDir, "journal"))!;
        Assert.Equal(("journal", (Guid?)repo), (recorded.Name, recorded.ProjectId));
    }

    private sealed class HereProjects : IProjectApi
    {
        public Guid? ProjectId { get; set; }

        public Task<ApiResult<RemoteProject>> ResolveProjectAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
            Task.FromResult(ApiResult<RemoteProject>.Success(new RemoteProject { ProjectId = ProjectId, Name = "repo" }));

        public Task<ApiResult<ProjectLinkChange>> LinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, string project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<ProjectLinkChange>> UnlinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
