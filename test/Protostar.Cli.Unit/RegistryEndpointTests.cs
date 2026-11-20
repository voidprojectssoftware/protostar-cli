using Protostar.Cli.Auth;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Tests for <see cref="RegistryEndpoint"/>'s precedence and the saved default in <see cref="DefaultRegistryStore"/>.
/// Each test points <c>PROTOSTAR_CONFIG_DIR</c> at a temp folder and clears <c>PROTOSTAR_REGISTRY_URL</c>, restoring
/// both afterwards, so the real <c>~/.protostar</c> and the developer's environment are never read.
/// </summary>
[Collection("ProtostarConfigDir")]
public sealed class RegistryEndpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "protostar-registry-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousDir = Environment.GetEnvironmentVariable(ProtostarPaths.ConfigDirEnvVar);
    private readonly string? _previousUrl = Environment.GetEnvironmentVariable(AuthConstants.RegistryEnvVar);

    public RegistryEndpointTests()
    {
        Environment.SetEnvironmentVariable(ProtostarPaths.ConfigDirEnvVar, _dir);
        Environment.SetEnvironmentVariable(AuthConstants.RegistryEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ProtostarPaths.ConfigDirEnvVar, _previousDir);
        Environment.SetEnvironmentVariable(AuthConstants.RegistryEnvVar, _previousUrl);
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { /* the test wrote nothing */ }
    }

    [Fact]
    public void With_nothing_set_the_built_in_default_is_used()
    {
        Assert.Equal(new ResolvedRegistry(new Uri(AuthConstants.DefaultRegistryUrl), RegistrySource.Default), RegistryEndpoint.ResolveWithSource(null));
    }

    [Fact]
    public void The_flag_beats_the_environment_which_beats_the_saved_default()
    {
        Assert.True(new DefaultRegistryStore().Save(new Uri("https://saved.example")));
        Assert.Equal(new ResolvedRegistry(new Uri("https://saved.example"), RegistrySource.ConfigFile), RegistryEndpoint.ResolveWithSource(null));

        Environment.SetEnvironmentVariable(AuthConstants.RegistryEnvVar, "https://env.example");
        Assert.Equal(new ResolvedRegistry(new Uri("https://env.example"), RegistrySource.Environment), RegistryEndpoint.ResolveWithSource(null));

        Assert.Equal(new ResolvedRegistry(new Uri("https://flag.example"), RegistrySource.Flag), RegistryEndpoint.ResolveWithSource("https://flag.example"));
    }

    [Fact]
    public void The_saved_default_is_the_registry_authority_in_config_json_under_the_config_dir()
    {
        new DefaultRegistryStore().Save(new Uri("https://localhost:7643/some/path"));

        Assert.Equal(Path.Combine(_dir, "config.json"), ProtostarPaths.ConfigFile());
        Assert.Contains("\"registry\": \"https://localhost:7643\"", File.ReadAllText(ProtostarPaths.ConfigFile()));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[\"an array\"]")]
    [InlineData("{\"registry\": {\"url\": \"https://newer.example\"}}")]
    [InlineData("{\"registry\": \"\"}")]
    public void An_unreadable_config_file_counts_as_no_default(string contents)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ProtostarPaths.ConfigFile(), contents);

        Assert.Equal(RegistrySource.Default, RegistryEndpoint.ResolveWithSource(null).Source);
    }

    [Fact]
    public void Fields_a_newer_cli_wrote_are_ignored_on_read_and_kept_on_write()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ProtostarPaths.ConfigFile(), "{\"registry\": \"https://old.example\", \"future\": {\"a\": 1}}");

        Assert.Equal(new Uri("https://old.example"), RegistryEndpoint.Resolve(null));

        new DefaultRegistryStore().Save(new Uri("https://new.example"));

        Assert.Equal(new Uri("https://new.example"), RegistryEndpoint.Resolve(null));
        Assert.Contains("\"future\"", File.ReadAllText(ProtostarPaths.ConfigFile()));
    }

    [Fact]
    public void An_invalid_saved_url_is_an_error_that_names_the_config_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ProtostarPaths.ConfigFile(), "{\"registry\": \"ftp://nope\"}");

        var error = Assert.Throws<FormatException>(() => RegistryEndpoint.Resolve(null));
        Assert.Contains(ProtostarPaths.ConfigFile(), error.Message);
    }
}
