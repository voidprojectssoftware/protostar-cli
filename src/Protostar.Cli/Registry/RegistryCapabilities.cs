using System.Collections.Concurrent;
using Protostar.Cli.Auth;

namespace Protostar.Cli.Registry;

/// <summary>Checks, before a command uses a feature, that the registry is reachable, speaks this CLI's API, and has the feature.</summary>
/// <remarks>
/// <para>
/// The one place commands ask "can this registry do X". A shared registry can lag the CLI by a release or more, so a
/// command that needs a feature added within API v1 names its <see cref="RegistryCapability"/> here and, when the
/// registry lacks it, stops with <see cref="ApiCompatibility.TooOld"/> instead of failing on a 404.
/// </para>
/// <para>
/// <b>Lenient and strict.</b> A registry from before the capability list (registry#15) cannot say what it has. For
/// most features that counts as unknown and the command goes ahead (<see cref="RequireAsync"/>): the request either
/// works, or a bare 404 is turned into the same message. Reads that must come back redacted use <see cref="RequireListedAsync"/>,
/// where unknown refuses too: a registry that ignores <c>redacted=true</c> would serve secrets, and refusing is the
/// lesser harm.
/// </para>
/// </remarks>
internal interface IRegistryCapabilities
{
    /// <summary>Checks the registry, treating a registry that lists no capabilities as having them all.</summary>
    /// <param name="registry">The registry base URL.</param>
    /// <param name="cancellationToken">Cancels the <c>/v1/meta</c> request.</param>
    /// <param name="capabilities">The features the command is about to use.</param>
    Task<RegistryCheck> RequireAsync(Uri registry, CancellationToken cancellationToken, params string[] capabilities);

    /// <summary>Checks the registry, refusing one that lists no capabilities, for features that must not be assumed.</summary>
    /// <inheritdoc cref="RequireAsync" path="/param"/>
    Task<RegistryCheck> RequireListedAsync(Uri registry, CancellationToken cancellationToken, params string[] capabilities);
}

/// <summary>Why a <see cref="RegistryCheck"/> stopped the command.</summary>
internal enum RegistryCheckFailure
{
    None,

    /// <summary>The registry did not answer <c>/v1/meta</c>.</summary>
    Unreachable,

    /// <summary>It speaks another API major, or lacks a required capability; <see cref="RegistryCheck.Detail"/> says which.</summary>
    Incompatible,
}

/// <summary>The outcome of checking a registry's capabilities.</summary>
/// <param name="Failure">Why the command must stop, or <see cref="RegistryCheckFailure.None"/>.</param>
/// <param name="Detail">The message for people; for a missing capability, <see cref="ApiCompatibility.TooOld"/>.</param>
/// <param name="Missing">The required capability the registry lacks, when that is the failure.</param>
/// <param name="Meta">The registry's <c>/v1/meta</c>, when it answered.</param>
internal sealed record RegistryCheck(RegistryCheckFailure Failure, string? Detail, string? Missing, RegistryMeta? Meta)
{
    public bool Ok => Failure == RegistryCheckFailure.None;

    /// <summary>Whether the registry has an optional feature the command can do without.</summary>
    public CapabilitySupport Supports(string capability) => ApiCompatibility.Supports(Meta, capability);
}

/// <summary>Production <see cref="IRegistryCapabilities"/>.</summary>
/// <remarks>
/// <c>/v1/meta</c> is read once per registry per process and kept, so a command (and the background push a hook
/// starts) asks it once however many services check. A failed read is not kept: the next check asks again.
/// <c>protostar mcp</c> is a long-lived process, so it sees a registry upgraded while it runs only after it restarts,
/// which a harness does with every new session.
/// </remarks>
internal sealed class RegistryCapabilities : IRegistryCapabilities
{
    private readonly IRegistryApi _api;
    private readonly ConcurrentDictionary<Uri, RegistryMeta> _meta = new();

    public RegistryCapabilities(IRegistryApi api) => _api = api;

    /// <inheritdoc />
    public Task<RegistryCheck> RequireAsync(Uri registry, CancellationToken cancellationToken, params string[] capabilities) =>
        CheckAsync(registry, capabilities, strict: false, cancellationToken);

    /// <inheritdoc />
    public Task<RegistryCheck> RequireListedAsync(Uri registry, CancellationToken cancellationToken, params string[] capabilities) =>
        CheckAsync(registry, capabilities, strict: true, cancellationToken);

    private async Task<RegistryCheck> CheckAsync(Uri registry, string[] capabilities, bool strict, CancellationToken cancellationToken)
    {
        var meta = await GetMetaAsync(registry, cancellationToken);
        if (meta is null)
            return new RegistryCheck(RegistryCheckFailure.Unreachable, null, null, null);
        if (ApiCompatibility.Check(meta) is { } incompatible)
            return new RegistryCheck(RegistryCheckFailure.Incompatible, incompatible, null, meta);

        foreach (var capability in capabilities)
        {
            var support = ApiCompatibility.Supports(meta, capability);
            if (support == CapabilitySupport.Missing || strict && support == CapabilitySupport.Unknown)
                return new RegistryCheck(RegistryCheckFailure.Incompatible, ApiCompatibility.TooOld(capability), capability, meta);
        }

        return new RegistryCheck(RegistryCheckFailure.None, null, null, meta);
    }

    private async Task<RegistryMeta?> GetMetaAsync(Uri registry, CancellationToken cancellationToken)
    {
        if (_meta.TryGetValue(registry, out var known))
            return known;
        var meta = await _api.GetMetaAsync(registry, cancellationToken);
        if (meta is not null)
            _meta[registry] = meta;
        return meta;
    }
}
