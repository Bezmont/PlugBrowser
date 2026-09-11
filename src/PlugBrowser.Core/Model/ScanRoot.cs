namespace PlugBrowser.Core.Model;

/// <summary>Where a scan root came from, which decides whether the user may remove it.</summary>
public enum ScanRootOrigin
{
    /// <summary>A conventional install location for the platform. Always offered, never removable —
    /// the user can only disable it, because removing it would just reappear on the next launch.</summary>
    Convention = 0,

    /// <summary>Declared by an installer in <c>HKLM\SOFTWARE\VST\VSTPluginsPath</c>.</summary>
    Registry = 1,

    /// <summary>Added by the user. The only kind that can be removed.</summary>
    User = 2,
}

/// <summary>A folder the scanner walks, and whether it is currently switched on.</summary>
public sealed record ScanRoot
{
    public required string Path { get; init; }

    public required ScanRootOrigin Origin { get; init; }

    /// <summary>Whether the next scan includes this folder.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether the folder is present on disk right now.</summary>
    /// <remarks>A missing root is shown rather than hidden: a user who added a path on an external
    /// drive should see it greyed out when the drive is absent, not silently lose it.</remarks>
    public bool Exists { get; init; }

    /// <summary>Number of plugins the last scan attributed to this root, for the options list.</summary>
    public int PluginCount { get; init; }

    /// <summary>Only user-added roots can be deleted; the rest are intrinsic to the platform.</summary>
    public bool CanRemove => Origin == ScanRootOrigin.User;
}
