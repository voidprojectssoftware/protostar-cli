using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Black-box tests for the <see cref="IHookCapability"/> contract as implemented by
/// <see cref="ClaudeCodeHarness"/>. Asserts only contract-observable behavior: the returned
/// <see cref="HookChange"/>, the echoed settings path, file existence / text content / byte
/// stability on disk, and whether a call throws. No assertions on the internal hook JSON schema.
/// </summary>
public sealed class ClaudeCodeHooksTests : IDisposable
{
    // A separator-free absolute path: the settings file is JSON, where backslashes would serialize as
    // "\\", so a Windows-style path would not survive a raw substring check of the written file. The
    // SUT treats this purely as opaque text, so the separator style is irrelevant to what is tested.
    private const string ExePath = "/opt/protostar/protostar";

    private readonly string _tempDir;
    private readonly string _settingsPath;

    public ClaudeCodeHooksTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "protostar-hookstest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best effort: leave temp residue rather than fail the test on cleanup.
        }
    }

    private HarnessLocation Location() => new HarnessLocation(_tempDir, _settingsPath);

    private static IHookCapability NewSut() =>
        new ClaudeCodeHarness(new ClaudeCodeSkillParser(), new ClaudeCodeSkillMapper());

    // ---- Happy path -------------------------------------------------------

    [Fact]
    public void Install_into_a_location_with_no_settings_file_reports_Added()
    {
        IHookCapability sut = NewSut();

        HookChangeSet result = sut.InstallHooks(Location(), ExePath, dryRun: false);

        Assert.Equal(HookChange.Added, result.Change);
        Assert.Equal(_settingsPath, result.SettingsPath);
        Assert.True(File.Exists(_settingsPath));
    }

    [Fact]
    public void Install_writes_a_file_that_references_the_exe_path()
    {
        IHookCapability sut = NewSut();

        sut.InstallHooks(Location(), ExePath, dryRun: false);

        string text = File.ReadAllText(_settingsPath);
        Assert.Contains(ExePath, text);
    }

    [Fact]
    public void Install_when_a_settings_file_already_exists_reports_Updated()
    {
        // ASSUMES: writing valid JSON with no protostar hooks counts as a pre-existing file,
        // so the install path returns Updated (file existed) rather than Added (file created).
        File.WriteAllText(_settingsPath, "{\"theme\":\"dark\"}");
        IHookCapability sut = NewSut();

        HookChangeSet result = sut.InstallHooks(Location(), ExePath, dryRun: false);

        Assert.Equal(HookChange.Updated, result.Change);
        Assert.Equal(_settingsPath, result.SettingsPath);
    }

    [Fact]
    public void Remove_after_install_reports_Removed()
    {
        IHookCapability sut = NewSut();
        sut.InstallHooks(Location(), ExePath, dryRun: false);

        HookChangeSet result = sut.RemoveHooks(Location(), dryRun: false);

        Assert.Equal(HookChange.Removed, result.Change);
        Assert.Equal(_settingsPath, result.SettingsPath);
    }

    // ---- Idempotency ------------------------------------------------------

    [Fact]
    public void Install_twice_reports_Unchanged_on_the_second_call()
    {
        IHookCapability sut = NewSut();
        sut.InstallHooks(Location(), ExePath, dryRun: false);

        HookChangeSet second = sut.InstallHooks(Location(), ExePath, dryRun: false);

        Assert.Equal(HookChange.Unchanged, second.Change);
    }

    [Fact]
    public void Install_twice_does_not_change_the_bytes_on_the_second_call()
    {
        IHookCapability sut = NewSut();
        sut.InstallHooks(Location(), ExePath, dryRun: false);
        byte[] afterFirst = File.ReadAllBytes(_settingsPath);

        sut.InstallHooks(Location(), ExePath, dryRun: false);
        byte[] afterSecond = File.ReadAllBytes(_settingsPath);

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void Remove_twice_reports_Unchanged_on_the_second_call()
    {
        IHookCapability sut = NewSut();
        sut.InstallHooks(Location(), ExePath, dryRun: false);
        sut.RemoveHooks(Location(), dryRun: false);

        HookChangeSet second = sut.RemoveHooks(Location(), dryRun: false);

        Assert.Equal(HookChange.Unchanged, second.Change);
    }

    // ---- Negative / edge --------------------------------------------------

    [Fact]
    public void Remove_when_no_settings_file_exists_reports_Unchanged()
    {
        IHookCapability sut = NewSut();

        HookChangeSet result = sut.RemoveHooks(Location(), dryRun: false);

        Assert.Equal(HookChange.Unchanged, result.Change);
        Assert.Equal(_settingsPath, result.SettingsPath);
    }

    [Fact]
    public void Remove_when_no_settings_file_exists_does_not_create_the_file()
    {
        // ASSUMES: a no-op remove never materialises a settings file that was absent.
        IHookCapability sut = NewSut();

        sut.RemoveHooks(Location(), dryRun: false);

        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public void Remove_when_the_file_has_no_protostar_hooks_reports_Unchanged()
    {
        File.WriteAllText(_settingsPath, "{\"theme\":\"dark\"}");
        IHookCapability sut = NewSut();

        HookChangeSet result = sut.RemoveHooks(Location(), dryRun: false);

        Assert.Equal(HookChange.Unchanged, result.Change);
    }

    [Fact]
    public void Remove_when_the_file_has_no_protostar_hooks_leaves_bytes_unchanged()
    {
        File.WriteAllText(_settingsPath, "{\"theme\":\"dark\"}");
        byte[] before = File.ReadAllBytes(_settingsPath);
        IHookCapability sut = NewSut();

        sut.RemoveHooks(Location(), dryRun: false);
        byte[] after = File.ReadAllBytes(_settingsPath);

        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(true)]
    public void Install_with_dryRun_does_not_write_a_file_to_disk(bool dryRun)
    {
        IHookCapability sut = NewSut();

        HookChangeSet result = sut.InstallHooks(Location(), ExePath, dryRun);

        // dryRun computes the change but must not persist it.
        Assert.False(File.Exists(_settingsPath));
        Assert.Equal(_settingsPath, result.SettingsPath);
        // ASSUMES: dryRun still REPORTS the would-be change (Added) rather than Unchanged,
        // per the remark "the change is computed and returned but not persisted".
        Assert.Equal(HookChange.Added, result.Change);
    }

    [Theory]
    [InlineData(true)]
    public void Remove_with_dryRun_does_not_modify_the_file_on_disk(bool dryRun)
    {
        IHookCapability sut = NewSut();
        sut.InstallHooks(Location(), ExePath, dryRun: false);
        byte[] before = File.ReadAllBytes(_settingsPath);

        HookChangeSet result = sut.RemoveHooks(Location(), dryRun);
        byte[] after = File.ReadAllBytes(_settingsPath);

        Assert.Equal(before, after);
        // ASSUMES: dryRun still REPORTS the would-be change (Removed) rather than Unchanged.
        Assert.Equal(HookChange.Removed, result.Change);
    }

    [Fact]
    public void Install_preserves_unrelated_keys_already_in_the_settings_file()
    {
        File.WriteAllText(_settingsPath, "{\"theme\":\"dark\"}");
        IHookCapability sut = NewSut();

        sut.InstallHooks(Location(), ExePath, dryRun: false);

        JsonNode? root = JsonNode.Parse(File.ReadAllText(_settingsPath));
        Assert.NotNull(root);
        Assert.Equal("dark", (string?)root!["theme"]);
    }

    [Fact]
    public void Remove_preserves_unrelated_keys_in_the_settings_file()
    {
        File.WriteAllText(_settingsPath, "{\"theme\":\"dark\"}");
        IHookCapability sut = NewSut();
        sut.InstallHooks(Location(), ExePath, dryRun: false);

        sut.RemoveHooks(Location(), dryRun: false);

        JsonNode? root = JsonNode.Parse(File.ReadAllText(_settingsPath));
        Assert.NotNull(root);
        Assert.Equal("dark", (string?)root!["theme"]);
    }

    [Fact]
    public void Install_against_an_invalid_json_file_throws_and_leaves_bytes_unchanged()
    {
        // The remark: an existing settings file that is not valid JSON is left untouched and
        // the call throws rather than risk overwriting unrecognised content.
        byte[] garbage = Encoding.UTF8.GetBytes("this is not json {");
        File.WriteAllBytes(_settingsPath, garbage);
        IHookCapability sut = NewSut();

        // ASSUMES: any exception type is acceptable; the contract specifies "throws" but not which type.
        Assert.ThrowsAny<Exception>(() => sut.InstallHooks(Location(), ExePath, dryRun: false));
        Assert.Equal(garbage, File.ReadAllBytes(_settingsPath));
    }

    [Fact]
    public void Remove_against_an_invalid_json_file_throws_and_leaves_bytes_unchanged()
    {
        // ASSUMES: the "invalid JSON is left untouched and throws" remark applies symmetrically
        // to RemoveHooks, not only InstallHooks (the remark is stated for both methods).
        byte[] garbage = Encoding.UTF8.GetBytes("this is not json {");
        File.WriteAllBytes(_settingsPath, garbage);
        IHookCapability sut = NewSut();

        Assert.ThrowsAny<Exception>(() => sut.RemoveHooks(Location(), dryRun: false));
        Assert.Equal(garbage, File.ReadAllBytes(_settingsPath));
    }
}
