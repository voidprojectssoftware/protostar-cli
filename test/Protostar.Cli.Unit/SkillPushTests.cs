using System.Net;
using System.Text;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <see cref="ISkillPushService"/> with in-memory fakes for discovery, session, and registry.</summary>
public sealed class SkillPushServiceTests : IDisposable
{
    private const string Registry = "https://registry.test";
    private readonly string _root = Directory.CreateTempSubdirectory("pstar-push-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private DiscoveredSkill Skill(string name, SkillScope scope = SkillScope.Global, string? dir = null)
    {
        var path = Path.Combine(_root, dir ?? $"{scope}-{name}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), $"---\nname: {name}\n---\nbody");
        var projectRoot = scope == SkillScope.Project ? Path.Combine(_root, "repo") : null;
        return new DiscoveredSkill { Name = name, Scope = scope, Path = path, ProjectRoot = projectRoot };
    }

    private static StoredToken Session(bool expired = false) => new()
    {
        Registry = Registry,
        AccessToken = "token-1",
        ExpiresAtUtc = expired ? DateTimeOffset.UtcNow.AddMinutes(-5) : DateTimeOffset.UtcNow.AddHours(1),
    };

    private static SkillPushRequest Request(params string[] names) => new(names, null, null, null, Registry);

    private readonly FakeLedger _ledger = new();
    private readonly FakeFacts _facts = new();

    private (ISkillPushService Sut, FakeRegistryApi Api) Build(
        IEnumerable<DiscoveredSkill> skills, StoredToken? session = null, FakeRegistryApi? api = null)
    {
        api ??= new FakeRegistryApi();
        var sut = new SkillPushService(new FakeSkillService(skills.ToList()), new FakeTokens(session), api, _ledger, _facts, new FakeMachine());
        return (sut, api);
    }

    [Fact]
    public async Task A_successful_push_is_recorded_and_the_next_run_skips_the_network()
    {
        var (sut, api) = Build([Skill("alpha")], Session());

        await sut.PushAsync(Request(), CancellationToken.None);
        var second = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Single(api.Pushed);
        Assert.Equal(1, api.MetaCalls);
        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Unchanged, 1, null), Assert.Single(second.Entries).Outcome);
    }

    [Fact]
    public async Task Unchanged_by_the_ledger_needs_no_session()
    {
        var skill = Skill("alpha");
        var (seed, _) = Build([skill], Session());
        await seed.PushAsync(Request(), CancellationToken.None);
        var (sut, api) = Build([skill], session: null);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.None, result.Failure);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task A_changed_skill_is_uploaded_again()
    {
        var skill = Skill("alpha");
        var (sut, api) = Build([skill], Session());
        await sut.PushAsync(Request(), CancellationToken.None);

        File.WriteAllText(Path.Combine(skill.Path, "SKILL.md"), "---\nname: alpha\n---\nedited");
        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task Force_uploads_even_when_the_ledger_matches()
    {
        var (sut, api) = Build([Skill("alpha")], Session());
        await sut.PushAsync(Request(), CancellationToken.None);

        await sut.PushAsync(Request() with { Force = true }, CancellationToken.None);

        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task A_refused_push_is_not_recorded()
    {
        var api = new FakeRegistryApi();
        api.Outcomes["alpha"] = new SkillPushOutcome(SkillPushStatus.Refused, null, "nope");
        var (sut, _) = Build([Skill("alpha")], Session(), api);

        await sut.PushAsync(Request(), CancellationToken.None);
        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task Pushes_every_discovered_skill_when_no_names_are_given()
    {
        var (sut, api) = Build([Skill("alpha"), Skill("beta")], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.None, result.Failure);
        Assert.True(result.Succeeded);
        Assert.Equal(["alpha", "beta"], api.Pushed.Select(p => p.Name));
        Assert.All(api.Pushed, p => Assert.Equal("token-1", p.Token));
        Assert.All(api.Pushed, p => Assert.Equal("SKILL.md", Assert.Single(p.Files).RelativePath));
    }

    [Fact]
    public async Task Pushes_only_the_named_skill_case_insensitively()
    {
        var (sut, api) = Build([Skill("alpha"), Skill("beta")], Session());

        var result = await sut.PushAsync(Request("BETA"), CancellationToken.None);

        Assert.Equal("beta", Assert.Single(result.Entries).Skill.Name);
        Assert.Equal("beta", Assert.Single(api.Pushed).Name);
    }

    [Fact]
    public async Task Unknown_skill_name_fails_before_any_upload()
    {
        var (sut, api) = Build([Skill("alpha")], Session());

        var result = await sut.PushAsync(Request("alpha", "ghost"), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.SkillNotFound, result.Failure);
        Assert.Equal("ghost", result.Detail);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task No_session_reports_not_logged_in()
    {
        var (sut, api) = Build([Skill("alpha")], session: null);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.NotLoggedIn, result.Failure);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Expired_session_that_could_not_refresh_reports_session_expired()
    {
        var (sut, api) = Build([Skill("alpha")], Session(expired: true));

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.SessionExpired, result.Failure);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Unreachable_registry_fails_before_any_upload()
    {
        var (sut, api) = Build([Skill("alpha")], Session(), new FakeRegistryApi { Meta = null });

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.RegistryUnreachable, result.Failure);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Registry_on_another_api_major_is_incompatible()
    {
        var api = new FakeRegistryApi { Meta = new RegistryMeta { ApiMajors = [2] } };
        var (sut, _) = Build([Skill("alpha")], Session(), api);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.Incompatible, result.Failure);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task Invalid_registry_url_is_reported()
    {
        var (sut, _) = Build([Skill("alpha")], Session());

        var result = await sut.PushAsync(new SkillPushRequest([], null, null, null, "not a url"), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.InvalidRegistry, result.Failure);
    }

    [Fact]
    public async Task A_refused_skill_does_not_stop_the_others_and_the_run_is_not_successful()
    {
        var api = new FakeRegistryApi();
        api.Outcomes["alpha"] = new SkillPushOutcome(SkillPushStatus.Refused, null, "too large");
        var (sut, _) = Build([Skill("alpha"), Skill("beta")], Session(), api);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(2, api.Pushed.Count);
        Assert.Equal(SkillPushStatus.Refused, result.Entries[0].Outcome.Status);
        Assert.Equal(SkillPushStatus.Created, result.Entries[1].Outcome.Status);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Second_skill_of_the_same_scope_and_name_with_identical_content_is_skipped()
    {
        var first = Skill("alpha", SkillScope.Global, "one-alpha");
        var second = Skill("alpha", SkillScope.Global, "two-alpha");
        var (sut, api) = Build([first, second], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Single(api.Pushed);
        Assert.Equal(SkillPushStatus.Skipped, result.Entries[1].Outcome.Status);
        Assert.Contains("Identical", result.Entries[1].Outcome.Message);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Second_skill_of_the_same_scope_and_name_with_different_content_is_a_conflict()
    {
        var first = Skill("alpha", SkillScope.Global, "one-alpha");
        var second = Skill("alpha", SkillScope.Global, "two-alpha");
        File.WriteAllText(Path.Combine(second.Path, "SKILL.md"), "---\nname: alpha\n---\nanother harness's version");
        var (sut, api) = Build([first, second], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var uploaded = Encoding.UTF8.GetString(Assert.Single(api.Pushed).Files[0].Content);
        Assert.EndsWith("body", uploaded);
        Assert.Equal(SkillPushStatus.Conflict, result.Entries[1].Outcome.Status);
        Assert.Contains(first.Path, result.Entries[1].Outcome.Message);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Same_name_conflict_is_detected_even_when_the_first_copy_is_unchanged_by_the_ledger()
    {
        var first = Skill("alpha", SkillScope.Global, "one-alpha");
        var (seed, _) = Build([first], Session());
        await seed.PushAsync(Request(), CancellationToken.None);
        var second = Skill("alpha", SkillScope.Global, "two-alpha");
        File.WriteAllText(Path.Combine(second.Path, "SKILL.md"), "different");
        var (sut, api) = Build([first, second], session: null);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Empty(api.Pushed);
        Assert.Equal(SkillPushStatus.Unchanged, result.Entries[0].Outcome.Status);
        Assert.Equal(SkillPushStatus.Conflict, result.Entries[1].Outcome.Status);
    }

    [Fact]
    public async Task A_global_and_a_project_skill_of_the_same_name_are_both_pushed_each_in_its_own_scope()
    {
        var global = Skill("deploy", SkillScope.Global);
        var project = Skill("deploy", SkillScope.Project);
        File.WriteAllText(Path.Combine(project.Path, "SKILL.md"), "---\nname: deploy\n---\nthe project's own version");
        var (sut, api) = Build([global, project], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, api.Pushed.Count);
        Assert.Null(api.Pushed[0].Project);
        Assert.NotNull(api.Pushed[1].Project);
    }

    [Fact]
    public async Task A_project_skill_is_pushed_with_its_project_folders_facts_and_this_machines_id()
    {
        var project = Skill("deploy", SkillScope.Project);
        var (sut, api) = Build([project], Session());

        await sut.PushAsync(Request(), CancellationToken.None);

        var sent = Assert.Single(api.Pushed).Project!;
        Assert.Equal(FakeMachine.Id, sent.MachineId);
        Assert.Equal(project.ProjectRoot, sent.Facts.WorkingDirectory);
        Assert.Equal([project.ProjectRoot!], _facts.Asked);
    }

    [Fact]
    public async Task A_global_skill_is_pushed_without_project_facts_and_resolves_nothing()
    {
        var (sut, api) = Build([Skill("deploy")], Session());

        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Null(Assert.Single(api.Pushed).Project);
        Assert.Empty(_facts.Asked);
    }

    [Fact]
    public async Task A_project_skill_recorded_as_global_by_an_older_cli_is_pushed_again_as_the_projects()
    {
        var project = Skill("deploy", SkillScope.Project);
        _ledger.Record(new Uri(Registry), project.Path, new SkillLedgerEntry(SkillFileReader.Hash(SkillFileReader.Read(project.Path)), 1, "deploy"));
        var (sut, api) = Build([project], Session());

        await sut.PushAsync(Request(), CancellationToken.None);
        var again = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.NotNull(Assert.Single(api.Pushed).Project);
        Assert.NotNull(_ledger.Get(new Uri(Registry), project.Path)!.ProjectId);
        Assert.Equal(SkillPushStatus.Unchanged, Assert.Single(again.Entries).Outcome.Status);
    }

    [Fact]
    public async Task The_capture_hooks_request_backs_up_both_the_global_and_the_project_skill_of_the_used_name()
    {
        // The hook only knows the skill's name and the session's cwd (see CaptureService), so it asks for the
        // name with the cwd as the project start, and each local copy goes to its own registry skill.
        var global = Skill("deploy", SkillScope.Global);
        var project = Skill("deploy", SkillScope.Project);
        File.WriteAllText(Path.Combine(project.Path, "SKILL.md"), "---\nname: deploy\n---\nproject");
        var (sut, api) = Build([global, project, Skill("other")], Session());

        var result = await sut.PushAsync(new SkillPushRequest(["deploy"], null, null, _root, Registry), CancellationToken.None);

        Assert.Equal([SkillScope.Global, SkillScope.Project], result.Entries.Select(e => e.Skill.Scope));
        Assert.All(result.Entries, e => Assert.Equal(SkillPushStatus.Created, e.Outcome.Status));
        Assert.Equal(2, api.Pushed.Count);
    }

    [Fact]
    public async Task No_skills_found_succeeds_without_contacting_the_registry()
    {
        var (sut, api) = Build([], session: null);

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal(SkillPushRunFailure.None, result.Failure);
        Assert.Empty(result.Entries);
        Assert.Equal(0, api.MetaCalls);
    }

    [Fact]
    public async Task A_file_with_a_padded_name_fails_the_skill_before_upload_with_a_clear_message()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows strips a trailing space from a file name, so such a file cannot exist there.

        var skill = Skill("alpha");
        File.WriteAllText(Path.Combine(skill.Path, "notes.md "), "x");
        var (sut, api) = Build([skill], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        var outcome = Assert.Single(result.Entries).Outcome;
        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("starts or ends with whitespace", outcome.Message);
        Assert.Empty(api.Pushed);
    }

    [Fact]
    public async Task A_successful_push_records_the_skill_name_in_the_ledger()
    {
        var skill = Skill("alpha");
        var (sut, _) = Build([skill], Session());

        await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Equal("alpha", _ledger.Get(new Uri(Registry), skill.Path)!.Name);
    }

    /// <summary>Facts for a folder outside git, recording which folders were asked about.</summary>
    internal sealed class FakeFacts : IProjectFactsResolver
    {
        public List<string> Asked { get; } = [];

        public ProjectFacts Resolve(string workingDirectory)
        {
            Asked.Add(workingDirectory);
            return ProjectFacts.NotGit(workingDirectory, workingDirectory);
        }
    }

    internal sealed class FakeMachine : IMachineIdentity
    {
        public static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        public MachineInfo Get() => new(Id, "test", "linux");

        public bool IsRegisteredWith(Uri registry) => true;

        public void MarkRegistered(Uri registry)
        {
        }
    }

    internal sealed class FakeLedger : ISkillLedger
    {
        private readonly Dictionary<string, SkillLedgerEntry> _entries = [];

        public SkillLedgerEntry? Get(Uri registry, string skillPath) => _entries.GetValueOrDefault(registry + "|" + skillPath);

        public void Record(Uri registry, string skillPath, SkillLedgerEntry entry) => _entries[registry + "|" + skillPath] = entry;

        public IReadOnlyDictionary<string, SkillLedgerEntry> Entries(Uri registry) =>
            _entries.Where(e => e.Key.StartsWith(registry + "|", StringComparison.Ordinal))
                .ToDictionary(e => e.Key[(registry + "|").Length..], e => e.Value);

        public void Forget(Uri registry, string skillPath) => _entries.Remove(registry + "|" + skillPath);
    }

    private sealed class FakeSkillService(IReadOnlyList<DiscoveredSkill> skills) : ISkillService
    {
        public SkillDiscoveryResult Discover(string? harnessId, string? harnessHome, string? projectStart) =>
            new(skills, SkillQueryFailure.None, null);
    }

    private sealed class FakeTokens(StoredToken? session) : IAccessTokenProvider
    {
        public Task<StoredToken?> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(session);
    }

    internal sealed class FakeRegistryApi : IRegistryApi
    {
        public RegistryMeta? Meta { get; init; } = new() { ApiMajors = [1] };
        public Dictionary<string, SkillPushOutcome> Outcomes { get; } = [];
        public List<(string Name, string Token, IReadOnlyList<SkillUploadFile> Files, SkillProject? Project)> Pushed { get; } = [];
        public int MetaCalls { get; private set; }

        public Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken)
        {
            MetaCalls++;
            return Task.FromResult(Meta);
        }

        public Task<SkillPushOutcome> PushSkillAsync(
            Uri registry, string accessToken, string name, IReadOnlyList<SkillUploadFile> files, SkillProject? project, CancellationToken cancellationToken)
        {
            Pushed.Add((name, accessToken, files, project));
            var projectId = project is null ? (Guid?)null : ProjectIdOf(project.Facts);
            return Task.FromResult(Outcomes.GetValueOrDefault(name) ?? new SkillPushOutcome(SkillPushStatus.Created, 1, null, projectId));
        }

        /// <summary>A stable project id for a folder: by root commit for git, else by folder, as the registry keys them.</summary>
        public static Guid ProjectIdOf(ProjectFacts facts) =>
            new(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(facts.RootCommit ?? facts.Directory)));

        /// <summary>The key <see cref="Contents"/> holds a skill under: its name, or <c>&lt;project id&gt;/&lt;name&gt;</c>.</summary>
        public static string Key(string name, Guid? projectId) => projectId is { } id ? $"{id}/{name}" : name;

        public List<RegistrySkill> Skills { get; } = [];
        public string? ListError { get; init; }
        public Dictionary<string, RegistrySkillContent> Contents { get; } = new(StringComparer.Ordinal);
        public List<string> Downloaded { get; } = [];

        public Task<RegistrySkillList> ListSkillsAsync(Uri registry, string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(ListError is null ? new RegistrySkillList(Skills, null) : new RegistrySkillList(null, ListError));

        /// <summary>Earlier versions, served when a download names a version: keyed <c>&lt;key&gt;@&lt;version&gt;</c>.</summary>
        public Dictionary<string, RegistrySkillContent> History { get; } = new(StringComparer.Ordinal);

        public Task<RegistrySkillDownload> GetSkillFilesAsync(
            Uri registry, string accessToken, string name, Guid? projectId, int? version, CancellationToken cancellationToken)
        {
            Downloaded.Add(Key(name, projectId));
            if (version is { } v && History.TryGetValue($"{Key(name, projectId)}@{v}", out var earlier))
                return Task.FromResult(new RegistrySkillDownload(earlier, null));
            return Task.FromResult(Contents.TryGetValue(Key(name, projectId), out var content) && (version is null || content.Version == version)
                ? new RegistrySkillDownload(content, null)
                : new RegistrySkillDownload(null, "not found"));
        }

        /// <summary>Each rename asked for: (old key, new name).</summary>
        public List<(string Key, string NewName)> Renamed { get; } = [];

        /// <summary>When set, every rename is refused with this message.</summary>
        public string? RenameError { get; set; }

        /// <summary>Each delete asked for, by key.</summary>
        public List<string> Deleted { get; } = [];

        public Task<ApiResult<string>> RenameSkillAsync(
            Uri registry, string accessToken, string name, Guid? projectId, string newName, CancellationToken cancellationToken)
        {
            Renamed.Add((Key(name, projectId), newName));
            if (RenameError is { } error)
                return Task.FromResult(ApiResult<string>.Fail(error));

            // Like the registry: the same skill, versions and content, under the new name.
            var index = Skills.FindIndex(s => s.Name == name && s.ProjectId == projectId);
            if (index >= 0)
                Skills[index] = Skills[index] with { Name = newName };
            if (Contents.Remove(Key(name, projectId), out var content))
                Contents[Key(newName, projectId)] = content with { Name = newName };
            return Task.FromResult(ApiResult<string>.Success(newName));
        }

        public Task<ApiResult<bool>> DeleteSkillAsync(Uri registry, string accessToken, string name, Guid? projectId, CancellationToken cancellationToken)
        {
            Deleted.Add(Key(name, projectId));
            Skills.RemoveAll(s => s.Name == name && s.ProjectId == projectId);
            Contents.Remove(Key(name, projectId));
            return Task.FromResult(ApiResult<bool>.Success(true));
        }
    }
}

/// <summary>Tests for <see cref="SkillFileReader"/> against a real temporary directory.</summary>
public sealed class SkillFileReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pstar-read-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content = "x")
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Reads_nested_files_with_forward_slash_paths_in_ordinal_order()
    {
        Write("SKILL.md", "hello");
        Write(Path.Combine("scripts", "run.sh"));
        Write(Path.Combine("references", "deep", "a.md"));

        var files = SkillFileReader.Read(_root);

        Assert.Equal(["SKILL.md", "references/deep/a.md", "scripts/run.sh"], files.Select(f => f.RelativePath));
        Assert.Equal("hello", Encoding.UTF8.GetString(files[0].Content));
    }

    [Fact]
    public void Hash_is_stable_and_changes_when_a_file_moves_or_changes()
    {
        Write("SKILL.md", "a");
        Write("b.md", "b");
        var first = SkillFileReader.Hash(SkillFileReader.Read(_root));
        Assert.Equal(first, SkillFileReader.Hash(SkillFileReader.Read(_root)));

        File.Move(Path.Combine(_root, "b.md"), Path.Combine(_root, "c.md"));
        var moved = SkillFileReader.Hash(SkillFileReader.Read(_root));
        Write("c.md", "changed");
        var changed = SkillFileReader.Hash(SkillFileReader.Read(_root));

        Assert.NotEqual(first, moved);
        Assert.NotEqual(moved, changed);
    }

    [Fact]
    public void Skips_version_control_and_dependency_folders()
    {
        Write("SKILL.md");
        Write(Path.Combine(".git", "HEAD"));
        Write(Path.Combine("node_modules", "pkg", "index.js"));

        var files = SkillFileReader.Read(_root);

        Assert.Equal("SKILL.md", Assert.Single(files).RelativePath);
    }

    [Fact]
    public void Skips_a_file_with_an_ignored_name_such_as_a_submodule_git_file()
    {
        // The registry refuses these names at push, so reading one would fail the whole skill.
        Write("SKILL.md");
        Write(".git", "gitdir: ../.git/modules/alpha");
        Write(Path.Combine("lib", "node_modules"), "x");

        var files = SkillFileReader.Read(_root);

        Assert.Equal("SKILL.md", Assert.Single(files).RelativePath);
    }

    [Fact]
    public void Does_not_follow_symbolic_links_below_the_root()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows needs elevation or developer mode.

        Write("SKILL.md");
        var outside = Directory.CreateTempSubdirectory("pstar-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "nope");
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
            File.CreateSymbolicLink(Path.Combine(_root, "file-link.txt"), Path.Combine(outside, "secret.txt"));

            var files = SkillFileReader.Read(_root);

            Assert.Equal("SKILL.md", Assert.Single(files).RelativePath);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}

/// <summary>Tests for <see cref="RegistryApi"/>'s request shape and response mapping, over a stub handler.</summary>
public sealed class RegistryApiTests
{
    private static readonly Uri Registry = new("https://registry.test");

    private static readonly IReadOnlyList<SkillUploadFile> Files =
        [new("SKILL.md", "---\nname: alpha\n---"u8.ToArray()), new("scripts/run.sh", "echo"u8.ToArray())];

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    [Fact]
    public async Task Push_sends_bearer_token_name_field_and_one_part_per_file()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, """{"status":"created","version":3}"""));
        using var api = new RegistryApi(handler);

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Created, 3, null), outcome);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://registry.test/v1/skills", handler.Uri!.ToString());
        Assert.Equal("Bearer tok", handler.Authorization);
        Assert.Contains("name=name", handler.Body);
        Assert.Contains("filename=SKILL.md", handler.Body);
        Assert.Matches("filename=\"?scripts/run.sh\"?", handler.Body);
        Assert.Contains("echo", handler.Body);
    }

    [Fact]
    public async Task A_project_push_sends_the_project_folder_as_a_json_field_and_reads_back_the_project()
    {
        var projectId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, $$"""{"status":"created","version":1,"projectId":"{{projectId}}","projectName":"github.com/acme/a"}"""));
        using var api = new RegistryApi(handler);
        var machine = Guid.NewGuid();
        var facts = ProjectFacts.NotGit("/w/a", "/w/a");

        var outcome = await api.PushSkillAsync(Registry, "tok", "deploy", Files, new SkillProject(machine, facts), CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Created, 1, null, projectId, "github.com/acme/a"), outcome);
        Assert.Contains("name=project", handler.Body);
        Assert.Contains($"\"machineId\":\"{machine}\"", handler.Body);
        Assert.Contains("\"workingDirectory\":\"/w/a\"", handler.Body);
    }

    [Fact]
    public async Task A_global_push_sends_no_project_field()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, """{"status":"created","version":1}"""));
        using var api = new RegistryApi(handler);

        await api.PushSkillAsync(Registry, "tok", "deploy", Files, null, CancellationToken.None);

        Assert.DoesNotContain("name=project", handler.Body);
    }

    [Fact]
    public async Task Listing_asks_for_project_skills_too_and_reads_their_project()
    {
        var projectId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $$"""{"skills":[{"name":"deploy","version":1,"contentHash":"AB","updatedAt":"2026-11-19T00:00:00Z","projectId":null},{"name":"deploy","version":2,"contentHash":"CD","updatedAt":"2026-11-19T00:00:00Z","projectId":"{{projectId}}","projectName":"a"}]}"""));
        using var api = new RegistryApi(handler);

        var list = await api.ListSkillsAsync(Registry, "tok", CancellationToken.None);

        Assert.Equal("https://registry.test/v1/skills?includeProjects=true", handler.Uri!.ToString());
        Assert.Equal([null, projectId], list.Skills!.Select(s => s.ProjectId));
        Assert.Equal("a", list.Skills![1].ProjectName);
    }

    [Fact]
    public async Task Listing_sessions_since_a_local_time_sends_it_in_utc()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "[]"));
        using var api = new RegistryApi(handler);
        var since = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-6));

        await api.ListAsync(Registry, "tok", new TranscriptListQuery(null, null, null, since, 25), CancellationToken.None);

        Assert.Equal("since=2026-11-01T06:00:00.0000000+00:00", Uri.UnescapeDataString(handler.Uri!.Query.Split('&')[1]));
    }

    [Fact]
    public async Task Downloading_a_project_skill_names_its_project()
    {
        var projectId = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, "{}"));
        using var api = new RegistryApi(handler);

        await api.GetSkillFilesAsync(Registry, "tok", "deploy", projectId, 3, CancellationToken.None);

        Assert.Equal($"https://registry.test/v1/skills/deploy/files?projectId={projectId}&version=3", handler.Uri!.ToString());
    }

    [Fact]
    public async Task Ok_maps_to_unchanged_with_the_current_version()
    {
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.OK, """{"status":"unchanged","version":2}""")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Unchanged, 2, null), outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Only a skill's creator can push new versions of it.")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "The skill exceeds 10 MB.")]
    [InlineData(HttpStatusCode.BadRequest, "The skill is missing a SKILL.md at its root.")]
    public async Task Client_errors_map_to_refused_with_the_problem_title(HttpStatusCode status, string title)
    {
        var body = $$"""{"title":"{{title}}","status":{{(int)status}}}""";
        using var api = new RegistryApi(new StubHandler(_ => Json(status, body, "application/problem+json")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Refused, null, title), outcome);
    }

    [Fact]
    public async Task Unauthorized_is_a_failure_pointing_at_login()
    {
        using var api = new RegistryApi(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("auth login", outcome.Message);
    }

    [Fact]
    public async Task Server_error_is_a_failure()
    {
        using var api = new RegistryApi(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("502", outcome.Message);
    }

    [Fact]
    public async Task Network_error_is_a_failure_not_an_exception()
    {
        using var api = new RegistryApi(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, null, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("connection refused", outcome.Message);
    }

    [Fact]
    public async Task Rename_posts_the_new_name_scoped_to_the_project_and_reads_back_the_name()
    {
        var project = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"oldName":"my notes","name":"journal","projectId":null}"""));
        using var api = new RegistryApi(handler);

        var result = await api.RenameSkillAsync(Registry, "tok", "my notes", project, "journal", CancellationToken.None);

        Assert.Equal("journal", result.Value);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"https://registry.test/v1/skills/my%20notes/rename?projectId={project}", handler.Uri!.AbsoluteUri);
        Assert.Contains("\"newName\":\"journal\"", handler.Body);
    }

    [Fact]
    public async Task A_refused_rename_carries_the_registrys_reason()
    {
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.Conflict, """{"title":"There is already a skill named 'journal' here."}""", "application/problem+json")));

        var result = await api.RenameSkillAsync(Registry, "tok", "notes", null, "journal", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("already a skill named 'journal'", result.Error);
    }

    [Fact]
    public async Task Delete_sends_a_delete_for_the_global_skill_when_there_is_no_project()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var api = new RegistryApi(handler);

        var result = await api.DeleteSkillAsync(Registry, "tok", "journal", null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal("https://registry.test/v1/skills/journal", handler.Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_link_reads_the_skills_that_moved()
    {
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.OK, """{"outcome":"linked","project":{"name":"repo"},"movedSkills":["deploy","notes"]}""")));

        var result = await api.LinkFolderAsync(Registry, "tok", Guid.NewGuid(), ProjectFacts.NotGit("/x", "/x"), "repo", CancellationToken.None);

        Assert.Equal(["deploy", "notes"], result.Value!.MovedSkills);
    }

    [Fact]
    public async Task Meta_is_parsed_and_an_unreachable_registry_gives_null()
    {
        using var ok = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.OK, """{"service":"protostar-registry","apiMajors":[1]}""")));
        using var down = new RegistryApi(new StubHandler(_ => throw new HttpRequestException("down")));

        var meta = await ok.GetMetaAsync(Registry, CancellationToken.None);
        Assert.Equal(new[] { 1 }, meta?.ApiMajors);
        Assert.Null(await down.GetMetaAsync(Registry, CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
