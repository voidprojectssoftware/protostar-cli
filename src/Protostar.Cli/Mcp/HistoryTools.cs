using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Protostar.Cli.Harness;
using Protostar.Cli.Registry;
using Protostar.Cli.Search;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Mcp;

/// <summary>The tools <c>protostar mcp</c> serves: read-only views of the user's backed-up sessions and memory, as JSON.</summary>
/// <remarks>
/// <para>
/// This is the presenter of the MCP face: each tool maps its arguments to a service call and shapes the answer, with
/// the field names of <c>--json</c> (docs/reference/json-output.md). A failure is a tool error (<c>isError</c>) whose
/// text is the same error object <c>--json</c> writes to standard error, with the same codes; nothing here throws for
/// a refused or unreachable registry, so the server keeps serving.
/// </para>
/// <para>
/// There are no write tools, by construction: nothing here calls anything that changes the registry or the disk.
/// Every text an agent sees was redacted by the registry: search snippets, transcripts read with <c>redacted=true</c>,
/// and memory read with <c>redacted=true</c>, each behind the capability that says the registry honours it.
/// </para>
/// <para>
/// A result is at most <see cref="MaxResultChars"/> characters of JSON, about 20,000 tokens, under the 25,000-token
/// limit Claude Code puts on one tool result by default, so a result is never cut off by the client. A transcript is
/// paged by message, and a single long field is cut at <see cref="MaxFieldChars"/>.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class HistoryTools
{
    /// <summary>The most characters of JSON one tool result carries.</summary>
    internal const int MaxResultChars = 80_000;

    /// <summary>The most characters of one text field of a transcript message (a prompt, a reply, a tool result or input).</summary>
    internal const int MaxFieldChars = 4_000;

    /// <summary>A tool argument is out of range or malformed.</summary>
    internal const string InvalidArgument = "invalid_argument";

    /// <summary>A project name matches more than one project with memory; the error's <c>projects</c> lists them.</summary>
    internal const string AmbiguousProject = "ambiguous_project";

    // Room left in a page for what is around the messages: the session's own record and the offsets.
    private const int EnvelopeChars = 4_000;

    private const int MaxQueryLength = 500;
    private const int MaxSearchLimit = 100;
    private const int MaxListLimit = 100;

    private readonly ISearchService _search;
    private readonly IAgentHistoryService _history;
    private readonly ISessionReader _reader;
    private readonly IHarnessCatalog _catalog;
    private readonly string? _registry;

    /// <param name="registry">The <c>--registry</c> option <c>protostar mcp</c> was started with, or <c>null</c> for the default.</param>
    public HistoryTools(ISearchService search, IAgentHistoryService history, ISessionReader reader, IHarnessCatalog catalog, string? registry)
    {
        _search = search;
        _history = history;
        _reader = reader;
        _catalog = catalog;
        _registry = registry;
    }

    [McpServerTool(Name = "search", Title = "Search history and memory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Search the developer's backed-up sessions and memory, from every project and machine, by what they say. Returns hits best first: " +
        "kind (session or memory), id (a session's id, for get_session), project, machineName, date and a snippet with each match between « and ». " +
        "Secrets are redacted by the registry.")]
    public async Task<CallToolResult> SearchAsync(
        [Description("The words to find. All are required; \"quoted words\" are a phrase, `or` gives an alternative, -word excludes.")] string query,
        [Description("Only `session` (sessions and their subagents) or `memory`. Both when left out.")] string? kind = null,
        [Description("Only this project: its id, text in its name, or `.` for the project of the folder the server runs in.")] string? project = null,
        [Description("How many hits, 1 to 100. Defaults to 20.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaxQueryLength)
            return Invalid($"query must be 1 to {MaxQueryLength} characters.");
        if (kind is not (null or "session" or "memory"))
            return Invalid("kind must be session or memory.");
        if (limit is < 1 or > MaxSearchLimit)
            return Invalid($"limit must be 1 to {MaxSearchLimit}.");

        var result = await _search.SearchAsync(
            new SearchRequest(_registry, query, kind, project, limit, Environment.CurrentDirectory), cancellationToken);
        if (Failure(result.Failure, result.Detail, result.Registry) is { } failed)
            return failed;

        var answer = new JsonObject { ["hits"] = new JsonArray([.. result.Hits.Select(SearchPresenter.Record)]) };
        if (result.FolderProject is { } folder)
            answer["folderProject"] = folder;
        return Ok(answer);
    }

    [McpServerTool(Name = "list_sessions", Title = "List sessions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the developer's backed-up sessions from every machine, newest activity first: id (for get_session), title, firstPrompt, " +
        "project, machineName, gitBranch, startedAt, lastActivityAt, message counts and subagentCount.")]
    public async Task<CallToolResult> ListSessionsAsync(
        [Description("Only sessions in this project: its id, or text in its name (for example the repo name).")] string? project = null,
        [Description("Only sessions from this machine id (a UUID, as machineId shows it).")] string? machine = null,
        [Description("Only sessions active on or after this date or time, ISO 8601 (for example 2026-11-01).")] string? since = null,
        [Description("How many sessions, 1 to 100. Defaults to 25.")] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        Guid? machineId = null;
        if (machine is not null)
        {
            if (!Guid.TryParse(machine, out var parsed))
                return Invalid("machine must be a machine id (a UUID).");
            machineId = parsed;
        }

        DateTimeOffset? sinceAt = null;
        if (since is not null)
        {
            if (!DateTimeOffset.TryParse(since, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return Invalid("since must be an ISO 8601 date or time, for example 2026-11-01.");
            sinceAt = parsed;
        }

        if (limit is < 1 or > MaxListLimit)
            return Invalid($"limit must be 1 to {MaxListLimit}.");

        var result = await _history.ListSessionsAsync(_registry, new TranscriptListQuery(project, machineId, null, sinceAt, limit), cancellationToken);
        return Failure(result.Failure, result.Detail, result.Registry)
            ?? Ok(new JsonObject { ["sessions"] = new JsonArray([.. result.Sessions.Select(SessionsJson.ListRecord)]) });
    }

    [McpServerTool(Name = "get_session", Title = "Read a session", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read a backed-up session as a conversation, in order, with secrets redacted: prompts, replies and tool calls (with full, also tool " +
        "inputs and results, and each subagent's conversation nested where it ran). Each message has an index. A long session comes in pages: " +
        "when nextOffset is present, call again with offset set to it. A field longer than 4,000 characters is cut, and its message has truncated: true.")]
    public async Task<CallToolResult> GetSessionAsync(
        [Description("The session's id, or enough of its start to name one session. A subagent's full id works too.")] string id,
        [Description("Also tool inputs and results, and subagents' conversations. Defaults to false.")] bool full = false,
        [Description("The index of the first message to return, from a previous page's nextOffset. Defaults to 0.")] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Invalid("id is required: a session id from search or list_sessions.");
        if (offset < 0)
            return Invalid("offset must be 0 or more.");

        id = id.Trim();
        var opened = await _history.OpenSessionAsync(_registry, id, cancellationToken);
        switch (opened.Outcome)
        {
            case SessionOpenOutcome.NotFound:
                return Error(SessionsJson.NotFound, $"No backed-up session matches '{id}'. Use search or list_sessions to find its id.");
            case SessionOpenOutcome.Ambiguous:
                return Error(
                    SessionsJson.AmbiguousId,
                    $"'{id}' matches more than one session. Give more of the id.",
                    new JsonObject { ["candidates"] = new JsonArray([.. opened.Candidates.Select(SessionsJson.Candidate)]) });
            case SessionOpenOutcome.Failed:
                return Failure(opened.Failure, opened.Detail, opened.Registry)!;
        }

        var session = opened.Session!;
        try
        {
            var page = await PageAsync(_reader.ReadAsync(session, full, cancellationToken), session.Detail.Transcript.Harness, full, offset, cancellationToken);
            var answer = new JsonObject
            {
                ["session"] = SessionsJson.ListRecord(new ListedSession(session.Detail.Transcript, null)),
                ["offset"] = offset,
                ["messages"] = new JsonArray([.. page.Messages]),
            };
            if (page.NextOffset is { } next)
                answer["nextOffset"] = next;
            return Ok(answer);
        }
        catch (SessionReadException ex)
        {
            return Error(SessionsJson.RegistryUnreachable, ex.Message);
        }
    }

    [McpServerTool(Name = "get_memory", Title = "Read memory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read the memory files the developer's agents keep about a project, as the registry has them now, with secrets redacted: each " +
        "file's path, version, the machine that wrote it and its content. Without path, every file of the project; files that do not fit are " +
        "listed under omitted, to read one at a time with path.")]
    public async Task<CallToolResult> GetMemoryAsync(
        [Description("The project: its id, or text in its name. search and list_sessions show projects.")] string project,
        [Description("Only this file, by its path in the project's memory folder (for example MEMORY.md).")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(project))
            return Invalid("project is required: a project id or text in its name.");

        var result = await _history.GetMemoryAsync(_registry, project.Trim(), string.IsNullOrWhiteSpace(path) ? null : path, cancellationToken);
        switch (result.Outcome)
        {
            case MemoryReadOutcome.NotFound:
                return Error(SessionsJson.NotFound, result.Detail ?? "Not found.", Projects(result.Candidates));
            case MemoryReadOutcome.Ambiguous:
                return Error(AmbiguousProject, $"'{project}' matches more than one project with memory. Give its id.", Projects(result.Candidates));
            case MemoryReadOutcome.Failed:
                return Failure(result.Failure, result.Detail, result.Registry)!;
        }

        return Ok(MemoryRecord(result.Content!, MaxResultChars));
    }

    /// <summary>One page of a session's messages and where the next page starts.</summary>
    /// <param name="NextOffset">The index of the first message not returned, or <c>null</c> when the page reaches the end.</param>
    internal sealed record SessionPage(IReadOnlyList<JsonNode> Messages, int? NextOffset);

    /// <summary>The messages from <paramref name="offset"/> on that fit in <see cref="MaxResultChars"/>, each with its long fields cut.</summary>
    /// <remarks>
    /// The entries are read as they download and stop being read once the page is full, which ends the download, so
    /// memory stays bounded by the page whatever the transcript's size. A later page reads the transcript again from the
    /// start and skips to its offset. A page always holds at least one message, so paging always moves on.
    /// </remarks>
    internal static async Task<SessionPage> PageAsync(IAsyncEnumerable<ShownEntry> entries, string harness, bool full, int offset, CancellationToken cancellationToken)
    {
        var messages = new List<JsonNode>();
        var used = 0;
        var index = 0;
        await foreach (var shown in entries.WithCancellation(cancellationToken))
        {
            if (index < offset)
            {
                index++;
                continue;
            }

            var record = Shorten(SessionsJson.ShowRecord(shown, harness, full).AsObject());
            record.Insert(0, "index", index);
            var size = SessionsJson.Serialize(record).Length + 1;
            if (messages.Count > 0 && used + size > MaxResultChars - EnvelopeChars)
                return new SessionPage(messages, index);
            messages.Add(record);
            used += size;
            index++;
        }

        return new SessionPage(messages, null);
    }

    /// <summary>A project's memory as get_memory returns it: its files with their text, as many as fit in <paramref name="budget"/> characters.</summary>
    /// <remarks>
    /// Files are kept in the registry's order (by path) until the next would not fit; the rest are named under
    /// <c>omitted</c>. A first file too long on its own is cut, with <c>truncated</c> set, so the answer is never empty.
    /// </remarks>
    internal static JsonObject MemoryRecord(RemoteMemoryContent content, int budget)
    {
        var files = new JsonArray();
        var omitted = new JsonArray();
        var used = EnvelopeChars;
        foreach (var file in content.Files)
        {
            var record = new JsonObject { ["path"] = file.Path, ["version"] = file.Version, ["machineId"] = file.MachineId };
            if (file.MachineName is { } machine)
                record["machineName"] = machine;
            if (file.DeletedEverywhere)
                record["deletedEverywhere"] = true;
            record["content"] = Encoding.UTF8.GetString(file.Content);

            var size = SessionsJson.Serialize(record).Length + 1;
            if (omitted.Count > 0 || used + size > budget && files.Count > 0)
            {
                omitted.Add(file.Path);
                continue;
            }
            if (used + size > budget)
            {
                // Each character cut removes at least one character of JSON, so cutting the overflow (and a margin) fits.
                var text = record["content"]!.GetValue<string>();
                record["content"] = text[..Math.Max(0, text.Length - (used + size - budget) - 100)];
                record["truncated"] = true;
                size = SessionsJson.Serialize(record).Length + 1;
            }
            files.Add(record);
            used += size;
        }

        var answer = new JsonObject { ["projectId"] = content.ProjectId, ["project"] = content.ProjectName, ["files"] = files };
        if (omitted.Count > 0)
            answer["omitted"] = omitted;
        return answer;
    }

    // The record with each long text cut to MaxFieldChars and marked; a long tool input becomes its cut JSON text.
    private static JsonObject Shorten(JsonObject record)
    {
        foreach (var name in new[] { "text", "summary" })
        {
            if (record[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > MaxFieldChars)
            {
                record[name] = text[..MaxFieldChars];
                record["truncated"] = true;
            }
        }

        if (record["input"] is { } input && SessionsJson.Serialize(input) is { Length: > MaxFieldChars } raw)
        {
            record["input"] = raw[..MaxFieldChars];
            record["truncated"] = true;
        }

        return record;
    }

    private CallToolResult? Failure(SessionSyncFailure failure, string? detail, Uri? registry) =>
        SessionsPresenter.FailureMessage(failure, detail, registry, _catalog) is { } message
            ? Error(SessionsJson.Code(failure)!, message)
            : null;

    private static JsonObject Projects(IReadOnlyList<RemoteMemoryProject> projects) => new()
    {
        ["projects"] = new JsonArray([.. projects.Select(p => (JsonNode)new JsonObject { ["projectId"] = p.ProjectId, ["project"] = p.ProjectName, ["files"] = p.Files })]),
    };

    private static CallToolResult Invalid(string message) => Error(InvalidArgument, message);

    private static CallToolResult Error(string code, string message, JsonObject? extra = null) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = SessionsJson.Serialize(SessionsJson.ErrorObject(code, message, extra)) }],
    };

    private static CallToolResult Ok(JsonObject answer) => new() { Content = [new TextContentBlock { Text = SessionsJson.Serialize(answer) }] };
}
