using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PlugBrowser.Core.Analysis;
using PlugBrowser.Core.Model;

namespace PlugBrowser.App.ViewModels;

/// <summary>One row/card in the browser, flattening a catalog entry into display-ready strings.</summary>
public sealed partial class PluginItemViewModel : ObservableObject
{
    private readonly PluginEntry _entry;
    private readonly string _searchHaystack;

    public PluginItemViewModel(PluginEntry entry, bool isFavorite)
    {
        _entry = entry;
        IsFavorite = isFavorite;

        // Built once rather than per keystroke: filtering runs over the whole catalog on every character
        // typed, and re-concatenating a few hundred strings each time is the one hot path here.
        _searchHaystack = string.Join(' ', new[]
            {
                Name, Vendor, Category, entry.FileProduct, entry.FileVersion,
                // Fully qualified: this class has its own Path property that shadows System.IO.Path.
                System.IO.Path.GetFileName(entry.Path),
            }
            .Where(s => !string.IsNullOrWhiteSpace(s)))
            .ToLowerInvariant();
    }

    /// <summary>The catalog record behind this row, needed to re-probe it.</summary>
    public PluginEntry Entry => _entry;

    public string Path => _entry.Path;

    public string Name => _entry.DisplayName;

    public string Vendor => _entry.DisplayVendor ?? "Unknown vendor";

    public PluginFormat Format => _entry.Format;

    public PluginKind Kind => FirstClass?.Kind ?? PluginKind.Unknown;

    public string? Category => FirstClass?.Category;

    public string Version => FirstClass?.Version ?? _entry.FileVersion ?? "—";

    public string FormatLabel => _entry.Format switch
    {
        PluginFormat.Vst3 => "VST3",
        PluginFormat.Vst2 => "VST2",
        _ => "?",
    };

    public string ArchitectureLabel => _entry.Architecture switch
    {
        PluginArchitecture.X64 => "64-bit",
        PluginArchitecture.X86 => "32-bit",
        PluginArchitecture.Arm64 => "ARM64",
        _ => "—",
    };

    public string KindLabel => Kind switch
    {
        PluginKind.Instrument => "Instrument",
        PluginKind.Effect => "Effect",
        _ => "Unclassified",
    };

    /// <summary>Human-readable file size, for the detail pane.</summary>
    public string SizeLabel => _entry.FileSize switch
    {
        < 1024 => $"{_entry.FileSize} B",
        < 1024 * 1024 => $"{_entry.FileSize / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{_entry.FileSize / (1024.0 * 1024):0.#} MB",
        _ => $"{_entry.FileSize / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public string SdkVersion => FirstClass?.SdkVersion ?? "—";

    public string StateLabel => _entry.State switch
    {
        ProbeState.Discovered => "Found on disk (not yet loaded)",
        ProbeState.Probed => "Loaded successfully",
        ProbeState.Failed => "Failed to load",
        ProbeState.TimedOut => "Timed out while loading",
        ProbeState.Blacklisted => "Skipped by you",
        ProbeState.Unsupported => "Not loadable by this build",
        _ => "—",
    };

    public string? StateDetail => _entry.StateDetail;

    /// <summary>True when something went wrong, so the UI can tint the entry.</summary>
    public bool IsProblem => _entry.State is ProbeState.Failed or ProbeState.TimedOut or ProbeState.Unsupported;

    /// <summary>Path to the best available image, or null when there is none yet.</summary>
    /// <remarks>Images are sorted vendor-snapshot-first when loaded, so the first is the best.</remarks>
    public string? ImagePath => FirstClass?.Images.FirstOrDefault()?.FilePath;

    /// <summary>Small image for the gallery grid, falling back to the full-size one.</summary>
    /// <remarks>
    /// The worker saves a 320px-wide thumbnail beside every capture, by convention <c>x.thumb.png</c>
    /// next to <c>x.png</c>. The grid draws hundreds of cards at once, and decoding full editor images —
    /// some 1440px wide — for every one of them costs far more memory than the cards need. A plugin
    /// whose editor was already narrower than the thumbnail width has no thumbnail, hence the fallback.
    /// </remarks>
    public string? ThumbnailPath
    {
        get
        {
            if (ImagePath is not { Length: > 0 } path)
                return null;

            string candidate = System.IO.Path.ChangeExtension(path, null) + ".thumb.png";
            return File.Exists(candidate) ? candidate : path;
        }
    }

    public bool HasImage => ImagePath is not null;

    private Bitmap? _thumbnail;
    private bool _thumbnailAttempted;
    private Bitmap? _detailImage;
    private bool _detailAttempted;

    /// <summary>Decoded thumbnail for the gallery card, or null if there is none or it failed to load.</summary>
    /// <remarks>
    /// Avalonia's <c>Image.Source</c> is an <c>IImage</c>, not a path: binding a string to it silently
    /// renders nothing, which is exactly how this first showed up — cards correctly hid their initials
    /// but stayed blank. Decoding happens here, once per item, and only when the card is actually
    /// realised.
    /// </remarks>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnailAttempted)
                return _thumbnail;

            _thumbnailAttempted = true;
            _thumbnail = Decode(ThumbnailPath, (int)CardImageWidth);
            return _thumbnail;
        }
    }

    /// <summary>Larger image for the detail pane.</summary>
    public Bitmap? DetailImage
    {
        get
        {
            if (_detailAttempted)
                return _detailImage;

            _detailAttempted = true;
            _detailImage = Decode(ImagePath, DetailImageWidth);
            return _detailImage;
        }
    }

    private Bitmap? _fullImage;
    private bool _fullAttempted;

    /// <summary>The capture at its native resolution, for the zoomed overlay.</summary>
    /// <remarks>Decoded separately from <see cref="DetailImage"/> and only when the overlay is first
    /// opened: this is the one place the full pixels are actually wanted, and holding them for every
    /// catalogued plugin would be pointless.</remarks>
    public Bitmap? FullImage
    {
        get
        {
            if (_fullAttempted)
                return _fullImage;

            _fullAttempted = true;
            _fullImage = DecodeNative(ImagePath);
            return _fullImage;
        }
    }

    /// <summary>Tags for facet filtering, e.g. <c>["Fx", "Delay"]</c>.</summary>
    public IReadOnlyList<string> Tags => FirstClass?.Tags ?? [];

    /// <summary>The tags that are categories (Delay, Reverb, Synth…), exactly as the Category filter
    /// lists them.</summary>
    public IReadOnlyList<string> CategoryNames =>
        Tags.Where(CategoryTags.IsFacetTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The clickable tags on this plugin's card: format, type, categories, then vendor.</summary>
    public IReadOnlyList<CardTag> CardTags => _cardTags ??= BuildCardTags();

    private IReadOnlyList<CardTag>? _cardTags;

    private List<CardTag> BuildCardTags()
    {
        var tags = new List<CardTag>
        {
            new(CardTagKind.Format, FormatLabel, Format.ToString(), $"Show only {FormatLabel} plugins"),

            // An unprobed plugin has no type yet, and there is no "unclassified" filter to add.
            Kind == PluginKind.Unknown
                ? new(CardTagKind.Kind, KindLabel, Kind.ToString(),
                      "Not loaded yet, so its type is unknown", IsClickable: false)
                : new(CardTagKind.Kind, KindLabel, Kind.ToString(),
                      Kind == PluginKind.Instrument ? "Show only instruments" : "Show only effects"),
        };

        foreach (var category in CategoryNames)
            tags.Add(new(CardTagKind.Category, category, category, $"Add {category} to the category filter"));

        tags.Add(new(CardTagKind.Vendor, VendorAbbreviation.Abbreviate(_entry.DisplayVendor), Vendor,
            $"{Vendor} — show only this vendor's plugins"));

        return tags;
    }

    /// <summary>Normalised product name, used to spot the same plugin installed in another format.</summary>
    public string DuplicateKey => _duplicateKey ??= DuplicateAnalyzer.NormalizeName(Name);

    private string? _duplicateKey;

    /// <summary>Width the detail pane decodes to — wider than it usually displays, so the image stays
    /// crisp as the resizable pane is widened, without holding a full 1440px editor capture in memory.</summary>
    private const int DetailImageWidth = 960;

    /// <summary>
    /// Loads an image scaled down to <paramref name="width"/>.
    /// </summary>
    /// <remarks>
    /// Decoded to the display size rather than natively: captures run up to 1535px wide, and the gallery
    /// realises every card at once, so decoding hundreds of them at full resolution would cost hundreds
    /// of megabytes to draw thumbnails a fifth that size.
    /// </remarks>
    private static Bitmap? Decode(string? path, int width)
    {
        if (path is not { Length: > 0 } || !File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // A truncated or deleted capture must not take the gallery down with it.
            return null;
        }
    }

    /// <summary>Loads an image at its stored resolution.</summary>
    private static Bitmap? DecodeNative(string? path)
    {
        if (path is not { Length: > 0 } || !File.Exists(path))
            return null;

        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The captured editor's width divided by its height, used to lay the card's image out at the
    /// plugin's real proportions.
    /// </summary>
    /// <remarks>
    /// Plugin editors vary enormously in shape — Comp FET-76 is 960x289 (3.3:1) while Analog Lab V is
    /// 1440x986 (1.5:1) — so a fixed-ratio image box would either crop one or letterbox the other. The
    /// grid gives every card the same width and lets height follow from this, so each thumbnail keeps
    /// the plugin's own proportions.
    /// </remarks>
    public double AspectRatio
    {
        get
        {
            var image = FirstClass?.Images.FirstOrDefault();
            if (image is null || image.Width <= 0 || image.Height <= 0)
                return DefaultAspectRatio;

            // Clamped so one pathological plugin cannot produce a card tall enough to break the grid.
            return Math.Clamp((double)image.Width / image.Height, 0.5, 6.0);
        }
    }

    /// <summary>Shape of the placeholder tile, chosen to match a typical plugin rather than a square.</summary>
    public const double DefaultAspectRatio = 16.0 / 9.0;

    /// <summary>Width of a card's image area, in device-independent pixels. Fixed so the grid stays
    /// aligned; height varies per plugin.</summary>
    public const double CardImageWidth = 184;

    /// <summary>Height the card's image area should take to honour <see cref="AspectRatio"/>.</summary>
    public double ThumbnailHeight => Math.Round(CardImageWidth / AspectRatio);

    /// <summary>Editor dimensions for the detail pane, or a dash when nothing was captured.</summary>
    public string EditorSizeLabel
    {
        get
        {
            var image = FirstClass?.Images.FirstOrDefault();
            return image is { Width: > 0, Height: > 0 } ? $"{image.Width} x {image.Height}" : "—";
        }
    }

    /// <summary>Parameter count, or a dash before the plugin has been loaded.</summary>
    /// <remarks>Reported as unknown rather than zero when unprobed: "0 parameters" would be a claim, and
    /// discovery never asked the plugin.</remarks>
    public string ParameterSummary => _entry.State == ProbeState.Probed
        ? $"{FirstClass?.ParameterCount ?? 0}"
        : "—";

    /// <summary>Placeholder shown until a screenshot exists — the plugin's initials.</summary>
    public string Initials
    {
        get
        {
            var words = Name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
            return words.Length switch
            {
                0 => "?",
                1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
                _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}",
            };
        }
    }

    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>The class shown when an entry is collapsed to one row. Most bundles expose exactly one.</summary>
    private PluginClass? FirstClass => _entry.Classes.Count > 0 ? _entry.Classes[0] : null;

    /// <summary>Case-insensitive substring match against every searchable field at once.</summary>
    public bool MatchesTerm(string term) =>
        _searchHaystack.Contains(term.ToLowerInvariant(), StringComparison.Ordinal);
}
