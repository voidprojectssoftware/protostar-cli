using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// Runs when <c>protostar</c> is invoked with no command. Prints a short, styled status so an
/// operator can confirm the CLI is installed and working. Real commands (auth, sync, hooks) land
/// in later tickets.
/// </summary>
internal sealed class DefaultCommand : Command<DefaultCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        AnsiConsole.MarkupLine($"[aqua]protostar[/] [grey]v{CliInfo.Version}[/]");
        AnsiConsole.MarkupLine("[grey]Live, continuous refinement of agent skills.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Run [yellow]protostar --help[/] to see available commands.");
        return 0;
    }
}
