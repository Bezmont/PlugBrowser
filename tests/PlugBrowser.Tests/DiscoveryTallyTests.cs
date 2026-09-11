using PlugBrowser.Core.Discovery;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Probing;
using PlugBrowser.Core.Storage;
using Xunit;

namespace PlugBrowser.Tests;

/// <summary>
/// The new / updated / existing counts a scan reports, so the user can see that it is only re-checking
/// what it already knows and picking out what has actually changed.
/// </summary>
public class DiscoveryTallyTests
{
    private static PluginEntry Entry(string path, long size, DateTime written) => new()
    {
        Path = path,
        Format = PluginFormat.Vst3,
        FileSize = size,
        LastWriteUtc = written,
    };

    private static readonly DateTime Written = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void Classify_SortsByPresenceSizeAndWriteTime()
    {
        var stored = new Dictionary<string, PluginEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\x\Same.vst3"] = Entry(@"C:\x\Same.vst3", 100, Written),
            [@"C:\x\Resized.vst3"] = Entry(@"C:\x\Resized.vst3", 100, Written),
            [@"C:\x\Touched.vst3"] = Entry(@"C:\x\Touched.vst3", 100, Written),
        };

        Assert.Equal(FoundKind.Existing, ScanService.Classify(Entry(@"C:\x\Same.vst3", 100, Written), stored));
        Assert.Equal(FoundKind.Updated, ScanService.Classify(Entry(@"C:\x\Resized.vst3", 200, Written), stored));
        Assert.Equal(FoundKind.Updated,
            ScanService.Classify(Entry(@"C:\x\Touched.vst3", 100, Written.AddMinutes(1)), stored));
        Assert.Equal(FoundKind.New, ScanService.Classify(Entry(@"C:\x\Fresh.vst3", 100, Written), stored));
    }

    /// <summary>Paths on Windows are case-insensitive, so a differently cased path is the same plugin.</summary>
    [Fact]
    public void Classify_IgnoresPathCase()
    {
        var stored = new Dictionary<string, PluginEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\VST3\Thing.vst3"] = Entry(@"C:\VST3\Thing.vst3", 100, Written),
        };

        Assert.Equal(FoundKind.Existing,
            ScanService.Classify(Entry(@"c:\vst3\THING.vst3", 100, Written), stored));
    }

    [Fact]
    public void Tally_CountsAndDescribesEachKind()
    {
        var tally = default(DiscoveryTally)
            .Add(FoundKind.Existing).Add(FoundKind.Existing).Add(FoundKind.New).Add(FoundKind.Updated);

        Assert.Equal(new DiscoveryTally(New: 1, Updated: 1, Existing: 2), tally);
        Assert.Equal("2 existing · 1 new · 1 updated", tally.ToString());
    }

    /// <summary>
    /// End to end through a real discovery pass.
    /// </summary>
    /// <remarks>An empty <c>.vst3</c> folder is still accepted by discovery — as a bundle with no
    /// binary — which makes it a plugin the test can create without shipping a real one. Such an entry
    /// has no size or write time, so "updated" is arranged by storing a different size for its path.</remarks>
    [Fact]
    public async Task ScanAsync_ReportsNewUpdatedAndExistingPlugins()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));

        string plugins = tree.Dir("plugins");
        string existing = Directory.CreateDirectory(Path.Combine(plugins, "Existing.vst3")).FullName;
        string updated = Directory.CreateDirectory(Path.Combine(plugins, "Updated.vst3")).FullName;
        Directory.CreateDirectory(Path.Combine(plugins, "New.vst3"));

        store.Save([
            Entry(existing, 0, default) with { State = ProbeState.Failed },
            Entry(updated, 12345, Written) with { State = ProbeState.Failed },
        ]);

        var worker = new WorkerRunner(Path.Combine(tree.Root, "no-such-worker.exe"));
        var service = new ScanService(store, worker) { ImageDirectory = tree.Dir("images") };
        var summary = await service.ScanAsync([plugins]);

        Assert.Equal(3, summary.Discovered);
        Assert.Equal(new DiscoveryTally(New: 1, Updated: 1, Existing: 1), summary.Tally);
        Assert.Contains("1 existing · 1 new · 1 updated", summary.ToString());
    }

    /// <summary>A reload of selected plugins does no discovery, so it must not claim any were new.</summary>
    [Fact]
    public void Summary_OmitsTheTally_WhenNoDiscoveryRan()
    {
        var summary = new ScanSummary(2, 2, 2, 0, 0, TimeSpan.FromSeconds(9));

        Assert.DoesNotContain("existing", summary.ToString());
        Assert.DoesNotContain("new", summary.ToString());
    }
}
