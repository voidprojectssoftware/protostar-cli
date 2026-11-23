using Protostar.Cli.Auth;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Sessions;

/// <summary>The custom agents a shown session delegated to, from the registry.</summary>
internal interface ISessionAgents
{
    /// <summary>The user's custom agents <paramref name="session"/> used (its subagents' included), in the registry's order; <c>null</c> when they cannot be listed.</summary>
    /// <remarks>
    /// Optional decoration for <c>sessions show</c>, as <see cref="ISessionSkills"/> is, so it never fails: a registry
    /// that lists capabilities without <c>session-agents</c> is not asked, and one that lists none is asked anyway, with
    /// any refusal (a 404 on an older registry) or failure giving <c>null</c>.
    /// </remarks>
    Task<IReadOnlyList<SessionAgent>?> GetAsync(OpenedSession session, CancellationToken cancellationToken);
}

/// <summary>Production <see cref="ISessionAgents"/>: one request, <c>GET /v1/transcripts/{harness}/{key}/agents</c>.</summary>
/// <remarks>
/// The registry does the matching (the session's project first, then personal agents, as Claude Code loads them) and
/// leaves out built-in and plugin agent types, so nothing on this machine is read.
/// </remarks>
internal sealed class SessionAgents : ISessionAgents
{
    private readonly IRegistryCapabilities _capabilities;
    private readonly ITranscriptApi _api;

    public SessionAgents(IRegistryCapabilities capabilities, ITranscriptApi api)
    {
        _capabilities = capabilities;
        _api = api;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionAgent>?> GetAsync(OpenedSession session, CancellationToken cancellationToken)
    {
        // Opening the session already read /v1/meta, which is kept, so this asks nothing.
        var check = await _capabilities.RequireAsync(session.Registry, cancellationToken);
        if (check.Supports(RegistryCapability.SessionAgents) == CapabilitySupport.Missing)
            return null;

        var transcript = session.Detail.Transcript;
        var listed = await _api.GetAgentsAsync(session.Registry, session.AccessToken, transcript.Harness, transcript.Key, cancellationToken);
        return listed.Ok ? listed.Value : null;
    }
}
