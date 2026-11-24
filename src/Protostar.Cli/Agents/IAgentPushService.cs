using Protostar.Cli.Harness;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Agents;

/// <summary>Backs up local custom agents to the registry: <c>protostar agents push</c> and the capture hook's background push.</summary>
/// <remarks>
/// The agents' copy of <see cref="Skills.ISkillPushService"/> (ADR 0003). Returns data only; <see cref="AgentPushPresenter"/> renders it.
/// </remarks>
internal interface IAgentPushService
{
    /// <summary>Discovers the requested agents and uploads each changed one as a new version.</summary>
    /// <remarks>
    /// <para>
    /// One request per agent, so one refusal never fails the others. A project agent is sent with its folder's facts. A
    /// file without a <c>name</c> (or <c>description</c>) in its front matter is refused here without being sent, since
    /// Claude Code would not load it. Two files of one scope with the same name: the first is pushed, an identical later
    /// one skipped, a different one reported as a conflict.
    /// </para>
    /// <para>
    /// Each push names the version its copy last synced (from <see cref="IAgentLedger"/>), so an edit made from an older
    /// version is refused as <see cref="AgentPushStatus.Stale"/> and the refusal is remembered. Except from the hook, a run
    /// with agents the ledger says are synced lists the registry's agents once, so a copy of an agent deleted or renamed
    /// elsewhere is reported (<see cref="AgentPushStatus.Removed"/>) instead of uploaded, unless <see cref="AgentPushRequest.Force"/>.
    /// </para>
    /// </remarks>
    Task<AgentPushRunResult> PushAsync(AgentPushRequest request, CancellationToken cancellationToken);

    /// <summary>The agents the push would upload: those whose bytes differ from their ledger record. Local only: no network.</summary>
    /// <remarks>
    /// What the capture hook asks at <c>SessionEnd</c> before starting a background push, so a session that changed no agent
    /// costs a hash of each agent file and nothing else. A file the registry already refused with these exact bytes, and a
    /// file without a name, do not count: pushing them again would only be refused again.
    /// </remarks>
    IReadOnlyList<DiscoveredAgent> Changed(string? harnessHome, string? projectStart, string? registry);
}

/// <summary>What to push and where.</summary>
/// <param name="Names">Agent names to push (case-insensitive); empty pushes every discovered agent.</param>
/// <param name="HarnessId">Limit discovery to this harness; <c>null</c> for every harness.</param>
/// <param name="HarnessHome">Override the harness config root.</param>
/// <param name="ProjectStart">Where the project-scope walk-up starts; <c>null</c> skips project agents.</param>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> falls back to the environment, the saved default, then the built-in one.</param>
/// <param name="Force">
/// Upload even when the ledger says the registry has this content, replace the registry's version when it changed elsewhere
/// since this copy synced (keep mine; the replaced version stays in history), and restore a deleted or renamed agent as new.
/// </param>
/// <param name="FromHook">
/// The capture hook's background push: the ledger is trusted without listing the registry, content the registry already
/// refused is reported from the ledger without asking again, and a file without a name or description is left out (the
/// command reports it).
/// </param>
/// <param name="As">
/// Push the one selected agent as a new registry agent of this name (keep both): its <c>name</c> line is set to it, and the
/// file is moved to <c>&lt;As&gt;.md</c> beside it, here and as the path the registry keeps, so the original agent can be
/// pulled back to the path it had.
/// </param>
internal sealed record AgentPushRequest(
    IReadOnlyList<string> Names, string? HarnessId, string? HarnessHome, string? ProjectStart, string? Registry, bool Force = false, bool FromHook = false,
    string? As = null);

/// <summary>Why a push run stopped before uploading anything.</summary>
internal enum AgentPushRunFailure
{
    /// <summary>The run went ahead; see the entries.</summary>
    None,

    /// <summary>The registry URL is not an absolute http(s) URL.</summary>
    InvalidRegistry,

    /// <summary>A <c>--harness</c> id matched no registered harness.</summary>
    UnknownHarness,

    /// <summary>The named harness has no custom agents.</summary>
    Unsupported,

    /// <summary>A requested agent name was not found on this machine.</summary>
    AgentNotFound,

    /// <summary>There is no stored session for the registry.</summary>
    NotLoggedIn,

    /// <summary>The stored session expired and could not be refreshed.</summary>
    SessionExpired,

    /// <summary>The registry did not answer.</summary>
    RegistryUnreachable,

    /// <summary>The registry speaks another API major or does not support agents; the detail says which.</summary>
    Incompatible,

    /// <summary>
    /// <c>--as</c> needs exactly one local agent to push under a new name that nothing in its scope, here or in the registry,
    /// already answers to, and a free file name for it; see the detail.
    /// </summary>
    InvalidAs,

    /// <summary>The registry would not list the agents needed to confirm what the ledger says; see the detail.</summary>
    ListFailed,
}

/// <summary>One agent file and what happened to it.</summary>
internal sealed record AgentPushEntry(DiscoveredAgent Agent, AgentPushOutcome Outcome);

/// <summary>The result of a push run.</summary>
/// <param name="Failure">Why the run stopped early, or <see cref="AgentPushRunFailure.None"/>.</param>
/// <param name="Detail">The offending value or explanation for a failure.</param>
/// <param name="Registry">The registry the run targeted; <c>null</c> when its URL was invalid.</param>
/// <param name="Entries">One entry per discovered agent file, in discovery order; empty on an early failure.</param>
internal sealed record AgentPushRunResult(AgentPushRunFailure Failure, string? Detail, Uri? Registry, IReadOnlyList<AgentPushEntry> Entries)
{
    /// <summary>True when the run went ahead and every agent reached the registry (or was an identical duplicate).</summary>
    public bool Succeeded => Failure == AgentPushRunFailure.None && Entries.All(e => e.Outcome.Succeeded || e.Outcome.Status == AgentPushStatus.Skipped);

    public static AgentPushRunResult Fail(AgentPushRunFailure failure, string? detail, Uri? registry = null) => new(failure, detail, registry, []);
}
