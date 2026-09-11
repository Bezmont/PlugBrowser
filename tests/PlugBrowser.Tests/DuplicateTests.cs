using PlugBrowser.App.ViewModels;
using PlugBrowser.Core.Analysis;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;
using Xunit;

namespace PlugBrowser.Tests;

public class DuplicateAnalyzerTests
{
    private static PluginEntry Entry(string name, PluginFormat format, long size = 1024 * 1024) => new()
    {
        Path = $@"C:\x\{(format == PluginFormat.Vst3 ? "VST3" : "VST2")}\{name}." +
               (format == PluginFormat.Vst3 ? "vst3" : "dll"),
        Format = format,
        Architecture = PluginArchitecture.X64,
        FileProduct = name,
        FileSize = size,
        State = ProbeState.Probed,
        Classes = [new PluginClass { Index = 0, Name = name, Kind = PluginKind.Effect }],
    };

    /// <summary>
    /// The same product is named inconsistently across its own builds, so the key has to survive
    /// punctuation, spacing and architecture markers.
    /// </summary>
    [Theory]
    [InlineData("Addictive Drums 2", "Addictive Drums 2 x64")]
    [InlineData("Comp FET-76", "Comp FET 76")]
    [InlineData("Analog Lab V", "analog lab v")]
    [InlineData("Solid EQ", "Solid EQ (x64)")]
    [InlineData("Massive X", "Massive X 64bit")]
    public void NormalizeName_MatchesTheSameProductAcrossBuilds(string a, string b) =>
        Assert.Equal(DuplicateAnalyzer.NormalizeName(a), DuplicateAnalyzer.NormalizeName(b));

    /// <summary>
    /// Over-normalising is the dangerous direction: a false duplicate invites someone to delete a plugin
    /// they still need, which is far worse than missing one.
    /// </summary>
    [Theory]
    [InlineData("bx_console N", "bx_console Focusrite SC")]
    [InlineData("Analog Four", "Analog Keys")]
    [InlineData("Augmented BRASS", "Augmented STRINGS")]
    [InlineData("Comp FET-76", "Comp VCA-65")]
    public void NormalizeName_KeepsDifferentProductsApart(string a, string b) =>
        Assert.NotEqual(DuplicateAnalyzer.NormalizeName(a), DuplicateAnalyzer.NormalizeName(b));

    [Fact]
    public void FindCrossFormatDuplicates_PairsTheTwoBuildsOfOneProduct()
    {
        var entries = new[]
        {
            Entry("Acid V", PluginFormat.Vst3),
            Entry("Acid V", PluginFormat.Vst2),
            Entry("Lonely Plugin", PluginFormat.Vst3),
        };

        var group = Assert.Single(DuplicateAnalyzer.FindCrossFormatDuplicates(entries));

        Assert.Equal("Acid V", group.Name);
        Assert.Equal(2, group.Entries.Count);
        Assert.Equal([PluginFormat.Vst3, PluginFormat.Vst2], group.Formats.Order());
    }

    /// <summary>Two copies in the same format are a different problem, and not what this view is for.</summary>
    [Fact]
    public void FindCrossFormatDuplicates_IgnoresTwoCopiesOfOneFormat()
    {
        var entries = new[]
        {
            Entry("Thing", PluginFormat.Vst3) with { Path = @"C:\a\Thing.vst3" },
            Entry("Thing", PluginFormat.Vst3) with { Path = @"C:\b\Thing.vst3" },
        };

        Assert.Empty(DuplicateAnalyzer.FindCrossFormatDuplicates(entries));
    }

    /// <summary>The VST3 copy is the keeper, so only the other build counts as reclaimable.</summary>
    [Fact]
    public void RedundantBytes_CountsOnlyTheNonPreferredCopy()
    {
        var entries = new[]
        {
            Entry("Acid V", PluginFormat.Vst3, size: 10 * 1024 * 1024),
            Entry("Acid V", PluginFormat.Vst2, size: 4 * 1024 * 1024),
        };

        var group = Assert.Single(DuplicateAnalyzer.FindCrossFormatDuplicates(entries));

        Assert.Equal(14 * 1024 * 1024, group.TotalBytes);
        Assert.Equal(4 * 1024 * 1024, group.RedundantBytes);
        // VST3 sorts first so the grid and the group name both lead with the keeper.
        Assert.Equal(PluginFormat.Vst3, group.Entries[0].Format);
    }

    [Fact]
    public void CrossFormatKeys_CoversOnlyTheDuplicatedProducts()
    {
        var entries = new[]
        {
            Entry("Acid V", PluginFormat.Vst3),
            Entry("Acid V", PluginFormat.Vst2),
            Entry("Lonely Plugin", PluginFormat.Vst3),
        };

        var keys = DuplicateAnalyzer.CrossFormatKeys(entries);

        Assert.Contains(DuplicateAnalyzer.NormalizeName("Acid V"), keys);
        Assert.DoesNotContain(DuplicateAnalyzer.NormalizeName("Lonely Plugin"), keys);
    }

    [Fact]
    public void NormalizeName_HandlesBlanks() =>
        Assert.Equal(string.Empty, DuplicateAnalyzer.NormalizeName(null));
}

public class UniqueFilterAndClearTests
{
    private static PluginEntry Entry(string name, PluginFormat format) => new()
    {
        Path = $@"C:\x\{name}.{(format == PluginFormat.Vst3 ? "vst3" : "dll")}",
        Format = format,
        Architecture = PluginArchitecture.X64,
        FileVendor = "Acme",
        FileProduct = name,
        State = ProbeState.Probed,
        Classes =
        [
            new PluginClass
            {
                Index = 0, Name = name, Vendor = "Acme",
                Category = "Fx|Delay", Kind = PluginKind.Effect, Tags = ["Fx", "Delay"],
            },
        ],
    };

    private static (MainWindowViewModel Vm, CatalogStore Store, TempTree Tree) Build()
    {
        var tree = new TempTree();
        var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        store.Save([
            Entry("Both Formats", PluginFormat.Vst3),
            Entry("Both Formats", PluginFormat.Vst2),
            Entry("Vst3 Only", PluginFormat.Vst3),
            Entry("Vst2 Only", PluginFormat.Vst2),
        ]);

        var vm = new MainWindowViewModel(store);
        vm.LoadCatalog();
        return (vm, store, tree);
    }

    [Fact]
    public void UniqueOnly_HidesBothCopiesOfADuplicatedProduct()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.Equal(4, vm.Visible.Count);

            vm.UniqueOnly = true;

            Assert.Equal(["Vst2 Only", "Vst3 Only"], vm.Visible.Select(v => v.Name).Order());
        }
    }

    [Fact]
    public void UniqueCount_IsReportedInTheLabel()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.Equal(2, vm.UniqueCount);
            Assert.Equal("Unique only (2)", vm.UniqueLabel);
        }
    }

    [Fact]
    public void ClearFilters_ResetsEverythingIncludingTheSearch()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.SearchText = "vst3";
            vm.ShowVst2 = false;
            vm.UniqueOnly = true;
            vm.ProblemsOnly = true;
            vm.SelectedVendor = "Acme";
            vm.Categories.Single(c => c.Name == "Delay").IsSelected = true;
            Assert.True(vm.HasAnyFilter);

            vm.ClearFiltersCommand.Execute(null);

            Assert.False(vm.HasAnyFilter);
            Assert.Empty(vm.SearchText);
            Assert.True(vm.ShowVst2);
            Assert.False(vm.UniqueOnly);
            Assert.False(vm.ProblemsOnly);
            Assert.Equal(MainWindowViewModel.AnyVendor, vm.SelectedVendor);
            Assert.False(vm.HasCategoryFilter);
            Assert.Equal(4, vm.Visible.Count);
        }
    }

    [Fact]
    public void ClearSearch_LeavesTheFacetsAlone()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.ShowVst2 = false;
            vm.SearchText = "both";
            Assert.True(vm.HasSearchText);

            vm.ClearSearchCommand.Execute(null);

            Assert.Empty(vm.SearchText);
            Assert.False(vm.HasSearchText);
            // The format facet is untouched, so only the VST3 entries remain.
            Assert.Equal(2, vm.Visible.Count);
        }
    }

    /// <summary>The button only earns its place when something is actually narrowing the view.</summary>
    [Fact]
    public void HasAnyFilter_IsFalseOnAnUntouchedView()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.False(vm.HasAnyFilter);
        }
    }
}

public class DuplicatesViewModelTests
{
    [Fact]
    public void BuildsOneRowPerFileAndSummarisesTheWaste()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));

        PluginEntry Make(string name, PluginFormat format, long mb) => new()
        {
            Path = $@"C:\x\{format}\{name}.{(format == PluginFormat.Vst3 ? "vst3" : "dll")}",
            Format = format,
            Architecture = PluginArchitecture.X64,
            FileProduct = name,
            FileSize = mb * 1024 * 1024,
            State = ProbeState.Probed,
            Classes = [new PluginClass { Index = 0, Name = name, Kind = PluginKind.Effect }],
        };

        store.Save([
            Make("Acid V", PluginFormat.Vst3, 10),
            Make("Acid V", PluginFormat.Vst2, 4),
            Make("Lonely", PluginFormat.Vst3, 7),
        ]);

        var vm = new DuplicatesViewModel(store);

        Assert.Equal(1, vm.ProductCount);
        Assert.Equal(2, vm.FileCount);
        Assert.Equal(1, vm.UniqueCount);
        Assert.Equal(4, vm.RedundantMb);
        Assert.Equal(2, vm.Rows.Count);

        // Only the removable copy remains when the keeper is filtered out.
        vm.RedundantOnly = true;
        var only = Assert.Single(vm.Rows);
        Assert.Equal("VST2", only.Format);
        Assert.False(only.IsPreferredFormat);
    }

    [Fact]
    public void ExportsTabSeparatedTextForASpreadsheet()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));

        PluginEntry Make(string name, PluginFormat format) => new()
        {
            Path = $@"C:\x\{format}\{name}.vst3",
            Format = format,
            FileProduct = name,
            State = ProbeState.Probed,
            Classes = [new PluginClass { Index = 0, Name = name, Kind = PluginKind.Effect }],
        };

        store.Save([Make("Acid V", PluginFormat.Vst3), Make("Acid V", PluginFormat.Vst2)]);

        string tsv = new DuplicatesViewModel(store).ToTabSeparated();
        var lines = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Product\tName\tFormat", lines[0]);
        Assert.Equal(3, lines.Length); // header + two files
        Assert.Contains("Acid V", lines[1]);
    }
}
