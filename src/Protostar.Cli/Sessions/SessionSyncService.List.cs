using Protostar.Cli.Harness;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Sessions;

/// <summary>The <c>sessions list</c> half of <see cref="SessionSyncService"/>.</summary>
/// <remarks>
/// <para>
/// Two sources, merged: the registry's page of sessions, and this machine's sessions with bytes not backed up yet,
/// found by the same stat-only planning as <c>sessions status</c>. A session and its subagents count as one pending
/// session. A pending session the registry's page already holds (matched on harness and key) is shown once, as the
/// registry's row with the pending bytes added; the rest become rows of their own.
/// </para>
/// <para>
/// A row of its own needs a project, which only the file can say: the working directory is read from a bounded head
/// of it (never the whole transcript), resolved to facts with git, then to the registry's project for that folder
/// (once per folder), so its name and id are what a backup would file it under. Offline, the name is the folder's
/// last segment, the registry's own name for a folder without a remote. <c>--project</c> then matches local rows as
/// the registry matches its own: an id exactly, otherwise text in the name ignoring case. Offline there is no id, so
/// an id matches no local row. Heads are read newest first and only until the limit is filled.
/// </para>
/// </remarks>
internal sealed partial class SessionSyncService
{
    /// <inheritdoc />
    public async Task<SessionListResult> ListAsync(string? registryOption, string? harnessHome, TranscriptListQuery query, CancellationToken cancellationToken)
    {
        var status = Status(registryOption, query.Harness, harnessHome);
        if (status.Failure != SessionSyncFailure.None)
            return new SessionListResult(status.Failure, status.Detail, status.Registry, []);
        var registry = status.Registry!;
        var limit = Math.Clamp(query.Limit, 1, 200);
        var machine = _machine.Get();

        // Another machine's list has none of this machine's sessions, pending or not.
        var pending = query.Machine is { } wanted && wanted != machine.Id ? [] : PendingSessions(status.Transcripts);

        var failure = SessionSyncFailure.None;
        string? detail = null;
        string? token = null;
        IReadOnlyList<RemoteTranscript> remote = [];
        var stored = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (stored is null)
        {
            failure = SessionSyncFailure.NotLoggedIn;
        }
        else if (stored.IsExpired)
        {
            failure = SessionSyncFailure.SessionExpired;
        }
        else
        {
            var listed = await _api.ListAsync(registry, stored.AccessToken, query, cancellationToken);
            if (listed.Ok)
                (remote, token) = (listed.Value!, stored.AccessToken);
            else
                (failure, detail) = (SessionSyncFailure.RegistryUnreachable, listed.Error);
        }

        var pendingByKey = pending.ToDictionary(p => (p.Session.Harness, p.Session.Key));
        var rows = remote
            .Select(r => new ListedSession(r, pendingByKey.Remove((r.Harness, r.Key), out var p) ? Pending(p.Session, p.Backup, fromFile: false) : null))
            .ToList();

        var folders = new Dictionary<string, RemoteProject?>(StringComparer.Ordinal);
        var added = 0;
        foreach (var (session, backup) in pending.Where(p => pendingByKey.ContainsKey((p.Session.Harness, p.Session.Key))))
        {
            if (added >= limit)
                break;
            if (query.Since is { } since && backup.ChangedAt < since)
                continue;

            var project = await ProjectOfAsync(session, registry, token, machine.Id, folders, cancellationToken);
            if (!Matches(project, query.Project))
                continue;

            rows.Add(new ListedSession(
                new RemoteTranscript
                {
                    Harness = session.Harness,
                    Key = session.Key,
                    Kind = "session",
                    ProjectId = project.Id,
                    Project = project.Name,
                    ProjectSubpath = project.Subpath,
                    WorkingDirectory = project.WorkingDirectory,
                    MachineId = machine.Id,
                    MachineName = machine.Name,
                    SourcePath = session.Path,
                    LastActivityAt = backup.ChangedAt,
                    Length = backup.Size,
                    SubagentCount = backup.Subagents,
                },
                Pending(session, backup, fromFile: true)));
            added++;
        }

        var listedRows = rows.OrderByDescending(r => r.When).Take(limit).ToList();
        return new SessionListResult(failure, detail, registry, listedRows);
    }

    private sealed record LocalBackup(long Bytes, bool InRegistry, DateTimeOffset ChangedAt, long Size, int Subagents);

    // A row the registry listed holds part of the session by definition, whatever the ledger says.
    private static PendingBackup Pending(LocalTranscript session, LocalBackup backup, bool fromFile) =>
        new(backup.Bytes, backup.InRegistry || !fromFile, fromFile, session.Path, backup.ChangedAt);

    private sealed record LocalProject(Guid? Id, string? Name, string? Subpath, string? WorkingDirectory);

    // This machine's sessions with anything not backed up, newest change first, each with its subagents folded in.
    private static List<(LocalTranscript Session, LocalBackup Backup)> PendingSessions(IReadOnlyList<TranscriptStatus> transcripts) =>
        transcripts
            .GroupBy(t => (t.Transcript.Harness, Key: t.Transcript.Kind == TranscriptKind.Session ? t.Transcript.Key : t.Transcript.ParentKey ?? t.Transcript.Key))
            .Where(g => g.Sum(t => t.BytesPending) > 0)
            .Select(g => (Session: g.FirstOrDefault(t => t.Transcript.Kind == TranscriptKind.Session), All: g.ToList()))
            .Where(g => g.Session is not null)
            .Select(g => (g.Session!.Transcript, new LocalBackup(
                g.All.Sum(t => t.BytesPending),
                g.All.Any(t => t.BytesBackedUp > 0),
                g.All.Max(t => t.LastModifiedAt),
                g.Session.SizeOnDisk,
                g.All.Count - 1)))
            .OrderByDescending(p => p.Item2.ChangedAt)
            .ToList();

    // The project a session not in the registry's page belongs to. Reads at most the harness's bounded head of the file.
    private async Task<LocalProject> ProjectOfAsync(
        LocalTranscript session, Uri registry, string? token, Guid machineId, Dictionary<string, RemoteProject?> folders, CancellationToken cancellationToken)
    {
        if (_catalog.ById(session.Harness) is not ISessionCapability sessions || sessions.ReadWorkingDirectory(session) is not { } directory)
            return new LocalProject(null, null, null, null);

        var facts = _projects.Resolve(directory);
        if (token is not null && !folders.TryGetValue(facts.Directory, out _))
        {
            var resolved = await _projectApi.ResolveProjectAsync(registry, token, machineId, facts, cancellationToken);
            folders[facts.Directory] = resolved.Ok ? resolved.Value : null;
        }

        var subpath = string.IsNullOrEmpty(facts.Subpath) ? null : facts.Subpath;
        return folders.GetValueOrDefault(facts.Directory) is { } project
            ? new LocalProject(project.ProjectId, project.Name, project.Subpath ?? subpath, directory)
            : new LocalProject(null, Path.GetFileName(Path.TrimEndingDirectorySeparator(facts.MainWorktree ?? facts.Directory)), subpath, directory);
    }

    // The registry's rule for --project: an id matches exactly, anything else is text in the name, ignoring case.
    private static bool Matches(LocalProject project, string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || (Guid.TryParse(filter, out var id) ? project.Id == id : project.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);
}
