using CommunityToolkit.Mvvm.ComponentModel;

namespace PlugBrowser.App.ViewModels;

/// <summary>One selectable category in the left-hand facet list, e.g. "Delay" or "Dynamics".</summary>
/// <remarks>
/// Built from the subcategory tags the plugins themselves report rather than a fixed list. Vendors emit
/// tags the VST3 spec never defined, and a hardcoded set would quietly hide them; deriving the list means
/// a facet exists for whatever is actually installed.
/// </remarks>
public sealed partial class CategoryFilterViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public CategoryFilterViewModel(string name, int count, Action onChanged)
    {
        Name = name;
        _count = count;
        _onChanged = onChanged;
    }

    public string Name { get; }

    /// <summary>
    /// How many plugins carry this tag within the *rest* of the current query.
    /// </summary>
    /// <remarks>Recomputed whenever any other filter changes, so it always answers "how many would I
    /// get if I ticked this" rather than reporting a fixed catalog-wide total.</remarks>
    [ObservableProperty]
    private int _count;

    public string Label => $"{Name} ({Count})";

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Label));

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}
