namespace PlugBrowser.Core.Model;

/// <summary>
/// One plugin binary on disk — a flat <c>.vst3</c> DLL, a <c>.vst3</c> bundle directory, or a VST2
/// <c>.dll</c>. This is the unit the scanner discovers, the worker probes, and the cache keys on.
/// </summary>
/// <remarks>
/// A single bundle can expose several plugins (VST3 calls them <em>classes</em>), which is why the
/// classes hang off this record rather than being folded into it. StreamRecorder's catalog collapsed
/// every bundle to class 0 and so lost the rest; PlugBrowser keeps them all.
/// </remarks>
public sealed record PluginEntry
{
    /// <summary>Absolute path to the bundle directory or flat plugin file. The identity of the entry.</summary>
    public required string Path { get; init; }

    public required PluginFormat Format { get; init; }

    public PluginArchitecture Architecture { get; init; }

    /// <summary>Path to the actual PE binary. For a flat plugin this equals <see cref="Path"/>; for a
    /// bundle it is <c>Contents/&lt;arch&gt;-win/&lt;name&gt;.vst3</c> inside it.</summary>
    public string? BinaryPath { get; init; }

    /// <summary>Size of the binary in bytes. Part of the cache-invalidation key.</summary>
    public long FileSize { get; init; }

    /// <summary>Last-write time of the binary. Part of the cache-invalidation key.</summary>
    public DateTime LastWriteUtc { get; init; }

    /// <summary>Vendor from the PE version resource (<c>CompanyName</c>), available without loading the
    /// plugin. Superseded by the vendor the plugin itself reports once probed.</summary>
    public string? FileVendor { get; init; }

    /// <summary>Product name from the PE version resource. Often blank even when the vendor is set.</summary>
    public string? FileProduct { get; init; }

    /// <summary>Version string from the PE version resource.</summary>
    public string? FileVersion { get; init; }

    public ProbeState State { get; init; } = ProbeState.Discovered;

    /// <summary>Why the entry is in a failure state, for display in the scan failures list.</summary>
    public string? StateDetail { get; init; }

    /// <summary>Classes exposed by this binary. Empty until probed; a bundle with a
    /// <c>moduleinfo.json</c> can populate it during discovery without loading anything.</summary>
    public IReadOnlyList<PluginClass> Classes { get; init; } = [];

    /// <summary>Best display name available at this stage: the first class's name once probed,
    /// otherwise the product or file name.</summary>
    public string DisplayName =>
        Classes.Count > 0 ? Classes[0].Name
        : !string.IsNullOrWhiteSpace(FileProduct) ? FileProduct!
        : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Best vendor available at this stage.</summary>
    public string? DisplayVendor =>
        Classes.Count > 0 && !string.IsNullOrWhiteSpace(Classes[0].Vendor) ? Classes[0].Vendor
        : FileVendor;
}

/// <summary>
/// One plugin class inside a binary — what a user thinks of as "a plugin". Identified by its VST3
/// class ID (CID) rather than its index in the factory, because indices shift when a vendor ships an
/// update and would silently re-point cached data at the wrong plugin.
/// </summary>
public sealed record PluginClass
{
    /// <summary>VST3 class ID as 32 hex characters, when known. Null for VST2 and for classes
    /// discovered without a <c>moduleinfo.json</c>.</summary>
    public string? Cid { get; init; }

    /// <summary>Index in the factory. A fallback address only — see the CID note above.</summary>
    public int Index { get; init; }

    public required string Name { get; init; }

    public string? Vendor { get; init; }

    /// <summary>Raw VST3 subcategory string, e.g. <c>"Fx|EQ"</c> or <c>"Instrument|Synth"</c>.</summary>
    public string? Category { get; init; }

    /// <summary>The VST3 <em>class</em> category, e.g. <c>"Audio Module Class"</c>. Distinct from
    /// <see cref="Category"/>, which holds the musical subcategories.</summary>
    /// <remarks>A factory advertises plumbing alongside plugins — a Component Controller Class and a
    /// Plugin Compatibility Class accompany nearly every real plugin — and only
    /// <c>Audio Module Class</c> is something a user would recognise as a plugin. Kept rather than
    /// discarded because a class we filtered out is still the right answer to "why is this bundle
    /// showing nothing?".</remarks>
    public string? ClassCategory { get; init; }

    /// <summary>Whether this class is a plugin a user would see, as opposed to factory plumbing.</summary>
    public bool IsAudioModule =>
        ClassCategory is null ||
        ClassCategory.Equals(AudioModuleClass, StringComparison.OrdinalIgnoreCase);

    /// <summary>The VST3 class category naming an actual plugin.</summary>
    public const string AudioModuleClass = "Audio Module Class";

    public string? Version { get; init; }

    /// <summary>VST3 SDK version the plugin was built against.</summary>
    public string? SdkVersion { get; init; }

    public PluginKind Kind { get; init; }

    /// <summary>Number of automatable parameters the plugin reported when loaded. Zero until probed.</summary>
    public int ParameterCount { get; init; }

    /// <summary>Category split on <c>|</c> into individual facet tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Images for this class, best first.</summary>
    public IReadOnlyList<PluginImage> Images { get; init; } = [];
}

/// <summary>A picture of a plugin, stored as a file on disk with only its path held in the catalog.</summary>
public sealed record PluginImage
{
    public required ImageKind Kind { get; init; }
    public required string FilePath { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Which capture rung produced it. <see cref="CaptureMethod.None"/> for vendor snapshots.</summary>
    public CaptureMethod Method { get; init; }
}
