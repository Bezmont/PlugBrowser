using System.Text;
using PlugBrowser.Core.Model;

namespace PlugBrowser.Core.Analysis;

/// <summary>One plugin available in more than one format.</summary>
/// <param name="Name">Display name of the best entry in the group.</param>
/// <param name="Key">Normalised key the entries were matched on.</param>
/// <param name="Entries">Every catalogued copy, newest format first.</param>
public sealed record DuplicateGroup(string Name, string Key, IReadOnlyList<PluginEntry> Entries)
{
    /// <summary>Formats this plugin is installed in.</summary>
    public IReadOnlyList<PluginFormat> Formats =>
        [.. Entries.Select(e => e.Format).Distinct().Order()];

    /// <summary>Combined size of every copy, for the "what would I reclaim?" column.</summary>
    public long TotalBytes => Entries.Sum(e => e.FileSize);

    /// <summary>Size of the copies that are not the preferred format.</summary>
    /// <remarks>What removing the redundant format would actually save, which is the number a user
    /// deciding whether to switch off their VST2 folders wants to see.</remarks>
    public long RedundantBytes => Entries.Where(e => e.Format != PluginFormat.Vst3).Sum(e => e.FileSize);
}

/// <summary>
/// Finds plugins installed in more than one format — nearly always a VST2 and a VST3 build of the same
/// product, shipped by one installer.
/// </summary>
/// <remarks>
/// Worth surfacing because the duplicates are invisible individually but obvious in aggregate: on the
/// machine this was developed against they account for a large share of the catalog, and every one is a
/// second copy of a plugin the user already has in a better format. Seeing them listed is what makes
/// "switch off the VST2 folders" an informed decision rather than a guess.
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

    /// <summary>Groups the catalog by product and returns those present in more than one format.</summary>
    public static IReadOnlyList<DuplicateGroup> FindCrossFormatDuplicates(IEnumerable<PluginEntry> entries)
    {
        var groups = new List<DuplicateGroup>();

        foreach (var group in GroupByProduct(entries))
        {
            // More than one *format*, not merely more than one file: two VST3 copies in different
            // folders are a different problem and not what this view is about.
            if (group.Value.Select(e => e.Format).Distinct().Count() < 2)
                continue;

            var ordered = group.Value
                .OrderBy(e => e.Format == PluginFormat.Vst3 ? 0 : 1)
                .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            groups.Add(new DuplicateGroup(ordered[0].DisplayName, group.Key, ordered));
        }

        return [.. groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)];
    }

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
