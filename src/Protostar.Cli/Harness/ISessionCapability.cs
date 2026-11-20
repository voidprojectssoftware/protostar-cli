namespace Protostar.Cli.Harness;

/// <summary>Capability: a harness whose session transcripts protostar can find on disk.</summary>
/// <remarks>
/// A transcript is one append-only JSONL file: a whole session, or one subagent's run inside a session.
/// The CLI finds and uploads the files and the registry understands their format, with two exceptions:
/// <see cref="ReadWorkingDirectory"/>, because only the CLI can run git in that directory to find its project, and
/// <see cref="ReadConversationLine"/>, because rendering a conversation is presentation, which is the CLI's job; the
/// registry serves the (redacted) lines and keeps no rendering of its own.
/// </remarks>
internal interface ISessionCapability
{
    /// <summary>Every transcript under <paramref name="location"/>, sessions and subagents.</summary>
    /// <remarks>Returns an empty list (never throws) when there are none or the folders are unreadable.</remarks>
    IReadOnlyList<LocalTranscript> LocateTranscripts(HarnessLocation location);

    /// <summary>
    /// The transcript at <paramref name="transcriptPath"/> plus its subagents, as named by a hook payload.
    /// </summary>
    /// <returns>An empty list when the path is not one of this harness's transcripts.</returns>
    IReadOnlyList<LocalTranscript> LocateSession(string transcriptPath);

    /// <summary>The working directory the transcript's session ran in, as the harness recorded it.</summary>
    /// <returns><c>null</c> when the file records none yet (or cannot be read).</returns>
    /// <remarks>Read from the file's own lines, never decoded from the folder it sits in, which is lossy (ADR 0002).</remarks>
    string? ReadWorkingDirectory(LocalTranscript transcript);

    /// <summary>The conversation entries one transcript line holds, in the order they happened.</summary>
    /// <returns>Nothing for a line that is blank, not valid JSON, or of a type that is not part of the conversation; never throws for a line's content.</returns>
    /// <remarks>Called one line at a time, so a transcript of any size is read without holding it in memory.</remarks>
    IEnumerable<ConversationEntry> ReadConversationLine(string line);
}

/// <summary>Whether a transcript is a whole session or one subagent's run.</summary>
internal enum TranscriptKind
{
    Session,
    Subagent,
}

/// <summary>A transcript file found on this machine.</summary>
/// <param name="Harness">The harness id, for example <c>claude-code</c>.</param>
/// <param name="Key">Its id within the harness: the session id, or <c>&lt;session id&gt;.&lt;agent id&gt;</c>.</param>
/// <param name="Kind">Session or subagent.</param>
/// <param name="ParentKey">For a subagent, its session's key.</param>
/// <param name="Path">The file.</param>
/// <param name="MetadataJson">Harness metadata kept beside it (a Claude Code subagent's <c>.meta.json</c>), or <c>null</c>.</param>
internal sealed record LocalTranscript(
    string Harness, string Key, TranscriptKind Kind, string? ParentKey, string Path, string? MetadataJson);
