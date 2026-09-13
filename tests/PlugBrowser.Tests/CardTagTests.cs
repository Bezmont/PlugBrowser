using PlugBrowser.App.ViewModels;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;
using Xunit;

namespace PlugBrowser.Tests;

/// <summary>Vendor names shortened for card tags, checked against the vendors in a real catalog.</summary>
public class VendorAbbreviationTests
{
    [Theory]
    [InlineData("Arturia", "Arturia")]                     // short names are left alone
    [InlineData("Cakewalk", "Cakewalk")]
    [InlineData("KORG", "KORG")]
    [InlineData("iZotope, Inc.", "iZotope")]               // company suffixes carry no identity
    [InlineData("MAGIX Software GmbH", "MAGIX")]           // …and nor do generic words
    [InlineData("Reason Studios", "Reason")]
    [InlineData("XLN Audio", "XLN")]
    [InlineData("Native Instruments", "NI")]               // too long, several words: initials
    [InlineData("Native Instruments GmbH", "NI")]
    [InlineData("Plugin Alliance", "PA")]
    [InlineData("AUDIO PLUGIN UNION", "APU")]              // initials keep the generic word: APU, not PU
    [InlineData("Spectrasonics", "Spectras…")]             // one long word: cut, and marked as cut
    [InlineData(null, "Unknown")]
    [InlineData("   ", "Unknown")]
    public void Abbreviate_KeepsTagsShortButRecognisable(string? vendor, string expected)
    {
        Assert.Equal(expected, VendorAbbreviation.Abbreviate(vendor));
    }

    [Theory]
    [InlineData("Native Instruments GmbH")]
    [InlineData("Spectrasonics")]
    [InlineData("Some Extremely Long Vendor Name Ltd")]
    public void Abbreviate_NeverExceedsTheMaximumLength(string vendor)
    {
        Assert.True(VendorAbbreviation.Abbreviate(vendor).Length <= VendorAbbreviation.MaxLength);
    }
}

/// <summary>
/// The clickable tags on gallery cards, and the filters they add when clicked.
/// </summary>
public class CardTagTests
{
    private static PluginEntry Entry(string name, string vendor, PluginFormat format, PluginKind kind,
        string category) => new()
    {
        Path = $@"C:\x\{name}.{(format == PluginFormat.Vst3 ? "vst3" : "dll")}",
        Format = format,
        Architecture = PluginArchitecture.X64,
        FileVendor = vendor,
        State = kind == PluginKind.Unknown ? ProbeState.Discovered : ProbeState.Probed,
        Classes = kind == PluginKind.Unknown
            ? []
            :
            [
                new PluginClass
                {
                    Index = 0,
                    Name = name,
                    Vendor = vendor,
                    Kind = kind,
                    Category = category,
                    Tags = category.Split('|'),
                },
            ],
    };

    private static (CatalogStore Store, MainWindowViewModel ViewModel) Catalog(TempTree tree, params PluginEntry[] entries)
    {
        var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        store.Save(entries);
        var viewModel = new MainWindowViewModel(store);
        viewModel.LoadCatalog();
        return (store, viewModel);
    }

    private static PluginItemViewModel Item(MainWindowViewModel viewModel, string name) =>
        viewModel.Visible.Single(i => i.Name == name);

    [Fact]
    public void CardTags_ShowFormatTypeCategoriesAndVendor_InThatOrder()
    {
        var item = new PluginItemViewModel(
            Entry("Verb", "Native Instruments GmbH", PluginFormat.Vst3, PluginKind.Effect, "Fx|Reverb|Delay|OnlyRT|Stereo"),
            isFavorite: false);

        Assert.Equal(["VST3", "Effect", "Reverb", "Delay", "NI"], item.CardTags.Select(t => t.Label));
        Assert.Equal(
            [CardTagKind.Format, CardTagKind.Kind, CardTagKind.Category, CardTagKind.Category, CardTagKind.Vendor],
            item.CardTags.Select(t => t.Kind));

        // The vendor tag filters by the full name, and says so, however short its label.
        var vendor = item.CardTags.Last();
        Assert.Equal("Native Instruments GmbH", vendor.Value);
        Assert.Contains("Native Instruments GmbH", vendor.ToolTip);
    }

    /// <summary>A category tag must always correspond to a checkbox in the Category filter, so the
    /// capability and type markers the filter leaves out are left off the card too.</summary>
    [Fact]
    public void CardTags_LeaveOutTheTagsTheCategoryFilterLeavesOut()
    {
        var item = new PluginItemViewModel(
            Entry("Synth", "Arturia", PluginFormat.Vst3, PluginKind.Instrument, "Instrument|Synth|OnlyRT|Mono"),
            isFavorite: false);

        Assert.Equal(["Synth"], item.CardTags.Where(t => t.Kind == CardTagKind.Category).Select(t => t.Label));
    }

    [Fact]
    public void UnclassifiedTag_IsNotClickable()
    {
        var item = new PluginItemViewModel(
            Entry("Unprobed", "Arturia", PluginFormat.Vst2, PluginKind.Unknown, ""), isFavorite: false);

        var kind = item.CardTags.Single(t => t.Kind == CardTagKind.Kind);
        Assert.Equal("Unclassified", kind.Label);
        Assert.False(kind.IsClickable);
    }

    [Fact]
    public void FormatTag_NarrowsTheFormatCheckboxesToThatFormat()
    {
        using var tree = new TempTree();
        var (store, viewModel) = Catalog(tree,
            Entry("A", "Arturia", PluginFormat.Vst3, PluginKind.Effect, "Fx|Delay"),
            Entry("B", "Arturia", PluginFormat.Vst2, PluginKind.Effect, "Fx|Delay"));
        using var _ = store;

        var tag = Item(viewModel, "B").CardTags.Single(t => t.Kind == CardTagKind.Format);
        viewModel.ApplyTagFilterCommand.Execute(tag);

        Assert.False(viewModel.ShowVst3);
        Assert.True(viewModel.ShowVst2);
        Assert.Equal(["B"], viewModel.Visible.Select(i => i.Name));
    }

    [Fact]
    public void TypeTag_NarrowsTheTypeCheckboxesToThatType()
    {
        using var tree = new TempTree();
        var (store, viewModel) = Catalog(tree,
            Entry("Fx", "Arturia", PluginFormat.Vst3, PluginKind.Effect, "Fx|Delay"),
            Entry("Keys", "Arturia", PluginFormat.Vst3, PluginKind.Instrument, "Instrument|Synth"));
        using var _ = store;

        var tag = Item(viewModel, "Keys").CardTags.Single(t => t.Kind == CardTagKind.Kind);
        viewModel.ApplyTagFilterCommand.Execute(tag);

        Assert.False(viewModel.ShowEffects);
        Assert.True(viewModel.ShowInstruments);
        Assert.Equal(["Keys"], viewModel.Visible.Select(i => i.Name));
    }

    [Fact]
    public void CategoryTag_TicksItsCheckbox_AlongsideAnyAlreadyTicked()
    {
        using var tree = new TempTree();
        var (store, viewModel) = Catalog(tree,
            Entry("Echo", "Arturia", PluginFormat.Vst3, PluginKind.Effect, "Fx|Delay"),
            Entry("Hall", "Arturia", PluginFormat.Vst3, PluginKind.Effect, "Fx|Reverb"),
            Entry("Comp", "Arturia", PluginFormat.Vst3, PluginKind.Effect, "Fx|Dynamics"));
        using var _ = store;

        var reverb = Item(viewModel, "Hall").CardTags.Single(t => t.Kind == CardTagKind.Category);
        viewModel.Categories.Single(c => c.Name == "Delay").IsSelected = true;

        viewModel.ApplyTagFilterCommand.Execute(reverb);

        Assert.Equal(["Delay", "Reverb"],
            viewModel.Categories.Where(c => c.IsSelected).Select(c => c.Name).OrderBy(n => n));
        Assert.Equal(["Echo", "Hall"], viewModel.Visible.Select(i => i.Name));
        Assert.True(viewModel.HasCategoryFilter);
    }

    [Fact]
    public void VendorTag_SelectsThatVendorInTheDropdown()
    {
        using var tree = new TempTree();
        var (store, viewModel) = Catalog(tree,
            Entry("One", "Native Instruments GmbH", PluginFormat.Vst3, PluginKind.Effect, "Fx|Delay"),
            Entry("Two", "Arturia", PluginFormat.Vst3, PluginKind.Effect, "Fx|Delay"));
        using var _ = store;

        var tag = Item(viewModel, "One").CardTags.Single(t => t.Kind == CardTagKind.Vendor);
        viewModel.ApplyTagFilterCommand.Execute(tag);

        Assert.Equal("Native Instruments GmbH", viewModel.SelectedVendor);
        Assert.Contains(viewModel.SelectedVendor, viewModel.Vendors);
        Assert.Equal(["One"], viewModel.Visible.Select(i => i.Name));
    }

    /// <summary>Tags add to the filters already set; they never reset the others.</summary>
    [Fact]
    public void Tags_StackOnTopOfTheExistingFilters()
    {
        using var tree = new TempTree();
        var (store, viewModel) = Catalog(tree,
            Entry("NI Delay", "Native Instruments", PluginFormat.Vst3, PluginKind.Effect, "Fx|Delay"),
            Entry("NI Verb", "Native Instruments", PluginFormat.Vst3, PluginKind.Effect, "Fx|Reverb"),
            Entry("NI Delay 2", "Native Instruments", PluginFormat.Vst2, PluginKind.Effect, "Fx|Delay"),
            Entry("Art Delay", "Arturia", PluginFormat.Vst3, PluginKind.Effect, "Fx|Delay"));
        using var _ = store;

        viewModel.SearchText = "delay";
        var target = Item(viewModel, "NI Delay");

        viewModel.ApplyTagFilterCommand.Execute(target.CardTags.Single(t => t.Kind == CardTagKind.Vendor));
        viewModel.ApplyTagFilterCommand.Execute(target.CardTags.Single(t => t.Kind == CardTagKind.Format));

        Assert.Equal("delay", viewModel.SearchText);
        Assert.Equal("Native Instruments", viewModel.SelectedVendor);
        Assert.False(viewModel.ShowVst2);
        Assert.Equal(["NI Delay"], viewModel.Visible.Select(i => i.Name));
    }

    [Fact]
    public void UnclassifiedTag_ChangesNothing_WhenInvoked()
    {
        using var tree = new TempTree();
        var (store, viewModel) = Catalog(tree,
            Entry("Unprobed", "Arturia", PluginFormat.Vst2, PluginKind.Unknown, ""),
            Entry("Fx", "Arturia", PluginFormat.Vst3, PluginKind.Effect, "Fx|Delay"));
        using var _ = store;

        var tag = Item(viewModel, "Unprobed").CardTags.Single(t => t.Kind == CardTagKind.Kind);
        viewModel.ApplyTagFilterCommand.Execute(tag);

        Assert.True(viewModel.ShowEffects);
        Assert.True(viewModel.ShowInstruments);
        Assert.Equal(2, viewModel.Visible.Count);
    }
}
