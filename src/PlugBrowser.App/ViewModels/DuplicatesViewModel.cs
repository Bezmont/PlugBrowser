using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlugBrowser.Core.Analysis;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;

namespace PlugBrowser.App.ViewModels;

/// <summary>One file in the duplicates grid — a single installed copy of a plugin.</summary>
/// <remarks>
/// Deliberately flat rather than a tree of groups: a flat grid sorts and scans like a spreadsheet, which
/// is how someone auditing a few hundred redundant files actually wants to work. The grouping survives
/// as <see cref="ProductName"/>, which repeats across the rows of one product.
/// </remarks>
public sealed class DuplicateRowViewModel
{
    public DuplicateRowViewModel(DuplicateGroup group, PluginEntry entry)
    {
        ProductName = group.Name;
        Entry = entry;
        FormatsInGroup = string.Join(" + ", group.Formats.Select(Label));
    }

    public PluginEntry Entry { get; }

    /// <summary>Name shared by every row of this product, so sorting by it keeps copies together.</summary>
    public string ProductName { get; }

    public string Name => Entry.DisplayName;

    public string Format => Label(Entry.Format);

    /// <summary>Which formats this product is installed in, e.g. "VST3 + VST2".</summary>
    public string FormatsInGroup { get; }

    public string Type => Entry.Classes.Count > 0
        ? Entry.Classes[0].Kind switch
        {
            PluginKind.Instrument => "Instrument",
            PluginKind.Effect => "Effect",
            _ => "Unclassified",
        }
        : "Unclassified";

    public string Vendor => Entry.DisplayVendor ?? "Unknown";

    public string Version => Entry.Classes.FirstOrDefault()?.Version ?? Entry.FileVersion ?? "—";

    public string Architecture => Entry.Architecture switch
    {
        PluginArchitecture.X64 => "64-bit",
        PluginArchitecture.X86 => "32-bit",
        PluginArchitecture.Arm64 => "ARM64",
        _ => "—",
    };

    public double SizeMb => Math.Round(Entry.FileSize / (1024.0 * 1024), 1);

    public bool HasScreenshot => Entry.Classes.Any(c => c.Images.Count > 0);

    /// <summary>Where the file actually lives — the column that tells the user which scan root to switch
    /// off if they want the copy gone.</summary>
    public string Location => Entry.Path;

    /// <summary>True for the copy in the format worth keeping, so the grid can de-emphasise the rest.</summary>
    public bool IsPreferredFormat => Entry.Format == PluginFormat.Vst3;

    private static string Label(PluginFormat format) => format switch
    {
        PluginFormat.Vst3 => "VST3",
        PluginFormat.Vst2 => "VST2",
        _ => "?",
    };
}

/// <summary>
/// The redundancy audit: every plugin installed in more than one format, listed file by file.
/// </summary>
/// <remarks>
/// Individually these are invisible; in aggregate they are most of the catalog. Listing them with their
/// sizes and paths is what turns "should I switch off my VST2 folders?" into a decision the user can
/// actually make.
/// </remarks>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    private readonly List<DuplicateRowViewModel> _all = [];

    public DuplicatesViewModel(CatalogStore store)
    {
        var entries = store.Load();
        var groups = DuplicateAnalyzer.FindCrossFormatDuplicates(entries);

        foreach (var group in groups)
        {
            foreach (var entry in group.Entries)
                _all.Add(new DuplicateRowViewModel(group, entry));
        }

        ProductCount = groups.Count;
        FileCount = _all.Count;
        RedundantMb = Math.Round(groups.Sum(g => g.RedundantBytes) / (1024.0 * 1024), 0);
        UniqueCount = entries.Count - _all.Count;

        // Folders holding a redundant copy — exactly the roots worth switching off.
        RedundantFolders = [.. _all
            .Where(r => !r.IsPreferredFormat)
            .Select(r => Path.GetDirectoryName(r.Location) ?? r.Location)
            .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}  ({g.Count()} {(g.Count() == 1 ? "file" : "files")})")];

        ApplyFilter();
    }

    public ObservableCollection<DuplicateRowViewModel> Rows { get; } = [];

    public int ProductCount { get; }
    public int FileCount { get; }
    public int UniqueCount { get; }
    public double RedundantMb { get; }

    /// <summary>Folders that contain a redundant copy, most first.</summary>
    public IReadOnlyList<string> RedundantFolders { get; }

    public string Summary => ProductCount == 0
        ? "No plugins are installed in more than one format."
        : $"{ProductCount} plugins are installed in more than one format — " +
          $"{FileCount} files, {RedundantMb:N0} MB of which is the redundant copy.";

    public string UniqueSummary => $"{UniqueCount} plugins exist in one format only.";

    public bool HasDuplicates => ProductCount > 0;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Hide the copy in the preferred format, leaving only what could be removed.</summary>
    [ObservableProperty]
    private bool _redundantOnly;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnRedundantOnlyChanged(bool value) => ApplyFilter();

    public string RowCountLabel => Rows.Count == 1 ? "1 row" : $"{Rows.Count:N0} rows";

    private void ApplyFilter()
    {
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Rows.Clear();
        foreach (var row in _all.Where(Matches))
            Rows.Add(row);

        OnPropertyChanged(nameof(RowCountLabel));

        bool Matches(DuplicateRowViewModel row)
        {
            if (RedundantOnly && row.IsPreferredFormat)
                return false;

            return terms.All(t =>
                row.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                row.Vendor.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                row.Location.Contains(t, StringComparison.OrdinalIgnoreCase));
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>Copies the grid as tab-separated text, which pastes straight into a spreadsheet.</summary>
    public string ToTabSeparated()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Product\tName\tFormat\tFormats installed\tType\tVendor\tVersion\tArchitecture\tSize (MB)\tScreenshot\tLocation");

        foreach (var r in Rows)
        {
            builder.AppendLine(string.Join('\t',
                r.ProductName, r.Name, r.Format, r.FormatsInGroup, r.Type, r.Vendor, r.Version,
                r.Architecture, r.SizeMb.ToString("0.0"), r.HasScreenshot ? "yes" : "no", r.Location));
        }

        return builder.ToString();
    }
}
