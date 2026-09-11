using PlugBrowser.Core.Model;

namespace PlugBrowser.Core.Discovery;

/// <summary>Progress for a running discovery pass, for the scan view's progress bar and status line.</summary>
/// <param name="Found">Plugins accepted so far.</param>
/// <param name="Examined">Files looked at, including rejected ones.</param>
/// <param name="CurrentPath">The file being examined, for the status line.</param>
/// <param name="Tally">How the plugins found so far compare with the catalog, when a classifier was given.</param>
public readonly record struct ScanProgress(int Found, int Examined, string CurrentPath, DiscoveryTally Tally = default);

/// <summary>How a discovered plugin compares with what the catalog already holds.</summary>
public enum FoundKind
{
    /// <summary>Not in the catalog at all.</summary>
    New,
    /// <summary>Catalogued, but the file's size or write time has changed since.</summary>
    Updated,
    /// <summary>Catalogued and unchanged.</summary>
    Existing,
}

/// <summary>Running counts of new, updated and existing plugins, so a scan can show the user that it is
/// merely re-checking what it already knows and picking out only what is new.</summary>
public readonly record struct DiscoveryTally(int New, int Updated, int Existing)
{
    public DiscoveryTally Add(FoundKind kind) => kind switch
    {
        FoundKind.New => this with { New = New + 1 },
        FoundKind.Updated => this with { Updated = Updated + 1 },
        _ => this with { Existing = Existing + 1 },
    };

    public override string ToString() => $"{Existing} existing · {New} new · {Updated} updated";
}

/// <summary>
/// Walks the scan roots and turns the files it finds into <see cref="PluginEntry"/> records, using only
/// what can be read from disk — no plugin is loaded here.
/// </summary>
/// <remarks>
/// The traversal rule that matters: an entry ending in <c>.vst3</c> is a plugin whether it is a file or
/// a directory, and a <c>.vst3</c> directory must <em>not</em> be descended into. Recursing into a
/// bundle would rediscover its own <c>Contents/x86_64-win/Name.vst3</c> binary as a second, phantom
/// plugin. This is inherited from StreamRecorder's scanner, which learned it the hard way.
/// </remarks>
public sealed class PluginScanner
{
    /// <summary>Folder names that never contain plugins but do contain many DLLs, skipped so a scan of
    /// a vendor's install tree does not spend its time in resource directories.</summary>
    private static readonly string[] SkipDirectories = ["Resources", "Contents", ".git", "node_modules"];

    /// <summary>Minimum gap between progress reports. See the note in <see cref="Scan"/>.</summary>
    private const int ProgressIntervalMs = 100;

    /// <summary>Discovers every plugin under <paramref name="roots"/>.</summary>
    /// <param name="classify">Optional: sorts each plugin found into new, updated or existing, so progress
    /// reports can carry those counts. Called on the scanning thread, once per accepted plugin.</param>
    public IReadOnlyList<PluginEntry> Scan(
        IEnumerable<string> roots,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<PluginEntry, FoundKind>? classify = null)
    {
        var results = new List<PluginEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int examined = 0;
        long lastReportTicks = 0;
        var tally = default(DiscoveryTally);

        foreach (var root in roots)
        {
            foreach (var candidate in Walk(root, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;

                if (!seen.Add(candidate))
                    continue; // the same folder can be reached through two configured roots

                var entry = Examine(candidate);
                if (entry is not null)
                {
                    results.Add(entry);
                    if (classify is not null)
                        tally = tally.Add(classify(entry));
                }

                Report(candidate);
            }
        }

        // Always finish on the true totals, however recently the last update went out.
        progress?.Report(new ScanProgress(results.Count, examined, string.Empty, tally));
        return results;

        // Reporting every examined file sounds harmless but is not: a UI-bound IProgress marshals each
        // call to the UI thread, and a scan walks thousands of files. Unthrottled, that flooded the
        // dispatcher badly enough to turn a 270ms scan into minutes of progress updates. Ten a second is
        // more than the eye can follow anyway.
        void Report(string candidate)
        {
            if (progress is null)
                return;

            long now = Environment.TickCount64;
            if (now - lastReportTicks < ProgressIntervalMs)
                return;

            lastReportTicks = now;
            progress.Report(new ScanProgress(results.Count, examined, candidate, tally));
        }
    }

    /// <summary>Yields candidate plugin paths under <paramref name="root"/>.</summary>
    /// <remarks>Hand-rolled rather than using <see cref="SearchOption.AllDirectories"/> because that
    /// aborts the whole enumeration on the first unreadable folder, and a scan of Program Files will
    /// meet one. This walks with an explicit stack so a denied folder costs only that folder.</remarks>
    private static IEnumerable<string> Walk(string root, CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dir = stack.Pop();

            List<string> subdirectories;
            try { subdirectories = Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var sub in subdirectories)
            {
                // A .vst3 directory is a bundle: yield it whole and never look inside.
                if (sub.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
                {
                    yield return sub;
                    continue;
                }

                string name = Path.GetFileName(sub);
                if (!SkipDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    stack.Push(sub);
            }

            List<string> files;
            try { files = Directory.EnumerateFiles(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                if (file.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }

    /// <summary>Decides whether a candidate path is a plugin and builds its entry.</summary>
    /// <returns>Null when the file is not a plugin — most commonly a support DLL sitting beside one.</returns>
    public static PluginEntry? Examine(string path)
    {
        bool isVst3Extension = path.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase);
        var (binaryPath, bundleArch) = isVst3Extension
            ? Vst3BundleReader.ResolveBinary(path)
            : (path, PluginArchitecture.Unknown);

        if (binaryPath is null)
        {
            // A .vst3 directory with no loadable binary for any architecture — real, but unusable.
            return isVst3Extension
                ? new PluginEntry
                {
                    Path = path,
                    Format = PluginFormat.Vst3,
                    State = ProbeState.Failed,
                    StateDetail = "Bundle contains no plugin binary for any known architecture.",
                }
                : null;
        }

        var pe = PeImage.TryRead(binaryPath);
        if (pe is null)
            return null; // not a PE file at all

        var format = ClassifyFormat(path, pe);
        if (format == PluginFormat.Unknown)
            return null;

        FileInfo info;
        try { info = new FileInfo(binaryPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        // The bundle folder names the architecture authoritatively; a flat file only has its PE header.
        var architecture = bundleArch != PluginArchitecture.Unknown ? bundleArch : pe.Architecture;

        var classes = format == PluginFormat.Vst3 ? Vst3BundleReader.ReadModuleInfo(path) : [];
        classes = AttachSnapshots(path, classes);

        return new PluginEntry
        {
            Path = path,
            Format = format,
            Architecture = architecture,
            BinaryPath = binaryPath,
            FileSize = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            FileVendor = pe.CompanyName,
            FileProduct = pe.ProductName ?? pe.FileDescription,
            FileVersion = pe.FileVersion,
            Classes = classes,
            State = architecture == PluginArchitecture.X86 && format == PluginFormat.Vst3
                ? ProbeState.Unsupported
                : ProbeState.Discovered,
            StateDetail = architecture == PluginArchitecture.X86 && format == PluginFormat.Vst3
                ? "32-bit VST3 cannot be loaded by the 64-bit worker."
                : null,
        };
    }

    /// <summary>
    /// Decides which plugin API a binary implements, from its extension and its exports.
    /// </summary>
    /// <remarks>
    /// The extension has to be part of the test. Plenty of real VST3 plugins — every Arturia one on the
    /// development machine — also export <c>main</c>, so treating that export alone as a VST2 marker
    /// would misfile them. So: a <c>.vst3</c> is VST3 if it exports <c>GetPluginFactory</c>, and only a
    /// <c>.dll</c> is ever considered for VST2.
    /// </remarks>
    public static PluginFormat ClassifyFormat(string path, PeImage pe)
    {
        if (path.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
        {
            // A bundle's exports were read from its inner binary, so this holds for both shapes.
            return pe.ExportsSymbol("GetPluginFactory") ? PluginFormat.Vst3 : PluginFormat.Unknown;
        }

        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            (pe.ExportsSymbol("VSTPluginMain") || pe.ExportsSymbol("main")))
        {
            return PluginFormat.Vst2;
        }

        return PluginFormat.Unknown;
    }

    /// <summary>Attaches any vendor snapshot PNGs the bundle ships for these classes.</summary>
    private static IReadOnlyList<PluginClass> AttachSnapshots(
        string bundlePath, IReadOnlyList<PluginClass> classes)
    {
        if (classes.Count == 0 || !Vst3BundleReader.IsBundleDirectory(bundlePath))
            return classes;

        var result = new List<PluginClass>(classes.Count);
        foreach (var c in classes)
        {
            string? snapshot = Vst3BundleReader.FindSnapshot(bundlePath, c.Cid);
            result.Add(snapshot is null
                ? c
                : c with
                {
                    Images =
                    [
                        new PluginImage
                        {
                            Kind = ImageKind.Snapshot,
                            FilePath = snapshot,
                            Method = CaptureMethod.None,
                        },
                    ],
                });
        }
        return result;
    }
}
