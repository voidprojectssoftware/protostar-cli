using Protostar.Cli.Auth;

namespace Protostar.Cli.Registry;

/// <summary>
/// The registry's authenticated <c>/v1</c> API, as the CLI uses it.
/// </summary>
/// <remarks>
/// Kept behind an interface so services that call the registry (skill push, the capture hook) are
/// tested with an in-memory fake instead of a running registry. Implementations never throw for an
/// unreachable registry or a refused request; those come back as typed results.
/// </remarks>
internal interface IRegistryApi
{
    /// <summary>
    /// Reads the registry's <c>/v1/meta</c> document.
    /// </summary>
    /// <returns>The metadata, or <c>null</c> when the registry is unreachable or did not answer with it.</returns>
    Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads one skill's files to <c>POST /v1/skills</c>.
    /// </summary>
    /// <param name="registry">The registry base URL.</param>
    /// <param name="accessToken">A current bearer token for that registry.</param>
    /// <param name="name">The skill's name, used by the registry only when its SKILL.md declares none.</param>
    /// <param name="files">Every file in the skill, with paths relative to the skill's root.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    Task<SkillPushOutcome> PushSkillAsync(
        Uri registry,
        string accessToken,
        string name,
        IReadOnlyList<SkillUploadFile> files,
        CancellationToken cancellationToken);
}

/// <summary>One file of a skill being uploaded.</summary>
/// <param name="RelativePath">The path relative to the skill's root, with <c>/</c> separators.</param>
/// <param name="Content">The file's bytes.</param>
internal sealed record SkillUploadFile(string RelativePath, byte[] Content);

/// <summary>What happened to one skill sent to the registry.</summary>
internal enum SkillPushStatus
{
    /// <summary>The registry stored a new version.</summary>
    Created,

    /// <summary>The registry already had this exact content as the current version; nothing was stored.</summary>
    Unchanged,

    /// <summary>The registry refused the skill (not its creator, too large, invalid); see the message.</summary>
    Refused,

    /// <summary>The skill was skipped before upload (for example a second skill with the same name).</summary>
    Skipped,

    /// <summary>The upload could not complete (unreachable registry, unreadable files, server error).</summary>
    Failed,
}

/// <summary>The outcome of sending one skill to the registry.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Version">The registry's current version number after the push; <c>null</c> unless created or unchanged.</param>
/// <param name="Message">Why a skill was refused, skipped, or failed; <c>null</c> on success.</param>
internal sealed record SkillPushOutcome(SkillPushStatus Status, int? Version, string? Message)
{
    /// <summary>True when the skill is now safely in the registry.</summary>
    public bool Succeeded => Status is SkillPushStatus.Created or SkillPushStatus.Unchanged;
}
