using System.ComponentModel;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// Invoked by an installed harness hook when a captured event fires (e.g. PostToolUse on the Skill
/// tool). Reads the hook's JSON payload from stdin and acknowledges it. This is the capture seam;
/// syncing the skill to the registry lands in a later ticket. It never blocks the harness and
/// always exits 0.
/// </summary>
internal sealed class CaptureCommand : Command<CaptureCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--hook <EVENT>")]
        [Description("The harness event that triggered capture (e.g. PostToolUse, SessionStart).")]
        public string? Hook { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        // Only read stdin when it is actually piped (a real hook invocation). Guards against blocking
        // if a user runs this by hand in a terminal.
        var payload = Console.IsInputRedirected ? Console.In.ReadToEnd() : string.Empty;
        var hook = settings.Hook ?? "unknown";

        // A quiet acknowledgement. For a successful hook, Claude Code surfaces stdout in the
        // transcript only, so this never leaks into the model's context.
        Console.WriteLine($"protostar capture: {hook} ({payload.Length} bytes)");
        return 0;
    }
}
