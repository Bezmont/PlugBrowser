using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlugBrowser.Core.Analysis;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;

namespace PlugBrowser.App.ViewModels;

/// <summary>One file in the plugin list — a single installed copy of a plugin.</summary>
/// <remarks>
/// Deliberately flat: a flat grid sorts, filters and exports like a spreadsheet, which is how someone
/// auditing a few hundred installed files actually wants to work.
/// </remarks>
public sealed class PluginRowViewModel
{
    public PluginRowViewModel(PluginEntry entry, IReadOnlyList<PluginFormat> formatsInstalled)
    {
        Entry = entry;
        InstalledAs = string.Join(" + ", formatsInstalled.Select(Label));
    }

    public PluginEntry Entry { get; }

    public string Name => Entry.DisplayName;

    public string Format => Label(Entry.Format);

    /// <summary>Every format this product is installed in, e.g. "VST3 + VST2", or just "VST3".</summary>
    public string InstalledAs { get; }

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

    public string Screenshot => HasScreenshot ? "Yes" : "No";

    /// <summary>Where the file actually lives.</summary>
    public string Location => Entry.Path;

    private static string Label(PluginFormat format) => format switch
    {
        PluginFormat.Vst3 => "VST3",
        PluginFormat.Vst2 => "VST2",
        _ => "?",
    };
}

/// <summary>
/// A drop-down filter over one grid column, offering exactly the values present in the catalog.
/// </summary>
public sealed partial class ColumnFilter : ObservableObject
{
    private readonly Action _changed;

    public ColumnFilter(string any, Func<PluginRowViewModel, string> value,
        IEnumerable<PluginRowViewModel> rows, Action changed)
    {
        Any = any;
        Value = value;
        _changed = changed;
        Options = [any, .. rows.Select(value).Distinct().Order(StringComparer.OrdinalIgnoreCase)];
        _selected = any;
    }

    /// <summary>The "no filter" entry, e.g. "All vendors".</summary>
    public string Any { get; }

    public Func<PluginRowViewModel, string> Value { get; }

    public IReadOnlyList<string> Options { get; }

    [ObservableProperty]
    private string _selected;

    /// <summary>ComboBox pushes null while its items are rebuilt; that means "no filter", not "match null".</summary>
    partial void OnSelectedChanged(string value)
    {
        if (value is null)
        {
            Selected = Any;
            return;
        }
        _changed();
    }

    public bool IsActive => Selected != Any;

    public bool Matches(PluginRowViewModel row) => !IsActive || Value(row) == Selected;
}

/// <summary>
/// Every plugin the catalog knows about, one row per installed file, as a sortable, filterable grid.
/// </summary>
public sealed partial class PluginListViewModel : ObservableObject
{
    private readonly List<PluginRowViewModel> _all = [];

    public PluginListViewModel(CatalogStore store)
    {
        var entries = store.Load();

        // Grouped by product so each row can say which formats that plugin is installed in.
        var formatsByProduct = entries
            .GroupBy(e => DuplicateAnalyzer.NormalizeName(e.DisplayName), StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PluginFormat>)[.. g.Select(e => e.Format).Distinct().Order()],
                StringComparer.Ordinal);

        foreach (var entry in entries.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            string key = DuplicateAnalyzer.NormalizeName(entry.DisplayName);
            _all.Add(new PluginRowViewModel(entry, formatsByProduct.TryGetValue(key, out var formats)
                ? formats
                : [entry.Format]));
        }

        FormatFilter = new ColumnFilter("All formats", r => r.Format, _all, ApplyFilter);
        InstalledAsFilter = new ColumnFilter("Installed as: any", r => r.InstalledAs, _all, ApplyFilter);
        TypeFilter = new ColumnFilter("All types", r => r.Type, _all, ApplyFilter);
        VendorFilter = new ColumnFilter("All vendors", r => r.Vendor, _all, ApplyFilter);
        ArchitectureFilter = new ColumnFilter("All architectures", r => r.Architecture, _all, ApplyFilter);
        ScreenshotFilter = new ColumnFilter("Screenshot: any", r => r.Screenshot, _all, ApplyFilter);
        _filters = [FormatFilter, InstalledAsFilter, TypeFilter, VendorFilter, ArchitectureFilter, ScreenshotFilter];

        ApplyFilter();
    }

    private readonly ColumnFilter[] _filters;

    public ObservableCollection<PluginRowViewModel> Rows { get; } = [];

    public ColumnFilter FormatFilter { get; }
    public ColumnFilter InstalledAsFilter { get; }
    public ColumnFilter TypeFilter { get; }
    public ColumnFilter VendorFilter { get; }
    public ColumnFilter ArchitectureFilter { get; }
    public ColumnFilter ScreenshotFilter { get; }

    public int TotalCount => _all.Count;

    public string Summary
    {
        get
        {
            if (_all.Count == 0)
                return "No plugins detected yet — run a scan first.";

            int vst3 = _all.Count(r => r.Entry.Format == PluginFormat.Vst3);
            int vst2 = _all.Count(r => r.Entry.Format == PluginFormat.Vst2);
            return $"{_all.Count:N0} plugins detected — {vst3:N0} VST3, {vst2:N0} VST2.";
        }
    }

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Outcome of the last CSV export, shown beside the row count.</summary>
    [ObservableProperty]
    private string? _exportStatus;

    public bool HasAnyFilter => SearchText.Length > 0 || _filters.Any(f => f.IsActive);

    public string RowCountLabel => Rows.Count == _all.Count
        ? $"{Rows.Count:N0} {(Rows.Count == 1 ? "plugin" : "plugins")}"
        : $"{Rows.Count:N0} of {_all.Count:N0} plugins";

    private void ApplyFilter()
    {
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Rows.Clear();
        foreach (var row in _all.Where(Matches))
            Rows.Add(row);

        OnPropertyChanged(nameof(RowCountLabel));
        OnPropertyChanged(nameof(HasAnyFilter));

        bool Matches(PluginRowViewModel row) =>
            _filters.All(f => f.Matches(row)) &&
            terms.All(t => SearchableText(row).Any(s => s.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The text columns the search box looks in.</summary>
    private static IEnumerable<string> SearchableText(PluginRowViewModel row) =>
        [row.Name, row.Vendor, row.Version, row.Type, row.Format, row.InstalledAs, row.Location];

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ClearFilters()
    {
        // Each reset re-applies the filter; cheap at catalog sizes, and keeps the filters self-contained.
        foreach (var filter in _filters)
            filter.Selected = filter.Any;
        SearchText = string.Empty;
    }

    /// <summary>Column titles shared by both exports, in grid order.</summary>
    public static readonly IReadOnlyList<string> ExportHeaders =
        ["Name", "Format", "Installed as", "Type", "Vendor", "Version", "Architecture", "Size (MB)",
         "Screenshot", "Location"];

    public static string TabSeparatedHeader => string.Join('\t', ExportHeaders);

    private static IEnumerable<string> ExportFields(PluginRowViewModel r) =>
        [r.Name, r.Format, r.InstalledAs, r.Type, r.Vendor, r.Version, r.Architecture,
         r.SizeMb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), r.Screenshot, r.Location];

    /// <summary>Copies rows as tab-separated text, which pastes straight into a spreadsheet.</summary>
    /// <param name="rows">Rows in the order to export — the grid's current sort. Defaults to the
    /// filtered rows in catalog order.</param>
    public string ToTabSeparated(IEnumerable<PluginRowViewModel>? rows = null)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(TabSeparatedHeader);

        foreach (var r in rows ?? Rows)
            builder.AppendLine(string.Join('\t', ExportFields(r)));

        return builder.ToString();
    }

    /// <summary>The same rows as RFC 4180 CSV, for saving to a file.</summary>
    /// <remarks>Every field is quoted: plugin names and paths contain commas, and quoting all of them is
    /// simpler to trust than deciding field by field.</remarks>
    public string ToCsv(IEnumerable<PluginRowViewModel>? rows = null)
    {
        static string Quote(string field) => $"\"{field.Replace("\"", "\"\"")}\"";

        var builder = new System.Text.StringBuilder();
        builder.Append(string.Join(',', ExportHeaders.Select(Quote))).Append("\r\n");

        foreach (var r in rows ?? Rows)
            builder.Append(string.Join(',', ExportFields(r).Select(Quote))).Append("\r\n");

        return builder.ToString();
    }
}
