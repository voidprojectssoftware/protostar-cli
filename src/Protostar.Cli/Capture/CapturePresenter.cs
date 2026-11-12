using Protostar.Cli.Registry;
using Protostar.Cli.Skills;

namespace Protostar.Cli.Capture;

/// <summary>
/// The output of <c>protostar capture</c>: one plain line, and the exit code, which is always 0.
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
        if (result.Action == CaptureAction.Failed || result.Push is null)
            return $"{prefix}: could not back up skill '{result.SkillName}': {result.Message}";

        var push = result.Push;
        if (push.Failure != SkillPushRunFailure.None)
        {
            return push.Failure == SkillPushRunFailure.SkillNotFound
                ? $"{prefix}: skill '{result.SkillName}' is not a local skill; not backed up."
                : $"{prefix}: could not back up skill '{result.SkillName}': {Reason(push)}";
        }

        var outcome = push.Entries.FirstOrDefault()?.Outcome;
        return outcome?.Status switch
        {
            SkillPushStatus.Created => $"{prefix}: backed up skill '{result.SkillName}' as v{outcome.Version}.",
            SkillPushStatus.Unchanged => $"{prefix}: skill '{result.SkillName}' unchanged.",
            null => $"{prefix}: nothing to do.",
            _ => $"{prefix}: skill '{result.SkillName}' not backed up: {outcome.Message}",
        };
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
          push.Entries.Any(e => e.Outcome.Status is SkillPushStatus.Refused or SkillPushStatus.Failed)));
}
