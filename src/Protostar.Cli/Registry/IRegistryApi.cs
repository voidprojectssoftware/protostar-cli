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

    /// <summary>
    /// Lists the caller's skills from <c>GET /v1/skills</c>, each described by its current version.
    /// </summary>
    Task<RegistrySkillList> ListSkillsAsync(Uri registry, string accessToken, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads one version of the caller's skill, files included, from <c>GET /v1/skills/{name}/files</c>.
    /// </summary>
    /// <param name="registry">The registry base URL.</param>
    /// <param name="accessToken">A current bearer token for that registry.</param>
    /// <param name="name">The skill's exact name, as <see cref="ListSkillsAsync"/> reported it.</param>
    /// <param name="version">The version to fetch; <c>null</c> for the current version.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    Task<RegistrySkillDownload> GetSkillFilesAsync(
        Uri registry,
        string accessToken,
        string name,
        int? version,
        CancellationToken cancellationToken);
}

/// <summary>One skill in the caller's registry, described by its current version.</summary>
/// <param name="Name">The skill's name, unique per user.</param>
/// <param name="Description">The current version's description; <c>null</c> when omitted.</param>
/// <param name="Version">The current version number.</param>
/// <param name="ContentHash">The registry's content hash; compare with <c>SkillFileReader.RegistryHash</c>.</param>
/// <param name="UpdatedAt">When the current version was pushed.</param>
internal sealed record RegistrySkill(string Name, string? Description, int Version, string ContentHash, DateTimeOffset UpdatedAt);

/// <summary>The result of listing skills: the skills, or why they could not be listed.</summary>
/// <param name="Skills">The caller's skills; <c>null</c> when the request failed.</param>
/// <param name="Error">Why the request failed; <c>null</c> on success.</param>
internal sealed record RegistrySkillList(IReadOnlyList<RegistrySkill>? Skills, string? Error);

/// <summary>One version of a skill with every file's bytes.</summary>
internal sealed record RegistrySkillContent(string Name, int Version, string ContentHash, IReadOnlyList<RegistrySkillFile> Files);

/// <summary>One downloaded file.</summary>
/// <param name="RelativePath">The path relative to the skill root, as the registry sent it (not yet validated).</param>
/// <param name="Sha256">The registry's hex SHA-256 of <paramref name="Content"/>.</param>
/// <param name="Content">The file's bytes.</param>
internal sealed record RegistrySkillFile(string RelativePath, string Sha256, byte[] Content);

/// <summary>The result of downloading a skill version.</summary>
/// <param name="Content">The version; <c>null</c> when the request failed.</param>
/// <param name="Error">Why the request failed; <c>null</c> on success.</param>
internal sealed record RegistrySkillDownload(RegistrySkillContent? Content, string? Error);

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

    /// <summary>The skill was not uploaded because an identical skill of the same name was.</summary>
    Skipped,

    /// <summary>
    /// The skill was not uploaded because a different skill of the same name was, and the registry holds one
    /// skill per name.
    /// </summary>
    Conflict,

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
