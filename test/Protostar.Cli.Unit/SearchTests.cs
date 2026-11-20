using System.Net;
using System.Text;
using Protostar.Cli.Auth;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Search;
using Protostar.Cli.Sessions;
using Xunit;
using static Protostar.Cli.Unit.SkillPushServiceTests;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <c>protostar search</c>: <see cref="SearchService"/>, the registry client and the snippet renderer.</summary>
public sealed class SearchTests
{
    private const string Registry = "https://registry.test";
    private static readonly Guid ProjectId = Guid.Parse("c508803e-edf0-4ec8-ae08-234abf9f3748");

    private readonly FakeSearchApi _search = new();
    private readonly FakeProjects _projects = new();
    private RegistryMeta? _meta = new() { ApiMajors = [1], Capabilities = ["sessions", "memory", "search"] };
    private StoredToken? _token = new() { Registry = Registry, AccessToken = "tok", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };

    private SearchService Sut() => new(
        new FakeTokens(SessionLookups.Of(_token)), new FakeRegistryApi { Meta = _meta }, _search, new NotGitResolver(), _projects, new FakeMachine());

    private static SearchRequest Request(string? project = null, string? kind = null) =>
        new(Registry, "parentUuid ordering", kind, project, 20, "/work/demo-app");

    [Fact]
    public async Task A_search_sends_the_query_kind_project_and_limit_and_returns_the_hits()
    {
        var result = await Sut().SearchAsync(Request("demo", "memory"), CancellationToken.None);

        Assert.Equal(SessionSyncFailure.None, result.Failure);
        Assert.Single(result.Hits);
        Assert.Equal(new SearchQuery("parentUuid ordering", "memory", "demo", 20), _search.Last);
    }

    [Fact]
    public async Task A_registry_that_lists_capabilities_without_search_is_refused_without_searching()
    {
        _meta = new() { ApiMajors = [1], Capabilities = ["sessions", "memory"] };

        var result = await Sut().SearchAsync(Request(), CancellationToken.None);

        Assert.Equal(SessionSyncFailure.Incompatible, result.Failure);
        Assert.Equal(ISearchApi.RegistryTooOldForSearch, result.Detail);
        Assert.Equal(SessionsJson.RegistryIncompatible, SessionsJson.Code(result.Failure));
        Assert.Null(_search.Last);
    }

    [Fact]
    public async Task A_registry_that_lists_no_capabilities_is_asked_and_its_404_means_too_old()
    {
        _meta = new() { ApiMajors = [1] };
        _search.Error = ISearchApi.RegistryTooOldForSearch;

        var result = await Sut().SearchAsync(Request(), CancellationToken.None);

        Assert.NotNull(_search.Last);
        Assert.Equal(SessionSyncFailure.Incompatible, result.Failure);
        Assert.Equal(ISearchApi.RegistryTooOldForSearch, result.Detail);
    }

    [Fact]
    public async Task Any_other_refusal_is_reported_as_the_registry_failing()
    {
        _search.Error = "The registry returned 500.";

        var result = await Sut().SearchAsync(Request(), CancellationToken.None);

        Assert.Equal(SessionSyncFailure.RegistryUnreachable, result.Failure);
        Assert.Equal("The registry returned 500.", result.Detail);
    }

    [Fact]
    public async Task Signed_out_fails_before_asking_the_registry_anything()
    {
        _token = null;

        var result = await Sut().SearchAsync(Request(), CancellationToken.None);

        Assert.Equal(SessionSyncFailure.NotLoggedIn, result.Failure);
        Assert.Null(_search.Last);
    }

    [Fact]
    public async Task Dot_searches_the_project_this_folder_resolves_to_by_its_id()
    {
        _projects.Resolved = new RemoteProject { ProjectId = ProjectId, Name = "github.com/example/demo-app" };

        var result = await Sut().SearchAsync(Request("."), CancellationToken.None);

        Assert.Equal(ProjectId.ToString(), _search.Last!.Project);
        Assert.Equal("github.com/example/demo-app", result.FolderProject);
        Assert.Equal(Path.GetFullPath("/work/demo-app"), _projects.AskedFor);
    }

    [Fact]
    public async Task Dot_in_a_folder_with_no_project_yet_finds_nothing_without_searching()
    {
        _projects.Resolved = new RemoteProject { Name = "scratch" };

        var result = await Sut().SearchAsync(Request("."), CancellationToken.None);

        Assert.Equal(SessionSyncFailure.None, result.Failure);
        Assert.Empty(result.Hits);
        Assert.Null(_search.Last);
    }

    [Fact]
    public async Task A_project_name_is_passed_through_for_the_registry_to_match()
    {
        await Sut().SearchAsync(Request("example"), CancellationToken.None);

        Assert.Equal("example", _search.Last!.Project);
        Assert.Null(_projects.AskedFor);
    }

    [Fact]
    public async Task The_client_maps_a_bare_404_to_too_old_and_escapes_the_query()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "");
        var api = new RegistryApi(handler);

        var result = await api.SearchAsync(new Uri(Registry), "tok", new SearchQuery("\"a b\" -c&d", "session", "x y", 5), CancellationToken.None);

        Assert.Equal(ISearchApi.RegistryTooOldForSearch, result.Error);
        Assert.Equal("/v1/search?q=%22a%20b%22%20-c%26d&limit=5&kind=session&project=x%20y", handler.Asked!.PathAndQuery);
    }

    [Fact]
    public async Task The_client_reads_the_registrys_hits()
    {
        const string body = """
            [{"kind":"session","harness":"claude-code","key":"7d3c","parentKey":null,"title":"Fix replay","path":null,
              "projectId":"c508803e-edf0-4ec8-ae08-234abf9f3748","projectName":"demo","machineId":"178aa838-787d-4a5c-82e3-009c277ccec5",
              "machineName":"vm","date":"2026-11-19T10:03:00+00:00","snippet":"«parentUuid» ordering","rank":0.4}]
            """;
        var api = new RegistryApi(new StubHandler(HttpStatusCode.OK, body));

        var result = await api.SearchAsync(new Uri(Registry), "tok", new SearchQuery("q", null, null, 20), CancellationToken.None);

        var hit = Assert.Single(result.Value!);
        Assert.Equal(("session", "7d3c", "Fix replay", "demo"), (hit.Kind, hit.Key, hit.Title, hit.ProjectName));
        Assert.Equal("«parentUuid» ordering", hit.Snippet);
    }

    [Fact]
    public void A_snippet_loses_its_markers_and_keeps_which_runs_matched()
    {
        var runs = SearchPresenter.Segments("We chose «parentUuid» «ordering»:\nline\tby line", 200);

        Assert.Equal(
            [("We chose ", false), ("parentUuid", true), (" ", false), ("ordering", true), (": line by line", false)],
            runs);
    }

    [Fact]
    public void A_long_snippet_is_cut_with_an_ellipsis_and_markers_do_not_count_against_the_room()
    {
        var runs = SearchPresenter.Segments("«abcdef» ghijkl", 10);

        Assert.Equal([("abcdef", true), (" ...", false)], runs);
        Assert.Equal(10, runs.Sum(r => r.Text.Length));
    }

    [Fact]
    public void A_snippet_that_fits_exactly_is_not_cut()
    {
        Assert.Equal([("abc", true), ("de", false)], SearchPresenter.Segments("«abc»de", 5));
    }

    [Fact]
    public void An_unclosed_marker_highlights_the_rest_and_a_stray_one_is_dropped()
    {
        Assert.Equal([("a", false), ("b", false), ("c", true)], SearchPresenter.Segments("a»b«c", 50));
    }

    [Fact]
    public void A_json_record_names_a_session_by_id_and_leaves_absent_fields_out()
    {
        var hit = new SearchHit
        {
            Kind = "session", Harness = "claude-code", Key = "7d3c.agent-a1", ParentKey = "7d3c", ProjectName = "demo",
            MachineId = Guid.Empty, Snippet = "«x»",
        };

        var json = SearchPresenter.Record(hit).ToJsonString();

        Assert.Equal(
            """{"kind":"session","id":"7d3c.agent-a1","harness":"claude-code","parentId":"7d3c","project":"demo","machineId":"00000000-0000-0000-0000-000000000000","snippet":"«x»"}""",
            json.Replace("\\u00AB", "«", StringComparison.Ordinal).Replace("\\u00BB", "»", StringComparison.Ordinal));
    }

    private sealed class FakeSearchApi : ISearchApi
    {
        public string? Error { get; set; }
        public SearchQuery? Last { get; private set; }

        public Task<ApiResult<IReadOnlyList<SearchHit>>> SearchAsync(Uri registry, string accessToken, SearchQuery query, CancellationToken cancellationToken)
        {
            Last = query;
            return Task.FromResult(Error is null
                ? ApiResult<IReadOnlyList<SearchHit>>.Success([new SearchHit { Kind = "session", Key = "k", Snippet = "«parentUuid»" }])
                : ApiResult<IReadOnlyList<SearchHit>>.Fail(Error));
        }
    }

    private sealed class FakeProjects : IProjectApi
    {
        public RemoteProject Resolved { get; set; } = new() { Name = "none" };
        public string? AskedFor { get; private set; }

        public Task<ApiResult<RemoteProject>> ResolveProjectAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken)
        {
            AskedFor = facts.WorkingDirectory;
            return Task.FromResult(ApiResult<RemoteProject>.Success(Resolved));
        }

        public Task<ApiResult<ProjectLinkChange>> LinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, string project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<ProjectLinkChange>> UnlinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NotGitResolver : IProjectFactsResolver
    {
        public ProjectFacts Resolve(string workingDirectory) => ProjectFacts.NotGit(workingDirectory, workingDirectory);
    }

    private sealed class FakeTokens(SessionLookup session) : IAccessTokenProvider
    {
        public Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(session);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
