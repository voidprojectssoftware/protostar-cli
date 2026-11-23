using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Spectre.Console;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>The custom agents <c>sessions show</c> lists for a session, and how an older registry is handled.</summary>
public sealed class SessionAgentsTests
{
    private const string Key = "7d3c9a52-1f4e-4b8a-9c0d-2e6f8a1b3c4d";
    private static readonly Uri Registry = new("https://registry.test");
    private static readonly Guid Project = Guid.Parse("c508803e-edf0-4ec8-ae08-234abf9f3748");
    private static readonly ClaudeCodeHarness Harness = new(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper());

    private readonly FakeRegistry _registry = new();

    private SessionAgents Sut()
    {
        var api = new RegistryApi(_registry);
        return new SessionAgents(new RegistryCapabilities(api), api);
    }

    private static OpenedSession Session() => new(
        Registry, "tok",
        new TranscriptDetail(new RemoteTranscript { Harness = "claude-code", Key = Key, Kind = "session", Title = "Demo", ProjectId = Project }, []),
        Harness);

    [Fact]
    public async Task The_agents_are_the_registrys_list_as_it_sent_it()
    {
        _registry.Agents = [new JsonObject { ["name"] = "reviewer", ["uses"] = 2, ["agentId"] = Guid.NewGuid(), ["projectId"] = Project, ["version"] = 3, ["versionInferred"] = true }];

        var agents = await Sut().GetAsync(Session(), CancellationToken.None);

        var agent = Assert.Single(agents!);
        Assert.Equal(("reviewer", 2, 3, true, Project), (agent.Name, agent.Uses, agent.Version, agent.VersionInferred, agent.ProjectId));
        Assert.Contains(_registry.Asked, u => u.AbsolutePath == $"/v1/transcripts/claude-code/{Key}/agents");
    }

    [Fact]
    public async Task A_registry_that_lists_capabilities_without_session_agents_is_not_asked()
    {
        _registry.Capabilities = ["sessions", "session-skills", "redacted-read"];

        Assert.Null(await Sut().GetAsync(Session(), CancellationToken.None));
        Assert.DoesNotContain(_registry.Asked, u => u.AbsolutePath.EndsWith("/agents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_registry_too_old_to_list_capabilities_is_asked_and_its_404_gives_no_list()
    {
        _registry.Capabilities = null;
        _registry.Agents = null;

        Assert.Null(await Sut().GetAsync(Session(), CancellationToken.None));
        Assert.Contains(_registry.Asked, u => u.AbsolutePath.EndsWith("/agents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_conversation_lists_the_agents_used_after_the_skills_with_their_scope_and_inferred_version()
    {
        var output = new StringWriter();
        var writer = new ConversationWriter(AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(new StringWriter()) }), output, plain: true);
        IReadOnlyList<SessionAgent> agents =
        [
            new() { Name = "reviewer", Uses = 3, AgentId = Guid.NewGuid(), ProjectId = Project, Version = 2, VersionInferred = true },
            new() { Name = "planner", Uses = 1, AgentId = Guid.NewGuid(), Version = 1, VersionInferred = true },
            new() { Name = "old-helper", Uses = 1, Deleted = true },
        ];
        ShownEntry[] entries = [new(Key, 0, new PromptEntry(null, false, "Hi"))];

        var exit = await SessionShowPresenter.RenderAsync(
            Session(), entries.ToAsyncEnumerable(), full: false, json: false, writer, Task.FromResult<IReadOnlyList<UsedSkill>?>([]),
            Task.FromResult<IReadOnlyList<SessionAgent>?>(agents));

        Assert.Equal(0, exit);
        Assert.Equal(
            $"""
            Demo
            claude-code
            {Key}
            Skills used: none
            Agents used
              reviewer    3 uses  v2?  project
              planner      1 use  v1?  personal
              old-helper   1 use       deleted from the registry
              ? the version in use then, inferred from when the agent's versions were pushed

            You
              Hi

            """.ReplaceLineEndings(),
            output.ToString());
    }

    [Fact]
    public async Task No_agent_of_the_users_says_so_and_without_a_list_there_is_no_agents_block()
    {
        var none = await Render(Task.FromResult<IReadOnlyList<SessionAgent>?>([]));
        var unknown = await Render(Task.FromResult<IReadOnlyList<SessionAgent>?>(null));

        Assert.Contains("Agents used: none of yours", none, StringComparison.Ordinal);
        Assert.DoesNotContain("Agents", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_record_carries_the_agents_when_the_registry_listed_them()
    {
        var transcript = Session().Detail.Transcript;
        IReadOnlyList<SessionAgent> agents = [new() { Name = "reviewer", Uses = 1, AgentId = Guid.NewGuid(), Version = 1, VersionInferred = true }];

        var record = SessionsJson.SessionRecord(transcript, null, agents);
        var none = SessionsJson.SessionRecord(transcript, null, null);

        var agent = record["agents"]![0]!;
        Assert.Equal(("reviewer", 1, true), (agent["name"]!.GetValue<string>(), agent["version"]!.GetValue<int>(), agent["versionInferred"]!.GetValue<bool>()));
        Assert.Null(agent["projectId"]);
        Assert.Null(none["agents"]);
    }

    private static async Task<string> Render(Task<IReadOnlyList<SessionAgent>?> agents)
    {
        var output = new StringWriter();
        var writer = new ConversationWriter(AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(new StringWriter()) }), output, plain: true);
        ShownEntry[] entries = [new(Key, 0, new PromptEntry(null, false, "Hi"))];
        await SessionShowPresenter.RenderAsync(Session(), entries.ToAsyncEnumerable(), full: false, json: false, writer, null, agents);
        return output.ToString();
    }

    // A registry answering meta and the session's agents, recording every URL asked.
    private sealed class FakeRegistry : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];
        public string[]? Capabilities { get; set; } = ["sessions", "redacted-read", "session-agents"];

        /// <summary>The agents the session used; <c>null</c> answers 404, as a registry without the route does.</summary>
        public JsonObject[]? Agents { get; set; } = [];

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
                $"/v1/transcripts/claude-code/{Key}/agents" when Agents is not null => Json(new JsonObject { ["agents"] = new JsonArray([.. Agents.Select(a => a.DeepClone())]) }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(JsonNode body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
