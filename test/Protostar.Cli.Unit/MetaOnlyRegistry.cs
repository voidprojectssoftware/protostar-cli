using Protostar.Cli.Auth;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Unit;

/// <summary>An <see cref="IRegistryApi"/> that answers only <c>/v1/meta</c>, for services that use it through <see cref="RegistryCapabilities"/>.</summary>
internal sealed class MetaOnlyRegistry : IRegistryApi
{
    /// <summary>A current registry: API v1, listing every capability this CLI checks.</summary>
    public static RegistryMeta Current => new()
    {
        ApiMajors = [1],
        Capabilities =
        [
            RegistryCapability.Skills, RegistryCapability.StaleSkillPush, RegistryCapability.ProjectSkills, RegistryCapability.SkillRename,
            RegistryCapability.SkillDelete, RegistryCapability.Sessions, RegistryCapability.TranscriptLookup, RegistryCapability.TranscriptPrefixLookup,
            RegistryCapability.SessionSkills, RegistryCapability.RedactedRead, RegistryCapability.Projects, RegistryCapability.Memory,
            RegistryCapability.MemoryContent, RegistryCapability.MemoryBaseVersions, RegistryCapability.Search, RegistryCapability.MemoryRedactedRead,
        ],
    };

    /// <summary>What <c>/v1/meta</c> answers; <c>null</c> for an unreachable registry.</summary>
    public RegistryMeta? Meta { get; set; } = Current;

    /// <summary>How many times <c>/v1/meta</c> was asked.</summary>
    public int MetaCalls { get; private set; }

    /// <summary>Capabilities over a registry that answers <paramref name="meta"/>.</summary>
    public static IRegistryCapabilities Capabilities(RegistryMeta? meta) => new RegistryCapabilities(new MetaOnlyRegistry { Meta = meta });

    public Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken)
    {
        MetaCalls++;
        return Task.FromResult(Meta);
    }

    public Task<SkillPushOutcome> PushSkillAsync(
        Uri registry, string accessToken, string name, IReadOnlyList<SkillUploadFile> files, SkillProject? project, int? baseVersion,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RegistrySkillList> ListSkillsAsync(Uri registry, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RegistrySkillDownload> GetSkillFilesAsync(
        Uri registry, string accessToken, string name, Guid? projectId, int? version, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ApiResult<string>> RenameSkillAsync(
        Uri registry, string accessToken, string name, Guid? projectId, string newName, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ApiResult<bool>> DeleteSkillAsync(Uri registry, string accessToken, string name, Guid? projectId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
