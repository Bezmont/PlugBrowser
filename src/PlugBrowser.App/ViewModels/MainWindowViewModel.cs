using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlugBrowser.Core.Analysis;
using PlugBrowser.Core.Discovery;
using PlugBrowser.Core.Probing;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;

namespace PlugBrowser.App.ViewModels;

/// <summary>The browser window: a filtered view over the catalog, plus the scan that fills it.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly CatalogStore _store;
    private readonly List<PluginItemViewModel> _all = [];

    /// <summary>Set while several filters are being restored at once, so the projection runs once at
    /// the end rather than after each one.</summary>
    private bool _suppressFilters;

    private CancellationTokenSource? _scanCancellation;

    public MainWindowViewModel(CatalogStore store) => _store = store;

    /// <summary>Every plugin passing the current filters, in display order.</summary>
    public ObservableCollection<PluginItemViewModel> Visible { get; } = [];

    /// <summary>Vendors present in the catalog, for the vendor facet. Index 0 is always
    /// <see cref="AnyVendor"/>, meaning no vendor filter.</summary>
    /// <remarks>Seeded with the sentinel rather than left empty: the ComboBox binds before the catalog
    /// is loaded, and a bound ComboBox with an empty list discards its selection and does not take it
    /// back when items arrive — which rendered the facet permanently blank.</remarks>
    public ObservableCollection<string> Vendors { get; } = [AnyVendor];

    public const string AnyVendor = "All vendors";

    /// <summary>Category facets, derived from the tags the catalogued plugins actually report.</summary>
    public ObservableCollection<CategoryFilterViewModel> Categories { get; } = [];

    /// <summary>Everything currently selected in the gallery. Multi-selection drives the bulk rescan.</summary>
    public ObservableCollection<PluginItemViewModel> SelectedItems { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedVendor = AnyVendor;

    [ObservableProperty]
    private bool _showVst3 = true;

    [ObservableProperty]
    private bool _showVst2 = true;

    [ObservableProperty]
    private bool _showEffects = true;

    [ObservableProperty]
    private bool _showInstruments = true;

    [ObservableProperty]
    private bool _favoritesOnly;

    [ObservableProperty]
    private bool _withImagesOnly;

    [ObservableProperty]
    private bool _problemsOnly;

    /// <summary>Show only plugins that exist in a single format.</summary>
    /// <remarks>Sits with the format facets because that is what it is about: hiding the VST2 copy of a
    /// plugin already installed as VST3. On the development machine that is 292 of 434 entries.</remarks>
    [ObservableProperty]
    private bool _uniqueOnly;

    /// <summary>Normalised names installed in more than one format, rebuilt whenever the catalog is.</summary>
    private IReadOnlySet<string> _crossFormatKeys = new HashSet<string>();

    [ObservableProperty]
    private PluginItemViewModel? _selected;

    [ObservableProperty]
    private string _status = "Ready.";

    [ObservableProperty]
    private bool _isScanning;

    /// <summary>True while the zoomed screenshot overlay is showing.</summary>
    [ObservableProperty]
    private bool _isImageZoomed;

    /// <summary>Progress of a rescan started from the gallery, 0-100.</summary>
    [ObservableProperty]
    private double _rescanProgress;

    /// <summary>Number of plugins shown out of the number known, for the status line — or, while
    /// selected plugins are being reloaded, how far through the selection the reload is.</summary>
    public string CountSummary =>
        _reloadTotal > 0 ? ReloadCountLabel(_reloadCompleted, _reloadTotal)
        : _all.Count == 0 ? "No plugins catalogued yet — run a scan."
        : $"{Visible.Count} of {_all.Count} plugins";

    /// <summary>Progress through a reload of selected plugins; zero total when none is running.</summary>
    private int _reloadCompleted, _reloadTotal;

    /// <summary>
    /// "2 of 5 selected": the plugin currently being reloaded, counted against the selection.
    /// </summary>
    /// <param name="completed">Plugins finished so far; the one in progress is the next.</param>
    /// <param name="total">Plugins being reloaded.</param>
    /// <remarks>Clamped because the final progress report arrives with every plugin completed, which
    /// would otherwise read "6 of 5".</remarks>
    public static string ReloadCountLabel(int completed, int total) =>
        $"{Math.Clamp(completed + 1, 1, Math.Max(total, 1))} of {total} selected";

    /// <summary>Headline count for the current filtered view.</summary>
    public string ResultCount => Visible.Count == 1 ? "1 plugin" : $"{Visible.Count:N0} plugins";

    /// <summary>Drives the clear-search affordance inside the search box.</summary>
    public bool HasSearchText => SearchText.Length > 0;

    // Counts for the fixed facets, each computed with its own group's selections excluded.
    [ObservableProperty] private int _vst3Count;
    [ObservableProperty] private int _uniqueCount;
    [ObservableProperty] private int _duplicateCount;
    [ObservableProperty] private int _vst2Count;
    [ObservableProperty] private int _effectCount;
    [ObservableProperty] private int _instrumentCount;
    [ObservableProperty] private int _favoriteCount;
    [ObservableProperty] private int _withImageCount;
    [ObservableProperty] private int _problemCount;

    public string Vst3Label => $"VST3 ({Vst3Count})";
    public string UniqueLabel => $"Unique only ({UniqueCount})";
    public string Vst2Label => $"VST2 ({Vst2Count})";
    public string EffectsLabel => $"Effects ({EffectCount})";
    public string InstrumentsLabel => $"Instruments ({InstrumentCount})";
    public string FavoritesLabel => $"Favourites only ({FavoriteCount})";
    public string WithImagesLabel => $"With screenshots ({WithImageCount})";
    public string ProblemsLabel => $"Problems only ({ProblemCount})";

    partial void OnVst3CountChanged(int value) => OnPropertyChanged(nameof(Vst3Label));
    partial void OnUniqueCountChanged(int value) => OnPropertyChanged(nameof(UniqueLabel));
    partial void OnVst2CountChanged(int value) => OnPropertyChanged(nameof(Vst2Label));
    partial void OnEffectCountChanged(int value) => OnPropertyChanged(nameof(EffectsLabel));
    partial void OnInstrumentCountChanged(int value) => OnPropertyChanged(nameof(InstrumentsLabel));
    partial void OnFavoriteCountChanged(int value) => OnPropertyChanged(nameof(FavoritesLabel));
    partial void OnWithImageCountChanged(int value) => OnPropertyChanged(nameof(WithImagesLabel));
    partial void OnProblemCountChanged(int value) => OnPropertyChanged(nameof(ProblemsLabel));

    // ---- multi-selection ---------------------------------------------------------------------

    /// <summary>Replaces the selection. Called by the view as the gallery selection changes.</summary>
    public void SetSelection(IEnumerable<PluginItemViewModel> items)
    {
        SelectedItems.Clear();
        foreach (var item in items)
            SelectedItems.Add(item);

        if (SelectedItems.Count == 1)
            Selected = SelectedItems[0];

        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultipleSelected));
        OnPropertyChanged(nameof(HasSingleSelected));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CommonVendor));
        OnPropertyChanged(nameof(CommonFormat));
        OnPropertyChanged(nameof(CommonKind));
        OnPropertyChanged(nameof(CommonCategory));
        OnPropertyChanged(nameof(SelectionImageCount));
        RescanSelectedCommand.NotifyCanExecuteChanged();
    }

    public int SelectionCount => SelectedItems.Count;

    public bool HasMultipleSelected => SelectedItems.Count > 1;

    public bool HasSingleSelected => SelectedItems.Count <= 1;

    public string SelectionSummary => $"{SelectedItems.Count} plugins selected";

    /// <summary>Shared vendor across the selection, or a placeholder when they differ.</summary>
    /// <remarks>Showing the first item's value for a mixed selection would be an outright lie, so a
    /// trait that is not actually shared reports how many distinct values there are instead.</remarks>
    public string CommonVendor => Common(p => p.Vendor);

    public string CommonFormat => Common(p => p.FormatLabel);

    public string CommonKind => Common(p => p.KindLabel);

    public string CommonCategory => Common(p => p.Category ?? "—");

    /// <summary>How many of the selected plugins already have a screenshot.</summary>
    public string SelectionImageCount =>
        $"{SelectedItems.Count(p => p.HasImage)} of {SelectedItems.Count} have screenshots";

    private string Common(Func<PluginItemViewModel, string> selector)
    {
        if (SelectedItems.Count == 0)
            return "—";

        var distinct = SelectedItems.Select(selector).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count == 1 ? distinct[0] : $"({distinct.Count} different)";
    }

    // ---- zoomed screenshot --------------------------------------------------------------------

    /// <summary>Opens the full-size overlay, if the selected plugin actually has an image.</summary>
    [RelayCommand]
    private void ZoomImage()
    {
        if (Selected?.HasImage == true)
            IsImageZoomed = true;
    }

    [RelayCommand]
    private void CloseZoom() => IsImageZoomed = false;

    // ---- rescan the selection -----------------------------------------------------------------

    /// <summary>
    /// Reloads and recaptures the selected plugins.
    /// </summary>
    /// <remarks>
    /// The case this exists for: a plugin that showed a registration or trial prompt when it was first
    /// captured, so the stored screenshot is a picture of that dialog. After authorising it the user
    /// needs those specific plugins recaptured — without re-running a scan over the whole library, which
    /// takes twenty minutes on the machine this was built against.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRescanSelected))]
    private async Task RescanSelectedAsync()
    {
        if (IsScanning || SelectedItems.Count == 0)
            return;

        // VST2 is catalogued from its file alone and is never loaded, so it cannot be recaptured.
        var targets = SelectedItems
            .Where(item => item.Format == PluginFormat.Vst3)
            .Select(item => item.Entry)
            .ToList();

        if (targets.Count == 0)
        {
            Status = "Only VST3 plugins can be reloaded — VST2 is catalogued from its file alone.";
            return;
        }

        var selectedPaths = SelectedItems.Select(i => i.Path).ToList();

        int skippedVst2 = SelectedItems.Count - targets.Count;

        _scanCancellation = new CancellationTokenSource();
        IsScanning = true;
        RescanProgress = 0;
        SetReloadProgress(0, targets.Count);

        try
        {
            var worker = new WorkerRunner(WorkerRunner.DefaultWorkerPath);
            if (!worker.IsAvailable)
            {
                Status = "Worker not found — cannot reload plugins.";
                return;
            }

            var service = new ScanService(_store, worker);
            var progress = new Progress<ScanStatus>(status =>
            {
                RescanProgress = (status.Fraction ?? 0) * 100;
                SetReloadProgress(status.Completed, targets.Count);
                Status = $"Reloading {ReloadCountLabel(status.Completed, targets.Count)}: {status.CurrentItem}";
            });

            var summary = await service.RescanAsync(targets, progress, _scanCancellation.Token);

            // Cleared before reloading the catalog, so the counter returns to the gallery totals.
            SetReloadProgress(0, 0);
            LoadCatalog();
            RestoreSelection(selectedPaths);
            Status = $"Reloaded {targets.Count} selected plugins — " +
                     $"{summary.Captured} screenshots, {summary.Failed} failed" +
                     (skippedVst2 > 0 ? $"; {skippedVst2} VST2 skipped (catalogued from file only)." : ".");
        }
        catch (OperationCanceledException)
        {
            SetReloadProgress(0, 0);
            LoadCatalog();
            RestoreSelection(selectedPaths);
            Status = "Reload cancelled — anything already recaptured was kept.";
        }
        catch (Exception ex)
        {
            Status = $"Reload failed: {ex.Message}";
        }
        finally
        {
            SetReloadProgress(0, 0);
            IsScanning = false;
            RescanProgress = 0;
            _scanCancellation?.Dispose();
            _scanCancellation = null;
        }
    }

    private void SetReloadProgress(int completed, int total)
    {
        _reloadCompleted = completed;
        _reloadTotal = total;
        OnPropertyChanged(nameof(CountSummary));
    }

    private bool CanRescanSelected() => SelectedItems.Count > 0 && !IsScanning;

    [RelayCommand]
    private void CancelRescan() => _scanCancellation?.Cancel();

    /// <summary>Re-selects by path after a reload, since the view models are rebuilt from scratch.</summary>
    private void RestoreSelection(IReadOnlyList<string> paths)
    {
        var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetSelection(Visible.Where(p => wanted.Contains(p.Path)));
        SelectionRestored?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after a reload so the view can re-apply the selection to the gallery.</summary>
    public event EventHandler? SelectionRestored;

    // Any filter change re-runs the same projection; these hooks are what the source generator calls.
    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedVendorChanged(string value)
    {
        // A ComboBox can write a null selection back when its items change under it. Coerce to the
        // "no filter" sentinel rather than carrying a null into the filter predicate.
        if (value is null)
        {
            SelectedVendor = AnyVendor;
            return;
        }
        ApplyFilters();
    }
    partial void OnShowVst3Changed(bool value) => ApplyFilters();
    partial void OnShowVst2Changed(bool value) => ApplyFilters();
    partial void OnShowEffectsChanged(bool value) => ApplyFilters();
    partial void OnShowInstrumentsChanged(bool value) => ApplyFilters();
    partial void OnFavoritesOnlyChanged(bool value) => ApplyFilters();
    partial void OnWithImagesOnlyChanged(bool value) => ApplyFilters();
    partial void OnProblemsOnlyChanged(bool value) => ApplyFilters();
    partial void OnUniqueOnlyChanged(bool value) => ApplyFilters();

    /// <summary>Loads the catalog from the database into the list. Cheap — no plugin is touched.</summary>
    public void LoadCatalog()
    {
        var favorites = _store.GetFavorites();

        _all.Clear();
        foreach (var entry in _store.Load())
            _all.Add(new PluginItemViewModel(entry, favorites.Contains(entry.Path)));

        _crossFormatKeys = DuplicateAnalyzer.CrossFormatKeys(_all.Select(p => p.Entry));

        RebuildVendorList();
        RebuildCategoryList();
        ApplyFilters();

        int withImages = _all.Count(p => p.HasImage);
        Status = _all.Count == 0
            ? "No plugins catalogued yet — open Scan & Options to run a scan."
            : $"{_all.Count} plugins · {withImages} with screenshots";
    }

    /// <summary>Saves the filter state and window geometry so the app opens as it was left.</summary>
    /// <remarks>Stored in the catalog database rather than a separate settings file: it is already
    /// open, already per-user, and keeping one store means one thing to back up or delete.</remarks>
    public void SaveState(double width, double height, bool maximized)
    {
        _store.SetSetting("filter.vst3", ShowVst3.ToString());
        _store.SetSetting("filter.vst2", ShowVst2.ToString());
        _store.SetSetting("filter.effects", ShowEffects.ToString());
        _store.SetSetting("filter.instruments", ShowInstruments.ToString());
        _store.SetSetting("filter.favorites", FavoritesOnly.ToString());
        _store.SetSetting("filter.withImages", WithImagesOnly.ToString());
        _store.SetSetting("filter.problems", ProblemsOnly.ToString());
        _store.SetSetting("filter.vendor", SelectedVendor);

        _store.SetSetting("window.width", width.ToString("0"));
        _store.SetSetting("window.height", height.ToString("0"));
        _store.SetSetting("window.maximized", maximized.ToString());
        _store.SetSetting("window.detailWidth", DetailPaneWidth.ToString("0"));
    }

    /// <summary>Narrowest the detail pane may be — its original fixed width, below which the metadata
    /// grid starts wrapping badly.</summary>
    public const double MinDetailPaneWidth = 340;

    /// <summary>Widest the detail pane may be restored at, so a width saved on a large monitor cannot
    /// swallow the gallery on a smaller one.</summary>
    public const double MaxDetailPaneWidth = 1400;

    /// <summary>Width of the resizable detail pane; the view reads it on open and writes it on close.</summary>
    public double DetailPaneWidth
    {
        get => _detailPaneWidth;
        set => _detailPaneWidth = Math.Clamp(value, MinDetailPaneWidth, MaxDetailPaneWidth);
    }

    private double _detailPaneWidth = MinDetailPaneWidth;

    /// <summary>Restores the filter state saved by <see cref="SaveState"/>.</summary>
    /// <remarks>Applied with notifications suppressed so restoring seven filters does not re-run the
    /// projection seven times over the whole catalog.</remarks>
    public void RestoreState()
    {
        _suppressFilters = true;
        try
        {
            ShowVst3 = ReadBool("filter.vst3", true);
            ShowVst2 = ReadBool("filter.vst2", true);
            ShowEffects = ReadBool("filter.effects", true);
            ShowInstruments = ReadBool("filter.instruments", true);
            FavoritesOnly = ReadBool("filter.favorites", false);
            WithImagesOnly = ReadBool("filter.withImages", false);
            ProblemsOnly = ReadBool("filter.problems", false);

            // Only restore a vendor that is still installed, or the window would open empty.
            string? vendor = _store.GetSetting("filter.vendor");
            if (vendor is not null && Vendors.Contains(vendor))
                SelectedVendor = vendor;

            if (double.TryParse(_store.GetSetting("window.detailWidth"), out double detailWidth))
                DetailPaneWidth = detailWidth;
        }
        finally
        {
            _suppressFilters = false;
        }
        ApplyFilters();
    }

    /// <summary>Saved window size, when there is one.</summary>
    public (double Width, double Height, bool Maximized)? SavedWindowGeometry
    {
        get
        {
            if (!double.TryParse(_store.GetSetting("window.width"), out double width) ||
                !double.TryParse(_store.GetSetting("window.height"), out double height))
                return null;

            // Guard against a geometry saved on a monitor that is no longer attached.
            if (width < 600 || height < 400 || width > 10000 || height > 10000)
                return null;

            return (width, height, ReadBool("window.maximized", false));
        }
    }

    private bool ReadBool(string key, bool fallback) =>
        bool.TryParse(_store.GetSetting(key), out bool value) ? value : fallback;

    [RelayCommand]
    private void ToggleFavorite(PluginItemViewModel? item)
    {
        if (item is null)
            return;

        item.IsFavorite = !item.IsFavorite;
        _store.SetFavorite(item.Path, item.IsFavorite);

        if (FavoritesOnly)
            ApplyFilters();
    }

    /// <summary>Opens the plugin's folder in Explorer with the file selected.</summary>
    [RelayCommand]
    private void RevealInExplorer(PluginItemViewModel? item)
    {
        if (item is null)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                // Quoted because plugin paths routinely contain spaces.
                Arguments = $"/select,\"{item.Path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status = $"Could not open Explorer: {ex.Message}";
        }
    }

    /// <summary>
    /// Recomputes every facet count against the other active filters.
    /// </summary>
    /// <remarks>
    /// Counts are only honest if they move with the rest of the query: choosing a vendor, or typing in
    /// the search box, should immediately show how many delays or reverbs remain within that narrower
    /// set. Each group is counted with its own selections excluded, so the numbers always read as
    /// "what I would get if I ticked this".
    /// </remarks>
    private void UpdateFacetCounts(string[] terms, IReadOnlySet<string> selectedCategories)
    {
        var forCategories = _all.Where(i => Matches(i, FacetGroup.Category, terms, selectedCategories)).ToList();
        var forFormat = _all.Where(i => Matches(i, FacetGroup.Format, terms, selectedCategories)).ToList();
        var forKind = _all.Where(i => Matches(i, FacetGroup.Kind, terms, selectedCategories)).ToList();
        var forOther = _all.Where(i => Matches(i, FacetGroup.Other, terms, selectedCategories)).ToList();

        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in forCategories)
        {
            foreach (var tag in item.Tags)
            {
                if (!CategoryTags.IsFacetTag(tag))
                    continue;
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
            }
        }

        foreach (var category in Categories)
            category.Count = tagCounts.GetValueOrDefault(category.Name);

        Vst3Count = forFormat.Count(i => i.Format == PluginFormat.Vst3);
        Vst2Count = forFormat.Count(i => i.Format == PluginFormat.Vst2);
        UniqueCount = forFormat.Count(i => !_crossFormatKeys.Contains(i.DuplicateKey));
        DuplicateCount = _all.Count(i => _crossFormatKeys.Contains(i.DuplicateKey));
        EffectCount = forKind.Count(i => i.Kind == PluginKind.Effect);
        InstrumentCount = forKind.Count(i => i.Kind == PluginKind.Instrument);
        FavoriteCount = forOther.Count(i => i.IsFavorite);
        WithImageCount = forOther.Count(i => i.HasImage);
        ProblemCount = forOther.Count(i => i.IsProblem);
    }

    /// <summary>
    /// Rebuilds the category facets from the tags present in the catalog.
    /// </summary>
    /// <remarks>
    /// <para>Which tags count is decided by <see cref="CategoryTags.IsFacetTag"/>, shared with the card
    /// tags so the two always agree.</para>
    /// <para>Selections are carried across a rebuild so a rescan does not silently widen the view.</para>
    /// </remarks>
    private void RebuildCategoryList()
    {
        var previouslySelected = Categories
            .Where(c => c.IsSelected)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _all)
        {
            foreach (var tag in item.Tags)
            {
                if (!CategoryTags.IsFacetTag(tag))
                    continue;
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        Categories.Clear();
        foreach (var (name, count) in counts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            Categories.Add(new CategoryFilterViewModel(name, count, ApplyFilters)
            {
                IsSelected = previouslySelected.Contains(name),
            });
        }
    }

    /// <summary>True when anything is narrowing the view, so the Clear button can show itself.</summary>
    public bool HasAnyFilter =>
        SearchText.Length > 0 || !ShowVst3 || !ShowVst2 || !ShowEffects || !ShowInstruments ||
        FavoritesOnly || WithImagesOnly || ProblemsOnly || UniqueOnly ||
        !SelectedVendor.Equals(AnyVendor, StringComparison.Ordinal) || HasCategoryFilter;

    /// <summary>Resets every filter, including the search box, back to showing the whole catalog.</summary>
    /// <remarks>Applied with notifications suppressed so the projection runs once at the end rather than
    /// eleven times over the whole catalog.</remarks>
    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilters = true;
        try
        {
            SearchText = string.Empty;
            ShowVst3 = true;
            ShowVst2 = true;
            ShowEffects = true;
            ShowInstruments = true;
            FavoritesOnly = false;
            WithImagesOnly = false;
            ProblemsOnly = false;
            UniqueOnly = false;
            SelectedVendor = AnyVendor;

            foreach (var category in Categories)
                category.IsSelected = false;
        }
        finally
        {
            _suppressFilters = false;
        }
        ApplyFilters();
    }

    /// <summary>Clears just the search box, leaving the facets alone.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>Clears every category facet.</summary>
    [RelayCommand]
    private void ClearCategories()
    {
        _suppressFilters = true;
        try
        {
            foreach (var category in Categories)
                category.IsSelected = false;
        }
        finally
        {
            _suppressFilters = false;
        }
        ApplyFilters();
    }

    /// <summary>
    /// Adds the filter behind a clicked card tag to the ones already active.
    /// </summary>
    /// <remarks>
    /// <para>Each tag drives the same control the user would otherwise set by hand, so the filter pane
    /// always shows why the list looks the way it does: a format or type tag narrows its checkbox pair
    /// to that one value, a category tag ticks its checkbox alongside any already ticked (categories
    /// combine as "any of these"), and a vendor tag selects that vendor. Every other filter is left
    /// exactly as it was.</para>
    /// <para>Applied with refreshing suppressed, so switching a checkbox pair re-runs the projection once.</para>
    /// </remarks>
    [RelayCommand]
    private void ApplyTagFilter(CardTag? tag)
    {
        if (tag is null || !tag.IsClickable)
            return;

        _suppressFilters = true;
        try
        {
            switch (tag.Kind)
            {
                case CardTagKind.Format when Enum.TryParse<PluginFormat>(tag.Value, out var format):
                    ShowVst3 = format == PluginFormat.Vst3;
                    ShowVst2 = format == PluginFormat.Vst2;
                    break;

                case CardTagKind.Kind when Enum.TryParse<PluginKind>(tag.Value, out var kind)
                                           && kind != PluginKind.Unknown:
                    ShowEffects = kind == PluginKind.Effect;
                    ShowInstruments = kind == PluginKind.Instrument;
                    break;

                case CardTagKind.Category:
                    var facet = Categories.FirstOrDefault(c =>
                        c.Name.Equals(tag.Value, StringComparison.OrdinalIgnoreCase));
                    if (facet is not null)
                        facet.IsSelected = true;
                    break;

                case CardTagKind.Vendor:
                    // The dropdown's own entry, so the ComboBox shows it as selected.
                    var vendor = Vendors.FirstOrDefault(v =>
                        v.Equals(tag.Value, StringComparison.OrdinalIgnoreCase));
                    if (vendor is not null)
                        SelectedVendor = vendor;
                    break;
            }
        }
        finally
        {
            _suppressFilters = false;
        }
        ApplyFilters();
    }

    /// <summary>True when at least one category facet is active, so the Clear link can show.</summary>
    public bool HasCategoryFilter => Categories.Any(c => c.IsSelected);

    /// <summary>Reconciles the vendor facet with the catalog, in place.</summary>
    /// <remarks>Deliberately not Clear-then-Add. Emptying a bound collection makes the ComboBox null its
    /// selection, and it will not re-adopt the value once the list refills, so the facet renders blank.
    /// Mutating around the always-present sentinel at index 0 keeps a valid selection at every moment.</remarks>
    private void RebuildVendorList()
    {
        var wanted = _all
            .Select(p => p.Vendor)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Drop vendors that are no longer installed, leaving the sentinel at index 0 alone.
        for (int i = Vendors.Count - 1; i >= 1; i--)
        {
            if (!wanted.Contains(Vendors[i], StringComparer.OrdinalIgnoreCase))
                Vendors.RemoveAt(i);
        }

        // Insert newcomers at their sorted position.
        for (int i = 0; i < wanted.Count; i++)
        {
            int target = i + 1;
            if (target >= Vendors.Count ||
                !Vendors[target].Equals(wanted[i], StringComparison.OrdinalIgnoreCase))
                Vendors.Insert(target, wanted[i]);
        }

        // The user's vendor survives a rescan unless it was uninstalled.
        if (!Vendors.Contains(SelectedVendor))
            SelectedVendor = AnyVendor;
    }

    /// <summary>The facet groups, so a count can be computed with its own group excluded.</summary>
    private enum FacetGroup
    {
        None,
        Format,
        Kind,
        Vendor,
        Category,
        Other,
    }

    /// <summary>
    /// Whether an item passes the filters, optionally ignoring one facet group.
    /// </summary>
    /// <remarks>
    /// The exclusion is what makes the counts useful. A facet count has to answer "how many would I get
    /// if I ticked this?", which means applying every *other* active filter but not the group the option
    /// belongs to. Counting with the group applied would show each selected option's own count and zero
    /// beside all its siblings, making the list useless for narrowing further.
    /// </remarks>
    private bool Matches(PluginItemViewModel item, FacetGroup ignore, string[] terms,
        IReadOnlySet<string> selectedCategories)
    {
        if (ignore != FacetGroup.Format)
        {
            if (item.Format == PluginFormat.Vst3 && !ShowVst3) return false;
            if (item.Format == PluginFormat.Vst2 && !ShowVst2) return false;
            if (UniqueOnly && _crossFormatKeys.Contains(item.DuplicateKey)) return false;
        }

        if (ignore != FacetGroup.Kind)
        {
            // Unclassified plugins are shown unless both kind filters are off, since an unprobed entry
            // has no kind yet and hiding them would empty the window.
            if (item.Kind == PluginKind.Effect && !ShowEffects) return false;
            if (item.Kind == PluginKind.Instrument && !ShowInstruments) return false;
            if (item.Kind == PluginKind.Unknown && !ShowEffects && !ShowInstruments) return false;
        }

        if (ignore != FacetGroup.Other)
        {
            if (FavoritesOnly && !item.IsFavorite) return false;
            if (WithImagesOnly && !item.HasImage) return false;
            if (ProblemsOnly && !item.IsProblem) return false;
        }

        if (ignore != FacetGroup.Vendor &&
            !SelectedVendor.Equals(AnyVendor, StringComparison.Ordinal) &&
            !SelectedVendor.Equals(item.Vendor, StringComparison.OrdinalIgnoreCase))
            return false;

        // Categories combine as OR: ticking Delay and Reverb asks for either, which is what a facet
        // list reads as. Requiring both would return almost nothing, since a plugin rarely claims two.
        if (ignore != FacetGroup.Category && selectedCategories.Count > 0 &&
            !item.Tags.Any(t => selectedCategories.Contains(t)))
            return false;

        // Search always applies. It is not a facet the user ticks, it is the frame everything sits in.
        return terms.All(item.MatchesTerm);
    }

    private void ApplyFilters()
    {
        if (_suppressFilters)
            return;

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selectedCategories = Categories
            .Where(c => c.IsSelected)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Visible.Clear();
        foreach (var item in _all
                     .Where(i => Matches(i, FacetGroup.None, terms, selectedCategories))
                     .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            Visible.Add(item);

        UpdateFacetCounts(terms, selectedCategories);

        // Keep a selection alive across filter changes: hold the current one if it survived the filter,
        // otherwise fall to the first result so the detail pane always has something to show.
        if (Selected is null || !Visible.Contains(Selected))
            Selected = Visible.FirstOrDefault();

        OnPropertyChanged(nameof(CountSummary));
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(HasCategoryFilter));
        OnPropertyChanged(nameof(HasAnyFilter));
        OnPropertyChanged(nameof(HasSearchText));

    }
}
