using PlugBrowser.App.ViewModels;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;
using Xunit;

namespace PlugBrowser.Tests;

public class CategoryFilterTests
{
    private static PluginEntry Fx(string name, string vendor, params string[] tags) => new()
    {
        Path = $@"C:\x\{name}.vst3",
        Format = PluginFormat.Vst3,
        Architecture = PluginArchitecture.X64,
        FileVendor = vendor,
        State = ProbeState.Probed,
        Classes =
        [
            new PluginClass
            {
                Index = 0,
                Name = name,
                Vendor = vendor,
                Category = string.Join('|', tags),
                Kind = tags.Contains("Instrument") ? PluginKind.Instrument : PluginKind.Effect,
                Tags = tags,
            },
        ],
    };

    private static (MainWindowViewModel Vm, CatalogStore Store, TempTree Tree) Build()
    {
        var tree = new TempTree();
        var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        store.Save([
            Fx("Tape Echo", "Acme", "Fx", "Delay"),
            Fx("Plate", "Acme", "Fx", "Reverb"),
            Fx("Bus Comp", "Bravo", "Fx", "Dynamics"),
            Fx("Chorus Pro", "Bravo", "Fx", "Modulation"),
            Fx("Big Synth", "Acme", "Instrument", "Synth"),
        ]);

        var vm = new MainWindowViewModel(store);
        vm.LoadCatalog();
        return (vm, store, tree);
    }

    /// <summary>
    /// Facets are derived from the tags plugins actually report, not a fixed vocabulary — vendors emit
    /// tags the spec never defined, and a hardcoded list would quietly hide them.
    /// </summary>
    [Fact]
    public void Categories_AreDerivedFromTags_WithCounts()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.Equal(["Delay", "Dynamics", "Modulation", "Reverb", "Synth"],
                vm.Categories.Select(c => c.Name));
            Assert.All(vm.Categories, c => Assert.Equal(1, c.Count));
            Assert.Equal("Delay (1)", vm.Categories[0].Label);
        }
    }

    /// <summary>"Fx" and "Instrument" duplicate the Type facet above; listing them twice would let the
    /// two controls be set into a contradiction that silently returns nothing.</summary>
    [Fact]
    public void Categories_ExcludeTheEffectAndInstrumentTags()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.DoesNotContain(vm.Categories, c => c.Name is "Fx" or "Instrument");
        }
    }

    /// <summary>Ticking two facets asks for either, which is how a facet list reads. Requiring both
    /// would return almost nothing, since a plugin rarely claims two.</summary>
    [Fact]
    public void SelectingSeveralCategories_CombinesAsOr()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.Categories.Single(c => c.Name == "Delay").IsSelected = true;
            Assert.Equal("Tape Echo", Assert.Single(vm.Visible).Name);

            vm.Categories.Single(c => c.Name == "Reverb").IsSelected = true;
            Assert.Equal(["Plate", "Tape Echo"], vm.Visible.Select(v => v.Name).Order());
        }
    }

    [Fact]
    public void CategoryFilter_CombinesWithOtherFacets()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.Categories.Single(c => c.Name == "Delay").IsSelected = true;
            vm.SelectedVendor = "Bravo";

            Assert.Empty(vm.Visible);
        }
    }

    [Fact]
    public void ClearCategories_RestoresTheFullView()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.Categories.Single(c => c.Name == "Delay").IsSelected = true;
            Assert.True(vm.HasCategoryFilter);

            vm.ClearCategoriesCommand.Execute(null);

            Assert.False(vm.HasCategoryFilter);
            Assert.Equal(5, vm.Visible.Count);
        }
    }

    /// <summary>A rescan rebuilds the facet list; silently dropping the user's selection would widen
    /// the view without them asking.</summary>
    [Fact]
    public void CategorySelection_SurvivesACatalogReload()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.Categories.Single(c => c.Name == "Reverb").IsSelected = true;

            vm.LoadCatalog();

            Assert.True(vm.Categories.Single(c => c.Name == "Reverb").IsSelected);
            Assert.Equal("Plate", Assert.Single(vm.Visible).Name);
        }
    }

    /// <summary>
    /// VST3 mixes processing-capability flags into the same subcategory string as musical categories,
    /// so "Fx|Delay|OnlyRT" declares a type and a constraint together. A list for choosing "delay" or
    /// "reverb" should not offer "OnlyRT" beside them.
    /// </summary>
    [Fact]
    public void Categories_ExcludeProcessingCapabilityFlags()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        store.Save([Fx("Realtime Delay", "Acme", "Fx", "Delay", "OnlyRT", "NoOfflineProcess", "Stereo")]);

        var vm = new MainWindowViewModel(store);
        vm.LoadCatalog();

        Assert.Equal(["Delay"], vm.Categories.Select(c => c.Name));
    }

    /// <summary>
    /// A count is only honest if it moves with the rest of the query. Choosing a vendor must
    /// immediately show how many delays or reverbs remain *within that vendor*.
    /// </summary>
    [Fact]
    public void CategoryCounts_NarrowWithTheVendorFilter()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.Equal(1, vm.Categories.Single(c => c.Name == "Delay").Count);
            Assert.Equal(1, vm.Categories.Single(c => c.Name == "Dynamics").Count);

            // Acme has the delay and the reverb; Bravo has the compressor and the chorus.
            vm.SelectedVendor = "Acme";

            Assert.Equal(1, vm.Categories.Single(c => c.Name == "Delay").Count);
            Assert.Equal(0, vm.Categories.Single(c => c.Name == "Dynamics").Count);
        }
    }

    [Fact]
    public void CategoryCounts_NarrowWithTheSearchBox()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.SearchText = "plate";

            Assert.Equal(1, vm.Categories.Single(c => c.Name == "Reverb").Count);
            Assert.Equal(0, vm.Categories.Single(c => c.Name == "Delay").Count);
        }
    }

    /// <summary>
    /// A group's own selections must be excluded from its own counts.
    /// </summary>
    /// <remarks>Otherwise ticking Delay would show "Delay (1)" and zero beside every sibling, making
    /// the list useless for widening the selection — the opposite of what a facet list is for.</remarks>
    [Fact]
    public void CategoryCounts_IgnoreTheCategoryFilterItself()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.Categories.Single(c => c.Name == "Delay").IsSelected = true;

            Assert.Single(vm.Visible);
            // Reverb still reports what ticking it would add, rather than collapsing to zero.
            Assert.Equal(1, vm.Categories.Single(c => c.Name == "Reverb").Count);
        }
    }

    [Fact]
    public void FormatAndTypeCounts_CrossFilterToo()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.Equal(5, vm.Vst3Count);
            Assert.Equal(4, vm.EffectCount);
            Assert.Equal(1, vm.InstrumentCount);

            vm.SelectedVendor = "Bravo";
            Assert.Equal(2, vm.Vst3Count);
            Assert.Equal(2, vm.EffectCount);
            Assert.Equal(0, vm.InstrumentCount);
        }
    }

    /// <summary>The Type counts must not be narrowed by the Type checkboxes themselves, or unticking
    /// one would strand it at zero with no way to see what re-ticking would restore.</summary>
    [Fact]
    public void TypeCounts_IgnoreTheTypeCheckboxes()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.ShowInstruments = false;

            Assert.Equal(4, vm.Visible.Count);
            Assert.Equal(1, vm.InstrumentCount);
        }
    }

    [Fact]
    public void CountLabels_ShowTheNumbers()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.Equal("VST3 (5)", vm.Vst3Label);
            Assert.Equal("Effects (4)", vm.EffectsLabel);
            Assert.Equal("Delay (1)", vm.Categories.Single(c => c.Name == "Delay").Label);
        }
    }

    [Fact]
    public void ResultCount_TracksTheFilteredView()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.Equal("5 plugins", vm.ResultCount);

            vm.Categories.Single(c => c.Name == "Delay").IsSelected = true;
            Assert.Equal("1 plugin", vm.ResultCount);
        }
    }
}

public class SelectionTests
{
    private static PluginEntry Entry(string name, string vendor, PluginFormat format, PluginKind kind,
        bool withImage = false) => new()
    {
        Path = $@"C:\x\{name}.vst3",
        Format = format,
        Architecture = PluginArchitecture.X64,
        FileVendor = vendor,
        State = ProbeState.Probed,
        Classes =
        [
            new PluginClass
            {
                Index = 0,
                Name = name,
                Vendor = vendor,
                Category = "Fx|Delay",
                Kind = kind,
                Tags = ["Fx", "Delay"],
                Images = withImage
                    ?
                    [
                        new PluginImage
                        {
                            Kind = ImageKind.Screenshot,
                            FilePath = @"C:\images\x.png",
                            Width = 800,
                            Height = 400,
                        },
                    ]
                    : [],
            },
        ],
    };

    private static (MainWindowViewModel Vm, CatalogStore Store, TempTree Tree) Build()
    {
        var tree = new TempTree();
        var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        store.Save([
            Entry("Alpha", "Acme", PluginFormat.Vst3, PluginKind.Effect, withImage: true),
            Entry("Beta", "Acme", PluginFormat.Vst3, PluginKind.Effect),
            Entry("Gamma", "Bravo", PluginFormat.Vst2, PluginKind.Instrument),
        ]);

        var vm = new MainWindowViewModel(store);
        vm.LoadCatalog();
        return (vm, store, tree);
    }

    [Fact]
    public void SingleSelection_ShowsTheFullDetailPane()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.SetSelection([vm.Visible[0]]);

            Assert.True(vm.HasSingleSelected);
            Assert.False(vm.HasMultipleSelected);
            Assert.Equal("Alpha", vm.Selected?.Name);
        }
    }

    /// <summary>
    /// A trait that is not actually shared must not be reported as if it were.
    /// </summary>
    /// <remarks>Showing the first item's vendor for a mixed selection would be an outright lie about
    /// the other plugins, so a differing trait says how many distinct values there are instead.</remarks>
    [Fact]
    public void MultipleSelection_ReportsOnlyGenuinelyCommonTraits()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            var alpha = vm.Visible.Single(v => v.Name == "Alpha");
            var beta = vm.Visible.Single(v => v.Name == "Beta");
            var gamma = vm.Visible.Single(v => v.Name == "Gamma");

            vm.SetSelection([alpha, beta]);
            Assert.True(vm.HasMultipleSelected);
            Assert.Equal("2 plugins selected", vm.SelectionSummary);
            Assert.Equal("Acme", vm.CommonVendor);
            Assert.Equal("VST3", vm.CommonFormat);

            vm.SetSelection([alpha, beta, gamma]);
            Assert.Equal("(2 different)", vm.CommonVendor);
            Assert.Equal("(2 different)", vm.CommonFormat);
            Assert.Equal("(2 different)", vm.CommonKind);
        }
    }

    [Fact]
    public void MultipleSelection_CountsHowManyAlreadyHaveScreenshots()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.SetSelection([
                vm.Visible.Single(v => v.Name == "Alpha"),
                vm.Visible.Single(v => v.Name == "Beta"),
            ]);

            Assert.Equal("1 of 2 have screenshots", vm.SelectionImageCount);
        }
    }

    [Fact]
    public void RescanSelected_IsOnlyAvailableWithASelection()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            Assert.False(vm.RescanSelectedCommand.CanExecute(null));

            vm.SetSelection([vm.Visible[0]]);
            Assert.True(vm.RescanSelectedCommand.CanExecute(null));
        }
    }

    /// <summary>VST2 is catalogued from its file alone and never loaded, so it cannot be recaptured —
    /// the user should be told that rather than watching nothing happen.</summary>
    [Fact]
    public async Task RescanSelected_ExplainsItselfWhenOnlyVst2IsSelected()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.SetSelection([vm.Visible.Single(v => v.Name == "Gamma")]);

            await vm.RescanSelectedCommand.ExecuteAsync(null);

            Assert.Contains("Only VST3", vm.Status);
            Assert.False(vm.IsScanning);
        }
    }

    [Fact]
    public void Zoom_OnlyOpensWhenThereIsAnImage()
    {
        var (vm, store, tree) = Build();
        using (store) using (tree)
        {
            vm.SetSelection([vm.Visible.Single(v => v.Name == "Beta")]);
            vm.ZoomImageCommand.Execute(null);
            Assert.False(vm.IsImageZoomed);

            vm.SetSelection([vm.Visible.Single(v => v.Name == "Alpha")]);
            vm.ZoomImageCommand.Execute(null);
            Assert.True(vm.IsImageZoomed);

            vm.CloseZoomCommand.Execute(null);
            Assert.False(vm.IsImageZoomed);
        }
    }
}
