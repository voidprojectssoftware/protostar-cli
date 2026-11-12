using System.ComponentModel;
using Protostar.Cli.Auth;
using Protostar.Cli.Capture;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// Invoked by an installed harness hook when a captured event fires (e.g. PostToolUse on the Skill tool).
/// </summary>
/// <remarks>
/// Reads the hook's JSON payload from stdin and hands it to <see cref="ICaptureService"/>, which backs
/// up a used skill when it changed. Bounded by <see cref="Timeout"/>, and always exits 0 so it never
/// blocks or fails the harness.
/// </remarks>
internal sealed class CaptureCommand : AsyncCommand<CaptureCommand.Settings>
{
    /// <summary>How long a capture may run before it gives up.</summary>
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
        // Only read stdin when it is actually piped (a real hook invocation). Guards against blocking
        // if a user runs this by hand in a terminal.
        var payload = Console.IsInputRedirected ? await Console.In.ReadToEndAsync(cancellation) : string.Empty;
        var hook = settings.Hook ?? "unknown";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Timeout);

        var result = await _capture.HandleAsync(hook, payload, timeout.Token);
        return CapturePresenter.Render(hook, result, Console.Out, AppendLog);
    }

    private static void AppendLog(string line)
    {
        try
        {
            Directory.CreateDirectory(ProtostarPaths.ConfigDir());
            File.AppendAllText(Path.Combine(ProtostarPaths.ConfigDir(), "capture.log"), line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort; the hook must still succeed.
        }
    }
}
