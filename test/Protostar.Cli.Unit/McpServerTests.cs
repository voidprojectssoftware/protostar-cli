using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Mcp;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Search;
using Protostar.Cli.Sessions;
using Xunit;
using static Protostar.Cli.Unit.SkillPushServiceTests;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <c>protostar mcp</c>: the tools it serves, their redaction, their errors and their size bounds.</summary>
/// <remarks>The tools run over the real <see cref="RegistryApi"/> against a fake HTTP handler, so what is asserted is what goes on the wire.</remarks>
public sealed class McpServerTests
{
    private const string Registry = "https://registry.test";
    private const string SessionId = "7d3c9a52-1f4e-4b8a-9c0d-2e6f8a1b3c4d";
    private static readonly Guid ProjectId = Guid.Parse("c508803e-edf0-4ec8-ae08-234abf9f3748");

    private readonly FakeRegistry _registry = new();
    private StoredToken? _token = new() { Registry = Registry, AccessToken = "tok", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };

    private HistoryTools Sut()
    {
        var api = new RegistryApi(_registry);
        var catalog = new Catalog(new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper()));
        var tokens = new Tokens(() => SessionLookups.Of(_token));
        var capabilities = new RegistryCapabilities(api);
        var reader = new SessionReader(catalog, tokens, capabilities, api);
        return new HistoryTools(
            new SearchService(tokens, capabilities, api, new NotGit(), api, new FakeMachine()),
            new AgentHistoryService(tokens, capabilities, api, api, api, reader),
            reader, catalog, Registry);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static JsonObject Body(CallToolResult result) => JsonNode.Parse(Text(result))!.AsObject();

    private static string ErrorCode(CallToolResult result)
    {
        Assert.True(result.IsError);
        return Body(result)["error"]!["code"]!.GetValue<string>();
    }

    [Fact]
    public void The_server_serves_exactly_six_tools_and_every_one_is_read_only()
    {
        var tools = HistoryServer.Tools(Sut()).Select(t => t.ProtocolTool).ToList();

        Assert.Equal(["search", "list_sessions", "get_session", "get_session_skills", "get_memory", "get_agent"], tools.Select(t => t.Name));
        Assert.All(tools, t =>
        {
            Assert.True(t.Annotations!.ReadOnlyHint);
            Assert.False(t.Annotations.DestructiveHint);
        });
    }

    [Fact]
    public async Task get_session_reads_the_transcript_with_redacted_true_and_returns_it_in_order()
    {
        var result = await Sut().GetSessionAsync(SessionId);

        Assert.NotEqual(true, result.IsError);
        var content = Assert.Single(_registry.Asked, u => u.AbsolutePath.EndsWith("/content", StringComparison.Ordinal));
        Assert.Equal("?redacted=true", content.Query);
        var messages = Body(result)["messages"]!.AsArray();
        Assert.Equal(["prompt", "reply"], messages.Select(m => m!["type"]!.GetValue<string>()));
        Assert.Equal([0, 1], messages.Select(m => m!["index"]!.GetValue<int>()));
        Assert.Equal(SessionId, Body(result)["session"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task get_session_full_nests_a_subagent_under_the_call_that_ran_it_not_a_result_that_mentions_its_id()
    {
        // #121: a Bash result that merely prints the subagent's id, then the Task call that really ran it.
        _registry.Content =
        [
            """{"type":"user","message":{"content":"Find the agent ids in the logs, then summarise"}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t0","name":"Bash","input":{"command":"grep agentId log.txt"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t0","content":"log.txt:3: agentId: a1b2"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Task","input":{"description":"Summarise","subagent_type":"general-purpose"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"A demo."}]},"toolUseResult":{"agentId":"a1b2"}}""",
        ];
        _registry.Subagent = ("a1b2",
        [
            """{"type":"user","isSidechain":true,"message":{"content":"Summarise"}}""",
            """{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"A demo."}]}}""",
        ]);

        var result = await Sut().GetSessionAsync(SessionId, full: true);

        var messages = Body(result)["messages"]!.AsArray().Select(m => m!.AsObject()).ToList();
        Assert.Equal(
            ["prompt", "toolCall", "toolResult", "toolCall", "toolResult", "subagentStart", "prompt", "reply", "subagentEnd"],
            messages.Select(m => m["type"]!.GetValue<string>()));
        Assert.False(messages[2].ContainsKey("agentId"));
        Assert.Equal("a1b2", messages[4]["agentId"]!.GetValue<string>());
        Assert.Equal(("general-purpose", true), (messages[5]["agentType"]!.GetValue<string>(), messages[5]["linked"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task get_session_on_a_registry_that_does_not_list_redacted_read_is_refused_before_anything_is_read()
    {
        _registry.Capabilities = ["sessions", "search"];

        var result = await Sut().GetSessionAsync(SessionId);

        Assert.Equal(SessionsJson.RegistryIncompatible, ErrorCode(result));
        Assert.DoesNotContain(_registry.Asked, u => u.AbsolutePath.StartsWith("/v1/transcripts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task search_with_kind_agent_asks_for_agents_and_returns_the_hits_name_version_and_description()
    {
        var result = await Sut().SearchAsync("flamingo", kind: "agent");

        Assert.NotEqual(true, result.IsError);
        var search = Assert.Single(_registry.Asked, u => u.AbsolutePath == "/v1/search");
        Assert.Contains("kind=agent", search.Query, StringComparison.Ordinal);
        var hit = Assert.Single(Body(result)["hits"]!.AsArray())!;
        Assert.Equal(("agent", "migrator", 2), (hit["kind"]!.GetValue<string>(), hit["name"]!.GetValue<string>(), hit["version"]!.GetValue<int>()));
        Assert.Equal("Plans migrations.", hit["description"]!.GetValue<string>());
        Assert.Null(hit["machineId"]);
    }

    [Theory]
    [InlineData("sessions,search,agent-content")]
    [InlineData(null)]
    public async Task search_with_kind_agent_on_a_registry_that_does_not_list_agent_search_is_refused_before_searching(string? capabilities)
    {
        _registry.Capabilities = capabilities?.Split(',');

        Assert.Equal(SessionsJson.RegistryIncompatible, ErrorCode(await Sut().SearchAsync("flamingo", kind: "agent")));
        Assert.DoesNotContain(_registry.Asked, u => u.AbsolutePath == "/v1/search");
    }

    [Fact]
    public async Task get_agent_reads_the_file_redacted_and_returns_it_whole()
    {
        var result = await Sut().GetAgentAsync("migrator");

        Assert.NotEqual(true, result.IsError);
        var content = Assert.Single(_registry.Asked, u => u.AbsolutePath == "/v1/agents/migrator/content");
        Assert.Equal("?redacted=true", content.Query);
        var body = Body(result);
        Assert.Equal(("migrator", 2, "migrator.md"), (body["name"]!.GetValue<string>(), body["version"]!.GetValue<int>(), body["path"]!.GetValue<string>()));
        Assert.Contains("[redacted:github-token]", body["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(body["projectId"]);
    }

    [Fact]
    public async Task get_agent_for_a_name_both_personal_and_in_a_project_lists_both_and_reads_the_one_whose_project_is_given()
    {
        var tools = Sut();

        var ambiguous = await tools.GetAgentAsync("reviewer");
        Assert.Equal(HistoryTools.AmbiguousAgent, ErrorCode(ambiguous));
        Assert.Equal(2, Body(ambiguous)["error"]!["agents"]!.AsArray().Count);
        Assert.DoesNotContain(_registry.Asked, u => u.AbsolutePath == "/v1/agents/reviewer/content");

        var inProject = await tools.GetAgentAsync("reviewer", project: "demo", version: 1);
        Assert.NotEqual(true, inProject.IsError);
        var content = Assert.Single(_registry.Asked, u => u.AbsolutePath == "/v1/agents/reviewer/content");
        Assert.Equal($"?redacted=true&projectId={ProjectId}&version=1", content.Query);
        Assert.Equal("github.com/example/demo", Body(inProject)["project"]!.GetValue<string>());
    }

    [Fact]
    public async Task get_agent_for_a_name_the_user_has_no_agent_of_is_not_found()
    {
        Assert.Equal(SessionsJson.NotFound, ErrorCode(await Sut().GetAgentAsync("general-purpose")));
    }

    [Fact]
    public async Task get_agent_for_a_version_the_registry_does_not_have_is_not_found()
    {
        Assert.Equal(SessionsJson.NotFound, ErrorCode(await Sut().GetAgentAsync("migrator", version: 9)));
    }

    [Theory]
    [InlineData("sessions,search,agents,agent-search")]
    [InlineData(null)]
    public async Task get_agent_on_a_registry_that_does_not_list_agent_content_is_refused_before_anything_is_read(string? capabilities)
    {
        _registry.Capabilities = capabilities?.Split(',');

        Assert.Equal(SessionsJson.RegistryIncompatible, ErrorCode(await Sut().GetAgentAsync("migrator")));
        Assert.DoesNotContain(_registry.Asked, u => u.AbsolutePath.StartsWith("/v1/agents", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_agent_file_is_cut_to_fit_and_marked_truncated()
    {
        var agent = new RemoteAgentContent { Name = "big", Version = 1, Path = "big.md", Content = Encoding.UTF8.GetBytes(new string('x', 200_000)) };

        var record = HistoryTools.AgentRecord(agent, null, HistoryTools.MaxResultChars);

        Assert.True(record["truncated"]!.GetValue<bool>());
        Assert.True(SessionsJson.Serialize(record).Length <= HistoryTools.MaxResultChars);
    }

    [Fact]
    public async Task get_memory_on_a_registry_too_old_to_list_capabilities_is_refused()
    {
        _registry.Capabilities = null;

        Assert.Equal(SessionsJson.RegistryIncompatible, ErrorCode(await Sut().GetMemoryAsync("demo")));
        Assert.DoesNotContain(_registry.Asked, u => u.AbsolutePath.StartsWith("/v1/memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Signed_out_every_tool_returns_not_signed_in_as_a_tool_error()
    {
        _token = null;
        var tools = Sut();

        Assert.Equal(SessionsJson.NotSignedIn, ErrorCode(await tools.SearchAsync("migration")));
        Assert.Equal(SessionsJson.NotSignedIn, ErrorCode(await tools.ListSessionsAsync()));
        Assert.Equal(SessionsJson.NotSignedIn, ErrorCode(await tools.GetSessionAsync(SessionId)));
        Assert.Equal(SessionsJson.NotSignedIn, ErrorCode(await tools.GetMemoryAsync("demo")));
        Assert.Equal(SessionsJson.NotSignedIn, ErrorCode(await tools.GetSessionSkillsAsync(SessionId)));
    }

    [Fact]
    public async Task An_unreachable_registry_is_a_registry_unreachable_tool_error()
    {
        _registry.Down = true;

        Assert.Equal(SessionsJson.RegistryUnreachable, ErrorCode(await Sut().ListSessionsAsync()));
    }

    [Fact]
    public async Task get_session_skills_finds_the_session_by_prefix_and_lists_its_skills_with_inferred_versions()
    {
        var result = await Sut().GetSessionSkillsAsync(SessionId[..8]);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains(_registry.Asked, u => u.AbsolutePath == $"/v1/transcripts/claude-code/{SessionId}/skills");
        Assert.Equal(SessionId, Body(result)["session"]!["id"]!.GetValue<string>());
        var skills = Body(result)["skills"]!.AsArray();
        Assert.Equal(["aspire", "plugin:review"], skills.Select(s => s!["name"]!.GetValue<string>()));
        Assert.Equal(3, skills[0]!["version"]!.GetValue<int>());
        Assert.True(skills[0]!["versionInferred"]!.GetValue<bool>());
        Assert.Equal(2, skills[0]!["uses"]!.GetValue<int>());
        Assert.Null(skills[1]!["version"]);
    }

    [Fact]
    public async Task get_session_skills_on_a_registry_without_session_skills_says_to_update_it()
    {
        _registry.Capabilities = ["sessions", "redacted-read", "search"];

        var result = await Sut().GetSessionSkillsAsync(SessionId);

        Assert.Equal(SessionsJson.RegistryIncompatible, ErrorCode(result));
        Assert.Contains("Update the registry", Body(result)["error"]!["message"]!.GetValue<string>());
        Assert.DoesNotContain(_registry.Asked, u => u.AbsolutePath.StartsWith("/v1/transcripts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task get_session_skills_for_an_unknown_id_is_not_found()
    {
        Assert.Equal(SessionsJson.NotFound, ErrorCode(await Sut().GetSessionSkillsAsync("00000000")));
    }

    [Fact]
    public async Task get_memory_finds_the_project_by_name_and_reads_it_with_redacted_true()
    {
        var result = await Sut().GetMemoryAsync("demo", "MEMORY.md");

        Assert.NotEqual(true, result.IsError);
        var content = Assert.Single(_registry.Asked, u => u.AbsolutePath == $"/v1/memory/{ProjectId}/content");
        Assert.Equal("?redacted=true&path=MEMORY.md", content.Query);
        var file = Assert.Single(Body(result)["files"]!.AsArray())!;
        Assert.Equal("token [redacted:github-token]\n", file["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task get_memory_for_a_path_the_project_does_not_have_is_not_found()
    {
        Assert.Equal(SessionsJson.NotFound, ErrorCode(await Sut().GetMemoryAsync("demo", "missing.md")));
    }

    [Fact]
    public async Task get_memory_for_a_name_matching_two_projects_lists_them()
    {
        var result = await Sut().GetMemoryAsync("example");

        Assert.Equal(HistoryTools.AmbiguousProject, ErrorCode(result));
        Assert.Equal(2, Body(result)["error"]!["projects"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task A_search_limit_out_of_range_is_an_invalid_argument(int limit)
    {
        Assert.Equal(HistoryTools.InvalidArgument, ErrorCode(await Sut().SearchAsync("migration", limit: limit)));
        Assert.Empty(_registry.Asked);
    }

    [Fact]
    public async Task A_long_session_comes_in_pages_under_the_cap_that_cover_every_message_once()
    {
        var entries = Enumerable.Range(0, 300)
            .Select(i => new ShownEntry(SessionId, 0, new PromptEntry(DateTimeOffset.UnixEpoch, false, $"{i} " + new string('x', 1_500))))
            .ToList();

        var seen = new List<int>();
        int? offset = 0;
        while (offset is { } from)
        {
            var page = await HistoryTools.PageAsync(entries.ToAsyncEnumerable(), "claude-code", false, from, CancellationToken.None);
            Assert.True(page.Messages.Sum(m => SessionsJson.Serialize(m).Length) < HistoryTools.MaxResultChars);
            seen.AddRange(page.Messages.Select(m => m["index"]!.GetValue<int>()));
            offset = page.NextOffset;
        }

        Assert.Equal(Enumerable.Range(0, 300), seen);
    }

    [Fact]
    public async Task A_message_longer_than_the_field_cap_is_cut_and_marked()
    {
        var entries = new[] { new ShownEntry(SessionId, 0, new ReplyEntry(null, false, new string('y', 50_000))) };

        var page = await HistoryTools.PageAsync(entries.ToAsyncEnumerable(), "claude-code", false, 0, CancellationToken.None);

        var message = Assert.Single(page.Messages);
        Assert.Equal(HistoryTools.MaxFieldChars, message["text"]!.GetValue<string>().Length);
        Assert.True(message["truncated"]!.GetValue<bool>());
        Assert.Null(page.NextOffset);
    }

    [Fact]
    public void Memory_that_does_not_fit_names_the_files_left_out_and_cuts_a_first_file_too_long_alone()
    {
        RemoteMemoryFile File(string path, int size) => new() { Path = path, Version = 1, Content = Encoding.UTF8.GetBytes(new string('m', size)) };
        var content = new RemoteMemoryContent
        {
            ProjectId = ProjectId, ProjectName = "demo",
            Files = [File("a.md", 30_000), File("b.md", 30_000), File("c.md", 30_000), File("d.md", 10)],
        };

        var fits = HistoryTools.MemoryRecord(content, HistoryTools.MaxResultChars);
        Assert.Equal(["a.md", "b.md"], fits["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));
        Assert.Equal(["c.md", "d.md"], fits["omitted"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.True(SessionsJson.Serialize(fits).Length <= HistoryTools.MaxResultChars);

        var alone = HistoryTools.MemoryRecord(content with { Files = [File("big.md", 200_000)] }, HistoryTools.MaxResultChars);
        Assert.True(alone["files"]![0]!["truncated"]!.GetValue<bool>());
        Assert.True(SessionsJson.Serialize(alone).Length <= HistoryTools.MaxResultChars);
    }

    // A registry answering the routes the tools use, recording every URL asked.
    private sealed class FakeRegistry : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];
        public string[]? Capabilities { get; set; } =
            ["sessions", "session-skills", "redacted-read", "memory", "memory-content", "search", "memory-redacted-read", "agents", "agent-content", "agent-search"];
        public bool Down { get; set; }

        /// <summary>The session's content lines; a prompt and a reply when not set.</summary>
        public string[]? Content { get; set; }

        /// <summary>A subagent of the session, as its agent id and content lines.</summary>
        public (string AgentId, string[] Lines)? Subagent { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
                throw new HttpRequestException("Connection refused");
            var url = request.RequestUri!;
            Asked.Add(url);
            var meta = new JsonObject { ["service"] = "protostar-registry", ["apiMajors"] = new JsonArray(1) };
            if (Capabilities is not null)
                meta["capabilities"] = new JsonArray([.. Capabilities.Select(c => (JsonNode)c)]);
            var transcript = new JsonObject { ["harness"] = "claude-code", ["key"] = SessionId, ["kind"] = "session", ["title"] = "Migration" };
            var subagentKey = Subagent is { } sub ? $"{SessionId}.agent-{sub.AgentId}" : null;
            var subagents = subagentKey is null
                ? new JsonArray()
                : new JsonArray(new JsonObject { ["harness"] = "claude-code", ["key"] = subagentKey, ["kind"] = "subagent", ["parentKey"] = SessionId });
            var unknownFile = new JsonObject { ["type"] = "https://protostar.dev/problems/unknown-memory-file", ["title"] = "No such file.", ["status"] = 404 };

            return Task.FromResult(url.AbsolutePath switch
            {
                "/v1/meta" => Json(meta),
                "/v1/transcripts" => Json(new JsonArray(transcript.DeepClone())),
                $"/v1/transcripts/claude-code/{SessionId}" => Json(new JsonObject { ["transcript"] = transcript, ["subagents"] = subagents }),
                $"/v1/transcripts/claude-code/{SessionId}/content" => Lines(Content ??
                [
                    """{"type":"user","message":{"content":"Add a migration"}}""",
                    """{"type":"assistant","message":{"content":[{"type":"text","text":"Added."}]}}""",
                ]),
                var path when subagentKey is not null && path == $"/v1/transcripts/claude-code/{subagentKey}/content" => Lines(Subagent!.Value.Lines),
                $"/v1/transcripts/claude-code/{SessionId}/skills" => Json(new JsonObject
                {
                    ["harness"] = "claude-code", ["key"] = SessionId,
                    ["skills"] = new JsonArray(
                        new JsonObject
                        {
                            ["name"] = "aspire", ["uses"] = 2, ["firstUsedAt"] = "2026-11-19T10:00:00Z", ["lastUsedAt"] = "2026-11-19T10:05:00Z",
                            ["skillId"] = Guid.NewGuid(), ["version"] = 3, ["versionInferred"] = true, ["deleted"] = false,
                        },
                        new JsonObject { ["name"] = "plugin:review", ["uses"] = 1, ["versionInferred"] = false, ["deleted"] = false }),
                }),
                "/v1/search" => Json(new JsonArray(new JsonObject
                {
                    ["kind"] = "agent", ["name"] = "migrator", ["version"] = 2, ["description"] = "Plans migrations.",
                    ["date"] = "2026-11-19T10:00:00Z", ["snippet"] = "Plans «flamingo» migrations",
                })),
                "/v1/agents" => Json(new JsonObject
                {
                    ["agents"] = new JsonArray(
                        new JsonObject { ["name"] = "migrator", ["version"] = 2, ["contentHash"] = "AB", ["firstVersion"] = 1 },
                        new JsonObject { ["name"] = "reviewer", ["version"] = 1, ["contentHash"] = "CD", ["firstVersion"] = 1 },
                        new JsonObject { ["name"] = "reviewer", ["version"] = 3, ["contentHash"] = "EF", ["projectId"] = ProjectId, ["projectName"] = "github.com/example/demo", ["firstVersion"] = 1 }),
                    ["removed"] = new JsonArray(),
                }),
                var path when path.StartsWith("/v1/agents/", StringComparison.Ordinal) && url.Query.Contains("version=9", StringComparison.Ordinal) => Json(
                    new JsonObject { ["type"] = "https://protostar.dev/problems/unknown-agent", ["title"] = "You have no version 9 of personal agent 'migrator'.", ["status"] = 404 },
                    HttpStatusCode.NotFound, "application/problem+json"),
                var path when path.StartsWith("/v1/agents/", StringComparison.Ordinal) && path.EndsWith("/content", StringComparison.Ordinal) => Json(new JsonObject
                {
                    ["name"] = path.Split('/')[3], ["version"] = 2, ["description"] = "Plans migrations.", ["path"] = path.Split('/')[3] + ".md",
                    ["contentHash"] = "AB", ["pushedAt"] = "2026-11-19T10:00:00Z", ["redacted"] = true,
                    ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("---\nname: migrator\n---\nUse [redacted:github-token].\n")),
                }),
                "/v1/memory" => Json(new JsonArray(
                    new JsonObject { ["projectId"] = ProjectId, ["projectName"] = "github.com/example/demo", ["files"] = 1 },
                    new JsonObject { ["projectId"] = Guid.NewGuid(), ["projectName"] = "github.com/example/homelab", ["files"] = 2 })),
                var path when path == $"/v1/memory/{ProjectId}/content" && url.Query.Contains("missing", StringComparison.Ordinal) =>
                    Json(unknownFile, HttpStatusCode.NotFound, "application/problem+json"),
                var path when path == $"/v1/memory/{ProjectId}/content" => Json(new JsonObject
                {
                    ["projectId"] = ProjectId, ["projectName"] = "github.com/example/demo",
                    ["files"] = new JsonArray(new JsonObject
                    {
                        ["path"] = "MEMORY.md", ["version"] = 1, ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("token [redacted:github-token]\n")),
                    }),
                }),
                var path when path.StartsWith("/v1/transcripts/", StringComparison.Ordinal) => Json(
                    new JsonObject { ["type"] = "https://protostar.dev/problems/unknown-transcript", ["title"] = "No such transcript.", ["status"] = 404 },
                    HttpStatusCode.NotFound, "application/problem+json"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Lines(string[] lines) =>
            new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(lines.Select(l => l + "\n")), Encoding.UTF8, "application/x-ndjson") };

        private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK, string type = "application/json") =>
            new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, type) };
    }

    private sealed class NotGit : IProjectFactsResolver
    {
        public ProjectFacts Resolve(string workingDirectory) => ProjectFacts.NotGit(workingDirectory, workingDirectory);
    }

    private sealed class Tokens(Func<SessionLookup> session) : IAccessTokenProvider
    {
        public Task<SessionLookup> GetSessionAsync(Uri registry, CancellationToken cancellationToken) => Task.FromResult(session());
    }

    private sealed class Catalog(params IHarness[] harnesses) : IHarnessCatalog
    {
        public IReadOnlyList<IHarness> All => harnesses;
        public string KnownIds => string.Join(", ", harnesses.Select(h => h.Id));
        public IHarness? ById(string id) => harnesses.FirstOrDefault(h => h.Id == id);
    }
}
