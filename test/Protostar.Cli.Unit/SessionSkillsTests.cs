using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;
using Spectre.Console;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>The skills <c>sessions show</c> lists for a session: which are installed here, and how an older registry is handled.</summary>
public sealed class SessionSkillsTests
{
    private const string Key = "7d3c9a52-1f4e-4b8a-9c0d-2e6f8a1b3c4d";
    private static readonly Uri Registry = new("https://registry.test");
    private static readonly Guid SessionProject = Guid.Parse("c508803e-edf0-4ec8-ae08-234abf9f3748");
    private static readonly ClaudeCodeHarness Harness = new(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper());

    private readonly FakeRegistry _registry = new();
    private readonly List<DiscoveredSkill> _local = [];

    private ISkillService? _skills;
    private IProjectFactsResolver _facts = new NotGit();

    private SessionSkills Sut()
    {
        var api = new RegistryApi(_registry);
        return new SessionSkills(new RegistryCapabilities(api), api, _skills ?? new LocalSkills(_local), _facts, api, new FakeMachine());
    }

    private static OpenedSession Session(Guid? project = null) => new(
        Registry, "tok",
        new TranscriptDetail(new RemoteTranscript { Harness = "claude-code", Key = Key, Kind = "session", Title = "Demo", ProjectId = project ?? SessionProject }, []),
        Harness);

    private void Used(params string[] names) =>
        _registry.Skills = [.. names.Select(n => new JsonObject { ["name"] = n, ["uses"] = 1 })];

    private void Installed(string name, SkillScope scope = SkillScope.Global) =>
        _local.Add(new DiscoveredSkill { Name = name, Scope = scope, Path = "/skills/" + name, ProjectRoot = scope == SkillScope.Project ? "/repo" : null });

    [Fact]
    public async Task A_personal_skill_is_installed_here_one_missing_is_not_and_a_plugin_skill_is_not_looked_for()
    {
        Used("code-review", "user-stories", "b-fac:orchestrated-delivery");
        Installed("code-review");

        var used = await Sut().GetAsync(Session(), null, "/work", CancellationToken.None);

        Assert.Equal([true, false, null], used!.Select(u => u.InstalledHere));
        Assert.True(used![2].FromPlugin);
        Assert.DoesNotContain(_registry.Asked, u => u.AbsolutePath == "/v1/projects/resolve");
    }

    [Fact]
    public async Task A_project_skill_counts_only_when_the_folder_is_the_session_project_and_is_resolved_once()
    {
        Used("deploy", "release");
        Installed("deploy", SkillScope.Project);
        Installed("release", SkillScope.Project);

        _registry.FolderProject = SessionProject;
        var same = await Sut().GetAsync(Session(), null, "/repo", CancellationToken.None);
        _registry.FolderProject = Guid.NewGuid();
        var other = await Sut().GetAsync(Session(), null, "/repo", CancellationToken.None);

        Assert.Equal([true, true], same!.Select(u => u.InstalledHere));
        Assert.Equal([false, false], other!.Select(u => u.InstalledHere));
        Assert.Equal(2, _registry.Asked.Count(u => u.AbsolutePath == "/v1/projects/resolve"));
    }

    [Fact]
    public async Task Local_skills_that_cannot_be_read_leave_installed_here_unknown_rather_than_failing()
    {
        Used("code-review", "b-fac:orchestrated-delivery");
        _skills = new UnreadableSkills();

        var used = await Sut().GetAsync(Session(), null, "/work", CancellationToken.None);

        Assert.Equal(["code-review", "b-fac:orchestrated-delivery"], used!.Select(u => u.Skill.Name));
        Assert.All(used!, u => Assert.Null(u.InstalledHere));
    }

    [Fact]
    public async Task A_project_folder_that_cannot_be_read_leaves_its_skills_unknown_rather_than_failing()
    {
        Used("deploy");
        Installed("deploy", SkillScope.Project);
        _facts = new UnreadableFolder();

        var used = await Sut().GetAsync(Session(), null, "/repo", CancellationToken.None);

        Assert.Null(Assert.Single(used!).InstalledHere);
    }

    [Fact]
    public async Task A_real_skills_folder_without_read_permission_leaves_installed_here_unknown()
    {
        // Unix permissions; on Windows the fake above covers the same path.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;
        var home = Directory.CreateTempSubdirectory("protostar-unreadable-");
        var skills = Directory.CreateDirectory(Path.Combine(home.FullName, "skills"));
        Directory.CreateDirectory(Path.Combine(skills.FullName, "code-review"));
        File.SetUnixFileMode(skills.FullName, UnixFileMode.None);
        try
        {
            Used("code-review");
            _skills = new SkillService(new HarnessCatalog([Harness]));

            var used = await Sut().GetAsync(Session(), home.FullName, home.FullName, CancellationToken.None);

            Assert.Null(Assert.Single(used!).InstalledHere);
        }
        finally
        {
            File.SetUnixFileMode(skills.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            home.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_registry_that_lists_capabilities_without_session_skills_is_not_asked()
    {
        Used("code-review");
        _registry.Capabilities = ["sessions", "redacted-read"];

        Assert.Null(await Sut().GetAsync(Session(), null, "/work", CancellationToken.None));
        Assert.DoesNotContain(_registry.Asked, u => u.AbsolutePath.EndsWith("/skills", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_registry_that_lists_no_capabilities_is_asked_and_its_404_gives_no_list()
    {
        _registry.Capabilities = null;
        _registry.Skills = null;

        Assert.Null(await Sut().GetAsync(Session(), null, "/work", CancellationToken.None));
        Assert.Contains(_registry.Asked, u => u.AbsolutePath == $"/v1/transcripts/claude-code/{Key}/skills");
    }

    [Fact]
    public async Task The_skills_are_listed_above_the_conversation_with_their_marks_and_a_legend()
    {
        var output = new StringWriter();
        var writer = new ConversationWriter(AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(new StringWriter()) }), output, plain: true);
        IReadOnlyList<UsedSkill> skills =
        [
            new(new SessionSkill { Name = "code-review", Uses = 3, SkillId = Guid.NewGuid(), Version = 3, VersionInferred = true }, true),
            new(new SessionSkill { Name = "user-stories", Uses = 1, SkillId = Guid.NewGuid(), Version = 1, VersionInferred = true }, false),
            new(new SessionSkill { Name = "old", Uses = 12, Deleted = true }, false),
            new(new SessionSkill { Name = "never-pushed", Uses = 1 }, true),
            new(new SessionSkill { Name = "b-fac:orchestrated-delivery", Uses = 1 }, null),
        ];
        ShownEntry[] entries = [new(Key, 0, new PromptEntry(null, false, "Hi"))];

        var exit = await SessionShowPresenter.RenderAsync(Session(), entries.ToAsyncEnumerable(), full: false, json: false, writer, Task.FromResult<IReadOnlyList<UsedSkill>?>(skills));

        Assert.Equal(0, exit);
        Assert.Equal(
            $"""
            Demo
            claude-code
            {Key}
            Skills used
              code-review                   3 uses  v3?
              user-stories                   1 use  v1?  not installed here
              old                          12 uses       deleted from the registry, not installed here
              never-pushed                   1 use       not in the registry
              b-fac:orchestrated-delivery    1 use       plugin
              ? the version in use then, inferred from when the skill's versions were pushed

            You
              Hi

            """.ReplaceLineEndings(),
            output.ToString());
    }

    [Fact]
    public async Task Without_a_list_there_is_no_skills_block()
    {
        var output = new StringWriter();
        var writer = new ConversationWriter(AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(new StringWriter()) }), output, plain: true);
        ShownEntry[] entries = [new(Key, 0, new PromptEntry(null, false, "Hi"))];

        await SessionShowPresenter.RenderAsync(Session(), entries.ToAsyncEnumerable(), full: false, json: false, writer, Task.FromResult<IReadOnlyList<UsedSkill>?>(null));

        Assert.DoesNotContain("Skills", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_record_carries_the_skills_with_installedHere_only_where_it_was_looked_for()
    {
        var transcript = Session().Detail.Transcript;
        IReadOnlyList<UsedSkill> skills =
        [
            new(new SessionSkill { Name = "code-review", Uses = 3, Version = 3, VersionInferred = true }, false),
            new(new SessionSkill { Name = "p:s", Uses = 1 }, null),
        ];

        var record = SessionsJson.SessionRecord(transcript, skills);
        var none = SessionsJson.SessionRecord(transcript, null);

        Assert.Equal("session", record["type"]!.GetValue<string>());
        Assert.Equal(Key, record["session"]!["id"]!.GetValue<string>());
        Assert.False(record["skills"]![0]!["installedHere"]!.GetValue<bool>());
        Assert.Equal(3, record["skills"]![0]!["version"]!.GetValue<int>());
        Assert.Null(record["skills"]![1]!["installedHere"]);
        Assert.Null(none["skills"]);
    }

    // A registry answering meta, the session's skills, and project resolution, recording every URL asked.
    private sealed class FakeRegistry : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];
        public string[]? Capabilities { get; set; } = ["sessions", "session-skills", "redacted-read"];

        /// <summary>The skills the session used; <c>null</c> answers 404, as a registry without the route does.</summary>
        public JsonObject[]? Skills { get; set; } = [];

        public Guid? FolderProject { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Asked.Add(url);
            var meta = new JsonObject { ["service"] = "protostar-registry", ["apiMajors"] = new JsonArray(1) };
            if (Capabilities is not null)
                meta["capabilities"] = new JsonArray([.. Capabilities.Select(c => (JsonNode)c)]);
            return Task.FromResult(url.AbsolutePath switch
            {
                "/v1/meta" => Json(meta),
                $"/v1/transcripts/claude-code/{Key}/skills" when Skills is not null => Json(new JsonObject { ["skills"] = new JsonArray([.. Skills.Select(s => s.DeepClone())]) }),
                "/v1/projects/resolve" => Json(new JsonObject { ["projectId"] = FolderProject, ["name"] = "repo", ["recognisedBy"] = "rootCommit" }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(JsonNode body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private sealed class LocalSkills(List<DiscoveredSkill> skills) : ISkillService
    {
        public SkillDiscoveryResult Discover(string? harnessId, string? harnessHome, string? projectStart) => new(skills, SkillQueryFailure.None, null);
    }

    private sealed class UnreadableSkills : ISkillService
    {
        public SkillDiscoveryResult Discover(string? harnessId, string? harnessHome, string? projectStart) =>
            throw new UnauthorizedAccessException("Access to the path '/home/u/.claude/skills' is denied.");
    }

    private sealed class UnreadableFolder : IProjectFactsResolver
    {
        public ProjectFacts Resolve(string workingDirectory) => throw new IOException("Permission denied: .git");
    }

    private sealed class NotGit : IProjectFactsResolver
    {
        public ProjectFacts Resolve(string workingDirectory) => ProjectFacts.NotGit(workingDirectory, workingDirectory);
    }

    private sealed class FakeMachine : IMachineIdentity
    {
        public MachineInfo Get() => new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "test", "linux");

        public bool IsRegisteredWith(Uri registry) => true;

        public void MarkRegistered(Uri registry)
        {
        }
    }
}
