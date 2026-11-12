using System.Net;
using System.Text;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
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
        return new DiscoveredSkill { Name = name, Scope = scope, Path = path };
    }

    private static StoredToken Session(bool expired = false) => new()
    {
        Registry = Registry,
        AccessToken = "token-1",
        ExpiresAtUtc = expired ? DateTimeOffset.UtcNow.AddMinutes(-5) : DateTimeOffset.UtcNow.AddHours(1),
    };

    private static SkillPushRequest Request(params string[] names) => new(names, null, null, null, Registry);

    private readonly FakeLedger _ledger = new();

    private (ISkillPushService Sut, FakeRegistryApi Api) Build(
        IEnumerable<DiscoveredSkill> skills, StoredToken? session = null, FakeRegistryApi? api = null)
    {
        api ??= new FakeRegistryApi();
        var sut = new SkillPushService(new FakeSkillService(skills.ToList()), new FakeTokens(session), api, _ledger);
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
    public async Task Second_skill_with_the_same_name_is_skipped_not_pushed()
    {
        var global = Skill("alpha", SkillScope.Global);
        var project = Skill("alpha", SkillScope.Project);
        var (sut, api) = Build([global, project], Session());

        var result = await sut.PushAsync(Request(), CancellationToken.None);

        Assert.Single(api.Pushed);
        Assert.Equal(SkillPushStatus.Skipped, result.Entries[1].Outcome.Status);
        Assert.True(result.Succeeded);
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

    internal sealed class FakeLedger : ISkillLedger
    {
        private readonly Dictionary<string, SkillLedgerEntry> _entries = [];

        public SkillLedgerEntry? Get(Uri registry, string skillPath) => _entries.GetValueOrDefault(registry + "|" + skillPath);

        public void Record(Uri registry, string skillPath, SkillLedgerEntry entry) => _entries[registry + "|" + skillPath] = entry;
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
        public List<(string Name, string Token, IReadOnlyList<SkillUploadFile> Files)> Pushed { get; } = [];
        public int MetaCalls { get; private set; }

        public Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken)
        {
            MetaCalls++;
            return Task.FromResult(Meta);
        }

        public Task<SkillPushOutcome> PushSkillAsync(
            Uri registry, string accessToken, string name, IReadOnlyList<SkillUploadFile> files, CancellationToken cancellationToken)
        {
            Pushed.Add((name, accessToken, files));
            return Task.FromResult(Outcomes.GetValueOrDefault(name) ?? new SkillPushOutcome(SkillPushStatus.Created, 1, null));
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

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, CancellationToken.None);

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
    public async Task Ok_maps_to_unchanged_with_the_current_version()
    {
        using var api = new RegistryApi(new StubHandler(_ => Json(HttpStatusCode.OK, """{"status":"unchanged","version":2}""")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, CancellationToken.None);

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

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, CancellationToken.None);

        Assert.Equal(new SkillPushOutcome(SkillPushStatus.Refused, null, title), outcome);
    }

    [Fact]
    public async Task Unauthorized_is_a_failure_pointing_at_login()
    {
        using var api = new RegistryApi(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("auth login", outcome.Message);
    }

    [Fact]
    public async Task Server_error_is_a_failure()
    {
        using var api = new RegistryApi(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("502", outcome.Message);
    }

    [Fact]
    public async Task Network_error_is_a_failure_not_an_exception()
    {
        using var api = new RegistryApi(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var outcome = await api.PushSkillAsync(Registry, "tok", "alpha", Files, CancellationToken.None);

        Assert.Equal(SkillPushStatus.Failed, outcome.Status);
        Assert.Contains("connection refused", outcome.Message);
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
