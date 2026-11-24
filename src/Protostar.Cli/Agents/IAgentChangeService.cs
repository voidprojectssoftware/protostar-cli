using Protostar.Cli.Registry;

namespace Protostar.Cli.Agents;

/// <summary>Renames or deletes a custom agent in the registry: <c>protostar agents rename</c> and <c>agents delete</c>.</summary>
/// <remarks>
/// <para>
/// The agents' copy of <see cref="Skills.ISkillChangeService"/> (ADR 0003). Finding the agent is a separate step from
/// changing it, so the command can show what it found (and ask before a delete) before anything happens. An agent is
/// found in the scopes the other agent commands use: personal, and the project of the folder it runs in. A name that is
/// both a personal agent and this folder's project agent is refused with both, rather than guessed.
/// </para>
/// <para>
/// This machine's copies of the agent are the files the ledger records as synced with it, in any checkout. A delete never
/// touches them. A rename rewrites their <c>name</c> line and nothing else: the file stays where it is, because an agent is
/// named by that field (ADR 0003), the registry keeps the path the agent was pushed from, and a pull on another machine
/// follows the rename in place too, so every machine keeps the file at the same path. Returns data only; rendering belongs to
/// <see cref="AgentChangePresenter"/>.
/// </para>
/// </remarks>
internal interface IAgentChangeService
{
    /// <summary>Finds the registry agent <see cref="AgentTargetRequest.Name"/> names, and this machine's copies of it.</summary>
    Task<AgentTargetResult> FindAsync(AgentTargetRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes <paramref name="target"/>'s registry agent, with every version. Local files are never touched.</summary>
    /// <remarks>
    /// The ledger records of the local copies are kept, so the capture hook still sees an unchanged copy as already backed up
    /// and does not push it back; an edited copy's push is refused by the registry's memory of the delete.
    /// </remarks>
    Task<AgentDeleteResult> DeleteAsync(AgentTarget target, CancellationToken cancellationToken);

    /// <summary>Renames <paramref name="target"/>'s registry agent to <paramref name="newName"/>, then rewrites this machine's copies to match.</summary>
    /// <remarks>
    /// <para>
    /// Refused before the registry is asked when a local copy has changes not yet pushed: renaming under unpushed work would
    /// leave a copy whose content is neither the old agent nor the new one, so the way out (push, or pull with
    /// <c>--force</c>) comes first.
    /// </para>
    /// <para>
    /// The registry renames the agent, not its stored file, which keeps the old <c>name</c> line; a pull rewrites that line
    /// as it installs (see <see cref="AgentInstaller"/>), so no new version is pushed. Each unedited copy has its
    /// <c>name</c> line set to the new name and its ledger record moved to the new name with the rewritten content's hash and
    /// the same version, which the pull classifies as up to date (or an update available, when it was behind).
    /// </para>
    /// </remarks>
    Task<AgentRenameResult> RenameAsync(AgentTarget target, string newName, CancellationToken cancellationToken);
}

/// <summary>Which agent to find.</summary>
/// <param name="Name">The registry agent's exact name.</param>
/// <param name="Registry">The <c>--registry</c> option; <c>null</c> falls back to the environment, the saved default, then the built-in one.</param>
/// <param name="ProjectStart">The folder whose project's agent may be meant; <c>null</c> (<c>--personal-only</c>) means the personal agent only.</param>
/// <param name="ProjectOnly">True when <c>--project</c> was given: only that folder's project agent is meant, never the personal one.</param>
internal sealed record AgentTargetRequest(string Name, string? Registry, string? ProjectStart, bool ProjectOnly = false);

/// <summary>A registry agent to rename or delete, with what is needed to change it.</summary>
/// <param name="Registry">The registry it is in.</param>
/// <param name="AccessToken">The session's bearer token.</param>
/// <param name="Agent">The registry agent.</param>
/// <param name="Copies">This machine's copies of it, from the ledger; empty when none.</param>
internal sealed record AgentTarget(Uri Registry, string AccessToken, RegistryAgent Agent, IReadOnlyList<LocalAgentCopy> Copies);

/// <summary>One of this machine's copies of a registry agent.</summary>
/// <param name="Path">The agent file.</param>
/// <param name="Recorded">What the ledger says was last synced there.</param>
/// <param name="HasLocalChanges">True when the file no longer matches that record (edited here and not pushed), or cannot be read.</param>
internal sealed record LocalAgentCopy(string Path, AgentLedgerEntry Recorded, bool HasLocalChanges);

/// <summary>The agent found, or why not.</summary>
/// <param name="Failure">Why the lookup stopped; <see cref="AgentPullRunFailure.AgentNotFound"/> or <see cref="AgentPullRunFailure.Ambiguous"/> carry the full explanation in <paramref name="Detail"/>.</param>
/// <param name="Detail">The failure's detail.</param>
/// <param name="Registry">The registry asked, when it could be resolved.</param>
/// <param name="Target">The agent; <c>null</c> on failure.</param>
internal sealed record AgentTargetResult(AgentPullRunFailure Failure, string? Detail, Uri? Registry, AgentTarget? Target)
{
    public static AgentTargetResult Fail(AgentPullRunFailure failure, string? detail, Uri? registry = null) => new(failure, detail, registry, null);
}

/// <summary>A delete's result.</summary>
/// <param name="Target">What was deleted (or not).</param>
/// <param name="Error">Why the registry refused; <c>null</c> when it is gone.</param>
internal sealed record AgentDeleteResult(AgentTarget Target, string? Error);

/// <summary>A rename's result.</summary>
/// <param name="Target">What was renamed, as it was before.</param>
/// <param name="NewName">The name it has now; <c>null</c> when not renamed.</param>
/// <param name="Error">Why the registry refused (for example, the name is taken); <c>null</c> otherwise.</param>
/// <param name="Edited">The local copies with unpushed changes that stopped the rename before the registry was asked; empty otherwise.</param>
/// <param name="Copies">What happened to each local copy; empty when not renamed.</param>
internal sealed record AgentRenameResult(
    AgentTarget Target, string? NewName, string? Error, IReadOnlyList<LocalAgentCopy> Edited, IReadOnlyList<LocalAgentRename> Copies);

/// <summary>What a rename did to one local copy.</summary>
/// <param name="Path">The agent file, which stays where it is.</param>
/// <param name="Renamed">True when its <c>name</c> line now says the new name.</param>
/// <param name="Note">Why it was left as it was; <c>null</c> when renamed.</param>
internal sealed record LocalAgentRename(string Path, bool Renamed, string? Note);
