using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Agents;

/// <summary>
/// Brings custom agents from the registry to this machine: shows how each registry agent compares with what is here, and
/// installs or updates them.
/// </summary>
/// <remarks>
/// The agents' copy of <see cref="Skills.ISkillPullService"/> (ADR 0003). Backs <c>protostar agents remote</c> and
/// <c>protostar agents pull</c>. Personal agents are compared with, and installed into, each harness's personal agents
/// folder. A project agent is compared and installed only when the folder the command runs in resolves to that agent's
/// project, and then into that checkout's own project agents folder (<c>&lt;repo top level&gt;/.claude/agents</c>), so
/// a worktree installs into itself. Other projects' agents are listed as theirs and never written. Returns data only;
/// <see cref="AgentPullPresenter"/> renders it.
/// </remarks>
internal interface IAgentPullService
{
    /// <summary>Lists the registry's agents and the local ones, each with its sync state.</summary>
    Task<AgentRemoteResult> CompareAsync(AgentRemoteRequest request, CancellationToken cancellationToken);

    /// <summary>Installs or updates the requested registry agents on this machine.</summary>
    /// <remarks>
    /// A copy with local changes is never overwritten unless <see cref="AgentPullRequest.Force"/> is set; it is reported as
    /// <see cref="AgentPullStatus.LocalChanges"/> (or <see cref="AgentPullStatus.ChangedBoth"/> when the registry moved on
    /// too) instead. A <see cref="AgentPullStatus.Conflict"/> is never written, even with <c>Force</c>. After a pull the
    /// ledger records the installed bytes, so the capture hook does not push them straight back. Local files are never
    /// deleted.
    /// </remarks>
    Task<AgentPullRunResult> PullAsync(AgentPullRequest request, CancellationToken cancellationToken);
}

/// <summary>Which registry and harnesses to compare.</summary>
/// <param name="HarnessId">Limit to this harness; <c>null</c> for every installed harness that has custom agents.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> falls back to the environment, the saved default, then the built-in one.</param>
/// <param name="ProjectStart">The folder whose project's agents are compared; <c>null</c> compares personal agents only.</param>
/// <param name="Names">Registry agent names to compare; <c>null</c> or empty compares every agent.</param>
/// <param name="Version">Compare with this version of the one named agent instead of its current version. Needs exactly one name.</param>
internal sealed record AgentRemoteRequest(
    string? HarnessId, string? HarnessHome, string? Registry, string? ProjectStart = null, IReadOnlyList<string>? Names = null, int? Version = null);

/// <summary>What to pull and where.</summary>
/// <param name="Names">Registry agent names to pull, matched exactly first and then ignoring case; empty pulls every registry agent.</param>
/// <param name="HarnessId">Limit to this harness; <c>null</c> for every installed harness that has custom agents.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> falls back to the environment, the saved default, then the built-in one.</param>
/// <param name="Force">Overwrite copies that have local changes, including ones changed here and in the registry (keep theirs).</param>
/// <param name="ProjectStart">The folder whose project's agents are installed; <c>null</c> pulls personal agents only.</param>
/// <param name="Version">Install this version of the one named agent instead of its current version. Needs exactly one name.</param>
internal sealed record AgentPullRequest(
    IReadOnlyList<string> Names, string? HarnessId, string? HarnessHome, string? Registry, bool Force = false, string? ProjectStart = null, int? Version = null);

/// <summary>Why a remote listing or pull stopped before doing anything.</summary>
internal enum AgentPullRunFailure
{
    /// <summary>The run went ahead.</summary>
    None,

    /// <summary>The registry URL is not an absolute http(s) URL.</summary>
    InvalidRegistry,

    /// <summary>A <c>--harness</c> id matched no registered harness.</summary>
    UnknownHarness,

    /// <summary>The named harness has no custom agents.</summary>
    Unsupported,

    /// <summary>No harness with custom agents is installed on this machine.</summary>
    NoHarness,

    /// <summary>There is no stored session for the registry.</summary>
    NotLoggedIn,

    /// <summary>The stored session has expired and could not be refreshed.</summary>
    SessionExpired,

    /// <summary>The registry did not answer.</summary>
    RegistryUnreachable,

    /// <summary>The registry does not speak this CLI's API major.</summary>
    Incompatible,

    /// <summary>The registry lists its capabilities and lacks one the run needs (<c>agents</c>, <c>agent-content</c>); the detail says which.</summary>
    RegistryTooOld,

    /// <summary>The registry answered but would not list the agents; see the detail.</summary>
    ListFailed,

    /// <summary>A requested agent name is not in the registry.</summary>
    AgentNotFound,

    /// <summary>A rename or delete named both a personal agent and this folder's project agent; the detail lists them and how to pick one.</summary>
    Ambiguous,

    /// <summary><c>--version</c> was given without exactly one agent name, or is not a version number; see the detail.</summary>
    InvalidVersion,

    /// <summary>The requested version of the named agent could not be fetched; see the detail.</summary>
    VersionNotFound,
}

/// <summary>How an agent on this machine compares with the registry.</summary>
internal enum AgentSyncState
{
    /// <summary>In the registry, not on this machine.</summary>
    NotInstalled,

    /// <summary>Here with exactly the registry's current content (or what a pull of it wrote, renamed to the agent's name).</summary>
    UpToDate,

    /// <summary>Here unchanged since it last synced, and the registry has a newer version.</summary>
    UpdateAvailable,

    /// <summary>Here and different from the registry in a way this machine made (or cannot rule out).</summary>
    LocalChanges,

    /// <summary>Changed here since the version it last synced, while the registry moved past that version, or refused as stale.</summary>
    ChangedBoth,

    /// <summary>Here but not in the registry.</summary>
    LocalOnly,

    /// <summary>
    /// Here, synced as a registry agent that was deleted or renamed since, possibly with a new agent of the same name there
    /// now (then <see cref="AgentComparison.Remote"/> is that agent). See <see cref="AgentComparison.Note"/>.
    /// </summary>
    RemovedInRegistry,

    /// <summary>Cannot be pulled safely: the file it would be written to exists and is not this agent. See the note.</summary>
    Conflict,

    /// <summary>A project agent of a project other than this folder's. Never written here; the note says whose it is.</summary>
    OtherProject,
}

/// <summary>One agent name in one harness, compared with the registry.</summary>
/// <param name="Harness">The harness the local side belongs to.</param>
/// <param name="Name">The agent name.</param>
/// <param name="State">How the two sides compare.</param>
/// <param name="Remote">The registry's agent; <c>null</c> for one that is only here.</param>
/// <param name="LocalPath">The agent file here; <c>null</c> when it is not here.</param>
/// <param name="LocalVersion">The registry version this machine last synced, from the ledger; <c>null</c> when unknown.</param>
/// <param name="Note">Why it is a conflict, whose it is, or what happened to it in the registry; <c>null</c> otherwise.</param>
/// <param name="Project">The project's display name for a project agent; <c>null</c> for a personal agent.</param>
internal sealed record AgentComparison(
    IHarness Harness, string Name, AgentSyncState State, RegistryAgent? Remote, string? LocalPath, int? LocalVersion, string? Note = null, string? Project = null);

/// <summary>The result of comparing the registry with this machine.</summary>
/// <param name="Version">The version asked for with <c>--version</c>; <c>null</c> when comparing with current versions.</param>
internal sealed record AgentRemoteResult(AgentPullRunFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<AgentComparison> Agents, int? Version = null)
{
    public static AgentRemoteResult Fail(AgentPullRunFailure failure, string? detail, Uri? registry = null) => new(failure, detail, registry, []);
}

/// <summary>What a pull did with one agent.</summary>
internal enum AgentPullStatus
{
    /// <summary>The agent was not here and is now installed.</summary>
    Installed,

    /// <summary>An older, unedited copy was replaced (or a local change overwritten with <c>--force</c>).</summary>
    Updated,

    /// <summary>Already the registry's version; nothing was written.</summary>
    UpToDate,

    /// <summary>Left alone because it has local changes; <c>--force</c> overwrites them.</summary>
    LocalChanges,

    /// <summary>Left alone because it changed here and in the registry; <c>--force</c> keeps the registry's.</summary>
    ChangedBoth,

    /// <summary>The download or the write failed; see the message.</summary>
    Failed,

    /// <summary>Not written, even with <c>--force</c>, because it would land on a file that is not this agent; see the message.</summary>
    Conflict,

    /// <summary>Not written: it belongs to a project other than this folder's.</summary>
    OtherProject,

    /// <summary>
    /// An unedited copy of an agent renamed in the registry, renamed here to follow: its <c>name</c> field now says the new
    /// name. The file stays where it is. The registry's agent then pulls into it.
    /// </summary>
    FollowedRename,

    /// <summary>A copy of an agent deleted or renamed in the registry, kept as it is; the message says why and the ways out.</summary>
    RemovedInRegistry,
}

/// <summary>One agent a pull handled.</summary>
/// <param name="Harness">The harness it was installed into.</param>
/// <param name="Name">The agent name.</param>
/// <param name="Status">What happened.</param>
/// <param name="Version">The registry version now installed (or that would have been).</param>
/// <param name="Path">The agent file.</param>
/// <param name="Message">Why it failed, or a note; <c>null</c> otherwise.</param>
/// <param name="Project">The project's display name for a project agent; <c>null</c> for a personal agent.</param>
internal sealed record AgentPullEntry(IHarness Harness, string Name, AgentPullStatus Status, int Version, string Path, string? Message, string? Project = null);

/// <summary>The result of a pull run.</summary>
/// <param name="Version">The version asked for with <c>--version</c>; <c>null</c> for current versions.</param>
internal sealed record AgentPullRunResult(AgentPullRunFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<AgentPullEntry> Entries, int? Version = null)
{
    /// <summary>True when the run went ahead and no agent failed or conflicted; copies kept for local changes are not failures.</summary>
    public bool Succeeded => Failure == AgentPullRunFailure.None && Entries.All(e => e.Status is not (AgentPullStatus.Failed or AgentPullStatus.Conflict));

    public static AgentPullRunResult Fail(AgentPullRunFailure failure, string? detail, Uri? registry = null) => new(failure, detail, registry, []);
}
