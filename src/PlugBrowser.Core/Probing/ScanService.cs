using System.Security.Cryptography;
using System.Text;
using PlugBrowser.Core.Discovery;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;

namespace PlugBrowser.Core.Probing;

/// <summary>Which stage a scan is in, so the UI can label the progress bar meaningfully.</summary>
public enum ScanPhase
{
    /// <summary>Walking folders. Fast, and no plugin is loaded.</summary>
    Discovering,
    /// <summary>Loading plugins and capturing editors. Slow, and the part that can fail.</summary>
    Probing,
    Finished,
    Cancelled,
}

/// <param name="Phase">Current stage.</param>
/// <param name="Completed">Bundles finished in the probing phase.</param>
/// <param name="Total">Bundles to probe. Zero while discovering, when the total is not yet known.</param>
/// <param name="CurrentItem">What is being worked on, for the status line.</param>
/// <param name="Captured">Screenshots obtained so far.</param>
/// <param name="Failed">Bundles that failed to load, timed out, or crashed.</param>
/// <param name="Tally">New, updated and existing plugins found by discovery. Empty for a reload of
/// selected plugins, which does no discovery.</param>
/// <param name="Action">What is being done with <paramref name="CurrentItem"/> in the probing phase.</param>
public readonly record struct ScanStatus(
    ScanPhase Phase, int Completed, int Total, string CurrentItem, int Captured, int Failed,
    DiscoveryTally Tally = default, ScanItemAction Action = ScanItemAction.None)
{
    /// <summary>Fraction complete, or null while the total is unknown (an indeterminate bar).</summary>
    public double? Fraction => Total > 0 ? (double)Completed / Total : null;
}

/// <summary>Outcome summary for the scan report shown when it finishes.</summary>
/// <param name="Discovered">Plugins found on disk.</param>
/// <param name="Probed">Bundles successfully loaded.</param>
/// <param name="Captured">Screenshots captured.</param>
/// <param name="Failed">Bundles that could not be loaded or timed out.</param>
/// <param name="Skipped">Bundles skipped because the catalog was already current.</param>
/// <param name="Elapsed">Wall-clock duration.</param>
/// <param name="SkippedFailed">Previously failed bundles not retried, because the user asked to skip them.</param>
/// <param name="Tally">New, updated and existing plugins, for a scan that ran discovery.</param>
public sealed record ScanSummary(
    int Discovered, int Probed, int Captured, int Failed, int Skipped, TimeSpan Elapsed,
    int SkippedFailed = 0, DiscoveryTally? Tally = null)
{
    public override string ToString() =>
        $"{Discovered} found · " +
        (Tally is { } tally ? $"{tally} · " : "") +
        $"{Probed} loaded · {Captured} screenshots · {Failed} failed · " +
        $"{Skipped} skipped as unchanged · " +
        (SkippedFailed > 0 ? $"{SkippedFailed} failed skipped · " : "") +
        $"{Elapsed.TotalSeconds:0}s";
}

/// <summary>What the probing phase is doing with the bundle it is currently on.</summary>
public enum ScanItemAction
{
    /// <summary>Not in the probing phase.</summary>
    None,
    /// <summary>Loading it in the worker.</summary>
    Loading,
    /// <summary>Passed over: loaded before and unchanged since.</summary>
    SkippedUnchanged,
    /// <summary>Passed over: failed before, unchanged since, and the user asked not to retry.</summary>
    SkippedFailed,
}

/// <summary>Why the probing phase passed over a bundle.</summary>
internal enum SkipReason
{
    /// <summary>Not skipped: the bundle is loaded.</summary>
    None,
    /// <summary>Loaded successfully before, and the file has not changed since.</summary>
    Unchanged,
    /// <summary>Failed or timed out before, the file has not changed, and the user asked not to retry.</summary>
    PreviouslyFailed,
}

/// <summary>
/// Runs a full scan: discover plugins on disk, then load each VST3 in the sandboxed worker to read its
/// real metadata and photograph its editor.
/// </summary>
/// <remarks>
/// The two phases are deliberately separate and separately useful. Discovery is fast, safe, and gives a
/// complete catalog on its own; probing is slow, risks hangs and crashes, and only enriches what
/// discovery already found. A cancelled or failed probing phase therefore still leaves a usable catalog.
/// </remarks>
public sealed class ScanService
{
    private readonly CatalogStore _store;
    private readonly WorkerRunner _worker;

    public ScanService(CatalogStore store, WorkerRunner worker)
    {
        _store = store;
        _worker = worker;
    }

    /// <summary>Where captured images live, one folder per bundle.</summary>
    public string ImageDirectory { get; init; } = CatalogStore.ImageDirectory;

    /// <summary>Skip bundles whose catalog row is already current. Turn off to force a full recapture.</summary>
    public bool SkipUnchanged { get; init; } = true;

    /// <summary>Don't retry bundles that failed or timed out last time, as long as the file is unchanged.</summary>
    /// <remarks>Off by default: some failures are intermittent (a plugin that timed out once may load
    /// fine next time), so retrying is the safer default. But a plugin that fails every time costs up
    /// to the full timeout on every scan, so the user can switch retrying off.</remarks>
    public bool SkipFailed { get; init; }

    /// <summary>Load plugins at all. When false the scan stops after discovery.</summary>
    public bool Probe { get; init; } = true;

    public async Task<ScanSummary> ScanAsync(
        IReadOnlyList<string> roots,
        IProgress<ScanStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        progress?.Report(new ScanStatus(ScanPhase.Discovering, 0, 0, "Searching for plugins…", 0, 0));

        var discoveryProgress = new Progress<ScanProgress>(p => progress?.Report(
            new ScanStatus(ScanPhase.Discovering, p.Found, 0, Path.GetFileName(p.CurrentPath), 0, 0, p.Tally)));

        // Read once and shared: the classifier compares against it while discovery runs, and
        // PreserveProbeResults carries its rows forward afterwards.
        var stored = _store.Load().ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

        var found = await Task.Run(
            () => new PluginScanner().Scan(roots, discoveryProgress, cancellationToken,
                entry => Classify(entry, stored)),
            cancellationToken).ConfigureAwait(false);

        // Recounted from the final list rather than taken from the last progress report, which the
        // Progress<T> machinery delivers asynchronously and so cannot be relied on to have arrived.
        var tally = found.Aggregate(default(DiscoveryTally), (t, e) => t.Add(Classify(e, stored)));

        var discovered = PreserveProbeResults(found, stored);

        // Persisted before probing starts: discovery alone is a complete, useful catalog, and it should
        // survive a cancelled or crashing probe phase.
        _store.Save(discovered);
        _store.PruneMissing();
        PruneOrphanedImages(discovered);

        if (!Probe || !_worker.IsAvailable)
        {
            progress?.Report(new ScanStatus(ScanPhase.Finished, 0, 0, "Discovery complete.", 0, 0, tally));
            return new ScanSummary(discovered.Count, 0, 0, 0, 0, started.Elapsed, Tally: tally);
        }

        return await ProbeAsync(discovered, started, SkipUnchanged, SkipFailed, tally, progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Sorts a freshly discovered plugin into new, updated or existing against the catalog.</summary>
    /// <remarks>Uses the same size-and-write-time test that decides whether stored probe results are
    /// kept, so "existing" here means exactly the plugins a scan will not need to reload.</remarks>
    internal static FoundKind Classify(PluginEntry found, IReadOnlyDictionary<string, PluginEntry> stored)
    {
        if (!stored.TryGetValue(found.Path, out var prior))
            return FoundKind.New;

        return prior.FileSize == found.FileSize && prior.LastWriteUtc == found.LastWriteUtc
            ? FoundKind.Existing
            : FoundKind.Updated;
    }

    /// <summary>
    /// Decides whether the probing phase can pass over a bundle.
    /// </summary>
    /// <remarks>
    /// Both rules only ever apply to a file that has not changed. That is guaranteed upstream:
    /// <see cref="PreserveProbeResults"/> keeps a stored row's state only when the size and write time
    /// still match, so a plugin that failed and has since been updated arrives here as
    /// <see cref="ProbeState.Discovered"/> and is always retried — the update may well be the fix.
    /// </remarks>
    internal static SkipReason ShouldSkip(
        PluginEntry entry, bool skipUnchanged, bool skipFailed, Func<PluginEntry, bool> isUpToDate)
    {
        if (skipUnchanged && entry.State == ProbeState.Probed && isUpToDate(entry))
            return SkipReason.Unchanged;

        if (skipFailed && entry.State is ProbeState.Failed or ProbeState.TimedOut)
            return SkipReason.PreviouslyFailed;

        return SkipReason.None;
    }

    /// <summary>
    /// Re-probes a specific set of already-catalogued plugins, always reloading them.
    /// </summary>
    /// <remarks>
    /// The case this exists for: a plugin whose editor showed a registration or trial prompt when it was
    /// first captured. Once the user authorises it, the stored screenshot is a picture of a dialog that
    /// will never appear again — so they need to recapture those specific plugins without re-running a
    /// twenty-minute scan over the whole library. The unchanged-file check is deliberately bypassed;
    /// nothing about the file changed, only the licence state outside it.
    /// </remarks>
    public async Task<ScanSummary> RescanAsync(
        IReadOnlyList<PluginEntry> entries,
        IProgress<ScanStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        if (!_worker.IsAvailable)
        {
            progress?.Report(new ScanStatus(ScanPhase.Finished, 0, 0, "Worker not available.", 0, 0));
            return new ScanSummary(entries.Count, 0, 0, 0, 0, started.Elapsed);
        }

        return await ProbeAsync(entries, started, skipUnchanged: false, skipFailed: false, tally: null,
                progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes image folders belonging to plugins that are no longer catalogued.
    /// </summary>
    /// <remarks>
    /// Captures are large — roughly a megabyte per plugin — so without this an uninstalled plugin would
    /// leave its screenshots on disk permanently, and the cache would only ever grow. Only folders whose
    /// names match the hash pattern this class generates are considered, so anything else a user has put
    /// in the directory is left alone.
    /// </remarks>
    private void PruneOrphanedImages(IReadOnlyList<PluginEntry> current)
    {
        if (!Directory.Exists(ImageDirectory))
            return;

        var live = current.Select(e => KeyFor(e.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in SafeDirectories(ImageDirectory))
        {
            string name = Path.GetFileName(directory);
            if (live.Contains(name) || !LooksLikeImageKey(name))
                continue;

            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or locked down — it will be retried on the next scan.
            }
        }
    }

    /// <summary>True for a folder name this class produced: a readable prefix, then "-", then 16 hex.</summary>
    private static bool LooksLikeImageKey(string name)
    {
        int separator = name.LastIndexOf('-');
        if (separator <= 0 || name.Length - separator - 1 != 16)
            return false;

        for (int i = separator + 1; i < name.Length; i++)
        {
            if (!Uri.IsHexDigit(name[i]))
                return false;
        }
        return true;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Carries previously probed results forward onto freshly discovered entries.
    /// </summary>
    /// <remarks>
    /// Discovery knows only what the filesystem says, so a freshly discovered entry carries no classes,
    /// no parameters and no screenshots. Saving those bare entries over the catalog would discard
    /// everything a previous scan had learned — every capture orphaned, every plugin re-probed — which
    /// made a rescan as expensive as a first scan and briefly emptied the gallery. So for any file that
    /// has not changed since it was last probed, the richer stored row wins.
    /// </remarks>
    private static IReadOnlyList<PluginEntry> PreserveProbeResults(
        IReadOnlyList<PluginEntry> discovered, IReadOnlyDictionary<string, PluginEntry> stored)
    {
        var result = new List<PluginEntry>(discovered.Count);
        foreach (var entry in discovered)
        {
            bool unchanged =
                stored.TryGetValue(entry.Path, out var prior) &&
                prior.State != ProbeState.Discovered &&
                prior.FileSize == entry.FileSize &&
                prior.LastWriteUtc == entry.LastWriteUtc;

            // Images are kept only while the file they depict is still the one on disk.
            result.Add(unchanged && prior is not null ? prior : entry);
        }
        return result;
    }

    private async Task<ScanSummary> ProbeAsync(
        IReadOnlyList<PluginEntry> discovered,
        System.Diagnostics.Stopwatch started,
        bool skipUnchanged,
        bool skipFailed,
        DiscoveryTally? tally,
        IProgress<ScanStatus>? progress,
        CancellationToken cancellationToken)
    {
        var blacklist = _store.GetBlacklist();

        // Only VST3 is loadable: VST2 is catalogued from its file alone by design, and a 32-bit binary
        // cannot be loaded by this 64-bit worker whatever its format.
        var candidates = discovered
            .Where(e => e.Format == PluginFormat.Vst3)
            .Where(e => e.Architecture is PluginArchitecture.X64 or PluginArchitecture.Unknown)
            .Where(e => !blacklist.Contains(e.Path))
            .ToList();

        int completed = 0, captured = 0, failed = 0, skipped = 0, skippedFailed = 0, probed = 0;

        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // An already-probed, unchanged bundle costs a process launch and up to 45 seconds to learn
            // nothing new, so skipping it is the difference between a quick rescan and a full one.
            var skip = ShouldSkip(entry, skipUnchanged, skipFailed,
                e => _store.IsUpToDate(e.Path, e.FileSize, e.LastWriteUtc));

            // Reported with what is being done to it, so the user can see existing plugins being
            // passed over quickly and only new or updated ones actually being loaded.
            var action = skip switch
            {
                SkipReason.Unchanged => ScanItemAction.SkippedUnchanged,
                SkipReason.PreviouslyFailed => ScanItemAction.SkippedFailed,
                _ => ScanItemAction.Loading,
            };
            progress?.Report(new ScanStatus(ScanPhase.Probing, completed, candidates.Count,
                entry.DisplayName, captured, failed, tally ?? default, action));

            string outputDirectory = Path.Combine(ImageDirectory, KeyFor(entry.Path));

            if (skip != SkipReason.None)
            {
                if (skip == SkipReason.Unchanged)
                    skipped++;
                else
                    skippedFailed++;

                completed++;
                continue;
            }

            var run = await _worker.RunAsync(entry.Path, outputDirectory, cancellationToken)
                .ConfigureAwait(false);

            var merged = Merge(entry, run);

            if (merged.State == ProbeState.Probed)
                probed++;
            else
                failed++;

            captured += merged.Classes.Sum(c => c.Images.Count(i => i.Kind == ImageKind.Screenshot));
            completed++;

            // Saved per bundle rather than in one batch at the end, so cancelling a long scan keeps
            // everything already captured.
            _store.Save([merged]);
        }

        progress?.Report(new ScanStatus(ScanPhase.Finished, completed, candidates.Count,
            "Scan complete.", captured, failed));

        return new ScanSummary(discovered.Count, probed, captured, failed, skipped, started.Elapsed,
            skippedFailed, tally);
    }

    /// <summary>Folds a worker report into the entry discovery produced.</summary>
    /// <remarks>The plugin's own answers win over anything inferred from the file: a real class name,
    /// vendor and category beat a PE version resource and a guess from the filename.</remarks>
    private static PluginEntry Merge(PluginEntry entry, WorkerRun run)
    {
        if (run.Result is null || !run.Result.Loaded)
        {
            return entry with
            {
                State = run.Outcome switch
                {
                    WorkerOutcome.TimedOut => ProbeState.TimedOut,
                    _ => ProbeState.Failed,
                },
                StateDetail = run.Detail ?? run.Result?.Error ?? "The plugin could not be loaded.",
            };
        }

        var classes = new List<PluginClass>();
        foreach (var reported in run.Result.Classes)
        {
            var images = new List<PluginImage>();
            if (reported.ImagePath is { Length: > 0 } imagePath && File.Exists(imagePath))
            {
                images.Add(new PluginImage
                {
                    Kind = ImageKind.Screenshot,
                    FilePath = imagePath,
                    Width = reported.EditorWidth,
                    Height = reported.EditorHeight,
                    Method = ParseMethod(reported.CaptureMethod),
                });
            }

            var existing = entry.Classes.FirstOrDefault(c => c.Index == reported.Index);

            // Keep any vendor snapshot discovery already attached for this class; it outranks a capture.
            images.AddRange(existing?.Images.Where(i => i.Kind == ImageKind.Snapshot) ?? []);

            // If this run produced no screenshot, hold on to the one from last time. A recapture that
            // fails — the plugin hung, or refused to open its editor this once — should not cost the
            // user a picture they already had.
            if (images.All(i => i.Kind != ImageKind.Screenshot))
            {
                images.AddRange(existing?.Images
                    .Where(i => i.Kind == ImageKind.Screenshot && File.Exists(i.FilePath)) ?? []);
            }

            var tags = Vst3BundleReader.SplitCategory(reported.Category);

            classes.Add(new PluginClass
            {
                Index = reported.Index,
                Cid = existing?.Cid,
                Name = reported.Name,
                Vendor = reported.Vendor,
                Category = reported.Category,
                ClassCategory = PluginClass.AudioModuleClass,
                Version = reported.Version,
                SdkVersion = existing?.SdkVersion,
                Kind = Vst3BundleReader.ClassifyKind(tags, reported.Category),
                ParameterCount = reported.ParameterCount,
                Tags = tags,
                Images = [.. images.OrderBy(i => i.Kind)],
            });
        }

        bool anyFailure = run.Outcome != WorkerOutcome.Completed;

        return entry with
        {
            Classes = classes.Count > 0 ? classes : entry.Classes,
            State = anyFailure
                ? (run.Outcome == WorkerOutcome.TimedOut ? ProbeState.TimedOut : ProbeState.Failed)
                : ProbeState.Probed,
            StateDetail = anyFailure ? run.Detail : null,
        };
    }

    private static CaptureMethod ParseMethod(string? method) => method switch
    {
        "PrintWindow" => CaptureMethod.PrintWindow,
        "WindowsGraphicsCapture" => CaptureMethod.WindowsGraphicsCapture,
        "OnScreenBitBlt" => CaptureMethod.OnScreenBitBlt,
        _ => CaptureMethod.None,
    };

    /// <summary>
    /// A stable, filesystem-safe folder name for a bundle path.
    /// </summary>
    /// <remarks>A hash rather than a sanitised path: plugin paths are long, contain characters that are
    /// awkward in filenames, and two different bundles can sanitise to the same string. The readable
    /// prefix is there purely so the images folder can be browsed by a human.</remarks>
    public static string KeyFor(string bundlePath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(bundlePath.ToLowerInvariant()));
        string digest = Convert.ToHexString(hash)[..16];

        // Lower-cased like the hashed portion: Windows paths are case-insensitive, so the same bundle
        // reached through two differently-cased roots must resolve to one image folder, not two.
        string readable = Path.GetFileNameWithoutExtension(bundlePath).ToLowerInvariant();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            readable = readable.Replace(invalid, '_');

        return $"{readable[..Math.Min(readable.Length, 40)]}-{digest}";
    }
}
