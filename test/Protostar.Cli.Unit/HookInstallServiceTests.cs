using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Protostar.Cli.Harness;
using Protostar.Cli.Hooks;
using Xunit;

namespace Protostar.Cli.Unit;

/// <summary>
/// Black-box contract tests for <see cref="IHookInstallService"/> as implemented by
/// <c>HookInstallService</c>. Every expectation is derived from the documented contract of the service,
/// its options/result types, and the collaborator interfaces it orchestrates. The catalog and harnesses
/// are faked in memory; the implementation file is never read.
/// </summary>
/// <remarks>
/// The single real disk touch is the install-time resolution of the protostar binary: the service
/// accepts <see cref="HookInstallOptions.ExePathOverride"/> and the path must exist on disk or the run
/// fails with <see cref="HookRunFailure.MissingExecutable"/>. Each test that needs a "real" exe uses the
/// per-instance temp file created in the constructor; tests that exercise MissingExecutable point at a
/// path under the temp dir that is never created.
/// </remarks>
public sealed class HookInstallServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _realExe;
    private readonly string _missingExe;

    public HookInstallServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "protostar-hookinstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _realExe = Path.Combine(_tempDir, "protostar.exe");
        File.WriteAllText(_realExe, "");
        _missingExe = Path.Combine(_tempDir, "does-not-exist.exe");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup; do not fail a test on temp residue.
        }
    }

    // ---- Fakes ------------------------------------------------------------

    /// <summary>A harness with no capabilities; can be present or absent via TryLocate.</summary>
    private sealed class FakeHarness : IHarness
    {
        private readonly bool _located;
        private readonly HarnessLocation _location;

        public FakeHarness(string id, bool located, HarnessLocation? location = null)
        {
            Id = id;
            DisplayName = id;
            _located = located;
            _location = location ?? new HarnessLocation($"/cfg/{id}", $"/cfg/{id}/settings.json");
        }

        public string Id { get; }
        public string DisplayName { get; }

        public bool TryLocate(string? rootOverride, out HarnessLocation location)
        {
            location = _location;
            return _located;
        }
    }

    /// <summary>A harness that supports hooks; install/remove return a preset change set or throw.</summary>
    private sealed class FakeHookHarness : IHarness, IHookCapability
    {
        private readonly bool _located;
        private readonly HarnessLocation _location;
        private readonly HookChangeSet? _installResult;
        private readonly HookChangeSet? _removeResult;
        private readonly bool _throwOnInstall;
        private readonly bool _throwOnRemove;

        public int InstallCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public string? LastExePath { get; private set; }
        public bool? LastDryRun { get; private set; }

        public FakeHookHarness(
            string id,
            bool located = true,
            HarnessLocation? location = null,
            HookChangeSet? installResult = null,
            HookChangeSet? removeResult = null,
            bool throwOnInstall = false,
            bool throwOnRemove = false)
        {
            Id = id;
            DisplayName = id;
            _located = located;
            _location = location ?? new HarnessLocation($"/cfg/{id}", $"/cfg/{id}/settings.json");
            _installResult = installResult ?? new HookChangeSet(HookChange.Added, $"/cfg/{id}/settings.json", "added");
            _removeResult = removeResult ?? new HookChangeSet(HookChange.Removed, $"/cfg/{id}/settings.json", "removed");
            _throwOnInstall = throwOnInstall;
            _throwOnRemove = throwOnRemove;
        }

        public string Id { get; }
        public string DisplayName { get; }

        public bool TryLocate(string? rootOverride, out HarnessLocation location)
        {
            location = _location;
            return _located;
        }

        public HookChangeSet InstallHooks(HarnessLocation location, string exePath, bool dryRun)
        {
            InstallCalls++;
            LastExePath = exePath;
            LastDryRun = dryRun;
            if (_throwOnInstall)
            {
                throw new InvalidOperationException("install boom");
            }

            return _installResult!;
        }

        public HookChangeSet RemoveHooks(HarnessLocation location, bool dryRun)
        {
            RemoveCalls++;
            LastDryRun = dryRun;
            if (_throwOnRemove)
            {
                throw new InvalidOperationException("remove boom");
            }

            return _removeResult!;
        }
    }

    private sealed class FakeCatalog : IHarnessCatalog
    {
        public FakeCatalog(params IHarness[] harnesses) => All = harnesses;

        public IReadOnlyList<IHarness> All { get; }

        public IHarness? ById(string id) =>
            All.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase));

        public string KnownIds => string.Join(", ", All.Select(h => h.Id));
    }

    private static IHookInstallService NewSut(params IHarness[] harnesses) =>
        new HookInstallService(new FakeCatalog(harnesses));

    private HookInstallOptions InstallOpts(IReadOnlyList<string>? ids = null, bool dryRun = false, string? exe = null) =>
        new HookInstallOptions { HarnessIds = ids, DryRun = dryRun, ExePathOverride = exe ?? _realExe };

    private static HookInstallOptions RemoveOpts(IReadOnlyList<string>? ids = null, bool dryRun = false) =>
        new HookInstallOptions { HarnessIds = ids, DryRun = dryRun };

    // ---- Install: nothing to do ------------------------------------------

    [Fact]
    public void Install_with_an_empty_catalog_does_nothing()
    {
        IHookInstallService sut = NewSut();

        HookRunResult result = sut.Install(InstallOpts());

        Assert.Equal(HookRunFailure.None, result.Failure);
        Assert.Empty(result.Results);
        Assert.Null(result.OffendingHarnessId);
    }

    [Fact]
    public void Install_when_no_harness_is_detected_does_nothing()
    {
        // A registered harness that TryLocate reports as absent is not a target.
        IHookInstallService sut = NewSut(new FakeHookHarness("claude-code", located: false));

        HookRunResult result = sut.Install(InstallOpts());

        Assert.Equal(HookRunFailure.None, result.Failure);
        Assert.Empty(result.Results);
    }

    // ---- Install: pre-flight failures (nothing touched) -------------------

    [Fact]
    public void Install_with_an_unknown_explicit_id_fails_with_UnknownHarness()
    {
        IHookInstallService sut = NewSut(new FakeHookHarness("claude-code"));

        HookRunResult result = sut.Install(InstallOpts(ids: new[] { "nope" }));

        Assert.Equal(HookRunFailure.UnknownHarness, result.Failure);
        Assert.Equal("nope", result.OffendingHarnessId);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void Install_with_an_explicit_id_lacking_hook_support_fails_with_Unsupported()
    {
        // The harness exists but does not implement IHookCapability.
        IHookInstallService sut = NewSut(new FakeHarness("plain", located: true));

        HookRunResult result = sut.Install(InstallOpts(ids: new[] { "plain" }));

        Assert.Equal(HookRunFailure.Unsupported, result.Failure);
        Assert.Equal("plain", result.OffendingHarnessId);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void Install_when_the_executable_cannot_be_located_fails_with_MissingExecutable()
    {
        IHookInstallService sut = NewSut(new FakeHookHarness("claude-code"));

        HookRunResult result = sut.Install(InstallOpts(ids: new[] { "claude-code" }, exe: _missingExe));

        Assert.Equal(HookRunFailure.MissingExecutable, result.Failure);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void Install_when_the_executable_cannot_be_located_does_not_touch_the_harness()
    {
        var harness = new FakeHookHarness("claude-code");
        IHookInstallService sut = NewSut(harness);

        sut.Install(InstallOpts(ids: new[] { "claude-code" }, exe: _missingExe));

        // MissingExecutable is a pre-flight stop: no harness should have been invoked.
        Assert.Equal(0, harness.InstallCalls);
    }

    // ---- Install: happy path ---------------------------------------------

    [Fact]
    public void Install_against_one_detected_target_returns_one_successful_result()
    {
        var preset = new HookChangeSet(HookChange.Added, "/cfg/claude-code/settings.json", "added hooks");
        var harness = new FakeHookHarness("claude-code", installResult: preset);
        IHookInstallService sut = NewSut(harness);

        HookRunResult result = sut.Install(InstallOpts());

        Assert.Equal(HookRunFailure.None, result.Failure);
        HookApplyResult applied = Assert.Single(result.Results);
        Assert.False(applied.Failed);
        Assert.Null(applied.Error);
        Assert.Same(preset, applied.Change);
        Assert.Same(harness, applied.Harness);
    }

    [Fact]
    public void Install_via_explicit_id_acts_on_that_harness()
    {
        var harness = new FakeHookHarness("claude-code");
        IHookInstallService sut = NewSut(harness, new FakeHookHarness("other"));

        HookRunResult result = sut.Install(InstallOpts(ids: new[] { "claude-code" }));

        Assert.Equal(HookRunFailure.None, result.Failure);
        HookApplyResult applied = Assert.Single(result.Results);
        Assert.Same(harness, applied.Harness);
        Assert.Equal(1, harness.InstallCalls);
    }

    [Fact]
    public void Install_forwards_the_resolved_exe_path_to_the_capability()
    {
        var harness = new FakeHookHarness("claude-code");
        IHookInstallService sut = NewSut(harness);

        sut.Install(InstallOpts(ids: new[] { "claude-code" }, exe: _realExe));

        // ASSUMES: the resolved binary path (here the override) is the exePath handed to InstallHooks.
        Assert.Equal(_realExe, harness.LastExePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Install_forwards_the_dry_run_flag_to_the_capability(bool dryRun)
    {
        var harness = new FakeHookHarness("claude-code");
        IHookInstallService sut = NewSut(harness);

        sut.Install(InstallOpts(ids: new[] { "claude-code" }, dryRun: dryRun));

        // ASSUMES: DryRun on the options is forwarded verbatim to the capability dryRun parameter.
        Assert.Equal(dryRun, harness.LastDryRun);
    }

    [Fact]
    public void Install_acts_on_every_detected_target()
    {
        var a = new FakeHookHarness("a");
        var b = new FakeHookHarness("b");
        IHookInstallService sut = NewSut(a, b);

        HookRunResult result = sut.Install(InstallOpts());

        Assert.Equal(HookRunFailure.None, result.Failure);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(1, a.InstallCalls);
        Assert.Equal(1, b.InstallCalls);
    }

    // ---- Install: one target throws --------------------------------------

    [Fact]
    public void Install_when_a_capability_throws_records_an_error_for_that_target()
    {
        var boom = new FakeHookHarness("boom", throwOnInstall: true);
        IHookInstallService sut = NewSut(boom);

        HookRunResult result = sut.Install(InstallOpts());

        // A per-target failure is not a pre-flight Failure: it surfaces in Results.
        Assert.Equal(HookRunFailure.None, result.Failure);
        HookApplyResult applied = Assert.Single(result.Results);
        Assert.True(applied.Failed);
        Assert.NotNull(applied.Error);
        Assert.Null(applied.Change);
    }

    [Fact]
    public void Install_when_one_target_throws_still_processes_the_others()
    {
        // ASSUMES: a throwing target is isolated; remaining detected targets are still applied,
        // so one bad harness does not abort the whole run.
        var boom = new FakeHookHarness("boom", throwOnInstall: true);
        var ok = new FakeHookHarness("ok");
        IHookInstallService sut = NewSut(boom, ok);

        HookRunResult result = sut.Install(InstallOpts());

        Assert.Equal(HookRunFailure.None, result.Failure);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(1, ok.InstallCalls);
        Assert.Contains(result.Results, r => r.Failed);
        Assert.Contains(result.Results, r => !r.Failed);
    }

    // ---- Install: selector narrowing (detection case) ---------------------

    [Fact]
    public void Install_selector_returning_a_subset_acts_only_on_that_subset()
    {
        var a = new FakeHookHarness("a");
        var b = new FakeHookHarness("b");
        IHookInstallService sut = NewSut(a, b);

        // Narrow to only the harness with id "a".
        HarnessSelector select = detected =>
            detected.Where(t => t.Harness.Id == "a").ToList();

        HookRunResult result = sut.Install(InstallOpts(), select);

        Assert.Equal(HookRunFailure.None, result.Failure);
        HookApplyResult applied = Assert.Single(result.Results);
        Assert.Equal("a", applied.Harness.Id);
        Assert.Equal(1, a.InstallCalls);
        Assert.Equal(0, b.InstallCalls);
    }

    [Fact]
    public void Install_selector_returning_an_empty_list_does_nothing()
    {
        var a = new FakeHookHarness("a");
        IHookInstallService sut = NewSut(a);

        HarnessSelector selectNone = _ => new List<HarnessTarget>();

        HookRunResult result = sut.Install(InstallOpts(), selectNone);

        Assert.Equal(HookRunFailure.None, result.Failure);
        Assert.Empty(result.Results);
        Assert.Equal(0, a.InstallCalls);
    }

    [Fact]
    public void Install_with_explicit_ids_skips_the_selector()
    {
        // FINDING / ASSUMES: HookInstallOptions.HarnessIds documents that explicit ids "Skip detection
        // and selection", while Install summary says it narrows "via select when given". Taking the
        // options doc as authoritative for the explicit-id path: a selector passed alongside explicit
        // ids must NOT be consulted. This test encodes that reading; if the selector IS meant to apply
        // even with explicit ids, this expectation is wrong (see report).
        var a = new FakeHookHarness("a");
        IHookInstallService sut = NewSut(a);
        bool selectorInvoked = false;
        HarnessSelector select = detected =>
        {
            selectorInvoked = true;
            return new List<HarnessTarget>();
        };

        HookRunResult result = sut.Install(InstallOpts(ids: new[] { "a" }), select);

        Assert.False(selectorInvoked);
        Assert.Single(result.Results);
        Assert.Equal(1, a.InstallCalls);
    }

    // ---- Uninstall: mirrors install, no executable needed -----------------

    [Fact]
    public void Uninstall_with_an_empty_catalog_does_nothing()
    {
        IHookInstallService sut = NewSut();

        HookRunResult result = sut.Uninstall(RemoveOpts());

        Assert.Equal(HookRunFailure.None, result.Failure);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void Uninstall_with_an_unknown_explicit_id_fails_with_UnknownHarness()
    {
        IHookInstallService sut = NewSut(new FakeHookHarness("claude-code"));

        HookRunResult result = sut.Uninstall(RemoveOpts(ids: new[] { "nope" }));

        Assert.Equal(HookRunFailure.UnknownHarness, result.Failure);
        Assert.Equal("nope", result.OffendingHarnessId);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void Uninstall_with_an_explicit_id_lacking_hook_support_fails_with_Unsupported()
    {
        IHookInstallService sut = NewSut(new FakeHarness("plain", located: true));

        HookRunResult result = sut.Uninstall(RemoveOpts(ids: new[] { "plain" }));

        Assert.Equal(HookRunFailure.Unsupported, result.Failure);
        Assert.Equal("plain", result.OffendingHarnessId);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void Uninstall_does_not_require_an_executable()
    {
        // RemoveOpts sets no ExePathOverride; uninstall must not fail with MissingExecutable.
        var harness = new FakeHookHarness("claude-code");
        IHookInstallService sut = NewSut(harness);

        HookRunResult result = sut.Uninstall(RemoveOpts(ids: new[] { "claude-code" }));

        Assert.NotEqual(HookRunFailure.MissingExecutable, result.Failure);
        Assert.Equal(HookRunFailure.None, result.Failure);
    }

    [Fact]
    public void Uninstall_against_one_target_returns_the_capabilitys_change_set()
    {
        var preset = new HookChangeSet(HookChange.Removed, "/cfg/claude-code/settings.json", "removed hooks");
        var harness = new FakeHookHarness("claude-code", removeResult: preset);
        IHookInstallService sut = NewSut(harness);

        HookRunResult result = sut.Uninstall(RemoveOpts());

        HookApplyResult applied = Assert.Single(result.Results);
        Assert.False(applied.Failed);
        Assert.Same(preset, applied.Change);
        Assert.Equal(1, harness.RemoveCalls);
    }

    [Fact]
    public void Uninstall_when_a_capability_throws_records_an_error_for_that_target()
    {
        var boom = new FakeHookHarness("boom", throwOnRemove: true);
        IHookInstallService sut = NewSut(boom);

        HookRunResult result = sut.Uninstall(RemoveOpts());

        Assert.Equal(HookRunFailure.None, result.Failure);
        HookApplyResult applied = Assert.Single(result.Results);
        Assert.True(applied.Failed);
        Assert.NotNull(applied.Error);
        Assert.Null(applied.Change);
    }

    [Fact]
    public void Uninstall_when_one_target_throws_still_processes_the_others()
    {
        var boom = new FakeHookHarness("boom", throwOnRemove: true);
        var ok = new FakeHookHarness("ok");
        IHookInstallService sut = NewSut(boom, ok);

        HookRunResult result = sut.Uninstall(RemoveOpts());

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(1, ok.RemoveCalls);
        Assert.Contains(result.Results, r => r.Failed);
        Assert.Contains(result.Results, r => !r.Failed);
    }

    [Fact]
    public void Uninstall_selector_returning_a_subset_acts_only_on_that_subset()
    {
        var a = new FakeHookHarness("a");
        var b = new FakeHookHarness("b");
        IHookInstallService sut = NewSut(a, b);

        HarnessSelector select = detected => detected.Where(t => t.Harness.Id == "a").ToList();

        HookRunResult result = sut.Uninstall(RemoveOpts(), select);

        HookApplyResult applied = Assert.Single(result.Results);
        Assert.Equal("a", applied.Harness.Id);
        Assert.Equal(0, b.RemoveCalls);
    }
}
