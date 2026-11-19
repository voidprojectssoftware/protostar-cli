using System.Text.Json;
using Protostar.Cli.Sessions;
using Protostar.Cli.Skills;

namespace Protostar.Cli.Capture;

/// <summary>
/// Production <see cref="ICaptureService"/>: backs up a skill after the harness uses it, and the session
/// transcript as it grows.
/// </summary>
/// <remarks>
/// Acts on Claude Code's <c>PostToolUse</c> event for the <c>Skill</c> tool, whose payload carries
/// <c>tool_input.skill</c> (the skill name) and <c>cwd</c> (where project skills are found). The push
/// goes through <see cref="ISkillPushService"/>, whose ledger makes an unchanged skill cost no network
/// call. Plugin skills (<c>plugin:name</c>) are not local user skills and are ignored.
/// </remarks>
internal sealed class CaptureService : ICaptureService
{
    /// <summary>Time allowed for uploading after each reply; the harness waits for the hook.</summary>
    internal static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(3);

    /// <summary>Minimum time between syncs of one session from <c>Stop</c> hooks.</summary>
    internal static readonly TimeSpan StopThrottle = TimeSpan.FromSeconds(20);

    /// <summary>Time allowed for the final upload when a session ends.</summary>
    internal static readonly TimeSpan SessionEndBudget = TimeSpan.FromSeconds(5);

    private readonly ISkillPushService _push;
    private readonly ISessionSyncService _sessions;

    public CaptureService(ISkillPushService push, ISessionSyncService sessions)
    {
        _push = push;
        _sessions = sessions;
    }

    /// <inheritdoc />
    public async Task<CaptureResult> HandleAsync(string hookEvent, string payload, CancellationToken cancellationToken)
    {
        if (hookEvent is "Stop" or "SessionEnd")
            return await SyncSessionAsync(hookEvent, payload, cancellationToken);

        if (!string.Equals(hookEvent, "PostToolUse", StringComparison.Ordinal))
            return Ignore(null, $"{hookEvent} is not acted on yet.");

        if (!TryReadSkillUse(payload, out var skill, out var cwd))
            return Ignore(null, "Not a Skill tool use.");

        if (skill.Contains(':', StringComparison.Ordinal))
            return Ignore(skill, "Plugin skills are not backed up.");

        try
        {
            var request = new SkillPushRequest([skill], HarnessId: null, HarnessHome: null, ProjectStart: cwd, Registry: null);
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

    // Syncs the session named by the payload's transcript_path (and its subagents), within a small time budget.
    // Stop fires after every reply, so it is throttled; SessionEnd always runs.
    private async Task<CaptureResult> SyncSessionAsync(string hookEvent, string payload, CancellationToken cancellationToken)
    {
        if (ReadString(payload, "transcript_path") is not { } transcriptPath || !File.Exists(transcriptPath))
            return Ignore(null, "No transcript to back up.");

        try
        {
            var stop = hookEvent == "Stop";
            var request = new SessionSyncRequest(
                Registry: null, HarnessId: null, HarnessHome: null, SessionPath: transcriptPath,
                Budget: stop ? StopBudget : SessionEndBudget, Throttle: stop ? StopThrottle : null);
            var result = await _sessions.SyncAsync(request, progress: null, cancellationToken);
            return new CaptureResult(CaptureAction.SessionBackedUp, null, null, null, result);
        }
        catch (OperationCanceledException)
        {
            return new CaptureResult(CaptureAction.Failed, null, null, "Timed out backing up the session.");
        }
        catch (Exception ex)
        {
            return new CaptureResult(CaptureAction.Failed, null, null, ex.Message);
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
