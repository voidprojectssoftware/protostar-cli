using System.Text.Json;
using System.Text.Json.Nodes;
using Protostar.Cli.Auth;
using Protostar.Cli.Capture;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;
using Spectre.Console;

namespace Protostar.Cli.Status;

/// <summary>The console face of <see cref="ISyncStatusService"/>: a short report, or one JSON object with <c>--json</c>.</summary>
/// <remarks>
/// Exit codes: 0 when this machine is syncing, 1 when anything in <see cref="SyncStatus.Problems"/> needs attention or
/// the registry setting is unusable. The JSON follows the contract of the <c>sessions</c> commands, written through <see cref="SessionsJson"/>:
/// data only on standard output, camelCase names, absent values left out, enums as camelCase strings, and a failure as
/// <c>{"error":{"code":...,"message":...}}</c> on standard error.
/// </remarks>
internal static class SyncStatusPresenter
{
    /// <summary>Renders <paramref name="status"/> and returns the exit code.</summary>
    public static int Render(SyncStatus status, bool json)
    {
        var problems = status.Problems;
        if (json)
        {
            SessionsJson.WriteObject(ToJson(status));
            return problems.Count == 0 ? 0 : 1;
        }

        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
        grid.AddRow("[grey]Registry[/]", $"{Markup.Escape(status.Registry.GetLeftPart(UriPartial.Authority))} [grey](from {Markup.Escape(SignInPresenter.Describe(status.Source))})[/]");
        grid.AddRow("[grey]Sign-in[/]", SignIn(status));
        grid.AddRow("[grey]Hooks[/]", Hooks(status));
        foreach (var backup in status.Backups)
            grid.AddRow($"[grey]{Name(backup.Kind)}[/]", Backup(backup));
        if (status.FailedSessions.Count > 0)
            grid.AddRow("[grey]Retrying[/]", $"[yellow]{SessionsPresenter.Count(status.FailedSessions.Count, "session")} whose backup failed[/], " +
                $"oldest {SessionsPresenter.Ago(status.FailedSessions[0].FailedAt)}; the next hook that reaches the registry retries them.");
        grid.AddRow("[grey]Pending[/]", status.PendingBytes == 0
            ? "nothing; every transcript is backed up"
            : $"{SessionsPresenter.Bytes(status.PendingBytes)} in {SessionsPresenter.Count(status.PendingTranscripts, "transcript")} [grey](goes with the next hook, or run `protostar sessions push`)[/]");
        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();

        if (problems.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]This machine is syncing.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine("[red]Needs attention:[/]");
        foreach (var problem in problems)
            AnsiConsole.MarkupLine($"  - {Markup.Escape(Advice(problem, status))}");
        return 1;
    }

    /// <summary>Renders an unusable registry setting and returns 1.</summary>
    public static int RenderInvalidRegistry(string message, bool json)
    {
        if (json)
            return SessionsJson.Error(SessionsJson.InvalidRegistry, message);
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return 1;
    }

    internal static JsonObject ToJson(SyncStatus status) => new()
    {
        ["ok"] = status.Problems.Count == 0,
        ["registry"] = status.Registry.GetLeftPart(UriPartial.Authority),
        ["registrySource"] = Node(status.Source),
        ["signIn"] = Node(new { state = status.SignIn, login = status.Login }),
        ["hooks"] = Node(status.Hooks.Select(h => new { harness = h.Harness, settingsPath = h.SettingsPath, installed = h.Installed, error = h.Error })),
        ["backups"] = Node(status.Backups.Select(b => new
        {
            kind = b.Kind,
            failing = b.Failing,
            lastSuccessAt = b.LastSuccessAt,
            lastFailureAt = b.LastFailureAt,
            failuresSinceSuccess = b.FailuresSinceSuccess,
            lastReason = b.LastReason,
            lastError = b.LastError,
        })),
        ["failedSessions"] = Node(status.FailedSessions.Select(f => new { id = f.Key, harness = f.Harness, path = f.Path, failedAt = f.FailedAt })),
        ["pending"] = Node(new { bytes = status.PendingBytes, transcripts = status.PendingTranscripts }),
        ["problems"] = Node(status.Problems),
    };

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, TranscriptJson.Options);

    private static string SignIn(SyncStatus status)
    {
        var who = status.Login is { Length: > 0 } login ? $" as [aqua]{Markup.Escape(login)}[/]" : string.Empty;
        return status.SignIn switch
        {
            SignInState.SignedIn => $"[green]signed in[/]{who}",
            SignInState.NotChecked => $"signed in{who} [grey](not checked with the registry: --offline)[/]",
            SignInState.Rejected => $"[red]the registry refused the stored sign-in[/]{who} [grey](expired or revoked)[/]",
            SignInState.Unreachable => $"[yellow]signed in{who}, but the registry did not answer within {SyncStatusService.ProbeTimeout.TotalSeconds:0} s[/]",
            _ => "[red]not signed in[/]",
        };
    }

    private static string Hooks(SyncStatus status)
    {
        if (status.Hooks.Count == 0)
            return "[red]no harness found that takes capture hooks[/]";
        return string.Join("\n", status.Hooks.Select(h =>
            h.Error is not null ? $"{Markup.Escape(h.Harness)}: [red]settings unreadable[/] [grey]({Markup.Escape(h.Error)})[/]"
            : h.Installed ? $"{Markup.Escape(h.Harness)}: [green]installed[/] [grey]({Markup.Escape(h.SettingsPath)})[/]"
            : $"{Markup.Escape(h.Harness)}: [red]not installed[/] [grey]({Markup.Escape(h.SettingsPath)})[/]"));
    }

    private static string Name(CaptureKind kind) => kind switch
    {
        CaptureKind.Skills => "Skills",
        CaptureKind.Sessions => "Sessions",
        _ => "Memory",
    };

    private static string Backup(BackupStatus backup)
    {
        if (!backup.Failing)
            return backup.LastSuccessAt is { } ok ? $"[green]last backed up {When(ok)}[/]" : "no background backup recorded yet";
        var failures = SessionsPresenter.Count(backup.FailuresSinceSuccess, "failure");
        var since = backup.LastSuccessAt is { } last ? $"since the last backup {When(last)}" : "and no backup recorded";
        return $"[red]{failures}[/] {since}; newest {When(backup.LastFailureAt!.Value)}: " +
            Markup.Escape(backup.LastError ?? backup.LastReason?.ToString() ?? "unknown error");
    }

    private static string When(DateTimeOffset at) => $"{SessionsPresenter.Ago(at)} [grey]({at.ToLocalTime():yyyy-MM-dd HH:mm})[/]";

    private static string Advice(SyncProblem problem, SyncStatus status) => problem switch
    {
        SyncProblem.NotSignedIn => $"Not signed in to {status.Registry.GetLeftPart(UriPartial.Authority)}, so nothing backs up. Run `protostar auth login`.",
        SyncProblem.SessionRejected => "The registry refused the stored sign-in. Run `protostar auth login`.",
        SyncProblem.RegistryUnreachable => "The registry did not answer. Backups wait until it does; check the URL and your connection, or run `protostar status --offline`.",
        SyncProblem.NoHarness => "No harness was found to install capture hooks into. Pass --harness-home if it lives elsewhere.",
        SyncProblem.HooksMissing => "Capture hooks are missing, so nothing backs up while you work. Run `protostar install-hooks`.",
        SyncProblem.SkillsFailing => $"Skill backups are failing{Hint(status, CaptureKind.Skills)}",
        SyncProblem.SessionsFailing => $"Session backups are failing{Hint(status, CaptureKind.Sessions)}",
        SyncProblem.MemoryFailing => $"Memory backups are failing{Hint(status, CaptureKind.Memory)}",
        _ => "Sessions whose backup failed are waiting to be retried. Run `protostar sessions push` to retry them now.",
    };

    private static string Hint(SyncStatus status, CaptureKind kind) => status.Backups.First(b => b.Kind == kind).LastReason switch
    {
        CaptureFailure.NotSignedIn or CaptureFailure.SessionExpired => ": not signed in. Run `protostar auth login`.",
        CaptureFailure.RegistryUnreachable => ": the registry could not be reached. They resume on their own once it can.",
        CaptureFailure.Refused when kind == CaptureKind.Skills => ": the registry refused a skill. Run `protostar skills remote` to choose which copy to keep.",
        _ => $". Details are in {Path.Combine(ProtostarPaths.ConfigDir(), "capture.log")}.",
    };
}
