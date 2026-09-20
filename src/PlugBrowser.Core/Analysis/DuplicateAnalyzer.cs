using System.Text;
using PlugBrowser.Core.Model;

namespace PlugBrowser.Core.Analysis;

/// <summary>
/// Finds plugins installed in more than one format — nearly always a VST2 and a VST3 build of the same
/// product, shipped by one installer.
/// </summary>
/// <remarks>
/// Worth surfacing because the duplicates are invisible individually but obvious in aggregate: on the
/// machine this was developed against they account for a large share of the catalog. The main gallery's
/// "unique only" facet and the plugin list's "installed as" column are both built on it.
/// </remarks>
public static class DuplicateAnalyzer
{
    /// <summary>
    /// Reduces a plugin name to a key that matches its counterpart in another format.
    /// </summary>
    /// <remarks>
    /// <para>The same product is named inconsistently across its own builds: "Addictive Drums 2" as VST3
    /// and "Addictive Drums 2 x64" as VST2, "Comp FET-76" against "Comp FET 76". Punctuation, spacing and
    /// architecture markers all have to go before the two can be recognised as one plugin.</para>
    /// <para>Deliberately conservative about what it strips. Over-normalising would merge genuinely
    /// different plugins — a false "duplicate" invites the user to delete something they still need,
    /// which is far worse than missing one.</para>
    /// </remarks>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        string working = name.ToLowerInvariant();

        // Architecture and format markers vendors append to one build but not the other.
        foreach (var marker in ArchitectureMarkers)
            working = working.Replace(marker, " ", StringComparison.Ordinal);

        var builder = new StringBuilder(working.Length);
        foreach (char c in working)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Substrings that mark a build rather than a product.
    /// </summary>
    /// <remarks>Matched before punctuation is stripped, so the separators are still present. "x64" is
    /// intentionally absent as a bare token: it is handled by the underscore and bracket forms, because
    /// stripping a bare "x64" anywhere would corrupt a name that legitimately contains it.</remarks>
    private static readonly string[] ArchitectureMarkers =
    [
        "(x64)", "(x86)", "(64 bit)", "(32 bit)", "(64-bit)", "(32-bit)",
        "_x64", "_x86", "-x64", "-x86", " x64", " x86", " ×64", " ×86",
        "64bit", "32bit", "64 bit", "32 bit",
    ];

    /// <summary>
    /// Keys of products installed in more than one format, for the "unique only" filter.
    /// </summary>
    public static IReadOnlySet<string> CrossFormatKeys(IEnumerable<PluginEntry> entries)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in GroupByProduct(entries))
        {
            if (group.Value.Select(e => e.Format).Distinct().Count() >= 2)
                keys.Add(group.Key);
        }

        return keys;
    }

    private static Dictionary<string, List<PluginEntry>> GroupByProduct(IEnumerable<PluginEntry> entries)
    {
        var byKey = new Dictionary<string, List<PluginEntry>>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            string key = NormalizeName(entry.DisplayName);
            if (key.Length == 0)
                continue;

            if (!byKey.TryGetValue(key, out var list))
                byKey[key] = list = [];
            list.Add(entry);
        }

        return byKey;
    }
}
