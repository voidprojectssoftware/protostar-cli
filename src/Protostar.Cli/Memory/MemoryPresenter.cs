using Protostar.Cli.Harness;
using Protostar.Cli.Sessions;
using Spectre.Console;

namespace Protostar.Cli.Memory;

/// <summary>Renders memory push and status results to the console and picks the exit code.</summary>
/// <remarks>Shows paths, counts and sizes only, never a file's content: memory can hold sensitive notes.</remarks>
internal static class MemoryPresenter
{
    /// <summary>Renders a push and returns the exit code: 0 when every folder was backed up (skipped folders are reported, not failed), 1 otherwise.</summary>
    public static int RenderPush(MemoryPushResult result, IHarnessCatalog catalog)
    {
        if (SessionsPresenter.FailureMessage(result.Failure, result.Detail, result.Registry, catalog) is { } message)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
            return 1;
        }

        var sent = result.Folders.Where(f => f.Uploaded + f.Deleted > 0 || f.Error is not null).ToList();
        if (sent.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Project");
            table.AddColumn("Folder");
            table.AddColumn(new TableColumn("Uploaded").RightAligned());
            table.AddColumn(new TableColumn("Deleted").RightAligned());
            table.AddColumn("Result");
            foreach (var f in sent)
            {
                table.AddRow(
                    Markup.Escape(f.ProjectName ?? "?"),
                    Markup.Escape(f.Directory),
                    f.Uploaded.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    f.Deleted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    f.Error is null ? "[green]backed up[/]" : $"[red]{Markup.Escape(f.Error)}[/]");
            }
            AnsiConsole.Write(table);
        }

        RenderProblems(result.Folders.SelectMany(f => f.Oversized.Select(o => (f.Folder, o))), result.Skipped);

        var authority = Markup.Escape(result.Registry!.GetLeftPart(UriPartial.Authority));
        var machine = Markup.Escape(result.Machine ?? "this machine");
        var uploaded = result.Folders.Sum(f => f.Uploaded);
        var deleted = result.Folders.Sum(f => f.Deleted);
        var projects = result.Folders.Count(f => f.Uploaded + f.Deleted > 0);
        if (result.Folders.Count == 0)
            AnsiConsole.MarkupLine("[grey]No agent memory found on this machine.[/]");
        else if (uploaded + deleted == 0 && result.Complete)
            AnsiConsole.MarkupLine($"Memory for {SessionsPresenter.Count(result.Folders.Count, "project")} was already up to date in [grey]{authority}[/].");
        else
            AnsiConsole.MarkupLine(
                $"Backed up {SessionsPresenter.Count(uploaded, "file")} for {SessionsPresenter.Count(projects, "project")} from [grey]{machine}[/]" +
                (deleted > 0 ? $", and recorded {SessionsPresenter.Count(deleted, "deleted file")}" : "") +
                $". Registry: [grey]{authority}[/].");

        return result.Complete ? 0 : 1;
    }

    /// <summary>Renders memory status and returns the exit code: 0 when nothing is pending, 1 otherwise.</summary>
    public static int RenderStatus(MemoryStatusResult result, IHarnessCatalog catalog)
    {
        if (SessionsPresenter.FailureMessage(result.Failure, result.Detail, result.Registry, catalog) is { } message)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
            return 1;
        }

        var pending = result.Folders.Where(f => f.Pending > 0).ToList();
        if (pending.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Folder");
            table.AddColumn("Not backed up");
            foreach (var f in pending)
            {
                var lines = f.New.Select(p => $"{Markup.Escape(p)} [grey](new)[/]")
                    .Concat(f.Changed.Select(p => $"{Markup.Escape(p)} [grey](changed)[/]"))
                    .Concat(f.Deleted.Select(p => $"{Markup.Escape(p)} [grey](deleted here)[/]"));
                table.AddRow(Markup.Escape(f.Directory), string.Join('\n', lines));
            }
            AnsiConsole.Write(table);
        }

        RenderProblems(result.Folders.SelectMany(f => f.Oversized.Select(o => (f.Folder, o))), result.Skipped);

        var files = result.Folders.Sum(f => f.Pending + f.UpToDate);
        AnsiConsole.MarkupLine(
            $"Memory for {SessionsPresenter.Count(result.Folders.Count, "project")} on this machine, {SessionsPresenter.Count(files, "file")}. " +
            (pending.Count == 0 ? "[green]All backed up.[/]" : $"[yellow]{SessionsPresenter.Count(pending.Sum(f => f.Pending), "file")} not backed up yet.[/] Run `protostar memory push`."));
        return result.FullySynced ? 0 : 1;
    }

    private static void RenderProblems(IEnumerable<(string Folder, OversizedMemoryFile File)> oversized, IReadOnlyList<SkippedMemoryFolder> skipped)
    {
        foreach (var (folder, file) in oversized)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Skipped[/] {Markup.Escape(folder)}/memory/{Markup.Escape(file.Path)}: {SessionsPresenter.Bytes(file.Size)}, " +
                $"over the {MemorySyncService.MaxFileBytes / 1024} KiB limit for a memory file.");
        }

        foreach (var s in skipped)
            AnsiConsole.MarkupLine($"[yellow]Skipped[/] memory folder {Markup.Escape(s.Folder)}: {Markup.Escape(s.Reason)}");
    }
}
