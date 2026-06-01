using System.ComponentModel;
using Protostar.Cli.Install;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>Removes an installed protostar binary and (on Windows) its PATH entry.</summary>
internal sealed class UninstallCommand : Command<UninstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--dir <DIR>")]
        [Description("Install directory to remove from. Defaults to the per-user location.")]
        public string? Dir { get; init; }

        [CommandOption("--no-modify-path")]
        [Description("Do not remove the install directory from PATH.")]
        public bool NoModifyPath { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var dir = settings.Dir ?? InstallLocations.DefaultDir();
        var dest = Path.Combine(dir, InstallLocations.ExecutableName);

        if (!File.Exists(dest))
        {
            AnsiConsole.MarkupLine($"[grey]Nothing to remove — {Markup.Escape(dest)} does not exist.[/]");
            return 0;
        }

        try
        {
            File.Delete(dest);
            // Remove the directory only if we created it and it is now empty.
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Uninstall failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (!settings.NoModifyPath)
            PathManager.RemoveFromPath(dir);
        AnsiConsole.MarkupLine($"Removed [aqua]protostar[/] from [grey]{Markup.Escape(dir)}[/].");
        return 0;
    }
}
