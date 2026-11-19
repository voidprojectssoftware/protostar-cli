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
    /// <summary>Writes the line for <paramref name="result"/> and returns 0.</summary>
    public static int Render(string hookEvent, CaptureResult result, TextWriter output, Action<string> log)
    {
        var line = Describe(hookEvent, result);
        output.WriteLine(line);

        if (NeedsAttention(result))
            log($"{DateTimeOffset.UtcNow:O} {line}");

        return 0;
    }

    internal static string Describe(string hookEvent, CaptureResult result)
    {
        var prefix = $"protostar capture ({hookEvent})";
        if (result.Action == CaptureAction.Ignored)
            return $"{prefix}: nothing to do.";
        if (result.Action == CaptureAction.SessionHandedOff)
            return $"{prefix}: backing up the session in the background.";
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

    /// <summary>Writes the line for a background session push, logs it when it needs attention, and returns 0.</summary>
    /// <remarks>
    /// The push was started by a hook and nobody sees its output, so the line goes to the log when something is
    /// wrong. Exit code 0 whatever happened: the hook that started it has long since returned.
    /// </remarks>
    public static int RenderBackgroundPush(SessionSyncResult result, TextWriter output, Action<string> log)
    {
        var line = $"protostar sessions push (background): {DescribeSessions(result)}";
        output.WriteLine(line);

        if (result.Failure != SessionSyncFailure.None || result.Transcripts.Any(t => t.Status == TranscriptSyncStatus.Failed))
            log($"{DateTimeOffset.UtcNow:O} {line}");

        return 0;
    }

    internal static string DescribeSessions(SessionSyncResult sync)
    {
        if (sync.Failure != SessionSyncFailure.None)
        {
            return sync.Failure switch
            {
                SessionSyncFailure.NotLoggedIn => "session not backed up: not signed in. Run `protostar auth login`.",
                SessionSyncFailure.SessionExpired => "session not backed up: the session expired. Run `protostar auth login`.",
                SessionSyncFailure.RegistryUnreachable => "session not backed up: the registry could not be reached. It goes on the next sync.",
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

    private static string Reason(SkillPushRunResult push) => push.Failure switch
    {
        SkillPushRunFailure.NotLoggedIn => "not signed in. Run `protostar auth login`.",
        SkillPushRunFailure.SessionExpired => "the session expired. Run `protostar auth login`.",
        SkillPushRunFailure.RegistryUnreachable => $"the registry at {push.Registry?.GetLeftPart(UriPartial.Authority)} is unreachable.",
        _ => $"{push.Detail ?? push.Failure.ToString()}",
    };

    // Worth a log line: anything other than success, ignore, or "not a local skill".
    private static bool NeedsAttention(CaptureResult result) =>
        result.Action == CaptureAction.Failed ||
        (result.Push is { } push &&
         (push.Failure is not (SkillPushRunFailure.None or SkillPushRunFailure.SkillNotFound) ||
          push.Entries.Any(e => e.Outcome.Status is SkillPushStatus.Refused or SkillPushStatus.Failed or SkillPushStatus.Conflict)));
}
