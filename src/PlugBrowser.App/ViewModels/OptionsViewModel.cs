using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlugBrowser.Core.Analysis;
using PlugBrowser.Core.Discovery;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Probing;
using PlugBrowser.Core.Storage;

namespace PlugBrowser.App.ViewModels;

/// <summary>
/// The Options dialog: which folders get scanned, and running the scan itself.
/// </summary>
/// <remarks>
/// Scanning lives here rather than on the main toolbar because the two are inseparable in practice —
/// the first question after a disappointing scan is always "did it look in the right places?", and
/// having the roots visible while the progress bar runs answers it without a second dialog.
/// </remarks>
public sealed partial class OptionsViewModel : ObservableObject
{
    private readonly CatalogStore _store;
    private CancellationTokenSource? _cancellation;

    public OptionsViewModel(CatalogStore store)
    {
        _store = store;
        _skipFailed = bool.TryParse(_store.GetSetting(SkipFailedSetting), out bool skip) && skip;
        LoadRoots();
    }

    private const string SkipFailedSetting = "scan.skipFailed";

    /// <summary>Raised when a scan finishes, so the main window can reload the catalog.</summary>
    public event EventHandler? CatalogChanged;

    public ObservableCollection<ScanRootViewModel> Roots { get; } = [];

    [ObservableProperty]
    private string _status = "Ready.";

    [ObservableProperty]
    private bool _isScanning;

    /// <summary>Progress as a percentage, for a determinate bar.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>True while the total is unknown — during discovery, which cannot be counted in advance.</summary>
    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private string _currentItem = string.Empty;

    /// <summary>Load each plugin to read real metadata and photograph its editor.</summary>
    /// <remarks>Off means discovery only: fast, completely safe, and still produces a full catalog —
    /// worth keeping available because probing is the part that can hang on a bad plugin.</remarks>
    [ObservableProperty]
    private bool _captureScreenshots = true;

    /// <summary>Re-probe plugins whose catalog entry is already current.</summary>
    [ObservableProperty]
    private bool _forceRescan;

    /// <summary>Don't retry plugins that failed last time and have not changed since.</summary>
    /// <remarks>Saved, unlike <see cref="ForceRescan"/>: that one is a one-off request, whereas choosing
    /// not to wait on the same broken plugins is a standing preference.</remarks>
    [ObservableProperty]
    private bool _skipFailed;

    partial void OnSkipFailedChanged(bool value) => _store.SetSetting(SkipFailedSetting, value.ToString());

    [ObservableProperty]
    private string? _lastSummary;

    /// <summary>True when the last scan is worth reporting in the dialog.</summary>
    public bool HasSummary => !string.IsNullOrEmpty(LastSummary);

    partial void OnLastSummaryChanged(string? value) => OnPropertyChanged(nameof(HasSummary));

    public string EnabledSummary
    {
        get
        {
            int enabled = Roots.Count(r => r.IsEnabled && r.Exists);
            return enabled == 0
                ? "No folders selected — the scan would find nothing."
                : $"{enabled} of {Roots.Count} folders will be scanned.";
        }
    }

    /// <summary>One-line account of how much of the catalog is the same plugin twice.</summary>
    /// <remarks>Surfaced beside the scan roots because that is where the user can act on it: the
    /// remedy is switching off the folders holding the redundant copies.</remarks>
    public string DuplicateSummary
    {
        get
        {
            var groups = DuplicateAnalyzer.FindCrossFormatDuplicates(_store.Load());
            if (groups.Count == 0)
                return "No plugins are installed in more than one format.";

            double megabytes = groups.Sum(g => g.RedundantBytes) / (1024.0 * 1024);
            return $"{groups.Count} plugins are installed in more than one format " +
                   $"({megabytes:N0} MB of redundant copies).";
        }
    }

    /// <summary>Rebuilds the root list from the platform and the stored user decisions.</summary>
    public void LoadRoots()
    {
        var counts = CountPluginsPerRoot();

        Roots.Clear();
        foreach (var root in ScanRootProvider.GetRoots(_store, counts))
            Roots.Add(new ScanRootViewModel(root, OnRootEnabledChanged));

        OnPropertyChanged(nameof(EnabledSummary));
        OnPropertyChanged(nameof(DuplicateSummary));
    }

    /// <summary>Attributes each catalogued plugin to the root it lives under, for the count column.</summary>
    /// <remarks>Longest matching root wins, so a plugin under a nested root is counted once, against the
    /// more specific folder rather than its parent.</remarks>
    private Dictionary<string, int> CountPluginsPerRoot()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var roots = ScanRootProvider.GetRoots(_store).Select(r => r.Path).ToList();

        foreach (var entry in _store.Load())
        {
            string? best = roots
                .Where(r => entry.Path.StartsWith(r + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Length)
                .FirstOrDefault();

            if (best is not null)
                counts[best] = counts.GetValueOrDefault(best) + 1;
        }
        return counts;
    }

    private void OnRootEnabledChanged(ScanRootViewModel root)
    {
        // Persisted immediately: a toggle is a decision, and losing it because the dialog was closed
        // with the window chrome rather than a button would be its own small bug.
        _store.SetScanRootEnabled(root.Path, root.IsEnabled, root.Origin);
        OnPropertyChanged(nameof(EnabledSummary));
    }

    /// <summary>Adds a folder the user picked. Returns false if it was already listed.</summary>
    public bool AddRoot(string path)
    {
        string normalized = ScanRootProvider.Normalize(path);
        if (normalized.Length == 0)
            return false;

        if (Roots.Any(r => r.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "That folder is already in the list.";
            return false;
        }

        _store.AddUserScanRoot(normalized);
        LoadRoots();
        Status = $"Added {normalized}.";
        return true;
    }

    [RelayCommand]
    private void RemoveRoot(ScanRootViewModel? root)
    {
        if (root is null || !root.CanRemove)
            return;

        _store.RemoveUserScanRoot(root.Path);
        LoadRoots();
        Status = $"Removed {root.Path}.";
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
            return;

        var roots = ScanRootProvider.EnabledRoots(Roots.Select(r => r.Root with { Enabled = r.IsEnabled }));
        if (roots.Count == 0)
        {
            Status = "Enable at least one folder that exists on disk.";
            return;
        }

        _cancellation = new CancellationTokenSource();
        IsScanning = true;
        IsIndeterminate = true;
        Progress = 0;
        LastSummary = null;

        try
        {
            var worker = new WorkerRunner(WorkerRunner.DefaultWorkerPath) { Capture = CaptureScreenshots };
            var service = new ScanService(_store, worker)
            {
                Probe = CaptureScreenshots,
                SkipUnchanged = !ForceRescan,
                SkipFailed = SkipFailed,
            };

            if (CaptureScreenshots && !worker.IsAvailable)
                Status = "Worker not found — scanning file metadata only.";

            var progress = new Progress<ScanStatus>(OnScanProgress);
            var summary = await service.ScanAsync(roots, progress, _cancellation.Token);

            LastSummary = summary.ToString();
            Status = "Scan complete.";
            LoadRoots();
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled — everything captured so far was kept.";
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            IsIndeterminate = false;
            CurrentItem = string.Empty;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void OnScanProgress(ScanStatus status)
    {
        CurrentItem = status.Action switch
        {
            ScanItemAction.Loading => $"Loading {status.CurrentItem}…",
            ScanItemAction.SkippedUnchanged => $"Existing, unchanged — skipped {status.CurrentItem}",
            ScanItemAction.SkippedFailed => $"Failed last time — skipped {status.CurrentItem}",
            _ => status.CurrentItem,
        };

        if (status.Fraction is { } fraction)
        {
            IsIndeterminate = false;
            Progress = fraction * 100;
        }
        else
        {
            IsIndeterminate = true;
        }

        Status = status.Phase switch
        {
            ScanPhase.Discovering =>
                $"Searching… {status.Completed} found: {status.Tally}",
            ScanPhase.Probing =>
                $"Found {status.Tally}. Checking {status.Completed}/{status.Total} · " +
                $"{status.Captured} screenshots · {status.Failed} failed",
            ScanPhase.Finished => "Scan complete.",
            ScanPhase.Cancelled => "Scan cancelled.",
            _ => Status,
        };
    }

    /// <summary>Builds the duplicates grid's view model over the same store.</summary>
    public DuplicatesViewModel CreateDuplicatesView() => new(_store);

    [RelayCommand]
    private void CancelScan()
    {
        _cancellation?.Cancel();
        Status = "Cancelling…";
    }
}
