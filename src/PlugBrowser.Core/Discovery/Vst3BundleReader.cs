using System.Text.Json;
using PlugBrowser.Core.Model;

namespace PlugBrowser.Core.Discovery;

/// <summary>
/// Understands the layout of a VST3 bundle and pulls out everything obtainable without loading it.
/// </summary>
/// <remarks>
/// A <c>.vst3</c> on Windows is either a plain DLL that happens to use that extension, or a bundle
/// <em>directory</em> shaped like <c>Name.vst3/Contents/x86_64-win/Name.vst3</c>, with optional
/// <c>Contents/Resources/moduleinfo.json</c> and
/// <c>Contents/Resources/Snapshots/&lt;CID&gt;_snapshot.png</c> alongside it.
/// Both forms are common; on the machine this was developed against, 151 of 156 top-level entries were
/// flat files and only 3 shipped a moduleinfo.json, with no snapshots at all. So the rich-bundle path is
/// a bonus, and the PE version resource is the dependable source of pre-load metadata.
/// </remarks>
public static class Vst3BundleReader
{
    /// <summary>Architecture folder names in the order we prefer them — the x64 worker can only load
    /// the first.</summary>
    private static readonly (string Folder, PluginArchitecture Arch)[] ArchFolders =
    [
        ("x86_64-win", PluginArchitecture.X64),
        ("arm64-win", PluginArchitecture.Arm64),
        ("x86-win", PluginArchitecture.X86),
    ];

    /// <summary>True if <paramref name="path"/> is a bundle directory rather than a flat DLL.</summary>
    public static bool IsBundleDirectory(string path) => Directory.Exists(path);

    /// <summary>Locates the real PE binary inside a bundle, or returns the path itself when flat.</summary>
    public static (string? BinaryPath, PluginArchitecture Arch) ResolveBinary(string path)
    {
        if (!IsBundleDirectory(path))
            return (File.Exists(path) ? path : null, PluginArchitecture.Unknown);

        foreach (var (folder, arch) in ArchFolders)
        {
            string dir = Path.Combine(path, "Contents", folder);
            if (!Directory.Exists(dir))
                continue;

            // The binary is conventionally named after the bundle, but not always; fall back to
            // whatever single .vst3 is present rather than insisting on the name.
            var candidates = SafeEnumerateFiles(dir, "*.vst3");
            string? match = candidates.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f)
                        .Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault();

            if (match is not null)
                return (match, arch);
        }
        return (null, PluginArchitecture.Unknown);
    }

    /// <summary>Reads <c>Contents/Resources/moduleinfo.json</c> if the bundle ships one.</summary>
    /// <returns>The classes it declares, or an empty list when absent or unparseable.</returns>
    public static IReadOnlyList<PluginClass> ReadModuleInfo(string bundlePath)
    {
        if (!IsBundleDirectory(bundlePath))
            return [];

        string file = Path.Combine(bundlePath, "Contents", "Resources", "moduleinfo.json");
        if (!File.Exists(file))
            return [];

        try
        {
            // The moduleinfo.json format permits comments and trailing commas by spec.
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (!doc.RootElement.TryGetProperty("Classes", out var classes) ||
                classes.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<PluginClass>();
            int index = 0;
            foreach (var c in classes.EnumerateArray())
            {
                string? name = Str(c, "Name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string? classCategory = Str(c, "Category");

                // Index counts every class the factory declares, so it still matches the factory's own
                // numbering even though we only return the audio modules.
                int classIndex = index++;
                if (!string.IsNullOrEmpty(classCategory) &&
                    !classCategory.Equals(PluginClass.AudioModuleClass, StringComparison.OrdinalIgnoreCase))
                    continue;

                var subCategories = ReadSubCategories(c);

                result.Add(new PluginClass
                {
                    Cid = NormalizeCid(Str(c, "CID")),
                    Index = classIndex,
                    Name = name!,
                    Vendor = Str(c, "Vendor"),
                    Category = subCategories.Count > 0
                        ? string.Join(CategorySeparator, subCategories)
                        : null,
                    ClassCategory = classCategory,
                    Version = Str(c, "Version"),
                    SdkVersion = Str(c, "SDKVersion"),
                    Kind = ClassifyKind(subCategories, null),
                    Tags = subCategories,
                });
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Finds the vendor-supplied snapshot PNG for a class, if the bundle ships one.</summary>
    /// <remarks>Part of the VST3 spec and always more trustworthy than a captured screenshot — but rare
    /// in the wild, so treat a hit as a bonus rather than the expected case.</remarks>
    public static string? FindSnapshot(string bundlePath, string? cid)
    {
        if (cid is null || !IsBundleDirectory(bundlePath))
            return null;

        string dir = Path.Combine(bundlePath, "Contents", "Resources", "Snapshots");
        if (!Directory.Exists(dir))
            return null;

        // Files are named <CID>_snapshot.png, with a _snapshot_2.0x.png variant for high DPI.
        return SafeEnumerateFiles(dir, "*.png")
            .Where(f => Path.GetFileName(f).StartsWith(cid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Contains("_2.0x", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>The character VST3 uses to join subcategories, as in <c>"Fx|EQ"</c>.</summary>
    public const char CategorySeparator = '|';

    /// <summary>Splits a VST3 subcategory string such as <c>"Fx|EQ"</c> into facet tags.</summary>
    public static IReadOnlyList<string> SplitCategory(string? category) =>
        string.IsNullOrWhiteSpace(category)
            ? []
            : category.Split(CategorySeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Best-effort instrument/effect call from category strings alone.</summary>
    /// <remarks>Only a guess. The reliable signal is the presence of an event input bus, which needs the
    /// plugin loaded, so Phase 2 overwrites whatever this decides.</remarks>
    public static PluginKind ClassifyKind(IReadOnlyList<string> subCategories, string? category)
    {
        bool Has(string token) =>
            subCategories.Any(s => s.Equals(token, StringComparison.OrdinalIgnoreCase)) ||
            (category?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false);

        if (Has("Instrument")) return PluginKind.Instrument;
        if (Has("Fx")) return PluginKind.Effect;
        return PluginKind.Unknown;
    }

    /// <summary>Reads a class's musical subcategories, e.g. <c>["Instrument", "Synth"]</c>.</summary>
    /// <remarks>The property is spelled <c>"Sub Categories"</c>, with a space, in the files the VST3 SDK
    /// actually emits. The unspaced spelling is accepted too so a vendor writing the file by hand does
    /// not silently lose its categories.</remarks>
    private static IReadOnlyList<string> ReadSubCategories(JsonElement c)
    {
        if ((!c.TryGetProperty("Sub Categories", out var subs) || subs.ValueKind != JsonValueKind.Array) &&
            (!c.TryGetProperty("SubCategories", out subs) || subs.ValueKind != JsonValueKind.Array))
            return [];

        var list = new List<string>();
        foreach (var s in subs.EnumerateArray())
        {
            if (s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } value)
                list.Add(value);
        }
        return list;
    }

    /// <summary>Reduces a CID to a bare 32-character hex string, since vendors write it with and
    /// without braces and dashes and we key cached data on it.</summary>
    private static string? NormalizeCid(string? cid)
    {
        if (string.IsNullOrWhiteSpace(cid))
            return null;

        string trimmed = cid.Trim().Trim('{', '}').Replace("-", string.Empty, StringComparison.Ordinal);
        return trimmed.Length == 32 ? trimmed.ToUpperInvariant() : cid.Trim();
    }

    private static string? Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
