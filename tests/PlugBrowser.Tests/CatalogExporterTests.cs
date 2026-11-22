using System.Text.Json;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;
using Xunit;

namespace PlugBrowser.Tests;

public class CatalogExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-export-" + Guid.NewGuid().ToString("N"));

    public CatalogExporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTime Now = new(2026, 11, 19, 20, 15, 0, DateTimeKind.Utc);

    private PluginEntry Synth(string? imagePath = null) => new()
    {
        Path = @"C:\VST3\Synth.vst3",
        Format = PluginFormat.Vst3,
        Architecture = PluginArchitecture.X64,
        FileVersion = "2.1.0",
        State = ProbeState.Probed,
        Classes =
        [
            new PluginClass
            {
                Index = 0,
                Cid = "84E8DE5F9255222296FAE4133C935A18",
                Name = "Synth",
                Vendor = "Acme",
                Category = "Instrument|Synth",
                ClassCategory = PluginClass.AudioModuleClass,
                Kind = PluginKind.Instrument,
                ParameterCount = 42,
                Tags = ["Instrument", "Synth"],
                Images = imagePath is null ? [] :
                [
                    new PluginImage { Kind = ImageKind.Screenshot, FilePath = imagePath, Width = 800, Height = 600 },
                ],
            },
            // Factory plumbing: present in every VST3 factory, never something a user would pick.
            new PluginClass { Index = 1, Name = "Synth Controller", ClassCategory = "Component Controller Class" },
        ],
    };

    private static JsonElement Serialize(CatalogExporter.ExportDocument document, string path)
    {
        CatalogExporter.Write(document, path);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    [Fact]
    public void Header_identifies_format_and_version()
    {
        var root = Serialize(CatalogExporter.Build([Synth()], new HashSet<string>(), Now, "PlugBrowser 9.9"),
            Path.Combine(_dir, "plugins.json"));

        Assert.Equal("PlugBrowser.CatalogExport", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("2026-11-19T20:15:00Z", root.GetProperty("generatedUtc").GetString());
        Assert.Equal("PlugBrowser 9.9", root.GetProperty("generator").GetString());
    }

    [Fact]
    public void Plugin_and_class_fields_use_the_documented_names_and_values()
    {
        var root = Serialize(CatalogExporter.Build([Synth()], new HashSet<string>(), Now, "x"),
            Path.Combine(_dir, "plugins.json"));

        var plugin = root.GetProperty("plugins")[0];
        Assert.Equal(@"C:\VST3\Synth.vst3", plugin.GetProperty("path").GetString());
        Assert.Equal("vst3", plugin.GetProperty("format").GetString());
        Assert.Equal("x64", plugin.GetProperty("architecture").GetString());
        Assert.Equal("probed", plugin.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, plugin.GetProperty("stateDetail").ValueKind);
        Assert.Equal("Synth", plugin.GetProperty("displayName").GetString());
        Assert.Equal("Acme", plugin.GetProperty("displayVendor").GetString());
        Assert.Equal("2.1.0", plugin.GetProperty("fileVersion").GetString());

        var c = Assert.Single(plugin.GetProperty("classes").EnumerateArray());
        Assert.Equal("84E8DE5F9255222296FAE4133C935A18", c.GetProperty("cid").GetString());
        Assert.Equal("Synth", c.GetProperty("name").GetString());
        Assert.Equal("Instrument|Synth", c.GetProperty("category").GetString());
        Assert.Equal(["Instrument", "Synth"], c.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("instrument", c.GetProperty("kind").GetString());
        Assert.Equal(42, c.GetProperty("parameterCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("image").ValueKind);
    }

    [Fact]
    public void Only_audio_module_classes_are_exported()
    {
        var document = CatalogExporter.Build([Synth()], new HashSet<string>(), Now, "x");

        Assert.Equal(["Synth"], document.Plugins[0].Classes.Select(c => c.Name));
    }

    [Fact]
    public void Blacklist_overrides_the_scanned_state()
    {
        var blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"c:\vst3\synth.vst3" };
        var document = CatalogExporter.Build([Synth()], blacklist, Now, "x");

        Assert.Equal("blacklisted", document.Plugins[0].State);
    }

    [Fact]
    public void Thumbnail_is_reported_only_when_the_file_exists()
    {
        string image = Path.Combine(_dir, "class-0.png");
        File.WriteAllText(image, "");

        Assert.Null(CatalogExporter.Build([Synth(image)], new HashSet<string>(), Now, "x")
            .Plugins[0].Classes[0].Image!.ThumbnailPath);

        string thumbnail = Path.Combine(_dir, "class-0.thumb.png");
        File.WriteAllText(thumbnail, "");

        var exported = CatalogExporter.Build([Synth(image)], new HashSet<string>(), Now, "x")
            .Plugins[0].Classes[0].Image!;
        Assert.Equal(image, exported.Path);
        Assert.Equal(thumbnail, exported.ThumbnailPath);
        Assert.Equal("screenshot", exported.Source);
        Assert.Equal((800, 600), (exported.Width, exported.Height));
    }

    [Fact]
    public void Never_loaded_plugin_still_has_a_display_name()
    {
        var entry = new PluginEntry { Path = @"C:\VST3\Mystery.vst3", Format = PluginFormat.Vst3 };
        var plugin = CatalogExporter.Build([entry], new HashSet<string>(), Now, "x").Plugins[0];

        Assert.Equal("discovered", plugin.State);
        Assert.Equal("Mystery", plugin.DisplayName);
        Assert.Empty(plugin.Classes);
    }

    [Fact]
    public void Write_replaces_the_previous_export_and_leaves_no_temporary_files()
    {
        string path = Path.Combine(_dir, "export", "plugins.json");
        CatalogExporter.Write(CatalogExporter.Build([], new HashSet<string>(), Now, "first"), path);
        CatalogExporter.Write(CatalogExporter.Build([Synth()], new HashSet<string>(), Now, "second"), path);

        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal("second", root.GetProperty("generator").GetString());
        Assert.Equal(["plugins.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Export_round_trips_a_real_catalog()
    {
        string database = Path.Combine(_dir, "catalog.db");
        string export = Path.Combine(_dir, "plugins.json");

        using (var store = CatalogStore.Open(database))
        {
            store.Save([Synth()]);
            Assert.True(CatalogExporter.IsStale(database, export));
            Assert.Null(CatalogExporter.TryExport(store, export));
        }

        var root = JsonDocument.Parse(File.ReadAllText(export)).RootElement;
        Assert.Equal("Synth", root.GetProperty("plugins")[0].GetProperty("classes")[0].GetProperty("name").GetString());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Export_is_stale_when_missing_or_older_than_the_database()
    {
        string database = Path.Combine(_dir, "catalog.db");
        string export = Path.Combine(_dir, "plugins.json");
        File.WriteAllText(database, "");

        Assert.True(CatalogExporter.IsStale(database, export));

        File.WriteAllText(export, "{}");
        File.SetLastWriteTimeUtc(database, Now);
        File.SetLastWriteTimeUtc(export, Now.AddMinutes(1));
        Assert.False(CatalogExporter.IsStale(database, export));

        File.SetLastWriteTimeUtc(database, Now.AddMinutes(2));
        Assert.True(CatalogExporter.IsStale(database, export));
    }
}
