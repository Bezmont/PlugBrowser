using PlugBrowser.App.ViewModels;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Probing;
using PlugBrowser.Core.Storage;
using Xunit;

namespace PlugBrowser.Tests;

/// <summary>
/// Which bundles a scan's probing phase passes over. Probing costs a process launch and up to the full
/// timeout per bundle, so these rules decide whether a routine rescan takes seconds or many minutes.
/// </summary>
public class ScanSkipTests
{
    private static PluginEntry Entry(ProbeState state) => new()
    {
        Path = @"C:\x\Thing.vst3",
        Format = PluginFormat.Vst3,
        Architecture = PluginArchitecture.X64,
        State = state,
    };

    private static bool UpToDate(PluginEntry _) => true;
    private static bool Changed(PluginEntry _) => false;

    [Fact]
    public void UnchangedLoadedPlugin_IsSkipped()
    {
        var reason = ScanService.ShouldSkip(Entry(ProbeState.Probed), skipUnchanged: true, skipFailed: false, UpToDate);

        Assert.Equal(SkipReason.Unchanged, reason);
    }

    [Fact]
    public void UnchangedLoadedPlugin_IsReloaded_WhenARescanIsForced()
    {
        var reason = ScanService.ShouldSkip(Entry(ProbeState.Probed), skipUnchanged: false, skipFailed: true, UpToDate);

        Assert.Equal(SkipReason.None, reason);
    }

    [Fact]
    public void LoadedPluginWhoseFileChanged_IsReloaded()
    {
        var reason = ScanService.ShouldSkip(Entry(ProbeState.Probed), skipUnchanged: true, skipFailed: true, Changed);

        Assert.Equal(SkipReason.None, reason);
    }

    /// <summary>The default: failures are retried, because some are intermittent.</summary>
    [Theory]
    [InlineData(ProbeState.Failed)]
    [InlineData(ProbeState.TimedOut)]
    public void FailedPlugin_IsRetried_ByDefault(ProbeState state)
    {
        var reason = ScanService.ShouldSkip(Entry(state), skipUnchanged: true, skipFailed: false, UpToDate);

        Assert.Equal(SkipReason.None, reason);
    }

    [Theory]
    [InlineData(ProbeState.Failed)]
    [InlineData(ProbeState.TimedOut)]
    public void FailedPlugin_IsSkipped_WhenSkipFailedIsOn(ProbeState state)
    {
        var reason = ScanService.ShouldSkip(Entry(state), skipUnchanged: true, skipFailed: true, UpToDate);

        Assert.Equal(SkipReason.PreviouslyFailed, reason);
    }

    /// <summary>Skip failed is independent of the unchanged rule: forcing a full rescan still leaves
    /// known failures alone if the user asked for that.</summary>
    [Fact]
    public void FailedPlugin_IsStillSkipped_WhenARescanIsForced()
    {
        var reason = ScanService.ShouldSkip(Entry(ProbeState.Failed), skipUnchanged: false, skipFailed: true, UpToDate);

        Assert.Equal(SkipReason.PreviouslyFailed, reason);
    }

    /// <summary>A new plugin, or a failed one that has since been updated, arrives as Discovered — the
    /// stored failure is only carried forward while the file is unchanged — and is always loaded.</summary>
    [Fact]
    public void NewOrUpdatedPlugin_IsAlwaysLoaded()
    {
        var reason = ScanService.ShouldSkip(Entry(ProbeState.Discovered), skipUnchanged: true, skipFailed: true, UpToDate);

        Assert.Equal(SkipReason.None, reason);
    }

    [Fact]
    public void Summary_MentionsSkippedFailures_OnlyWhenThereAreAny()
    {
        var none = new ScanSummary(10, 5, 5, 1, 4, TimeSpan.FromSeconds(3));
        var some = none with { SkippedFailed = 2 };

        Assert.DoesNotContain("failed skipped", none.ToString());
        Assert.Contains("2 failed skipped", some.ToString());
    }

    /// <summary>Unlike "Re-scan unchanged plugins", this is a standing preference and must survive the
    /// dialog being closed and the app restarted.</summary>
    [Fact]
    public void SkipFailedSetting_SurvivesARestart()
    {
        using var tree = new TempTree();
        string dbPath = Path.Combine(tree.Root, "catalog.db");

        using (var store = CatalogStore.Open(dbPath))
        {
            var options = new OptionsViewModel(store);
            Assert.False(options.SkipFailed);
            options.SkipFailed = true;
        }

        using (var store = CatalogStore.Open(dbPath))
            Assert.True(new OptionsViewModel(store).SkipFailed);
    }
}
