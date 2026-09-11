using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;
using Xunit;

namespace PlugBrowser.Tests;

public class CatalogStoreTests
{
    private static PluginEntry SampleEntry(string path = @"C:\x\Thing.vst3") => new()
    {
        Path = path,
        Format = PluginFormat.Vst3,
        Architecture = PluginArchitecture.X64,
        BinaryPath = path,
        FileSize = 1234,
        LastWriteUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        FileVendor = "Acme",
        FileProduct = "Thing",
        FileVersion = "1.0.0",
        State = ProbeState.Probed,
        Classes =
        [
            new PluginClass
            {
                Index = 1,
                Cid = "84E8DE5F9255222296FAE4133C935A18",
                Name = "Thing",
                Vendor = "Acme",
                Category = "Instrument|Synth",
                ClassCategory = PluginClass.AudioModuleClass,
                Version = "1.0.0",
                SdkVersion = "VST 3.7.12",
                Kind = PluginKind.Instrument,
                Images =
                [
                    new PluginImage
                    {
                        Kind = ImageKind.Screenshot,
                        FilePath = @"C:\images\thing.png",
                        Width = 800,
                        Height = 600,
                        Method = CaptureMethod.PrintWindow,
                    },
                ],
            },
        ],
    };

    private static CatalogStore OpenTemp(TempTree tree, string name = "catalog.db") =>
        CatalogStore.Open(Path.Combine(tree.Root, name));

    [Fact]
    public void Save_ThenLoad_RoundTripsEverything()
    {
        using var tree = new TempTree();
        using var store = OpenTemp(tree);
        var original = SampleEntry();

        store.Save([original]);
        var loaded = Assert.Single(store.Load());

        Assert.Equal(original.Path, loaded.Path);
        Assert.Equal(original.Format, loaded.Format);
        Assert.Equal(original.Architecture, loaded.Architecture);
        Assert.Equal(original.FileSize, loaded.FileSize);
        Assert.Equal(original.LastWriteUtc, loaded.LastWriteUtc);
        Assert.Equal(original.FileVendor, loaded.FileVendor);
        Assert.Equal(original.State, loaded.State);

        var loadedClass = Assert.Single(loaded.Classes);
        Assert.Equal("84E8DE5F9255222296FAE4133C935A18", loadedClass.Cid);
        Assert.Equal(1, loadedClass.Index);
        Assert.Equal(PluginKind.Instrument, loadedClass.Kind);
        // Tags are derived on load rather than stored, so the category stays the single source of truth.
        Assert.Equal(["Instrument", "Synth"], loadedClass.Tags);

        var image = Assert.Single(loadedClass.Images);
        Assert.Equal(CaptureMethod.PrintWindow, image.Method);
        Assert.Equal(800, image.Width);
    }

    [Fact]
    public void IsUpToDate_TracksBothSizeAndTimestamp()
    {
        using var tree = new TempTree();
        using var store = OpenTemp(tree);
        var entry = SampleEntry();
        store.Save([entry]);

        Assert.True(store.IsUpToDate(entry.Path, entry.FileSize, entry.LastWriteUtc));
        Assert.False(store.IsUpToDate(entry.Path, entry.FileSize + 1, entry.LastWriteUtc));
        Assert.False(store.IsUpToDate(entry.Path, entry.FileSize, entry.LastWriteUtc.AddSeconds(1)));
        Assert.False(store.IsUpToDate(@"C:\x\Other.vst3", entry.FileSize, entry.LastWriteUtc));
    }

    /// <summary>A plugin that loses a class in a vendor update must not keep the old one around.</summary>
    [Fact]
    public void Save_ReplacesClasses_RatherThanAccumulatingThem()
    {
        using var tree = new TempTree();
        using var store = OpenTemp(tree);
        var entry = SampleEntry();

        store.Save([entry]);
        store.Save([entry with { Classes = [entry.Classes[0] with { Index = 0, Name = "Renamed" }] }]);

        var loaded = Assert.Single(store.Load());
        var only = Assert.Single(loaded.Classes);
        Assert.Equal("Renamed", only.Name);
    }

    /// <summary>Caching failures is what keeps a rescan from paying the timeout for a broken plugin
    /// every single time.</summary>
    [Fact]
    public void Save_PersistsFailureStates()
    {
        using var tree = new TempTree();
        using var store = OpenTemp(tree);

        store.Save([SampleEntry() with
        {
            State = ProbeState.TimedOut,
            StateDetail = "Worker exceeded 15s.",
            Classes = [],
        }]);

        var loaded = Assert.Single(store.Load());
        Assert.Equal(ProbeState.TimedOut, loaded.State);
        Assert.Equal("Worker exceeded 15s.", loaded.StateDetail);
    }

    [Fact]
    public void FavoritesAndBlacklist_RoundTrip()
    {
        using var tree = new TempTree();
        using var store = OpenTemp(tree);
        const string path = @"C:\x\Thing.vst3";

        Assert.Empty(store.GetFavorites());
        store.SetFavorite(path, true);
        Assert.Contains(path, store.GetFavorites());
        store.SetFavorite(path, false);
        Assert.Empty(store.GetFavorites());

        store.Blacklist(path, "crashes on load");
        Assert.Contains(path, store.GetBlacklist());
        store.Unblacklist(path);
        Assert.Empty(store.GetBlacklist());
    }

    /// <summary>Setting a favourite twice must not throw — the UI toggles freely.</summary>
    [Fact]
    public void SetFavorite_IsIdempotent()
    {
        using var tree = new TempTree();
        using var store = OpenTemp(tree);

        store.SetFavorite(@"C:\x\Thing.vst3", true);
        store.SetFavorite(@"C:\x\Thing.vst3", true);

        Assert.Single(store.GetFavorites());
    }

    [Fact]
    public void PruneMissing_DropsEntriesWhoseFilesAreGone()
    {
        using var tree = new TempTree();
        using var store = OpenTemp(tree);
        string present = tree.File("Present.vst3", "x");

        store.Save([SampleEntry(present), SampleEntry(@"C:\nowhere\Gone.vst3")]);

        Assert.Equal(1, store.PruneMissing());
        Assert.Equal(present, Assert.Single(store.Load()).Path);
    }

    /// <summary>
    /// The reason a schema bump exists: when probing logic changes, cached rows — especially cached
    /// failures — may now be wrong, so they are discarded wholesale. The user's own choices survive.
    /// </summary>
    [Fact]
    public void ReopeningWithANewerSchema_DiscardsCacheButKeepsUserData()
    {
        using var tree = new TempTree();
        string dbPath = Path.Combine(tree.Root, "catalog.db");
        const string path = @"C:\x\Thing.vst3";

        using (var store = CatalogStore.Open(dbPath))
        {
            store.Save([SampleEntry(path)]);
            store.SetFavorite(path, true);
            store.Blacklist(path, "flaky");
        }

        SimulateSchemaBump(dbPath);

        using (var store = CatalogStore.Open(dbPath))
        {
            Assert.Empty(store.Load());
            Assert.Contains(path, store.GetFavorites());
            Assert.Contains(path, store.GetBlacklist());
        }
    }

    /// <summary>Rewrites the stored schema version to an older value, so the next open sees a mismatch.
    /// Cheaper and clearer than actually recompiling with a different constant.</summary>
    private static void SimulateSchemaBump(string dbPath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE meta SET value = $v WHERE key = 'schema_version';";
        command.Parameters.AddWithValue("$v", (CatalogStore.SchemaVersion - 1).ToString());
        command.ExecuteNonQuery();
    }
}
