using Protostar.Cli.Auth;
using Xunit;

namespace Protostar.Cli.Acceptance;

/// <summary>
/// In-process tests for the file-based credential store. Each test redirects
/// <c>PROTOSTAR_CONFIG_DIR</c> at a throwaway temp folder, so nothing touches the real
/// <c>~/.protostar</c>. Tests within a class run sequentially in xUnit, so the process-global env
/// var is safe here.
/// </summary>
public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _previous;

    public CredentialStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "protostar-credtest-" + Guid.NewGuid().ToString("N"));
        _previous = Environment.GetEnvironmentVariable(ProtostarPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(ProtostarPaths.ConfigDirEnvVar, _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ProtostarPaths.ConfigDirEnvVar, _previous);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Save_then_load_round_trips_a_large_token()
    {
        var store = new TokenStore();
        var registry = new Uri("https://localhost:7443");
        var token = new StoredToken
        {
            Registry = "https://localhost:7443",
            // Deliberately larger than the Windows Credential Manager blob limit (2560 bytes) that
            // the old OS-keychain store choked on.
            AccessToken = new string('a', 4000),
            RefreshToken = "refresh-xyz",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            Login = "alice",
            Name = "Alice",
            Subject = "user-1",
        };

        Assert.True(store.Save(token));

        var loaded = store.Load(registry);
        Assert.NotNull(loaded);
        Assert.Equal(token.AccessToken, loaded!.AccessToken);
        Assert.Equal("alice", loaded.Login);
        Assert.True(File.Exists(ProtostarPaths.CredentialsFile()));
    }

    [Fact]
    public void Delete_removes_the_session()
    {
        var store = new TokenStore();
        var registry = new Uri("https://localhost:7443");
        store.Save(new StoredToken
        {
            Registry = "https://localhost:7443",
            AccessToken = "t",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        });

        store.Delete(registry);

        Assert.Null(store.Load(registry));
    }

    [Fact]
    public void Sessions_for_different_registries_are_independent()
    {
        var store = new TokenStore();
        store.Save(new StoredToken { Registry = "https://a.example", AccessToken = "ta", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1), Login = "a" });
        store.Save(new StoredToken { Registry = "https://b.example", AccessToken = "tb", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1), Login = "b" });

        Assert.Equal("a", store.Load(new Uri("https://a.example"))!.Login);
        Assert.Equal("b", store.Load(new Uri("https://b.example"))!.Login);
    }

    [Fact]
    public void Credentials_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows relies on the per-user profile ACL, not Unix file modes.

        new TokenStore().Save(new StoredToken
        {
            Registry = "https://localhost:7443",
            AccessToken = "t",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        });

        var mode = File.GetUnixFileMode(ProtostarPaths.CredentialsFile());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
