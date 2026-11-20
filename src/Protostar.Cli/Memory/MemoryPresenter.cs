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
        var conflicted = result.Folders.Where(f => f.Conflicts.Count > 0).ToList();
        if (pending.Count + conflicted.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Folder");
            table.AddColumn("Not backed up, or to resolve");
            foreach (var f in result.Folders.Where(f => f.Pending > 0 || f.Conflicts.Count > 0))
            {
                var lines = f.New.Select(p => $"{Markup.Escape(p)} [grey](new)[/]")
                    .Concat(f.Changed.Select(p => $"{Markup.Escape(p)} [grey](changed)[/]"))
                    .Concat(f.Deleted.Select(p => $"{Markup.Escape(p)} [grey](deleted here)[/]"))
                    .Concat(f.Conflicts.Select(p => $"{Markup.Escape(p)} [yellow](conflict: merge it into the file it is beside, then delete it)[/]"));
                table.AddRow(Markup.Escape(f.Directory), string.Join('\n', lines));
            }
            AnsiConsole.Write(table);
        }

        RenderProblems(result.Folders.SelectMany(f => f.Oversized.Select(o => (f.Folder, o))), result.Skipped);

        var files = result.Folders.Sum(f => f.Pending + f.UpToDate);
        var conflicts = conflicted.Sum(f => f.Conflicts.Count);
        AnsiConsole.MarkupLine(
            $"Memory for {SessionsPresenter.Count(result.Folders.Count, "project")} on this machine, {SessionsPresenter.Count(files, "file")}. " +
            (pending.Count == 0 ? "[green]All backed up.[/]" : $"[yellow]{SessionsPresenter.Count(pending.Sum(f => f.Pending), "file")} not backed up yet.[/] Run `protostar memory push`.") +
            (conflicts > 0 ? $" [yellow]{SessionsPresenter.Count(conflicts, "conflict")} to resolve.[/]" : ""));
        return result.FullySynced ? 0 : 1;
    }

    /// <summary>Renders a pull and returns the exit code: 0 when no folder failed (kept files and absent projects are reported, not failed), 1 otherwise.</summary>
    public static int RenderPull(MemoryPullResult result, IHarnessCatalog catalog)
    {
        if (SessionsPresenter.FailureMessage(result.Failure, result.Detail, result.Registry, catalog) is { } message)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
            return 1;
        }

        foreach (var line in PullLines(result))
            AnsiConsole.MarkupLine(line);
        return result.Complete ? 0 : 1;
    }

    /// <summary>A pull's summary line, then one line per checkout written to, kept file, and project not on this machine.</summary>
    /// <remarks>Shared with the Memory step of <c>protostar setup</c>, so a pull reads the same either way.</remarks>
    public static List<string> PullLines(MemoryPullResult result)
    {
        if (result.NoHarness)
            return ["No supported harness that keeps agent memory was found on this machine."];
        if (result.Projects == 0)
            return ["No memory in the registry yet."];

        var conflicted = result.Folders.Sum(f => f.Notes.Count(n => n.Kind == MemoryPullNoteKind.Conflicted));
        var lines = new List<string>
        {
            $"Pulled memory for {SessionsPresenter.Count(result.Folders.Select(f => f.ProjectName).Distinct().Count(), "project")}: " +
            $"{result.Written} written, {result.Updated} updated, {result.Folders.Sum(f => f.UpToDate)} already here" +
            (conflicted > 0 ? $", [yellow]{conflicted} conflicted[/]" : "") +
            (result.NotHere.Count > 0 ? $", [grey]{SessionsPresenter.Count(result.NotHere.Count, "project")} not on this machine[/]" : "") + ".",
        };

        foreach (var folder in result.Folders)
        {
            lines.Add(
                $"  [aqua]{Markup.Escape(folder.ProjectName)}[/] [grey]{Markup.Escape(folder.Directory)}[/]: " +
                (folder.Error is { } error
                    ? $"[red]{Markup.Escape(error)}[/]"
                    : $"{folder.Written} written, {folder.Updated} updated, {folder.UpToDate} already here [grey]({Markup.Escape(folder.MemoryFolder)})[/]"));
            foreach (var note in folder.Notes)
                lines.Add($"    {Markup.Escape(note.Path)}: {Describe(note)}");
        }

        foreach (var absent in result.NotHere)
            lines.Add($"  [yellow]Skipped[/] [aqua]{Markup.Escape(absent.ProjectName)}[/]: not on this machine. Clone it, open it in your harness or name its folder with --scan, then pull again.");
        return lines;
    }

    private static string Describe(MemoryPullNote note) => note.Kind switch
    {
        MemoryPullNoteKind.Conflicted =>
            $"[yellow]conflicted (here and on {Machines(note)})[/]. Kept this machine's; {Machines(note)}'s is beside it as " +
            $"{Markup.Escape(note.Detail ?? "")}, listed in {MemoryIndex.FileName}. Merge it by hand, then delete the copy.",
        MemoryPullNoteKind.Merged => $"changed here and on {Machines(note)}; merged line by line, {SessionsPresenter.Count(int.Parse(note.Detail ?? "0", System.Globalization.CultureInfo.InvariantCulture), "line")} added.",
        MemoryPullNoteKind.Restored => $"[yellow]deleted here, then changed on {Machines(note)}; brought back with that change.[/]",
        MemoryPullNoteKind.IndexTooLong =>
            $"[yellow]{Markup.Escape(note.Detail ?? "")} lines; Claude Code loads only the first {MemoryIndex.LoadedLines} lines " +
            $"({MemoryIndex.LoadedBytes / 1024} KB), so trim it.[/]",
        MemoryPullNoteKind.DeletedHere => "[grey]deleted here; not brought back.[/]",
        MemoryPullNoteKind.DeletedEverywhere => "[grey]deleted on every machine; not written.[/]",
        MemoryPullNoteKind.DeletedElsewhere => $"[yellow]deleted on {Machines(note)}; kept here.[/]",
        _ => "[red]refused: its path or content failed a safety check; not written.[/]",
    };

    private static string Machines(MemoryPullNote note) => Markup.Escape(string.Join(", ", note.Machines));

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
