using System.ComponentModel;
using Protostar.Cli.Capture;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// Invoked by an installed harness hook when a captured event fires (e.g. PostToolUse on the Skill tool).
/// </summary>
/// <remarks>
/// Reads the hook's JSON payload from stdin and hands it to <see cref="ICaptureService"/>, which backs
/// up a used skill when it changed, or hands a session to a background push. Bounded by <see cref="Timeout"/>, and always exits 0 so it never
/// blocks or fails the harness.
/// </remarks>
internal sealed class CaptureCommand : AsyncCommand<CaptureCommand.Settings>
{
    /// <summary>How long a capture may run before it gives up. The upload budgets inside are smaller.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly ICaptureService _capture;

    public CaptureCommand(ICaptureService capture) => _capture = capture;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--hook <EVENT>")]
        [Description("The harness event that triggered capture (e.g. PostToolUse, SessionStart).")]
        public string? Hook { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var hook = settings.Hook ?? "unknown";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Timeout);

        // The bound covers reading stdin too, or a harness that never closes it would hang the hook (#122).
        string payload;
        try
        {
            payload = await ReadPayloadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return CapturePresenter.RenderUnread(hook, Timeout, Console.Out, CaptureLog.Append);
        }

        var result = await _capture.HandleAsync(hook, payload, timeout.Token);
        return CapturePresenter.Render(hook, result, Console.Out, CaptureLog.Append, CaptureRecords.Append);
    }

    // Only reads stdin when it is piped (a real hook invocation), so running this by hand in a terminal does not wait
    // for input. Console.In reads synchronously and ignores cancellation, so the read runs on the thread pool and is
    // abandoned when the bound passes; the process then exits with it still blocked, which is harmless.
    private static async Task<string> ReadPayloadAsync(CancellationToken cancellationToken) =>
        Console.IsInputRedirected
            ? await Task.Run(Console.In.ReadToEnd, CancellationToken.None).WaitAsync(cancellationToken)
            : string.Empty;
}
