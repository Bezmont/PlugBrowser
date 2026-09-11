using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlugBrowser.Core.Probing;

/// <summary>
/// The worker's report on one bundle — the contract between the sandboxed worker process and the app.
/// </summary>
/// <remarks>
/// Delivered as a JSON file, never over stdout. Plugins routinely write to stdout and stderr as they
/// load (Qt's <c>OleInitialize failed</c> is the classic), which would corrupt any stream-based
/// protocol. StreamRecorder hit exactly this and moved to a temp file; the same applies here.
/// </remarks>
public sealed record InspectResult
{
    /// <summary>Bundle that was inspected.</summary>
    public required string Path { get; init; }

    /// <summary>True if the module loaded and its factory could be enumerated.</summary>
    public bool Loaded { get; init; }

    /// <summary>Why the inspection failed, when <see cref="Loaded"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Set by the worker's own watchdog just before it kills the process for overrunning its budget.
    /// </summary>
    /// <remarks>
    /// Needed because the watchdog ends the process with <c>Environment.FailFast</c>, which exits with
    /// <c>0xC0000409</c> — a code the parent cannot distinguish from a genuine crash. Without this flag
    /// every hung plugin was reported as "failed to load" rather than "timed out", which is both wrong
    /// and less actionable: a timeout is worth retrying with a longer budget, a crash is not.
    /// </remarks>
    public bool TimedOut { get; init; }

    /// <summary>One entry per audio-effect class the factory exposes.</summary>
    public IReadOnlyList<InspectedClass> Classes { get; init; } = [];

    /// <summary>
    /// True once the factory's class list was walked to completion.
    /// </summary>
    /// <remarks>
    /// The worker writes a report as soon as the module loads, before touching any class, so that a
    /// plugin which hangs still leaves evidence behind. That means an empty <see cref="Classes"/> list
    /// is ambiguous on its own — it is either a bundle that genuinely exposes nothing, or one whose
    /// worker was killed part-way through. This flag separates the two: only a run that finished
    /// enumerating sets it. Kontakt 8 is the case in point — it reported zero classes purely because it
    /// exceeded its budget.
    /// </remarks>
    public bool Enumerated { get; init; }

    /// <summary>True when the bundle loaded, enumeration completed, and it really exposes no classes.</summary>
    public bool NoClassesExposed => Loaded && Enumerated && Classes.Count == 0;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a worker report, returning null if it is absent or unparseable.</summary>
    public static InspectResult? FromJsonFile(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<InspectResult>(File.ReadAllText(path), Options)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One class inside a bundle, as reported by the worker.</summary>
public sealed record InspectedClass
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public string? Vendor { get; init; }
    public string? Category { get; init; }
    public string? Version { get; init; }

    /// <summary>Whether the plugin could be instantiated at all.</summary>
    public bool Instantiated { get; init; }

    /// <summary>Why instantiation failed, when it did.</summary>
    public string? Error { get; init; }

    public int ParameterCount { get; init; }

    /// <summary>Parameters, capped — some plugins expose thousands and the catalog only shows a list.</summary>
    public IReadOnlyList<InspectedParameter> Parameters { get; init; } = [];

    /// <summary>Whether the plugin offers an editor view at all. A plugin without one gets no
    /// screenshot, and that is a fact about the plugin rather than a capture failure.</summary>
    public bool HasEditor { get; init; }

    public int EditorWidth { get; init; }
    public int EditorHeight { get; init; }

    /// <summary>Captured screenshot path, if capture succeeded.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Which rung of the capture ladder produced the image.</summary>
    public string? CaptureMethod { get; init; }

    /// <summary>Why no image was produced.</summary>
    public string? CaptureError { get; init; }
}

/// <summary>One automatable parameter.</summary>
public sealed record InspectedParameter
{
    public required uint Id { get; init; }
    public required string Title { get; init; }
    public string? Units { get; init; }
    public double DefaultNormalized { get; init; }
    public int StepCount { get; init; }
    public bool IsBypass { get; init; }
    public bool CanAutomate { get; init; }
}
