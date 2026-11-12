using Protostar.Cli.Skills;

namespace Protostar.Cli.Capture;

/// <summary>
/// Handles one harness hook event delivered to <c>protostar capture</c>.
/// </summary>
/// <remarks>
/// Runs inside the user's agent session, so it must never throw and never block for long: every
/// failure comes back as a <see cref="CaptureResult"/>, and the caller bounds the time with its
/// cancellation token.
/// </remarks>
internal interface ICaptureService
{
    /// <summary>Acts on the hook <paramref name="payload"/> (the JSON the harness wrote to stdin).</summary>
    /// <param name="hookEvent">The event name the hook was installed for, for example <c>PostToolUse</c>.</param>
    /// <param name="payload">The raw JSON from the harness; may be empty or malformed.</param>
    /// <param name="cancellationToken">Bounds how long the hook may run.</param>
    Task<CaptureResult> HandleAsync(string hookEvent, string payload, CancellationToken cancellationToken);
}

/// <summary>What a capture did.</summary>
internal enum CaptureAction
{
    /// <summary>The event is not one protostar acts on yet; nothing was done.</summary>
    Ignored,

    /// <summary>A used skill was checked against the registry; see <see cref="CaptureResult.Push"/>.</summary>
    SkillBackedUp,

    /// <summary>The event looked actionable but could not be handled (bad payload, timeout, error).</summary>
    Failed,
}

/// <summary>The outcome of one capture.</summary>
/// <param name="Action">What happened.</param>
/// <param name="SkillName">The skill the event was about, when there was one.</param>
/// <param name="Push">The push run for <see cref="CaptureAction.SkillBackedUp"/>; <c>null</c> otherwise.</param>
/// <param name="Message">Why the capture was ignored or failed; <c>null</c> on success.</param>
internal sealed record CaptureResult(CaptureAction Action, string? SkillName, SkillPushRunResult? Push, string? Message);
