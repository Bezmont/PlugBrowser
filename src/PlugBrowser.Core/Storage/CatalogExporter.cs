using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlugBrowser.Core.Model;

namespace PlugBrowser.Core.Storage;

/// <summary>
/// Writes the catalog as a stable, versioned JSON file for other programs to read.
/// </summary>
/// <remarks>
/// The SQLite catalog is PlugBrowser's private cache: its tables are dropped and rebuilt whenever the
/// schema version changes, so nothing outside this app should read it. The export is the public
/// interface instead, documented in <c>docs/catalog-export.md</c>. Change that document along with this
/// file, and bump <see cref="FormatVersion"/> only for a change that breaks an existing reader.
/// </remarks>
public static class CatalogExporter
{
    /// <summary>The value of the export's <c>format</c> field, so a reader can tell what it opened.</summary>
    public const string FormatName = "PlugBrowser.CatalogExport";

    /// <summary>Bumped only for breaking changes. Adding a field is not one.</summary>
    public const int FormatVersion = 1;

    /// <summary>Where the export lives, beside the catalog.</summary>
    public static string DefaultPath =>
        Path.Combine(CatalogStore.DefaultDirectory, "export", "plugins.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Nulls are written, not omitted: the spec lists every field, and "null" says "not known"
        // more clearly than a field that is sometimes missing.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Writes the export, replacing any previous one in a single step.</summary>
    public static void Export(CatalogStore store, string? path = null)
    {
        string target = path ?? DefaultPath;
        var document = Build(store.Load(), store.GetBlacklist(), DateTime.UtcNow, GeneratorName());
        Write(document, target);
    }

    /// <summary>
    /// Writes the export, but never lets a failure escape. The export is a convenience for other
    /// programs; a locked or read-only folder must not fail the scan that triggered it.
    /// </summary>
    /// <returns>Null on success, otherwise the reason it failed.</returns>
    public static string? TryExport(CatalogStore store, string? path = null)
    {
        try
        {
            Export(store, path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// True when the export is missing or older than the catalog database, so it should be rewritten.
    /// </summary>
    /// <remarks>Both the database and its WAL file are checked: in WAL mode, recent writes can live only
    /// in the <c>-wal</c> file, leaving the main file's timestamp behind.</remarks>
    public static bool IsStale(string databasePath, string? exportPath = null)
    {
        string target = exportPath ?? DefaultPath;
        if (!File.Exists(target))
            return true;

        DateTime exported = File.GetLastWriteTimeUtc(target);
        return new[] { databasePath, databasePath + "-wal" }
            .Where(File.Exists)
            .Any(p => File.GetLastWriteTimeUtc(p) > exported);
    }

    /// <summary>Builds the document. Separate from writing it so it can be tested without a disk.</summary>
    internal static ExportDocument Build(
        IEnumerable<PluginEntry> entries, IReadOnlySet<string> blacklist, DateTime generatedUtc,
        string generator) => new(
        Format: FormatName,
        Version: FormatVersion,
        GeneratedUtc: DateTime.SpecifyKind(generatedUtc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ"),
        Generator: generator,
        Plugins: entries
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Select(e => ToPlugin(e, blacklist.Contains(e.Path)))
            .ToList());

    private static ExportPlugin ToPlugin(PluginEntry entry, bool blacklisted) => new(
        Path: entry.Path,
        Format: entry.Format switch
        {
            PluginFormat.Vst3 => "vst3",
            PluginFormat.Vst2 => "vst2",
            _ => "unknown",
        },
        Architecture: entry.Architecture switch
        {
            PluginArchitecture.X64 => "x64",
            PluginArchitecture.X86 => "x86",
            PluginArchitecture.Arm64 => "arm64",
            _ => "unknown",
        },
        // The blacklist is the user's own decision, so it outranks whatever the last scan concluded.
        State: blacklisted ? "blacklisted" : entry.State switch
        {
            ProbeState.Discovered => "discovered",
            ProbeState.Probed => "probed",
            ProbeState.Failed => "failed",
            ProbeState.TimedOut => "timedOut",
            ProbeState.Blacklisted => "blacklisted",
            ProbeState.Unsupported => "unsupported",
            _ => "unknown",
        },
        StateDetail: entry.StateDetail,
        DisplayName: entry.DisplayName,
        DisplayVendor: entry.DisplayVendor,
        FileVersion: entry.FileVersion,
        // Controller and compatibility classes are factory plumbing, not plugins anyone would pick.
        Classes: entry.Classes.Where(c => c.IsAudioModule).Select(ToClass).ToList());

    private static ExportClass ToClass(PluginClass c) => new(
        Cid: c.Cid,
        Index: c.Index,
        Name: c.Name,
        Vendor: c.Vendor,
        Category: c.Category,
        Tags: c.Tags.Count > 0 ? c.Tags : Discovery.Vst3BundleReader.SplitCategory(c.Category),
        Kind: c.Kind switch
        {
            PluginKind.Effect => "effect",
            PluginKind.Instrument => "instrument",
            _ => "unknown",
        },
        Version: c.Version,
        ParameterCount: c.ParameterCount,
        // Images arrive best first (vendor snapshots before captures), so the first is the one to show.
        Image: c.Images.Count > 0 ? ToImage(c.Images[0]) : null);

    private static ExportImage ToImage(PluginImage image) => new(
        Path: image.FilePath,
        ThumbnailPath: ThumbnailFor(image.FilePath),
        Width: image.Width,
        Height: image.Height,
        Source: image.Kind == ImageKind.Snapshot ? "snapshot" : "screenshot");

    /// <summary>
    /// The worker saves a 320px-wide thumbnail beside each capture as <c>x.thumb.png</c>. An editor that
    /// was already narrower than that gets none, so the thumbnail is reported only if it exists.
    /// </summary>
    internal static string? ThumbnailFor(string imagePath)
    {
        string candidate = Path.ChangeExtension(imagePath, null) + ".thumb.png";
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Writes to a temporary file and moves it into place, so a reader never sees half a file.</summary>
    internal static void Write(ExportDocument document, string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, document, JsonOptions);

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string GeneratorName()
    {
        var assembly = typeof(CatalogExporter).Assembly;
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString();

        // Build metadata ("+commit") is noise to a reader.
        if (version?.IndexOf('+') is int plus and >= 0)
            version = version[..plus];

        return string.IsNullOrEmpty(version) ? "PlugBrowser" : $"PlugBrowser {version}";
    }

    internal sealed record ExportDocument(
        string Format, int Version, string GeneratedUtc, string Generator, IReadOnlyList<ExportPlugin> Plugins);

    internal sealed record ExportPlugin(
        string Path, string Format, string Architecture, string State, string? StateDetail,
        string DisplayName, string? DisplayVendor, string? FileVersion, IReadOnlyList<ExportClass> Classes);

    internal sealed record ExportClass(
        string? Cid, int Index, string Name, string? Vendor, string? Category, IReadOnlyList<string> Tags,
        string Kind, string? Version, int ParameterCount, ExportImage? Image);

    internal sealed record ExportImage(
        string Path, string? ThumbnailPath, int Width, int Height, string Source);
}
