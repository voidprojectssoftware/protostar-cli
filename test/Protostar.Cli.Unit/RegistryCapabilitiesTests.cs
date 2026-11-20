using Protostar.Cli.Auth;
using Protostar.Cli.Registry;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>Contract tests for <see cref="IRegistryCapabilities"/>: what a command may assume about a registry before using a feature.</summary>
public sealed class RegistryCapabilitiesTests
{
    private static readonly Uri Registry = new("https://registry.test");

    private readonly MetaOnlyRegistry _api = new();

    private RegistryCapabilities Sut() => new(_api);

    [Fact]
    public async Task A_listed_capability_passes()
    {
        _api.Meta = new RegistryMeta { ApiMajors = [1], Capabilities = ["sessions", "memory"] };

        var check = await Sut().RequireAsync(Registry, CancellationToken.None, RegistryCapability.Memory);

        Assert.True(check.Ok);
        Assert.Equal(CapabilitySupport.Listed, check.Supports(RegistryCapability.Sessions));
    }

    [Fact]
    public async Task A_missing_capability_is_refused_with_the_standard_message()
    {
        _api.Meta = new RegistryMeta { ApiMajors = [1], Capabilities = ["sessions"] };

        var check = await Sut().RequireAsync(Registry, CancellationToken.None, RegistryCapability.Sessions, RegistryCapability.Memory);

        Assert.Equal(RegistryCheckFailure.Incompatible, check.Failure);
        Assert.Equal(RegistryCapability.Memory, check.Missing);
        Assert.Equal("This registry is too old for memory backups. Update the registry.", check.Detail);
    }

    [Fact]
    public async Task A_registry_with_no_list_is_unknown_and_lenient_checks_go_ahead()
    {
        _api.Meta = new RegistryMeta { ApiMajors = [1] };

        var check = await Sut().RequireAsync(Registry, CancellationToken.None, RegistryCapability.Memory);

        Assert.True(check.Ok);
        Assert.Equal(CapabilitySupport.Unknown, check.Supports(RegistryCapability.TranscriptPrefixLookup));
    }

    [Fact]
    public async Task A_registry_with_no_list_is_refused_by_a_strict_check()
    {
        _api.Meta = new RegistryMeta { ApiMajors = [1] };

        var check = await Sut().RequireListedAsync(Registry, CancellationToken.None, RegistryCapability.RedactedRead);

        Assert.Equal(RegistryCheckFailure.Incompatible, check.Failure);
        Assert.Equal("This registry is too old for serving sessions with secrets redacted. Update the registry.", check.Detail);
    }

    [Fact]
    public async Task A_strict_check_passes_when_the_capability_is_listed()
    {
        _api.Meta = new RegistryMeta { ApiMajors = [1], Capabilities = ["redacted-read"] };

        Assert.True((await Sut().RequireListedAsync(Registry, CancellationToken.None, RegistryCapability.RedactedRead)).Ok);
    }

    [Fact]
    public async Task Another_api_major_is_incompatible_whatever_it_lists()
    {
        _api.Meta = new RegistryMeta { ApiMajors = [2], Capabilities = ["memory"] };

        var check = await Sut().RequireAsync(Registry, CancellationToken.None, RegistryCapability.Memory);

        Assert.Equal(RegistryCheckFailure.Incompatible, check.Failure);
        Assert.Null(check.Missing);
        Assert.Contains("API v1", check.Detail);
    }

    [Fact]
    public async Task An_unreachable_registry_is_unreachable_and_asked_again_next_time()
    {
        _api.Meta = null;
        var sut = Sut();

        Assert.Equal(RegistryCheckFailure.Unreachable, (await sut.RequireAsync(Registry, CancellationToken.None)).Failure);
        _api.Meta = MetaOnlyRegistry.Current;
        Assert.True((await sut.RequireAsync(Registry, CancellationToken.None, RegistryCapability.Search)).Ok);
        Assert.Equal(2, _api.MetaCalls);
    }

    [Fact]
    public async Task Meta_is_read_once_per_registry_however_many_checks_run()
    {
        var sut = Sut();

        await sut.RequireAsync(Registry, CancellationToken.None, RegistryCapability.Sessions);
        await sut.RequireListedAsync(Registry, CancellationToken.None, RegistryCapability.RedactedRead);
        await sut.RequireAsync(new Uri("https://other.test"), CancellationToken.None);

        Assert.Equal(2, _api.MetaCalls);
    }

    [Fact]
    public void Names_are_compared_exactly()
    {
        var meta = new RegistryMeta { ApiMajors = [1], Capabilities = ["Memory", "future-thing"] };

        Assert.Equal(CapabilitySupport.Missing, ApiCompatibility.Supports(meta, RegistryCapability.Memory));
    }
}
