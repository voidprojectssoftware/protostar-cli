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
        var reader = new SessionReader(catalog, tokens, api);
        return new HistoryTools(
            new SearchService(tokens, api, api, new NotGit(), api, new FakeMachine()),
            new AgentHistoryService(tokens, api, api, api, reader),
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
    public void The_server_serves_exactly_four_tools_and_every_one_is_read_only()
    {
        var tools = HistoryServer.Tools(Sut()).Select(t => t.ProtocolTool).ToList();

        Assert.Equal(["search", "list_sessions", "get_session", "get_memory"], tools.Select(t => t.Name));
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
    public async Task get_session_on_a_registry_that_does_not_list_redacted_read_is_refused_before_anything_is_read()
    {
        _registry.Capabilities = ["sessions", "search"];

        var result = await Sut().GetSessionAsync(SessionId);

        Assert.Equal(SessionsJson.RegistryIncompatible, ErrorCode(result));
        Assert.DoesNotContain(_registry.Asked, u => u.AbsolutePath.StartsWith("/v1/transcripts", StringComparison.Ordinal));
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
    }

    [Fact]
    public async Task An_unreachable_registry_is_a_registry_unreachable_tool_error()
    {
        _registry.Down = true;

        Assert.Equal(SessionsJson.RegistryUnreachable, ErrorCode(await Sut().ListSessionsAsync()));
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
        public string[]? Capabilities { get; set; } = ["sessions", "redacted-read", "memory", "memory-content", "search", "memory-redacted-read"];
        public bool Down { get; set; }

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
            var unknownFile = new JsonObject { ["type"] = "https://protostar.dev/problems/unknown-memory-file", ["title"] = "No such file.", ["status"] = 404 };

            return Task.FromResult(url.AbsolutePath switch
            {
                "/v1/meta" => Json(meta),
                "/v1/transcripts" => Json(new JsonArray(transcript.DeepClone())),
                $"/v1/transcripts/claude-code/{SessionId}" => Json(new JsonObject { ["transcript"] = transcript, ["subagents"] = new JsonArray() }),
                $"/v1/transcripts/claude-code/{SessionId}/content" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"type":"user","message":{"content":"Add a migration"}}""" + "\n" +
                        """{"type":"assistant","message":{"content":[{"type":"text","text":"Added."}]}}""" + "\n",
                        Encoding.UTF8, "application/x-ndjson"),
                },
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
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

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
