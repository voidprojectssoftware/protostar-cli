using System.ComponentModel;
using Protostar.Cli.Install;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands;

/// <summary>
/// Self-installs the running binary: copies this executable into a per-user directory and (unless
/// told not to) ensures that directory is on PATH. Designed to be run from the downloaded
/// self-contained binary — `protostar install`.
/// </summary>
internal sealed class InstallCommand : Command<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--dir <DIR>")]
        [Description("Install directory. Defaults to a per-user location.")]
        public string? Dir { get; init; }

        [CommandOption("--no-modify-path")]
        [Description("Do not add the install directory to PATH.")]
        public bool NoModifyPath { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var source = Environment.ProcessPath;
        if (source is null || !File.Exists(source))
        {
            AnsiConsole.MarkupLine("[red]Could not determine the running executable to install.[/]");
            return 1;
        }

        var dir = settings.Dir ?? InstallLocations.DefaultDir();
        var dest = Path.Combine(dir, InstallLocations.ExecutableName);

        if (PathsEqual(source, dest))
        {
            AnsiConsole.MarkupLine($"[green]protostar[/] is already installed at [grey]{Markup.Escape(dest)}[/].");
            ReportPath(dir, settings.NoModifyPath);
            return 0;
        }

        try
        {
            Directory.CreateDirectory(dir);
            File.Copy(source, dest, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dest,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Install failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"Installed [aqua]protostar[/] [grey]v{CliInfo.Version}[/] → [grey]{Markup.Escape(dest)}[/]");
        ReportPath(dir, settings.NoModifyPath);
        return 0;
    }

    private static void ReportPath(string dir, bool noModifyPath)
    {
        if (noModifyPath)
        {
            if (!PathManager.IsOnPath(dir))
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(dir)} is not on PATH (left unchanged).[/]");
            return;
        }

        var hint = PathManager.EnsureOnPath(dir);
        if (hint is not null)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(hint)}[/]");
    }

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
    }
}
