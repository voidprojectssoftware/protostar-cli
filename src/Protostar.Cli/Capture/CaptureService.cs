using System.Text.Json;
using Protostar.Cli.Skills;

namespace Protostar.Cli.Capture;

/// <summary>
/// Production <see cref="ICaptureService"/>: backs up a skill after the harness uses it, and the session
/// transcript as it grows.
/// </summary>
/// <remarks>
/// Acts on Claude Code's <c>PostToolUse</c> event for the <c>Skill</c> tool, whose payload carries
/// <c>tool_input.skill</c> (the skill name) and <c>cwd</c> (where project skills are found). Neither the input
/// (<c>skill</c>, <c>args</c>) nor the tool's response (<c>success</c>, <c>commandName</c>, <c>allowedTools</c>,
/// <c>model</c>, <c>status</c>) says whether the skill used was a global or a project skill, or gives its path,
/// and both can share a name. So the hook backs up every local skill of that name: the global one and the one
/// in <c>cwd</c>'s project, each as its own registry skill. The push goes through
/// <see cref="ISkillPushService"/>, whose ledger makes the one that did not change cost no network call, and makes a
/// copy the registry already refused as stale (changed here and in the registry) cost none either until it changes.
/// Plugin skills (<c>plugin:name</c>) are not local user skills and are ignored.
/// <c>Stop</c> and <c>SessionEnd</c> hand the session to <see cref="IBackgroundPush"/> and return at once.
/// </remarks>
internal sealed class CaptureService : ICaptureService
{
    /// <summary>Minimum time between pushes of one session started by <c>Stop</c> hooks.</summary>
    /// <remarks>
    /// <c>Stop</c> fires once per reply. The push runs in the background, so the window costs the user no time
    /// either way; what it bounds is the work: at most two pushes a minute per session, each a process start and
    /// a few requests, however fast the replies come. 30 seconds is also the most of a live session a crash can
    /// lose beyond the reply in flight, which is a fair trade for a backup that is otherwise free. A normal end
    /// is covered by <c>SessionEnd</c>, which is never throttled.
    /// </remarks>
    internal static readonly TimeSpan StopThrottle = TimeSpan.FromSeconds(30);

    private readonly ISkillPushService _push;
    private readonly IBackgroundPush _background;

    public CaptureService(ISkillPushService push, IBackgroundPush background)
    {
        _push = push;
        _background = background;
    }

    /// <inheritdoc />
    public async Task<CaptureResult> HandleAsync(string hookEvent, string payload, CancellationToken cancellationToken)
    {
        if (hookEvent is "Stop" or "SessionEnd")
            return HandOffSession(hookEvent, payload);

        if (!string.Equals(hookEvent, "PostToolUse", StringComparison.Ordinal))
            return Ignore(null, $"{hookEvent} is not acted on yet.");

        if (!TryReadSkillUse(payload, out var skill, out var cwd))
            return Ignore(null, "Not a Skill tool use.");

        if (skill.Contains(':', StringComparison.Ordinal))
            return Ignore(skill, "Plugin skills are not backed up.");

        try
        {
            var request = new SkillPushRequest([skill], HarnessId: null, HarnessHome: null, ProjectStart: cwd, Registry: null, FromHook: true);
            var result = await _push.PushAsync(request, cancellationToken);
            return new CaptureResult(CaptureAction.SkillBackedUp, skill, result, null);
        }
        catch (OperationCanceledException)
        {
            return new CaptureResult(CaptureAction.Failed, skill, null, "Timed out backing up the skill.");
        }
        catch (Exception ex)
        {
            // The hook must never fail the harness, whatever went wrong underneath.
            return new CaptureResult(CaptureAction.Failed, skill, null, ex.Message);
        }
    }

    // Hands the session named by the payload's transcript_path (and its subagents) to a detached push, so the
    // harness is not kept waiting for the upload. Stop fires after every reply, so it is throttled; SessionEnd
    // always runs.
    private CaptureResult HandOffSession(string hookEvent, string payload)
    {
        if (ReadString(payload, "transcript_path") is not { } transcriptPath || !File.Exists(transcriptPath))
            return Ignore(null, "No transcript to back up.");

        try
        {
            _background.Start(transcriptPath, hookEvent == "Stop" ? StopThrottle : null);
            return new CaptureResult(CaptureAction.SessionHandedOff, null, null, null);
        }
        catch (Exception ex)
        {
            // Process start failures surface as several exception types; none may fail the harness.
            return new CaptureResult(CaptureAction.Failed, null, null, $"Could not start the session backup: {ex.Message}");
        }
    }

    private static string? ReadString(string payload, string property)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Reads tool_name, tool_input.skill and cwd from a PostToolUse payload. Anything else is not a skill use.
    private static bool TryReadSkillUse(string payload, out string skill, out string? cwd)
    {
        skill = string.Empty;
        cwd = null;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tool_name", out var tool) || tool.ValueKind != JsonValueKind.String || tool.GetString() != "Skill" ||
                !root.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object ||
                !input.TryGetProperty("skill", out var name) || name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()))
            {
                return false;
            }

            skill = name.GetString()!.Trim();
            if (root.TryGetProperty("cwd", out var dir) && dir.ValueKind == JsonValueKind.String && Directory.Exists(dir.GetString()))
                cwd = dir.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CaptureResult Ignore(string? skill, string message) => new(CaptureAction.Ignored, skill, null, message);
}
