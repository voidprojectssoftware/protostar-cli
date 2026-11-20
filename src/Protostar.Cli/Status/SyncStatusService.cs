using Protostar.Cli.Auth;
using Protostar.Cli.Capture;
using Protostar.Cli.Harness;
using Protostar.Cli.Hooks;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Status;

/// <summary>Answers "is this machine syncing?" for <c>protostar status</c>.</summary>
/// <remarks>
/// Offline first: everything comes from this machine (the credential store, the harness settings, the capture records,
/// the session ledger and its failure marks). The registry is asked one thing only, whether the stored sign-in is
/// still good, bounded by <see cref="SyncStatusService.ProbeTimeout"/>, and not at all with
/// <see cref="SyncStatusRequest.Offline"/>.
/// </remarks>
internal interface ISyncStatusService
{
    /// <summary>Gathers the status for the registry <paramref name="request"/> resolves to.</summary>
    Task<SyncStatus> CheckAsync(SyncStatusRequest request, CancellationToken cancellationToken);
}

/// <summary>What to check.</summary>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> resolves it as every command does.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="Offline">Do not ask the registry whether the sign-in is still good.</param>
internal sealed record SyncStatusRequest(string? Registry, string? HarnessHome, bool Offline);

/// <summary>Whether this machine is signed in to the registry.</summary>
internal enum SignInState
{
    /// <summary>No stored sign-in.</summary>
    SignedOut,

    /// <summary>Signed in, and the registry accepted it just now.</summary>
    SignedIn,

    /// <summary>A sign-in is stored; the registry was not asked (<c>--offline</c>).</summary>
    NotChecked,

    /// <summary>A sign-in is stored but the registry refused it (expired or revoked).</summary>
    Rejected,

    /// <summary>A sign-in is stored but the registry did not answer within the probe timeout.</summary>
    Unreachable,
}

/// <summary>Whether one harness has protostar's capture hooks.</summary>
/// <param name="Harness">The harness id.</param>
/// <param name="SettingsPath">The settings file the hooks live in.</param>
/// <param name="Installed">Whether protostar's hooks are in it.</param>
/// <param name="Error">Why the settings could not be read; <c>null</c> when they could.</param>
internal sealed record HookStatus(string Harness, string SettingsPath, bool Installed, string? Error);

/// <summary>How background backups of one kind have gone, from the capture records.</summary>
/// <param name="Kind">What is backed up.</param>
/// <param name="LastSuccessAt">The last background backup that left nothing failed; <c>null</c> for never (in the records kept).</param>
/// <param name="LastFailureAt">The last one that failed; <c>null</c> for none.</param>
/// <param name="FailuresSinceSuccess">Failures after <paramref name="LastSuccessAt"/>, or in all the records kept when there was none.</param>
/// <param name="LastReason">Why the last failure happened.</param>
/// <param name="LastError">The last failure's message.</param>
internal sealed record BackupStatus(
    CaptureKind Kind, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, int FailuresSinceSuccess,
    CaptureFailure? LastReason, string? LastError)
{
    /// <summary>Whether the newest backup of this kind failed, so the latest work is not in the registry.</summary>
    public bool Failing => FailuresSinceSuccess > 0;
}

/// <summary>A reason <c>protostar status</c> exits non-zero; the values are stable, as <c>--json</c> prints them.</summary>
internal enum SyncProblem
{
    /// <summary>No stored sign-in.</summary>
    NotSignedIn,

    /// <summary>The registry refused the stored sign-in.</summary>
    SessionRejected,

    /// <summary>The registry did not answer the probe.</summary>
    RegistryUnreachable,

    /// <summary>No harness that takes capture hooks was found.</summary>
    NoHarness,

    /// <summary>A harness is missing protostar's hooks, or its settings could not be read.</summary>
    HooksMissing,

    /// <summary>The newest skill backup failed.</summary>
    SkillsFailing,

    /// <summary>The newest session backup failed.</summary>
    SessionsFailing,

    /// <summary>The newest memory backup failed.</summary>
    MemoryFailing,

    /// <summary>Sessions are marked failed and wait for a hook to retry them.</summary>
    SessionsAwaitingRetry,
}

/// <summary>The answer to "is this machine syncing?".</summary>
/// <param name="Registry">The registry checked.</param>
/// <param name="Source">Where <paramref name="Registry"/> was chosen.</param>
/// <param name="SignIn">Whether this machine is signed in to it.</param>
/// <param name="Login">The signed-in login, when known.</param>
/// <param name="Hooks">One entry per harness found that takes hooks; empty when none was found.</param>
/// <param name="Backups">One entry per <see cref="CaptureKind"/>, in its order.</param>
/// <param name="FailedSessions">Sessions whose background backup failed and that a later hook retries, oldest first.</param>
/// <param name="PendingBytes">Transcript bytes on this machine not in the registry yet.</param>
/// <param name="PendingTranscripts">Transcripts with pending bytes.</param>
internal sealed record SyncStatus(
    Uri Registry, RegistrySource Source, SignInState SignIn, string? Login, IReadOnlyList<HookStatus> Hooks,
    IReadOnlyList<BackupStatus> Backups, IReadOnlyList<FailedBackup> FailedSessions, long PendingBytes, int PendingTranscripts)
{
    /// <summary>What needs attention; empty when this machine is syncing.</summary>
    /// <remarks>Pending bytes alone are not a problem: a live session always has some until its next push.</remarks>
    public IReadOnlyList<SyncProblem> Problems
    {
        get
        {
            var problems = new List<SyncProblem>();
            if (SignIn switch
                {
                    SignInState.SignedOut => SyncProblem.NotSignedIn,
                    SignInState.Rejected => SyncProblem.SessionRejected,
                    SignInState.Unreachable => SyncProblem.RegistryUnreachable,
                    _ => (SyncProblem?)null,
                } is { } signIn)
                problems.Add(signIn);
            if (Hooks.Count == 0)
                problems.Add(SyncProblem.NoHarness);
            else if (Hooks.Any(h => !h.Installed))
                problems.Add(SyncProblem.HooksMissing);
            foreach (var backup in Backups.Where(b => b.Failing))
            {
                problems.Add(backup.Kind switch
                {
                    CaptureKind.Skills => SyncProblem.SkillsFailing,
                    CaptureKind.Sessions => SyncProblem.SessionsFailing,
                    _ => SyncProblem.MemoryFailing,
                });
            }

            if (FailedSessions.Count > 0)
                problems.Add(SyncProblem.SessionsAwaitingRetry);
            return problems;
        }
    }
}

/// <summary>Production <see cref="ISyncStatusService"/>.</summary>
internal sealed class SyncStatusService : ISyncStatusService
{
    /// <summary>How long the sign-in probe may wait for the registry; past it the registry counts as unreachable.</summary>
    /// <remarks>Long enough for a refresh and a userinfo call to a healthy registry, short enough that status stays quick offline.</remarks>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly ITokenStore _tokens;
    private readonly ISignInService _signIn;
    private readonly IHookInstallService _hooks;
    private readonly ISessionSyncService _sessions;
    private readonly ISessionLedger _ledger;
    private readonly Func<IReadOnlyList<CaptureRecord>> _records;

    public SyncStatusService(ITokenStore tokens, ISignInService signIn, IHookInstallService hooks, ISessionSyncService sessions, ISessionLedger ledger)
        : this(tokens, signIn, hooks, sessions, ledger, () => CaptureRecords.Read(CaptureRecords.DefaultPath()))
    {
    }

    /// <summary>For tests: reads the capture records from <paramref name="records"/>.</summary>
    internal SyncStatusService(
        ITokenStore tokens, ISignInService signIn, IHookInstallService hooks, ISessionSyncService sessions, ISessionLedger ledger,
        Func<IReadOnlyList<CaptureRecord>> records)
    {
        _tokens = tokens;
        _signIn = signIn;
        _hooks = hooks;
        _sessions = sessions;
        _ledger = ledger;
        _records = records;
    }

    /// <inheritdoc />
    /// <exception cref="FormatException">The registry option, environment value, or saved default is not a usable URL.</exception>
    public async Task<SyncStatus> CheckAsync(SyncStatusRequest request, CancellationToken cancellationToken)
    {
        var (registry, source) = RegistryEndpoint.ResolveWithSource(request.Registry);
        var (signIn, login) = await SignInAsync(registry, request.Offline, cancellationToken);

        var hooks = _hooks.Uninstall(new HookInstallOptions { RootOverride = request.HarnessHome, DryRun = true }).Results
            .Select(r => new HookStatus(r.Harness.Id, r.Change?.SettingsPath ?? string.Empty, r.Change?.Change == HookChange.Removed, r.Error))
            .ToList();

        var authority = registry.GetLeftPart(UriPartial.Authority);
        var records = _records().Where(r => r.Registry is null || string.Equals(r.Registry, authority, StringComparison.OrdinalIgnoreCase)).ToList();
        var backups = Enum.GetValues<CaptureKind>().Select(kind => Summarize(kind, records.Where(r => r.Kind == kind).ToList())).ToList();

        var pending = _sessions.Status(registry.ToString(), null, request.HarnessHome).Transcripts.Where(t => t.BytesPending > 0).ToList();

        return new SyncStatus(
            registry, source, signIn, login, hooks, backups, _ledger.FailedBackups(registry),
            pending.Sum(t => t.BytesPending), pending.Count);
    }

    // The credential store answers signed out without the network; only a stored sign-in is checked, and briefly.
    private async Task<(SignInState, string?)> SignInAsync(Uri registry, bool offline, CancellationToken cancellationToken)
    {
        if (_tokens.Load(registry) is not { } stored)
            return (SignInState.SignedOut, null);
        if (offline)
            return (SignInState.NotChecked, stored.Login);

        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(ProbeTimeout);
        try
        {
            var check = await _signIn.CheckAsync(registry, probe.Token);
            return check.Verification switch
            {
                SessionVerification.Verified => (SignInState.SignedIn, check.Login),
                SessionVerification.Rejected => (SignInState.Rejected, check.Login),
                SessionVerification.NoSession => (SignInState.SignedOut, null),
                _ => (SignInState.Unreachable, check.Login),
            };
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException && !cancellationToken.IsCancellationRequested)
        {
            return (SignInState.Unreachable, stored.Login);
        }
    }

    internal static BackupStatus Summarize(CaptureKind kind, IReadOnlyList<CaptureRecord> records)
    {
        var lastSuccess = records.LastOrDefault(r => r.Ok);
        var lastFailure = records.LastOrDefault(r => !r.Ok);
        var since = records.Count(r => !r.Ok && (lastSuccess is null || r.At > lastSuccess.At));
        return new BackupStatus(kind, lastSuccess?.At, lastFailure?.At, since, lastFailure?.Reason, lastFailure?.Error);
    }
}
