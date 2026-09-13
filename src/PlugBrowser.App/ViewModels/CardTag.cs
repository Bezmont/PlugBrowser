namespace PlugBrowser.App.ViewModels;

/// <summary>Which filter a card tag feeds when clicked.</summary>
public enum CardTagKind
{
    Format,
    Kind,
    Category,
    Vendor,
}

/// <summary>
/// One clickable tag on a gallery card. Clicking it adds the matching filter to the current ones.
/// </summary>
/// <param name="Kind">The filter it feeds.</param>
/// <param name="Label">What the tag shows — abbreviated for vendors, so tags stay a similar width.</param>
/// <param name="Value">What the filter is set to: a format or kind name, a category, or the full vendor.</param>
/// <param name="ToolTip">What clicking does, and the full vendor name where the label is shortened.</param>
/// <param name="IsClickable">False for a tag with no filter behind it, such as "Unclassified".</param>
public sealed record CardTag(CardTagKind Kind, string Label, string Value, string ToolTip, bool IsClickable = true);

/// <summary>
/// Which of a plugin's tags count as categories — shared by the Category filter and the card tags, so
/// every category tag on a card matches a checkbox in the filter list exactly.
/// </summary>
public static class CategoryTags
{
    /// <summary>True for a tag that belongs in the Category filter.</summary>
    /// <remarks>
    /// <para>"Fx" and "Instrument" are excluded: they are the effect/instrument split the Type facet
    /// already covers, and listing them twice would let a user set two controls into a contradiction
    /// that silently returns nothing.</para>
    /// <para>VST3 capability subcategories are excluded too. The spec puts them in the same
    /// <c>|</c>-separated string as the musical categories, so "Fx|Delay|OnlyRT" declares an effect, a
    /// delay, and a realtime-only constraint at once. In a list for choosing "delay" or "reverb" they
    /// are noise — <c>OnlyRT</c> and <c>NoOfflineProcess</c> each matched nine plugins here with
    /// nothing in common musically. Channel-layout markers are excluded for the same reason.</para>
    /// </remarks>
    public static bool IsFacetTag(string tag) =>
        !string.IsNullOrWhiteSpace(tag) &&
        !tag.Equals("Fx", StringComparison.OrdinalIgnoreCase) &&
        !tag.Equals("Instrument", StringComparison.OrdinalIgnoreCase) &&
        !CapabilityTags.Contains(tag);

    private static readonly HashSet<string> CapabilityTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "OnlyRT", "OnlyOfflineProcess", "NoOfflineProcess", "OnlyARA", "Distributable",
        "Mono", "Stereo", "Ambisonics", "Up-Downmix",
    };
}

/// <summary>
/// Shortens vendor names for card tags, so a long name does not make its tag far wider than the rest.
/// </summary>
/// <remarks>
/// Tuned against the vendors in a real catalog: "MAGIX Software GmbH" → "MAGIX", "XLN Audio" → "XLN",
/// "Native Instruments" → "NI", "AUDIO PLUGIN UNION" → "APU", while short names such as "Arturia" are
/// left alone. The full name is always available in the tag's tooltip.
/// </remarks>
public static class VendorAbbreviation
{
    /// <summary>Longest label kept as written — about the width of the "Instrument" tag.</summary>
    public const int MaxLength = 9;

    /// <summary>Company-form suffixes that say nothing about who the vendor is.</summary>
    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "ltd", "llc", "gmbh", "corp", "corporation", "co", "limited", "sa", "ag", "bv", "pty",
        "sarl", "srl", "sas", "oy", "ab", "aps", "kg", "plc",
    };

    /// <summary>Words so common among audio vendors that dropping them still leaves the name recognisable.</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio", "software", "music", "sound", "sounds", "studio", "studios", "technologies",
        "technology", "tech", "labs", "lab", "digital", "systems", "plugins", "plug-ins",
        "productions", "dsp", "multimedia", "media",
    };

    public static string Abbreviate(string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
            return "Unknown";

        var words = vendor
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.TrimEnd('.'))
            .Where(w => w.Length > 0 && !LegalSuffixes.Contains(w))
            .ToList();

        if (words.Count == 0)
            return Shorten(vendor.Trim());

        // Generic words go whenever something distinctive is left: "XLN Audio" is "XLN" to anyone who
        // would recognise it at all.
        var distinctive = words.Where(w => !GenericWords.Contains(w)).ToList();
        if (distinctive.Count == 0)
            distinctive = words;

        string joined = string.Join(' ', distinctive);
        if (joined.Length <= MaxLength)
            return joined;

        // Several words: initials of every word, generic ones included, so "Audio Plugin Union" is
        // "APU" as its users call it, rather than "PU".
        if (distinctive.Count > 1)
            return string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));

        return Shorten(joined);
    }

    private static string Shorten(string word) =>
        word.Length <= MaxLength ? word : word[..(MaxLength - 1)] + "…";
}
