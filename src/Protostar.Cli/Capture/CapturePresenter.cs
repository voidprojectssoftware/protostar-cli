using Protostar.Cli.Agents;
using Protostar.Cli.Memory;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;

namespace Protostar.Cli.Capture;

/// <summary>
/// The output of <c>protostar capture</c> and of the background session push it starts: one plain line, and exit code 0.
/// </summary>
/// <remarks>
/// For a successful hook Claude Code shows stdout in the transcript only, never to the model, so the
/// line is for a person reading the transcript. Plain text, not Spectre markup, because the harness
/// captures it raw. Failures that a person should act on (not signed in, refused) are also appended to
/// <c>~/.protostar/capture.log</c>, since the hook otherwise runs out of sight.
/// </remarks>
internal static class CapturePresenter
{
    /// <summary>Writes the line for <paramref name="result"/>, logs and records it, and returns 0.</summary>
    /// <param name="record">Receives the structured outcome (<see cref="CaptureRecords"/>) when there is one to keep.</param>
    public static int Render(string hookEvent, CaptureResult result, TextWriter output, Action<string> log, Action<CaptureRecord> record)
    {
        var line = Describe(hookEvent, result);
        output.WriteLine(line);

        if (NeedsAttention(result))
            log($"{DateTimeOffset.UtcNow:O} {line}");
        if (Record(result, line) is { } kept)
            record(kept);

        return 0;
    }

    /// <summary>Writes and logs the line for a hook whose input never finished arriving, and returns 0.</summary>
    /// <remarks>
    /// Logged, since a harness that holds stdin open is worth knowing about, but not recorded: with no payload it is not
    /// known whether the event was a skill use or a session to back up, so neither kind is marked as failing.
    /// </remarks>
    public static int RenderUnread(string hookEvent, TimeSpan waited, TextWriter output, Action<string> log)
    {
        var line = $"protostar capture ({hookEvent}): the hook's input did not end within {waited.TotalSeconds:0} seconds, so nothing was backed up.";
        output.WriteLine(line);
        log($"{DateTimeOffset.UtcNow:O} {line}");
        return 0;
    }

    // A skill backup's outcome, or a failure to start the session backup. Nothing for an ignored event, a session
    // handed off (its push keeps its own record), or a skill that is not local, since none of them backed anything up.
    internal static CaptureRecord? Record(CaptureResult result, string line)
    {
        var now = DateTimeOffset.UtcNow;
        if (result.Action == CaptureAction.Failed)
            return new CaptureRecord(now, result.SkillName is null ? CaptureKind.Sessions : CaptureKind.Skills, false, null, 0,
                Reason: CaptureFailure.Other, Error: result.Message);
        if (result.Action != CaptureAction.SkillBackedUp || result.Push is not { } push || push.Failure == SkillPushRunFailure.SkillNotFound)
            return null;

        var registry = push.Registry?.GetLeftPart(UriPartial.Authority);
        var reason = push.Failure switch
        {
            SkillPushRunFailure.None => push.Entries.Any(e => e.Outcome.Status is SkillPushStatus.Failed) ? CaptureFailure.Other
                : push.Entries.Any(e => e.Outcome.Status is SkillPushStatus.Refused or SkillPushStatus.Conflict or SkillPushStatus.Stale or SkillPushStatus.Removed)
                    ? CaptureFailure.Refused
                    : (CaptureFailure?)null,
            SkillPushRunFailure.NotLoggedIn => CaptureFailure.NotSignedIn,
            SkillPushRunFailure.SessionExpired => CaptureFailure.SessionExpired,
            SkillPushRunFailure.RegistryUnreachable => CaptureFailure.RegistryUnreachable,
            SkillPushRunFailure.Incompatible => CaptureFailure.Incompatible,
            _ => CaptureFailure.Other,
        };
        return reason is null
            ? new CaptureRecord(now, CaptureKind.Skills, true, registry, push.Entries.Count)
            : new CaptureRecord(now, CaptureKind.Skills, false, registry, push.Entries.Count, Reason: reason, Error: ErrorText(line));
    }

    // The human line without its "protostar capture (Event): " prefix, so status can quote it as the error.
    private static string ErrorText(string line) => line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..];

    internal static string Describe(string hookEvent, CaptureResult result)
    {
        var prefix = $"protostar capture ({hookEvent})";
        if (result.Action == CaptureAction.Ignored)
            return $"{prefix}: nothing to do.";
        if (result.Action == CaptureAction.SessionHandedOff)
            return result.AgentsHandedOff > 0
                ? $"{prefix}: backing up the session and {SessionsPresenter.Count(result.AgentsHandedOff, "changed agent")} in the background."
                : $"{prefix}: backing up the session in the background.";
        if (result.Action == CaptureAction.AgentsHandedOff)
            return $"{prefix}: backing up {SessionsPresenter.Count(result.AgentsHandedOff, "changed agent")} in the background.";
        if (result.Action == CaptureAction.Failed && result.SkillName is null)
            return $"{prefix}: {result.Message}";
        if (result.Action == CaptureAction.Failed || result.Push is null)
            return $"{prefix}: could not back up skill '{result.SkillName}': {result.Message}";

        var push = result.Push;
        if (push.Failure != SkillPushRunFailure.None)
        {
            return push.Failure == SkillPushRunFailure.SkillNotFound
                ? $"{prefix}: skill '{result.SkillName}' is not a local skill; not backed up."
                : $"{prefix}: could not back up skill '{result.SkillName}': {Reason(push)}";
        }

        // A global and a project skill can share the used name, and both are backed up (see CaptureService).
        if (push.Entries.Count > 1)
            return $"{prefix}: skill '{result.SkillName}': {string.Join("; ", push.Entries.Select(DescribeScoped))}.";

        var outcome = push.Entries.FirstOrDefault()?.Outcome;
        var line = outcome?.Status switch
        {
            SkillPushStatus.Created => $"{prefix}: backed up skill '{result.SkillName}' as v{outcome.Version}.",
            SkillPushStatus.Unchanged => $"{prefix}: skill '{result.SkillName}' unchanged.",
            SkillPushStatus.Stale => $"{prefix}: skill '{result.SkillName}' not backed up: {outcome.Message} Run `protostar skills remote` to choose which to keep.",
            null => $"{prefix}: nothing to do.",
            _ => $"{prefix}: skill '{result.SkillName}' not backed up: {outcome.Message}",
        };

        return line;
    }

    // "global backed up as v2", "project (github.com/acme/a) unchanged", or why one was not backed up.
    private static string DescribeScoped(SkillPushEntry entry)
    {
        var scope = entry.Outcome.ProjectName is { } project ? $"project ({project})" : entry.Skill.Scope.ToString().ToLowerInvariant();
        return entry.Outcome.Status switch
        {
            SkillPushStatus.Created => $"{scope} backed up as v{entry.Outcome.Version}",
            SkillPushStatus.Unchanged => $"{scope} unchanged",
            _ => $"{scope} at {entry.Skill.Path} not backed up: {entry.Outcome.Message}",
        };
    }

    /// <summary>Writes the line for the background agent push a <c>SessionEnd</c> started, logs it when it needs attention, records it, and returns 0.</summary>
    /// <remarks>A refusal answered from the ledger (<see cref="Registry.AgentPushOutcome.Remembered"/>) is not logged again.</remarks>
    public static int RenderBackgroundAgentPush(AgentPushRunResult result, TextWriter output, Action<string> log, Action<CaptureRecord> record)
    {
        var line = $"protostar agents push (background): {DescribeAgents(result)}";
        output.WriteLine(line);

        var attention = result.Failure != AgentPushRunFailure.None ||
            result.Entries.Any(e => e.Outcome.Status is AgentPushStatus.Refused or AgentPushStatus.Failed or AgentPushStatus.Conflict ||
                                    e.Outcome is { Status: AgentPushStatus.Stale or AgentPushStatus.Removed, Remembered: false });
        if (attention)
            log($"{DateTimeOffset.UtcNow:O} {line}");
        record(Record(result, line));
        return 0;
    }

    internal static CaptureRecord Record(AgentPushRunResult result, string line)
    {
        var now = DateTimeOffset.UtcNow;
        var registry = result.Registry?.GetLeftPart(UriPartial.Authority);
        CaptureFailure? reason = result.Failure switch
        {
            AgentPushRunFailure.None => result.Entries.Any(e => e.Outcome.Status is AgentPushStatus.Failed) ? CaptureFailure.Other
                : result.Entries.Any(e => e.Outcome.Status is AgentPushStatus.Refused or AgentPushStatus.Conflict or AgentPushStatus.Stale or AgentPushStatus.Removed)
                    ? CaptureFailure.Refused
                    : null,
            AgentPushRunFailure.NotLoggedIn => CaptureFailure.NotSignedIn,
            AgentPushRunFailure.SessionExpired => CaptureFailure.SessionExpired,
            AgentPushRunFailure.RegistryUnreachable => CaptureFailure.RegistryUnreachable,
            AgentPushRunFailure.Incompatible => CaptureFailure.Incompatible,
            _ => CaptureFailure.Other,
        };
        return reason is null
            ? new CaptureRecord(now, CaptureKind.Agents, true, registry, result.Entries.Count)
            : new CaptureRecord(now, CaptureKind.Agents, false, registry, result.Entries.Count, Reason: reason, Error: ErrorText(line));
    }

    // "agent 'reviewer' backed up as v3; agent 'linter' not backed up: ...", or why the run stopped.
    internal static string DescribeAgents(AgentPushRunResult result)
    {
        if (AgentPushPresenter.FailureMessage(result, null) is { } failure)
            return $"agents not backed up: {failure}";

        var changed = result.Entries.Where(e => e.Outcome.Status != AgentPushStatus.Unchanged).ToList();
        if (changed.Count == 0)
            return "agents already backed up.";
        return string.Join("; ", changed.Select(e => e.Outcome.Status switch
        {
            AgentPushStatus.Created => $"agent '{e.Agent.Name}' backed up as v{e.Outcome.Version}",
            AgentPushStatus.Stale => $"agent '{e.Agent.Name}' not backed up: {e.Outcome.Message} Run `protostar agents push {e.Agent.Name}` to choose which to keep",
            _ => $"agent '{e.Agent.Name ?? e.Agent.RelativePath}' not backed up: {e.Outcome.Message}",
        })) + ".";
    }

    /// <summary>Writes the line for a background session push, logs it when it needs attention, and returns 0.</summary>
    /// <remarks>
    /// The push was started by a hook and nobody sees its output, so the line goes to the log when something is
    /// wrong. Exit code 0 whatever happened: the hook that started it has long since returned.
    /// </remarks>
    public static int RenderBackgroundPush(SessionSyncResult result, TextWriter output, Action<string> log, Action<CaptureRecord> record)
    {
        var line = $"protostar sessions push (background): {DescribeSessions(result)}{DescribeRetries(result.Retried)}";
        output.WriteLine(line);

        if (result.Failure != SessionSyncFailure.None ||
            result.Transcripts.Concat(result.Retried ?? []).Any(t => t.Status == TranscriptSyncStatus.Failed))
            log($"{DateTimeOffset.UtcNow:O} {line}");
        if (Record(result, line) is { } kept)
            record(kept);

        return 0;
    }

    // Nothing when every transcript was skipped (throttled, or another push has it) and no earlier session was
    // retried: such a run tried nothing, so it says nothing about whether backups work.
    internal static CaptureRecord? Record(SessionSyncResult result, string line)
    {
        var now = DateTimeOffset.UtcNow;
        var registry = result.Registry?.GetLeftPart(UriPartial.Authority);
        var handled = result.Transcripts.Concat(result.Retried ?? []).ToList();
        var bytes = handled.Sum(t => t.BytesUploaded);
        var error = ErrorText(line);
        if (result.Failure != SessionSyncFailure.None)
            return new CaptureRecord(now, CaptureKind.Sessions, false, registry, 0, 0, Reason(result.Failure), error);
        if (handled.Any(t => t.Status == TranscriptSyncStatus.Failed))
            return new CaptureRecord(now, CaptureKind.Sessions, false, registry, handled.Count, bytes, CaptureFailure.Other, error);
        return handled.All(t => t.Status == TranscriptSyncStatus.Skipped)
            ? null
            : new CaptureRecord(now, CaptureKind.Sessions, true, registry, handled.Count, bytes);
    }

    private static CaptureFailure Reason(SessionSyncFailure failure) => failure switch
    {
        SessionSyncFailure.NotLoggedIn => CaptureFailure.NotSignedIn,
        SessionSyncFailure.SessionExpired => CaptureFailure.SessionExpired,
        SessionSyncFailure.RegistryUnreachable => CaptureFailure.RegistryUnreachable,
        SessionSyncFailure.Incompatible => CaptureFailure.Incompatible,
        _ => CaptureFailure.Other,
    };

    /// <summary>Reports what a hook's background push did with its session's project memory; logs only failures, and never contents.</summary>
    public static void RenderBackgroundMemoryPush(MemoryPushResult result, TextWriter output, Action<string> log, Action<CaptureRecord> record)
    {
        var line = "protostar memory push (background): " + (result.Failure != SessionSyncFailure.None
            ? $"memory not backed up: {result.Detail ?? MemoryFailure(result.Failure)}"
            : result.Folders.FirstOrDefault(f => f.Error is not null) is { } failed
                ? $"memory of {failed.Directory} not backed up: {failed.Error}"
                : $"{result.Folders.Sum(f => f.Uploaded)} memory files backed up, {result.Folders.Sum(f => f.Deleted)} deletions recorded.");
        output.WriteLine(line);
        if (!result.Complete)
            log($"{DateTimeOffset.UtcNow:O} {line}");
        if (Record(result, line) is { } kept)
            record(kept);
    }

    private static string MemoryFailure(SessionSyncFailure failure) => failure switch
    {
        SessionSyncFailure.NotLoggedIn => "not signed in. Run `protostar auth login`.",
        SessionSyncFailure.SessionExpired => "the session expired. Run `protostar auth login`.",
        SessionSyncFailure.RegistryUnreachable => "the registry could not be reached. The next hook backup that reaches it tries again.",
        _ => failure.ToString(),
    };

    // Nothing when the session's project keeps no memory: there was nothing to back up.
    internal static CaptureRecord? Record(MemoryPushResult result, string line)
    {
        var now = DateTimeOffset.UtcNow;
        var registry = result.Registry?.GetLeftPart(UriPartial.Authority);
        var uploaded = result.Folders.Sum(f => f.Uploaded);
        if (result.Failure != SessionSyncFailure.None)
            return new CaptureRecord(now, CaptureKind.Memory, false, registry, 0, Reason: Reason(result.Failure), Error: ErrorText(line));
        if (!result.Complete)
            return new CaptureRecord(now, CaptureKind.Memory, false, registry, uploaded, Reason: CaptureFailure.Other, Error: ErrorText(line));
        return result.Folders.Count == 0 ? null : new CaptureRecord(now, CaptureKind.Memory, true, registry, uploaded);
    }

    internal static string DescribeSessions(SessionSyncResult sync)
    {
        if (sync.Failure != SessionSyncFailure.None)
        {
            return sync.Failure switch
            {
                SessionSyncFailure.NotLoggedIn => "session not backed up: not signed in. Run `protostar auth login`.",
                SessionSyncFailure.SessionExpired => "session not backed up: the session expired. Run `protostar auth login`.",
                SessionSyncFailure.RegistryUnreachable => "session not backed up: the registry could not be reached. The next hook backup that reaches it retries this session, or run `protostar sessions push`.",
                _ => $"session not backed up: {sync.Detail ?? sync.Failure.ToString()}",
            };
        }

        var sent = sync.Transcripts.Sum(t => t.BytesUploaded);
        var failed = sync.Transcripts.FirstOrDefault(t => t.Status == TranscriptSyncStatus.Failed);
        if (failed is not null)
            return $"session not fully backed up: {failed.Message}";
        if (sync.Transcripts.Any(t => t.Status is TranscriptSyncStatus.Partial))
            return $"backed up {SessionsPresenter.Bytes(sent)} of the session; the rest goes next time.";
        if (sync.Transcripts.FirstOrDefault(t => t.Status is TranscriptSyncStatus.Skipped) is { } skipped)
            return sent > 0
                ? $"backed up {SessionsPresenter.Bytes(sent)} of the session; {SessionsPresenter.Bytes(skipped.BytesPending)} waits: {skipped.Message}"
                : $"{SessionsPresenter.Bytes(skipped.BytesPending)} of the session not backed up yet: {skipped.Message}";
        return sent > 0 ? $"backed up {SessionsPresenter.Bytes(sent)} of the session." : "session already backed up.";
    }

    // What the push did for earlier sessions whose backup failed, counted per session, subagents with their session.
    internal static string DescribeRetries(IReadOnlyList<TranscriptSyncOutcome>? retried)
    {
        if (retried is null || retried.Count == 0)
            return string.Empty;

        var sessions = retried.GroupBy(o => (o.Transcript.Harness, o.Transcript.ParentKey ?? o.Transcript.Key)).ToList();
        var backedUp = sessions.Count(g => g.All(o => o.Status is TranscriptSyncStatus.UpToDate or TranscriptSyncStatus.Uploaded));
        var failed = sessions.Count(g => g.Any(o => o.Status == TranscriptSyncStatus.Failed));
        var waiting = sessions.Count - backedUp - failed;
        var line = $" Retried {SessionsPresenter.Count(sessions.Count, "earlier session")} whose backup had failed: {backedUp} backed up";
        if (failed > 0)
            line += $", {failed} failed again (retried later)";
        if (waiting > 0)
            line += $", {waiting} left to another push";
        return line + ".";
    }

    private static string Reason(SkillPushRunResult push) => push.Failure switch
    {
        SkillPushRunFailure.NotLoggedIn => "not signed in. Run `protostar auth login`.",
        SkillPushRunFailure.SessionExpired => "the session expired. Run `protostar auth login`.",
        SkillPushRunFailure.RegistryUnreachable => $"the registry at {push.Registry?.GetLeftPart(UriPartial.Authority)} is unreachable.",
        _ => $"{push.Detail ?? push.Failure.ToString()}",
    };

    // Worth a log line: anything other than success, ignore, or "not a local skill". A stale refusal, or one as a copy of a
    // skill removed from the registry, is logged once:
    // later uses of the same content are answered from the ledger (Remembered) and not logged again.
    private static bool NeedsAttention(CaptureResult result) =>
        result.Action == CaptureAction.Failed ||
        (result.Push is { } push &&
         (push.Failure is not (SkillPushRunFailure.None or SkillPushRunFailure.SkillNotFound) ||
          push.Entries.Any(e => e.Outcome.Status is SkillPushStatus.Refused or SkillPushStatus.Failed or SkillPushStatus.Conflict ||
                                e.Outcome is { Status: SkillPushStatus.Stale or SkillPushStatus.Removed, Remembered: false })));
}
