using System.Text;
using Protostar.Cli.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>How Claude Code's transcript lines read as a conversation, for <c>sessions show</c>.</summary>
public sealed class ClaudeCodeConversationTests
{
    private static readonly ClaudeCodeHarness Harness = new(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper());

    private static List<ConversationEntry> Read(string line) => [.. Harness.ReadConversationLine(line)];

    [Theory]
    [InlineData("""{"type":"frobnicate","weird":true}""")]
    [InlineData("""{"type":"system","subtype":"informational","content":"Context compacted"}""")]
    [InlineData("""{"type":"summary","summary":"Demo","leafUuid":"x"}""")]
    [InlineData("""{"type":"ai-title","aiTitle":"Title"}""")]
    [InlineData("""{"type":"file-history-snapshot","snapshot":{}}""")]
    [InlineData("""{"type":"user","isMeta":true,"message":{"role":"user","content":"<local-command-caveat>"}}""")]
    [InlineData("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"private"}]}}""")]
    [InlineData("""{"type":"user","message":"not an object"}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"image"},42,null]}}""")]
    [InlineData("""{"no type at all":1}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"type":"user", "torn""")]
    [InlineData("not json")]
    [InlineData("")]
    public void A_line_that_is_not_part_of_the_conversation_yields_nothing_and_never_throws(string line) =>
        Assert.Empty(Read(line));

    [Fact]
    public void A_typed_prompt_is_a_prompt()
    {
        var prompt = Assert.IsType<PromptEntry>(Assert.Single(Read(
            """{"type":"user","timestamp":"2026-11-19T10:00:00Z","message":{"role":"user","content":"List the files"}}""")));

        Assert.Equal("List the files", prompt.Text);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:00Z"), prompt.At);
    }

    [Fact]
    public void An_assistant_line_yields_its_text_and_tool_calls_in_order_without_thinking()
    {
        var entries = Read("""
            {"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"Listing."},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls   -la\n","description":"List files"}}]}}
            """);

        Assert.Collection(entries,
            e => Assert.Equal("Listing.", Assert.IsType<ReplyEntry>(e).Text),
            e =>
            {
                var call = Assert.IsType<ToolCallEntry>(e);
                Assert.Equal(("t1", "Bash", "ls -la"), (call.Id, call.Name, call.Summary));
                Assert.Null(call.Subagent);
            });
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("Agent")]
    public void A_call_that_starts_a_subagent_carries_its_type_and_description(string tool)
    {
        var call = Assert.IsType<ToolCallEntry>(Assert.Single(Read(
            $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"{{{tool}}}","input":{"description":"Summarise README","prompt":"Read it.","subagent_type":"general-purpose"}}]}}""")));

        Assert.Equal(new SubagentCall("general-purpose", "Summarise README"), call.Subagent);
        Assert.Equal("Summarise README", call.Summary);
    }

    [Fact]
    public void A_tool_result_names_the_subagent_it_ran_from_tool_use_result()
    {
        var result = Assert.IsType<ToolResultEntry>(Assert.Single(Read(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"Done."},{"type":"image"}]}]},"toolUseResult":{"status":"completed","agentId":"a1b2"}}""")));

        Assert.Equal(("t2", "Done.", false, "a1b2"), (result.ToolUseId, result.Text, result.IsError, result.AgentId));
    }

    [Fact]
    public void Without_tool_use_result_the_agent_id_in_the_result_text_is_used()
    {
        var result = Assert.IsType<ToolResultEntry>(Assert.Single(Read(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":"Done.\nagentId: c3d4 (for resuming)"}]},"toolUseResult":"a string"}""")));

        Assert.Equal("c3d4", result.AgentId);
    }

    [Fact]
    public void An_error_result_and_a_sidechain_line_are_marked()
    {
        var result = Assert.IsType<ToolResultEntry>(Assert.Single(Read(
            """{"type":"user","isSidechain":true,"message":{"content":[{"type":"tool_result","tool_use_id":"t3","content":"No such file","is_error":true}]}}""")));

        Assert.True(result.IsError);
        Assert.True(result.Sidechain);
        Assert.Null(result.AgentId);
    }
}

/// <summary>Contract tests for <see cref="ISessionReader"/>: finding a session by id or prefix, and reading it in order.</summary>
public sealed class SessionReaderTests
{
    private const string Registry = "https://registry.test";
    private const string SessionId = "7d3c9a52-1f4e-4b8a-9c0d-2e6f8a1b3c4d";
    private const string SubagentId = SessionId + ".agent-a1b2";

    private readonly FakeTranscripts _api = new();
    private readonly MetaOnlyRegistry _meta = new();
    private StoredToken? _token = new() { Registry = Registry, AccessToken = "tok", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };

    private ISessionReader Sut() => new SessionReader(
        new Catalog(new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper())),
        new Tokens(() => SessionLookups.Of(_token)), new RegistryCapabilities(_meta), _api);

    private Task<SessionOpenResult> Open(string id) => Sut().OpenAsync(Registry, null, id, CancellationToken.None);

    private static RemoteTranscript Session(string key, int minute = 0) => new()
    {
        Harness = "claude-code", Key = key, Kind = "session", Title = key[..4],
        LastActivityAt = DateTimeOffset.Parse("2026-11-19T10:00:00Z").AddMinutes(minute),
    };

    private static readonly string[] Conversation =
    [
        """{"type":"user","message":{"content":"Summarise the README"}}""",
        """{"type":"frobnicate"}""",
        """{"type":"assistant","message":{"content":[{"type":"text","text":"Asking a helper."},{"type":"tool_use","id":"t1","name":"Task","input":{"description":"Summarise","subagent_type":"general-purpose"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"A demo."}]},"toolUseResult":{"agentId":"a1b2"}}""",
        """{"type":"assistant","message":{"content":"It is a demo."}}""",
    ];

    private static readonly string[] SubagentLines =
    [
        """{"type":"user","isSidechain":true,"message":{"content":"Summarise"}}""",
        """{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"A demo."}]}}""",
    ];

    // A registry from before the capability list (registry#15), which cannot say it redacts what it serves.
    private static RegistryMeta BeforeCapabilities => new() { ApiMajors = [1] };

    // A registry that serves sessions redacted but cannot look a prefix up.
    private static RegistryMeta WithoutPrefixLookup => new() { ApiMajors = [1], Capabilities = [RegistryCapability.Sessions, RegistryCapability.RedactedRead] };

    [Fact]
    public async Task A_whole_id_is_looked_up_directly_on_a_registry_without_prefix_lookup()
    {
        _meta.Meta = WithoutPrefixLookup;
        _api.Add(Session(SessionId), Conversation);

        var opened = await Open(SessionId);

        Assert.Equal(SessionOpenOutcome.Found, opened.Outcome);
        Assert.Equal(SessionId, opened.Session!.Detail.Transcript.Key);
        Assert.Equal(0, _api.Lists);
    }

    [Fact]
    public async Task A_whole_id_is_one_prefix_lookup_on_a_registry_that_has_it()
    {
        _api.Add(Session(SessionId), Conversation);

        var opened = await Open(SessionId);

        Assert.Equal(SessionOpenOutcome.Found, opened.Outcome);
        Assert.Equal([SessionId], _api.Prefixes);
        Assert.Equal((1, 1), (_api.Lists, _api.Gets));
    }

    [Fact]
    public async Task A_prefix_is_resolved_in_one_request_when_the_registry_lists_prefix_lookup()
    {
        for (var i = 0; i < 250; i++)
            _api.Add(Session($"{i:x8}-0000-4000-8000-000000000000", minute: 1000 - i), []);
        _api.Add(Session(SessionId, minute: 0), Conversation);

        var opened = await Open("7d3c9a52");

        Assert.Equal(SessionOpenOutcome.Found, opened.Outcome);
        Assert.Equal(["7d3c9a52"], _api.Prefixes);
        Assert.Equal(SessionReader.MaxCandidates + 1, _api.Limits.Single());
        // The one lookup, then the session itself.
        Assert.Equal((1, 1), (_api.Lists, _api.Gets));
    }

    [Fact]
    public async Task A_prefix_shorter_than_the_registry_accepts_is_matched_by_paging()
    {
        _api.Add(Session(SessionId), Conversation);
        _api.Add(Session("11111111-2222-4333-8444-555555555555"), []);

        var opened = await Open("7d3");

        Assert.Equal(SessionOpenOutcome.Found, opened.Outcome);
        Assert.Empty(_api.Prefixes);
        Assert.Equal(1, _api.Lists);
    }

    [Fact]
    public async Task A_whole_subagent_id_is_still_found_when_no_session_starts_with_it()
    {
        _api.Add(Session(SessionId), Conversation);
        _api.Add(new RemoteTranscript { Harness = "claude-code", Key = SubagentId, Kind = "subagent", ParentKey = SessionId }, SubagentLines);

        var opened = await Open(SubagentId);

        Assert.Equal(SessionOpenOutcome.Found, opened.Outcome);
        Assert.Equal(SubagentId, opened.Session!.Detail.Transcript.Key);
        Assert.Equal([SubagentId], _api.Prefixes);
    }

    [Fact]
    public async Task A_registry_that_lists_capabilities_without_sessions_is_refused_before_anything_is_asked()
    {
        _meta.Meta = new RegistryMeta { ApiMajors = [1], Capabilities = ["skills"] };

        var opened = await Open(SessionId);

        Assert.Equal((SessionOpenOutcome.Failed, SessionSyncFailure.Incompatible), (opened.Outcome, opened.Failure));
        Assert.Equal("This registry is too old for session backups. Update the registry.", opened.Detail);
        Assert.Equal((0, 0), (_api.Lists, _api.Gets));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_registry_that_does_not_list_redacted_read_is_refused_before_anything_is_asked(bool listsCapabilities)
    {
        // Lenient for sessions, strict for redaction: a registry too old to list it might ignore redacted=true (#122).
        _meta.Meta = listsCapabilities ? new RegistryMeta { ApiMajors = [1], Capabilities = [RegistryCapability.Sessions] } : BeforeCapabilities;
        _api.Add(Session(SessionId), Conversation);

        var opened = await Open(SessionId);

        Assert.Equal((SessionOpenOutcome.Failed, SessionSyncFailure.Incompatible), (opened.Outcome, opened.Failure));
        Assert.Equal("This registry is too old for serving sessions with secrets redacted. Update the registry.", opened.Detail);
        Assert.Equal((0, 0), (_api.Lists, _api.Gets));
        Assert.Empty(_api.Downloads);
    }

    [Fact]
    public async Task An_unambiguous_prefix_opens_its_session_ignoring_case()
    {
        _api.Add(Session(SessionId), Conversation);
        _api.Add(Session("11111111-2222-4333-8444-555555555555"), []);

        var opened = await Open("7D3C9A");

        Assert.Equal(SessionOpenOutcome.Found, opened.Outcome);
        Assert.Equal(SessionId, opened.Session!.Detail.Transcript.Key);
    }

    [Fact]
    public async Task An_ambiguous_prefix_is_refused_with_the_candidates()
    {
        _api.Add(Session(SessionId, minute: 1), Conversation);
        _api.Add(Session("7d3c1111-2222-4333-8444-555555555555", minute: 2), []);

        var opened = await Open("7d3c");

        Assert.Equal(SessionOpenOutcome.Ambiguous, opened.Outcome);
        Assert.Equal(["7d3c1111-2222-4333-8444-555555555555", SessionId], opened.Candidates.Select(c => c.Key));
        Assert.Equal(["7d3c"], _api.Prefixes);
    }

    [Fact]
    public async Task A_prefix_is_matched_on_every_page_of_the_list_on_a_registry_without_prefix_lookup()
    {
        _meta.Meta = WithoutPrefixLookup;
        for (var i = 0; i < 250; i++)
            _api.Add(Session($"{i:x8}-0000-4000-8000-000000000000", minute: 1000 - i), []);
        _api.Add(Session(SessionId, minute: 0), Conversation);

        var opened = await Open("7d3c9a52");

        Assert.Equal(SessionOpenOutcome.Found, opened.Outcome);
        Assert.Equal(2, _api.Lists);
    }

    [Fact]
    public async Task An_id_that_matches_nothing_is_not_found()
    {
        _api.Add(Session(SessionId), Conversation);

        Assert.Equal(SessionOpenOutcome.NotFound, (await Open("deadbeef")).Outcome);
    }

    [Fact]
    public async Task A_prefix_the_registry_refuses_falls_back_to_the_whole_key()
    {
        _api.RefusePrefixes = true;
        _api.Add(Session(SessionId), Conversation);

        var opened = await Open(SessionId);

        Assert.Equal(SessionOpenOutcome.Found, opened.Outcome);
        Assert.Equal([SessionId], _api.Prefixes);
        Assert.Equal(SessionId, opened.Session!.Detail.Transcript.Key);
    }

    [Fact]
    public async Task A_prefix_the_registry_refuses_that_is_no_whole_key_is_not_found_not_unreachable()
    {
        _api.RefusePrefixes = true;
        _api.Add(Session(SessionId), Conversation);

        var opened = await Open("7d3c9a52");

        Assert.Equal((SessionOpenOutcome.NotFound, SessionSyncFailure.None), (opened.Outcome, opened.Failure));
        Assert.Equal(1, _api.Gets);
    }

    [Fact]
    public async Task Without_a_sign_in_nothing_is_asked_of_the_registry()
    {
        _token = null;

        var opened = await Open(SessionId);

        Assert.Equal((SessionOpenOutcome.Failed, SessionSyncFailure.NotLoggedIn), (opened.Outcome, opened.Failure));
        Assert.Equal(0, _api.Gets);
    }

    [Fact]
    public async Task A_registry_error_is_a_failure_not_a_missing_session()
    {
        _api.GetError = "The registry returned 500.";

        var opened = await Open(SessionId);

        Assert.Equal((SessionOpenOutcome.Failed, SessionSyncFailure.RegistryUnreachable), (opened.Outcome, opened.Failure));
    }

    [Fact]
    public async Task By_default_tool_calls_show_but_not_their_results_or_subagents()
    {
        _api.Add(Session(SessionId), Conversation, (SubagentId, SubagentLines));

        var shown = await ReadAll(full: false);

        Assert.Equal(["prompt", "reply", "call:Task", "reply"], shown.Select(Describe));
        Assert.All(shown, s => Assert.Equal(SessionId, s.TranscriptId));
        Assert.DoesNotContain(SubagentId, _api.Downloads);
    }

    [Fact]
    public async Task With_full_a_subagent_is_nested_right_after_the_result_of_the_call_that_ran_it()
    {
        _api.Add(Session(SessionId), Conversation, (SubagentId, SubagentLines));

        var shown = await ReadAll(full: true);

        Assert.Equal(
            ["prompt", "reply", "call:Task", "result", "start:general-purpose", "prompt", "reply", "end", "reply"],
            shown.Select(Describe));
        Assert.Equal([0, 0, 0, 0, 1, 1, 1, 1, 0], shown.Select(s => s.Depth));
        Assert.Equal(SubagentId, shown[5].TranscriptId);
        Assert.True(((SubagentStartEntry)shown[4].Entry).Linked);
    }

    [Fact]
    public async Task A_subagent_no_result_names_is_shown_after_the_session()
    {
        var unlinked = SessionId + ".agent-zz99";
        _api.Add(Session(SessionId), Conversation, (unlinked, SubagentLines));

        var shown = await ReadAll(full: true);

        Assert.Equal(["start:", "prompt", "reply", "end"], shown.TakeLast(4).Select(Describe));
        Assert.False(((SubagentStartEntry)shown[^4].Entry).Linked);
    }

    // #121: a Bash result that merely prints a subagent's id, then the Task call that really ran it.
    private static readonly string[] MentionThenTask =
    [
        """{"type":"user","message":{"content":"Find the agent ids in the logs, then summarise"}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t0","name":"Bash","input":{"command":"grep agentId log.txt"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t0","content":"log.txt:3: agentId: a1b2"}]},"toolUseResult":{"stdout":"log.txt:3: agentId: a1b2"}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Task","input":{"description":"Summarise","subagent_type":"general-purpose"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"A demo."}]},"toolUseResult":{"agentId":"a1b2"}}""",
    ];

    [Fact]
    public async Task A_result_that_only_mentions_an_agent_id_neither_takes_the_subagent_nor_carries_the_id()
    {
        _api.Add(Session(SessionId), MentionThenTask, (SubagentId, SubagentLines));

        var shown = await ReadAll(full: true);

        Assert.Equal(
            ["prompt", "call:Bash", "result", "call:Task", "result", "start:general-purpose", "prompt", "reply", "end"],
            shown.Select(Describe));
        var bash = (ToolResultEntry)shown[2].Entry;
        Assert.Null(bash.AgentId);
        Assert.False(SessionsJson.ShowRecord(shown[2], "claude-code", full: true).AsObject().ContainsKey("agentId"));
        Assert.Equal("a1b2", ((ToolResultEntry)shown[4].Entry).AgentId);
        var start = (SubagentStartEntry)shown[5].Entry;
        Assert.Equal(("general-purpose", "Summarise", true), (start.AgentType, start.Description, start.Linked));
    }

    [Fact]
    public async Task A_structured_agent_id_on_a_result_of_a_call_that_ran_no_subagent_links_nothing()
    {
        string[] lines =
        [
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t0","name":"Bash","input":{"command":"true"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t0","content":"ok"}]},"toolUseResult":{"agentId":"a1b2"}}""",
        ];
        _api.Add(Session(SessionId), lines, (SubagentId, SubagentLines));

        var shown = await ReadAll(full: true);

        Assert.Equal(["call:Bash", "result", "start:", "prompt", "reply", "end"], shown.Select(Describe));
        Assert.Null(((ToolResultEntry)shown[1].Entry).AgentId);
        Assert.False(((SubagentStartEntry)shown[2].Entry).Linked);
    }

    [Fact]
    public async Task A_result_inside_a_subagent_that_mentions_an_agent_id_carries_no_id()
    {
        string[] inside =
        [
            """{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"tool_use","id":"s0","name":"Bash","input":{"command":"grep agentId log.txt"}}]}}""",
            """{"type":"user","isSidechain":true,"message":{"content":[{"type":"tool_result","tool_use_id":"s0","content":"log.txt:3: agentId: zz99"}]}}""",
        ];
        _api.Add(Session(SessionId), Conversation, (SubagentId, inside));

        var shown = await ReadAll(full: true);

        var result = Assert.Single(shown, s => s.TranscriptId == SubagentId && s.Entry is ToolResultEntry);
        Assert.Null(((ToolResultEntry)result.Entry).AgentId);
        Assert.False(SessionsJson.ShowRecord(result, "claude-code", full: true).AsObject().ContainsKey("agentId"));
    }

    [Fact]
    public async Task On_an_older_harness_the_agent_id_ending_a_subagent_calls_result_text_still_links_it()
    {
        string[] lines =
        [
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Task","input":{"description":"Summarise","subagent_type":"general-purpose"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"Found agentId: zz99 in a log.\nagentId: a1b2 (for resuming)"}]}}""",
        ];
        _api.Add(Session(SessionId), lines, (SubagentId, SubagentLines));

        var shown = await ReadAll(full: true);

        Assert.Equal(["call:Task", "result", "start:general-purpose", "prompt", "reply", "end"], shown.Select(Describe));
        Assert.Equal("a1b2", ((ToolResultEntry)shown[1].Entry).AgentId);
        Assert.True(((SubagentStartEntry)shown[2].Entry).Linked);
    }

    [Fact]
    public async Task The_conversation_drawn_from_a_mention_then_the_real_call_nests_the_subagent_under_the_call()
    {
        _api.Add(Session(SessionId), MentionThenTask, (SubagentId, SubagentLines));
        var reader = Sut();
        var opened = await reader.OpenAsync(Registry, null, SessionId, CancellationToken.None);
        var output = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings { Out = new Spectre.Console.AnsiConsoleOutput(new StringWriter()) });

        var exit = await SessionShowPresenter.RenderAsync(
            opened.Session!, reader.ReadAsync(opened.Session!, full: true, CancellationToken.None), full: true, json: false,
            new ConversationWriter(console, output, plain: true));

        Assert.Equal(0, exit);
        var text = output.ToString();
        var bash = text.IndexOf("▸ Bash", StringComparison.Ordinal);
        var task = text.IndexOf("▸ Task", StringComparison.Ordinal);
        var subagent = text.IndexOf("┌ Subagent general-purpose: Summarise", StringComparison.Ordinal);
        Assert.True(bash >= 0 && bash < task && task < subagent, text);
        Assert.DoesNotContain("not linked", text);
    }

    [Fact]
    public async Task Every_download_asks_for_redacted_content()
    {
        _api.Add(Session(SessionId), Conversation, (SubagentId, SubagentLines));

        await ReadAll(full: true);

        Assert.Equal([SubagentId, SessionId], _api.Downloads);
    }

    [Fact]
    public async Task A_download_cut_off_part_way_fails_after_the_entries_already_read()
    {
        _api.Add(Session(SessionId), Conversation);
        _api.CutOffAfterBytes = Encoding.UTF8.GetByteCount(Conversation[0] + "\n" + Conversation[1] + "\n");
        var reader = Sut();
        var opened = await reader.OpenAsync(Registry, null, SessionId, CancellationToken.None);
        var shown = new List<ShownEntry>();

        var error = await Assert.ThrowsAsync<SessionReadException>(async () =>
        {
            await foreach (var entry in reader.ReadAsync(opened.Session!, full: false, CancellationToken.None))
                shown.Add(entry);
        });

        Assert.Equal(["prompt"], shown.Select(Describe));
        Assert.Contains("stopped part way", error.Message);
    }

    private async Task<List<ShownEntry>> ReadAll(bool full)
    {
        var reader = Sut();
        var opened = await reader.OpenAsync(Registry, null, SessionId, CancellationToken.None);
        var shown = new List<ShownEntry>();
        await foreach (var entry in reader.ReadAsync(opened.Session!, full, CancellationToken.None))
            shown.Add(entry);
        return shown;
    }

    private static string Describe(ShownEntry shown) => shown.Entry switch
    {
        PromptEntry => "prompt",
        ReplyEntry => "reply",
        ToolCallEntry call => $"call:{call.Name}",
        ToolResultEntry => "result",
        SubagentStartEntry start => $"start:{start.AgentType}",
        SubagentEndEntry => "end",
        _ => "?",
    };

    private sealed class FakeTranscripts : ITranscriptApi
    {
        private readonly List<RemoteTranscript> _sessions = [];
        private readonly Dictionary<string, List<RemoteTranscript>> _subagents = [];
        private readonly Dictionary<string, string> _content = [];

        public int Gets { get; private set; }
        public int Lists { get; private set; }

        /// <summary>The <c>keyPrefix</c> of every list that sent one, and the limit it asked for.</summary>
        public List<string> Prefixes { get; } = [];
        public List<int> Limits { get; } = [];
        public string? GetError { get; set; }

        /// <summary>Refuse every <c>keyPrefix</c> with 400, as a registry with stricter rules for a prefix would.</summary>
        public bool RefusePrefixes { get; set; }
        public int? CutOffAfterBytes { get; set; }

        /// <summary>The key of every content download, in order; each one asked for redacted content (the only kind there is).</summary>
        public List<string> Downloads { get; } = [];

        public void Add(RemoteTranscript session, string[] lines, params (string Key, string[] Lines)[] subagents)
        {
            _sessions.Add(session);
            _content[session.Key] = string.Concat(lines.Select(l => l + "\n"));
            _subagents[session.Key] = [.. subagents.Select(s => new RemoteTranscript { Harness = session.Harness, Key = s.Key, Kind = "subagent", ParentKey = session.Key })];
            foreach (var (key, subLines) in subagents)
                _content[key] = string.Concat(subLines.Select(l => l + "\n"));
        }

        public Task<ApiResult<TranscriptDetail>> GetAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken)
        {
            Gets++;
            if (GetError is { } error)
                return Task.FromResult(ApiResult<TranscriptDetail>.Fail(error));
            var session = _sessions.FirstOrDefault(s => s.Harness == harness && s.Key == key);
            return Task.FromResult(session is null
                ? ApiResult<TranscriptDetail>.UnknownTranscript("No transcript with that harness and key.")
                : ApiResult<TranscriptDetail>.Success(new TranscriptDetail(session, _subagents.GetValueOrDefault(key) ?? [])));
        }

        public Task<ApiResult<IReadOnlyList<RemoteTranscript>>> ListAsync(Uri registry, string accessToken, TranscriptListQuery query, CancellationToken cancellationToken)
        {
            Lists++;
            if (query.KeyPrefix is { } prefix)
            {
                Prefixes.Add(prefix);
                Limits.Add(query.Limit);
                if (RefusePrefixes)
                    return Task.FromResult(ApiResult<IReadOnlyList<RemoteTranscript>>.RefusedRequest("keyPrefix is not valid."));
            }

            // Sessions only, as the registry lists them by default.
            IReadOnlyList<RemoteTranscript> page = [.. _sessions
                .Where(s => s.Kind == "session")
                .Where(s => query.KeyPrefix is null || s.Key.StartsWith(query.KeyPrefix, StringComparison.OrdinalIgnoreCase))
                .Where(s => query.Before is null || s.LastActivityAt < query.Before)
                .OrderByDescending(s => s.LastActivityAt)
                .Take(query.Limit)];
            return Task.FromResult(ApiResult<IReadOnlyList<RemoteTranscript>>.Success(page));
        }

        public Task<ApiResult<IReadOnlyList<SessionSkill>>> GetSkillsAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<IReadOnlyList<SessionAgent>>> GetAgentsAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<Stream>> OpenRedactedContentAsync(Uri registry, string accessToken, string harness, string key, CancellationToken cancellationToken)
        {
            Downloads.Add(key);
            var bytes = Encoding.UTF8.GetBytes(_content[key]);
            Stream stream = CutOffAfterBytes is { } cut ? new CutOffStream(bytes[..cut]) : new MemoryStream(bytes);
            return Task.FromResult(ApiResult<Stream>.Success(stream));
        }

        public Task<ApiResult<bool>> RegisterMachineAsync(Uri registry, string accessToken, Guid id, string name, string operatingSystem, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<TranscriptPosition>> RegisterTranscriptAsync(Uri registry, string accessToken, LocalTranscript transcript, Guid machineId, ProjectFacts? project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AppendResult> AppendAsync(Uri registry, string accessToken, string harness, string key, int revision, long offset, Guid machineId, ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApiResult<IReadOnlyList<TranscriptLookup>>> LookupAsync(Uri registry, string accessToken, IReadOnlyList<LocalTranscript> transcripts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // Serves its bytes, then fails the way a dropped connection does.
    private sealed class CutOffStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => Position < Length ? base.Read(buffer, offset, count) : throw new IOException("connection reset");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position < Length ? base.ReadAsync(buffer, cancellationToken) : throw new IOException("connection reset");
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
