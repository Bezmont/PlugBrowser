using CommunityToolkit.Mvvm.ComponentModel;
using PlugBrowser.Core.Model;

namespace PlugBrowser.App.ViewModels;

/// <summary>One row in the Options scan-root list.</summary>
public sealed partial class ScanRootViewModel : ObservableObject
{
    private readonly Action<ScanRootViewModel> _onEnabledChanged;

    public ScanRootViewModel(ScanRoot root, Action<ScanRootViewModel> onEnabledChanged)
    {
        Root = root;
        _isEnabled = root.Enabled;
        _onEnabledChanged = onEnabledChanged;
    }

    public ScanRoot Root { get; }

    public string Path => Root.Path;

    public ScanRootOrigin Origin => Root.Origin;

    public bool CanRemove => Root.CanRemove;

    /// <summary>Explains where the root came from, so "why can't I delete this?" is answered in place.</summary>
    public string OriginLabel => Root.Origin switch
    {
        ScanRootOrigin.Convention => "Standard location",
        ScanRootOrigin.Registry => "Registered by an installer",
        ScanRootOrigin.User => "Added by you",
        _ => string.Empty,
    };

    public string StatusLabel => !Root.Exists
        ? "Folder not found"
        : Root.PluginCount > 0
            ? $"{Root.PluginCount} plugins"
            : "No plugins found";

    public bool Exists => Root.Exists;

    /// <summary>Missing folders are shown dimmed rather than hidden — a root on an unmounted drive
    /// should be visibly absent, not silently dropped.</summary>
    public double PathOpacity => Root.Exists ? 1.0 : 0.45;

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value) => _onEnabledChanged(this);
}
