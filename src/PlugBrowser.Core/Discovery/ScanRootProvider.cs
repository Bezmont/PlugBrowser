using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;

namespace PlugBrowser.Core.Discovery;

/// <summary>
/// Produces the list of scan roots shown in Options: the platform's conventional folders and the
/// registry-declared ones, merged with whatever the user has enabled, disabled, or added.
/// </summary>
/// <remarks>
/// Conventional and registry roots are rediscovered on every launch rather than stored, so a plugin
/// folder that appears later (a newly installed DAW, a mounted drive) shows up on its own. Only the
/// user's <em>decisions</em> are persisted — which roots are switched off, and which they added
/// themselves. Storing the derived list instead would freeze the machine's layout at first run.
/// </remarks>
public static class ScanRootProvider
{
    /// <summary>All roots to show the user, in a stable display order.</summary>
    /// <param name="pluginCounts">Plugins per root from the catalog, for the count column.</param>
    public static IReadOnlyList<ScanRoot> GetRoots(
        CatalogStore store, IReadOnlyDictionary<string, int>? pluginCounts = null)
    {
        var stored = store.GetScanRootState();
        var roots = new Dictionary<string, ScanRoot>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, ScanRootOrigin origin)
        {
            string full = Normalize(path);
            if (full.Length == 0 || roots.ContainsKey(full))
                return;

            // Unknown to the store means never toggled, which is on.
            bool enabled = !stored.TryGetValue(full, out var state) || state.Enabled;

            roots[full] = new ScanRoot
            {
                Path = full,
                Origin = origin,
                Enabled = enabled,
                Exists = Directory.Exists(full),
                PluginCount = pluginCounts is not null && pluginCounts.TryGetValue(full, out int n) ? n : 0,
            };
        }

        foreach (var path in PluginPathProvider.DefaultVst3Roots())
            Add(path, ScanRootOrigin.Convention);

        foreach (var path in ConventionalVst2Roots())
            Add(path, ScanRootOrigin.Convention);

        if (OperatingSystem.IsWindows())
        {
            foreach (var path in PluginPathProvider.RegistryVst2Roots())
                Add(path, ScanRootOrigin.Registry);
        }

        // User roots come from the store alone — nothing else knows about them.
        foreach (var (path, state) in stored.Where(kv => kv.Value.Origin == ScanRootOrigin.User))
            Add(path, ScanRootOrigin.User);

        return [.. roots.Values
            .OrderBy(r => r.Origin)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The roots an actual scan will walk: enabled and present on disk.</summary>
    public static IReadOnlyList<string> EnabledRoots(IEnumerable<ScanRoot> roots) =>
        [.. roots.Where(r => r.Enabled && r.Exists).Select(r => r.Path)];

    /// <summary>The conventional VST2 folders, excluding the registry-declared ones.</summary>
    /// <remarks>Split out from <see cref="PluginPathProvider.DefaultVst2Roots"/> so each root can be
    /// labelled with where it came from; that method deliberately mixes both.</remarks>
    private static IEnumerable<string> ConventionalVst2Roots()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);

        yield return Path.Combine(common, "VST2");
        yield return Path.Combine(programFiles, "VSTPlugins");
        yield return Path.Combine(programFiles, "Steinberg", "VSTPlugins");
        yield return Path.Combine(programFilesX86, "VSTPlugins");
        yield return Path.Combine(programFilesX86, "Steinberg", "VSTPlugins");
    }

    /// <summary>Canonical form used as the storage key, so the same folder typed two ways is one root.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return string.Empty;
        }
    }
}
