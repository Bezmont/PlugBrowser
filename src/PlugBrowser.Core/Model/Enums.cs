namespace PlugBrowser.Core.Model;

/// <summary>Plugin API a binary implements. VST3 can be loaded and screenshotted; VST2 is catalogued
/// from file metadata only (see the VST2 note in the project README).</summary>
public enum PluginFormat
{
    Unknown = 0,
    Vst3 = 1,
    Vst2 = 2,
}

/// <summary>CPU architecture of the plugin binary. A 32-bit plugin cannot be loaded by the x64 worker.</summary>
public enum PluginArchitecture
{
    Unknown = 0,
    X86 = 1,
    X64 = 2,
    Arm64 = 3,
}

/// <summary>Whether a plugin makes sound (instrument) or processes it (effect).</summary>
/// <remarks>Phase 1 can only guess this from category strings and naming. Phase 2 decides it properly
/// from the presence of an event input bus, which is the only reliable signal.</remarks>
public enum PluginKind
{
    Unknown = 0,
    Effect = 1,
    Instrument = 2,
}

/// <summary>How far a bundle has got through the pipeline, and why it stopped.</summary>
public enum ProbeState
{
    /// <summary>Found on disk; only file-level metadata is known so far.</summary>
    Discovered = 0,
    /// <summary>Loaded successfully and its classes enumerated.</summary>
    Probed = 1,
    /// <summary>The worker ran but the plugin refused to load or reported no usable classes.</summary>
    Failed = 2,
    /// <summary>The worker hung and its process tree was killed.</summary>
    TimedOut = 3,
    /// <summary>Deliberately skipped by the user.</summary>
    Blacklisted = 4,
    /// <summary>Not loadable by this process (e.g. a 32-bit binary in the x64 worker).</summary>
    Unsupported = 5,
}

/// <summary>Where a plugin image came from, in descending order of trustworthiness.</summary>
public enum ImageKind
{
    /// <summary>A vendor-supplied PNG shipped inside the bundle under
    /// <c>Contents/Resources/Snapshots/</c>. Always correct when present — but rare in practice.</summary>
    Snapshot = 0,
    /// <summary>Captured by opening the plugin's own editor and grabbing the window's pixels.</summary>
    Screenshot = 1,
}

/// <summary>Which rung of the capture ladder produced a screenshot. Recorded so the UI can flag
/// low-confidence images and a rescan can retry only the weak ones.</summary>
public enum CaptureMethod
{
    None = 0,
    PrintWindow = 1,
    WindowsGraphicsCapture = 2,
    OnScreenBitBlt = 3,
}
