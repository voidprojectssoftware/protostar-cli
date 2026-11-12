using System.Text.Json;
using Protostar.Cli.Skills;

namespace Protostar.Cli.Capture;

/// <summary>
/// Production <see cref="ICaptureService"/>: backs up a skill after the harness uses it.
/// </summary>
/// <remarks>
/// Acts on Claude Code's <c>PostToolUse</c> event for the <c>Skill</c> tool, whose payload carries
/// <c>tool_input.skill</c> (the skill name) and <c>cwd</c> (where project skills are found). The push
/// goes through <see cref="ISkillPushService"/>, whose ledger makes an unchanged skill cost no network
/// call. Plugin skills (<c>plugin:name</c>) are not local user skills and are ignored.
/// </remarks>
internal sealed class CaptureService : ICaptureService
{
    private readonly ISkillPushService _push;

    public CaptureService(ISkillPushService push) => _push = push;

    /// <inheritdoc />
    public async Task<CaptureResult> HandleAsync(string hookEvent, string payload, CancellationToken cancellationToken)
    {
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
