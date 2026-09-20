using PlugBrowser.App.ViewModels;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;
using Xunit;

namespace PlugBrowser.Tests;

public class PluginListViewModelTests
{
    private static PluginEntry Make(string name, PluginFormat format, string vendor = "Acme",
        PluginKind kind = PluginKind.Effect, long mb = 1) => new()
    {
        Path = $@"C:\x\{format}\{name}.{(format == PluginFormat.Vst3 ? "vst3" : "dll")}",
        Format = format,
        Architecture = PluginArchitecture.X64,
        FileProduct = name,
        FileVendor = vendor,
        FileSize = mb * 1024 * 1024,
        State = ProbeState.Probed,
        Classes = [new PluginClass { Index = 0, Name = name, Vendor = vendor, Kind = kind }],
    };

    private static PluginListViewModel Build(TempTree tree, params PluginEntry[] entries)
    {
        var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        store.Save(entries);
        return new PluginListViewModel(store);
    }

    [Fact]
    public void ListsEveryPluginNotJustTheDuplicates()
    {
        using var tree = new TempTree();
        var vm = Build(tree,
            Make("Acid V", PluginFormat.Vst3),
            Make("Acid V", PluginFormat.Vst2),
            Make("Lonely", PluginFormat.Vst3));

        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("3 plugins", vm.RowCountLabel);
        Assert.Equal("VST3 + VST2", vm.Rows.First(r => r.Name == "Acid V").InstalledAs);
        Assert.Equal("VST3", vm.Rows.Single(r => r.Name == "Lonely").InstalledAs);
    }

    [Fact]
    public void ColumnFiltersOfferThePresentValuesAndCombine()
    {
        using var tree = new TempTree();
        var vm = Build(tree,
            Make("Acid V", PluginFormat.Vst3, "Arturia", PluginKind.Instrument),
            Make("Acid V", PluginFormat.Vst2, "Arturia", PluginKind.Instrument),
            Make("Solid EQ", PluginFormat.Vst3, "NI"));

        Assert.Equal(["All vendors", "Arturia", "NI"], vm.VendorFilter.Options);

        vm.VendorFilter.Selected = "Arturia";
        Assert.Equal(2, vm.Rows.Count);
        Assert.True(vm.HasAnyFilter);
        Assert.Equal("2 of 3 plugins", vm.RowCountLabel);

        vm.FormatFilter.Selected = "VST2";
        var only = Assert.Single(vm.Rows);
        Assert.Equal("VST2", only.Format);

        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(3, vm.Rows.Count);
        Assert.False(vm.HasAnyFilter);
    }

    [Fact]
    public void SearchMatchesAnyTextColumn()
    {
        using var tree = new TempTree();
        var vm = Build(tree, Make("Acid V", PluginFormat.Vst3), Make("Solid EQ", PluginFormat.Vst2));

        vm.SearchText = "vst2";
        Assert.Equal("Solid EQ", Assert.Single(vm.Rows).Name);

        vm.SearchText = @"x\Vst3";
        Assert.Equal("Acid V", Assert.Single(vm.Rows).Name);
    }

    [Fact]
    public void ExportsTheGivenRowsInTheGivenOrder()
    {
        using var tree = new TempTree();
        var vm = Build(tree, Make("Acid V", PluginFormat.Vst3), Make("Solid EQ", PluginFormat.Vst3));

        var lines = vm.ToTabSeparated(vm.Rows.Reverse())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(PluginListViewModel.TabSeparatedHeader, lines[0].TrimEnd('\r'));
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Solid EQ\tVST3", lines[1]);
        Assert.StartsWith("Acid V\tVST3", lines[2]);
    }
}

public class PluginListCsvTests
{
    [Fact]
    public void CsvQuotesEveryFieldAndEscapesQuotes()
    {
        using var tree = new TempTree();
        var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        store.Save([new PluginEntry
        {
            Path = @"C:\x\Big, ""Loud"" Comp.vst3",
            Format = PluginFormat.Vst3,
            FileProduct = @"Big, ""Loud"" Comp",
            FileSize = 3 * 1024 * 1024 / 2,
        }]);

        var lines = new PluginListViewModel(store).ToCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("\"Name\",\"Format\",\"Installed as\"", lines[0]);
        Assert.StartsWith("\"Big, \"\"Loud\"\" Comp\",\"VST3\"", lines[1]);
        Assert.Contains("\"1.5\"", lines[1]);
    }
}
