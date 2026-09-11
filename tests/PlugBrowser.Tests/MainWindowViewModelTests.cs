using PlugBrowser.App.ViewModels;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;
using Xunit;

namespace PlugBrowser.Tests;

/// <summary>Covers the browser's filtering, which is pure logic over the catalog and needs no UI.</summary>
public class MainWindowViewModelTests
{
    private static PluginEntry Entry(string name, string vendor, PluginFormat format, PluginKind kind) => new()
    {
        Path = $@"C:\x\{name}.{(format == PluginFormat.Vst3 ? "vst3" : "dll")}",
        Format = format,
        Architecture = PluginArchitecture.X64,
        FileVendor = vendor,
        FileProduct = name,
        Classes = [new PluginClass { Index = 0, Name = name, Vendor = vendor, Kind = kind }],
    };

    private static (MainWindowViewModel Vm, CatalogStore Store, TempTree Tree) Build()
    {
        var tree = new TempTree();
        var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        store.Save([
            Entry("Reverb", "Acme", PluginFormat.Vst3, PluginKind.Effect),
            Entry("Synthy", "Acme", PluginFormat.Vst3, PluginKind.Instrument),
            Entry("OldComp", "Bravo", PluginFormat.Vst2, PluginKind.Effect),
        ]);

        var vm = new MainWindowViewModel(store);
        vm.LoadCatalog();
        return (vm, store, tree);
    }

    [Fact]
    public void LoadCatalog_PopulatesVendorFacet_WithNoFilterFirstAndSelected()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.Equal(MainWindowViewModel.AnyVendor, vm.Vendors[0]);
            Assert.Equal(["All vendors", "Acme", "Bravo"], vm.Vendors);
            Assert.Equal(MainWindowViewModel.AnyVendor, vm.SelectedVendor);
            Assert.Equal(3, vm.Visible.Count);
        }
    }

    [Fact]
    public void VendorFilter_NarrowsToOneVendor()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.SelectedVendor = "Bravo";
            Assert.Equal("OldComp", Assert.Single(vm.Visible).Name);
        }
    }

    [Fact]
    public void FormatAndKindFilters_Compose()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.ShowVst2 = false;
            Assert.Equal(2, vm.Visible.Count);

            vm.ShowInstruments = false;
            Assert.Equal("Reverb", Assert.Single(vm.Visible).Name);
        }
    }

    [Fact]
    public void Search_RequiresEveryTermToMatch()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.SearchText = "acme";
            Assert.Equal(2, vm.Visible.Count);

            vm.SearchText = "acme synthy";
            Assert.Equal("Synthy", Assert.Single(vm.Visible).Name);

            vm.SearchText = "acme nonsense";
            Assert.Empty(vm.Visible);
        }
    }

    [Fact]
    public void Selection_FallsBackToFirstResult_WhenFilteredOut()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.SearchText = "oldcomp";
            Assert.Equal("OldComp", vm.Selected?.Name);

            vm.SearchText = "synthy";
            Assert.Equal("Synthy", vm.Selected?.Name);
        }
    }
}
