using Protostar.Cli.Auth;
using Protostar.Cli.Projects;

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
    /// <param name="project">The project folder a project skill lives in; <c>null</c> for a global skill.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <remarks>
    /// A registry from before project skills ignores <paramref name="project"/> and files the skill as global,
    /// so the registry must be upgraded before a CLI that sends it.
    /// </remarks>
    Task<SkillPushOutcome> PushSkillAsync(
        Uri registry,
        string accessToken,
        string name,
        IReadOnlyList<SkillUploadFile> files,
        SkillProject? project,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every one of the caller's skills, global and project, from <c>GET /v1/skills?includeProjects=true</c>,
    /// each described by its current version.
    /// </summary>
    Task<RegistrySkillList> ListSkillsAsync(Uri registry, string accessToken, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads one version of the caller's skill, files included, from <c>GET /v1/skills/{name}/files</c>.
    /// </summary>
    /// <param name="registry">The registry base URL.</param>
    /// <param name="accessToken">A current bearer token for that registry.</param>
    /// <param name="name">The skill's exact name, as <see cref="ListSkillsAsync"/> reported it.</param>
    /// <param name="projectId">The skill's project, as <see cref="ListSkillsAsync"/> reported it; <c>null</c> for a global skill.</param>
    /// <param name="version">The version to fetch; <c>null</c> for the current version.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    Task<RegistrySkillDownload> GetSkillFilesAsync(
        Uri registry,
        string accessToken,
        string name,
        Guid? projectId,
        int? version,
        CancellationToken cancellationToken);

    /// <summary>Renames the caller's skill through <c>POST /v1/skills/{name}/rename</c>, keeping its version history.</summary>
    /// <param name="registry">The registry base URL.</param>
    /// <param name="accessToken">A current bearer token for that registry.</param>
    /// <param name="name">The skill's exact current name.</param>
    /// <param name="projectId">The skill's project; <c>null</c> for a global skill.</param>
    /// <param name="newName">The name it takes.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The skill's name afterwards, or the registry's reason (a 409 when the name is taken).</returns>
    Task<ApiResult<string>> RenameSkillAsync(
        Uri registry, string accessToken, string name, Guid? projectId, string newName, CancellationToken cancellationToken);

    /// <summary>Deletes the caller's skill, with every version, through <c>DELETE /v1/skills/{name}</c>.</summary>
    /// <param name="registry">The registry base URL.</param>
    /// <param name="accessToken">A current bearer token for that registry.</param>
    /// <param name="name">The skill's exact name.</param>
    /// <param name="projectId">The skill's project; <c>null</c> for a global skill.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<ApiResult<bool>> DeleteSkillAsync(Uri registry, string accessToken, string name, Guid? projectId, CancellationToken cancellationToken);
}

/// <summary>The project folder a project skill is pushed from, for the registry to resolve to a project.</summary>
/// <param name="MachineId">This machine's id, which keys a folder outside git.</param>
/// <param name="Facts">The project folder's facts, from the one resolver sessions also use.</param>
internal sealed record SkillProject(Guid MachineId, ProjectFacts Facts);

/// <summary>One skill in the caller's registry, described by its current version.</summary>
/// <param name="Name">The skill's name, unique per user within its project (or among global skills).</param>
/// <param name="Description">The current version's description; <c>null</c> when omitted.</param>
/// <param name="Version">The current version number.</param>
/// <param name="ContentHash">The registry's content hash; compare with <c>SkillFileReader.RegistryHash</c>.</param>
/// <param name="UpdatedAt">When the current version was pushed.</param>
/// <param name="ProjectId">The project the skill belongs to; <c>null</c> for a global skill.</param>
/// <param name="ProjectName">That project's display name; <c>null</c> for a global skill.</param>
internal sealed record RegistrySkill(
    string Name, string? Description, int Version, string ContentHash, DateTimeOffset UpdatedAt, Guid? ProjectId = null, string? ProjectName = null);

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
    /// The skill was not uploaded because a different skill of the same name and scope was, and the registry
    /// holds one skill per name in each scope.
    /// </summary>
    Conflict,

    /// <summary>The upload could not complete (unreachable registry, unreadable files, server error).</summary>
    Failed,
}

/// <summary>The outcome of sending one skill to the registry.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Version">The registry's current version number after the push; <c>null</c> unless created or unchanged.</param>
/// <param name="Message">Why a skill was refused, skipped, or failed; <c>null</c> on success.</param>
/// <param name="ProjectId">The project the registry filed a project skill under; <c>null</c> for a global skill.</param>
/// <param name="ProjectName">That project's display name; <c>null</c> for a global skill.</param>
internal sealed record SkillPushOutcome(SkillPushStatus Status, int? Version, string? Message, Guid? ProjectId = null, string? ProjectName = null)
{
    /// <summary>True when the skill is now safely in the registry.</summary>
    public bool Succeeded => Status is SkillPushStatus.Created or SkillPushStatus.Unchanged;
}
