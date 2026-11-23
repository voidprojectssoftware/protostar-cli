namespace Protostar.Cli.Auth;

/// <summary>
/// The CLI/registry compatibility contract. Versions are not lockstepped; instead the registry
/// advertises the API majors it supports at <c>/v1/meta</c> and the CLI checks that the major it
/// speaks is among them before doing anything else.
/// </summary>
/// <remarks>
/// Within a major, the registry also lists its <see cref="RegistryCapability">capabilities</see>: the features added
/// within v1 that this build has. A registry from before the list sends none, which is <see cref="CapabilitySupport.Unknown"/>,
/// not "nothing supported".
/// </remarks>
internal static class ApiCompatibility
{
    /// <summary>Returns null when compatible, otherwise a human-readable explanation.</summary>
    public static string? Check(RegistryMeta? meta)
    {
        if (meta?.ApiMajors is not { Length: > 0 } majors)
            return "The registry did not report a supported API version.";

        if (!majors.Contains(AuthConstants.SupportedApiMajor))
        {
            var supported = string.Join(", ", majors.Select(m => $"v{m}"));
            return $"This protostar build speaks registry API v{AuthConstants.SupportedApiMajor}, " +
                   $"but the registry supports {supported}. Update protostar to a compatible version.";
        }

        return null;
    }

    /// <summary>Whether the registry described by <paramref name="meta"/> lists <paramref name="capability"/>.</summary>
    /// <remarks>Names are compared exactly, as the registry's contract says; names this CLI does not know are never asked about.</remarks>
    public static CapabilitySupport Supports(RegistryMeta? meta, string capability) =>
        meta?.Capabilities is not { } listed ? CapabilitySupport.Unknown
        : listed.Contains(capability, StringComparer.Ordinal) ? CapabilitySupport.Listed
        : CapabilitySupport.Missing;

    /// <summary>The one message for a registry that lacks <paramref name="capability"/>, whether it said so or answered 404.</summary>
    public static string TooOld(string capability) => $"This registry is too old for {RegistryCapability.Feature(capability)}. Update the registry.";
}

/// <summary>Whether a registry has a capability.</summary>
internal enum CapabilitySupport
{
    /// <summary>The registry lists it.</summary>
    Listed,

    /// <summary>The registry lists capabilities, and not this one.</summary>
    Missing,

    /// <summary>The registry predates the capability list (registry#15), so it cannot say.</summary>
    Unknown,
}

/// <summary>The <c>/v1/meta</c> capability names this CLI checks, as the registry defines them in <c>docs/api/capabilities.md</c>.</summary>
internal static class RegistryCapability
{
    public const string Skills = "skills";
    public const string StaleSkillPush = "stale-skill-push";
    public const string ProjectSkills = "project-skills";
    public const string SkillRename = "skill-rename";
    public const string SkillDelete = "skill-delete";
    public const string SkillTombstoneHashes = "skill-tombstone-hashes";
    public const string Sessions = "sessions";
    public const string TranscriptLookup = "transcript-lookup";
    public const string TranscriptPrefixLookup = "transcript-prefix-lookup";
    public const string SessionSkills = "session-skills";
    public const string RedactedRead = "redacted-read";
    public const string Projects = "projects";
    public const string Memory = "memory";
    public const string MemoryContent = "memory-content";
    public const string MemoryBaseVersions = "memory-base-versions";
    public const string Search = "search";
    public const string MemoryRedactedRead = "memory-redacted-read";
    public const string Agents = "agents";
    public const string AgentContent = "agent-content";
    public const string SessionAgents = "session-agents";
    public const string AgentSearch = "agent-search";

    /// <summary>What a person calls the feature <paramref name="capability"/> names, to finish "too old for ...".</summary>
    public static string Feature(string capability) => capability switch
    {
        Skills => "skill backups",
        StaleSkillPush => "detecting overwritten skills",
        ProjectSkills => "project skills",
        SkillRename => "renaming skills",
        SkillDelete => "deleting skills",
        Sessions => "session backups",
        TranscriptLookup => "verifying backups",
        TranscriptPrefixLookup => "looking sessions up by id prefix",
        SessionSkills => "listing the skills a session used",
        RedactedRead => "serving sessions with secrets redacted",
        Projects => "projects",
        Memory => "memory backups",
        MemoryContent => "bringing memory to this machine",
        MemoryBaseVersions => "memory version history",
        Search => "search",
        MemoryRedactedRead => "serving memory with secrets redacted",
        Agents => "agent backups",
        AgentContent => "reading agents",
        SessionAgents => "listing the agents a session used",
        AgentSearch => "searching agents",
        _ => capability,
    };
}
