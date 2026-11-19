using Protostar.Cli.Auth;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <see cref="IProjectService"/>: <c>protostar project</c> show, link and unlink.</summary>
public sealed class ProjectServiceTests : IDisposable
{
    private const string Registry = "https://registry.test";
    private static readonly Guid MachineId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly string _folder = Path.GetFullPath(Directory.CreateTempSubdirectory("pstar-project-").FullName);
    private readonly FakeApi _api = new();
    private readonly RecordingResolver _resolver = new();
    private StoredToken? _token = new() { Registry = Registry, AccessToken = "tok", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private ProjectService Sut() => new(_resolver, new FakeTokens(() => _token), _api, new FakeMachine());

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
        public int Calls { get; private set; }
        public ProjectFacts? LastFacts { get; private set; }
        public Guid LastMachine { get; private set; }
        public string? LastTarget { get; private set; }

        private static readonly RemoteProject Widget = new() { Name = "widget", RecognisedBy = "folder" };

        public Task<ApiResult<RemoteProject>> ResolveProjectAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
            Task.FromResult(Record(machineId, facts, Widget));

        public Task<ApiResult<ProjectLinkChange>> LinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, string project, CancellationToken cancellationToken)
        {
            LastTarget = project;
            return Task.FromResult(Record(machineId, facts, new ProjectLinkChange { Outcome = "linked", Project = Widget }));
        }

        public Task<ApiResult<ProjectLinkChange>> UnlinkFolderAsync(Uri registry, string accessToken, Guid machineId, ProjectFacts facts, CancellationToken cancellationToken) =>
            Task.FromResult(Record(machineId, facts, new ProjectLinkChange { Outcome = "unlinked", Project = Widget }));

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

    private sealed class FakeTokens(Func<StoredToken?> token) : IAccessTokenProvider
    {
        public Task<StoredToken?> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(token());
    }
}
