using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PlugBrowser.App.ViewModels;
using PlugBrowser.Core.Storage;

namespace PlugBrowser.App.Views;

public partial class MainWindow : Window
{
    private readonly CatalogStore? _store;

    /// <summary>Guards against reacting to a selection this code set itself.</summary>
    private bool _syncingSelection;

    /// <summary>Parameterless constructor for the XAML previewer.</summary>
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Win11Chrome.Attach(this, titleBarHeight: 48);
    }

    public MainWindow(CatalogStore store) : this() => _store = store;

    /// <summary>Restores the saved window size once the view model is attached.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (viewModel.SavedWindowGeometry is { } geometry)
        {
            Width = geometry.Width;
            Height = geometry.Height;
            if (geometry.Maximized)
                WindowState = WindowState.Maximized;
        }

        if (DetailColumn is { } column)
            column.Width = new GridLength(viewModel.DetailPaneWidth);

        // A reload rebuilds every row, so the ListBox has to be told what to re-select.
        viewModel.SelectionRestored += (_, _) => ReapplySelection(viewModel);
    }

    /// <summary>Persists filters and geometry on the way out.</summary>
    /// <remarks>Size is read from <see cref="Window.ClientSize"/> rather than Width/Height because a
    /// maximized window reports its restored size in those, and reopening maximized at the maximized
    /// dimensions would leave an over-sized window when it is later restored.</remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            bool maximized = WindowState == WindowState.Maximized;
            double width = maximized ? Width : ClientSize.Width;
            double height = maximized ? Height : ClientSize.Height;

            if (DetailColumn is { } column)
                viewModel.DetailPaneWidth = column.ActualWidth;

            viewModel.SaveState(width, height, maximized);
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Mirrors the gallery's multi-selection into the view model.
    /// </summary>
    /// <remarks>
    /// Done in the view rather than by binding <c>SelectedItems</c>: that property is a plain
    /// <see cref="System.Collections.IList"/> the control owns and mutates in place, which does not
    /// survive a compiled two-way binding intact.
    /// </remarks>
    private void OnGallerySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || DataContext is not MainWindowViewModel viewModel)
            return;

        if (sender is not ListBox list)
            return;

        viewModel.SetSelection(list.SelectedItems?.OfType<PluginItemViewModel>() ?? []);
    }

    private void ReapplySelection(MainWindowViewModel viewModel)
    {
        if (this.FindControl<ListBox>("Gallery") is not { } gallery)
            return;

        _syncingSelection = true;
        try
        {
            gallery.SelectedItems?.Clear();
            foreach (var item in viewModel.SelectedItems)
                gallery.SelectedItems?.Add(item);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>The resizable detail pane's column — the last one in the body grid.</summary>
    private ColumnDefinition? DetailColumn =>
        this.FindControl<Grid>("Body")?.ColumnDefinitions.LastOrDefault();

    /// <summary>Copies the selected plugin's name, e.g. for pasting into a DAW's plugin search.</summary>
    private async void OnCopyNameClick(object? sender, RoutedEventArgs e) => await CopySelectedNameAsync();

    /// <summary>
    /// Puts the selected plugin's name on the clipboard. Does nothing unless exactly one is selected:
    /// several names would need a format to be joined in, and none is obviously right.
    /// </summary>
    private async Task CopySelectedNameAsync()
    {
        if (DataContext is not MainWindowViewModel { HasSingleSelected: true, Selected: { } selected } viewModel)
            return;

        if (GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        await clipboard.SetTextAsync(selected.Name);
        viewModel.Status = $"Copied “{selected.Name}” to the clipboard.";
    }

    /// <summary>Double-clicking a card opens its screenshot full size.</summary>
    private void OnGalleryDoubleTapped(object? sender, TappedEventArgs e)
    {
        // A quick double click on a card tag is two filter clicks, not a request to zoom.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        if (DataContext is MainWindowViewModel viewModel)
            viewModel.ZoomImageCommand.Execute(null);
    }

    /// <summary>Clicking the detail pane's thumbnail opens it full size.</summary>
    private void OnDetailImagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.ZoomImageCommand.Execute(null);
    }

    /// <summary>A click anywhere on the overlay dismisses it.</summary>
    private void OnZoomOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.CloseZoomCommand.Execute(null);

        e.Handled = true;
    }

    /// <summary>Escape also closes the overlay, which is what every image viewer trains people to try.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel { IsImageZoomed: true } viewModel)
        {
            viewModel.CloseZoomCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // The platform's copy gestures (Ctrl+C, and Ctrl+Insert on Windows) copy the selected plugin's
        // name — except while a text box has focus, where they must keep copying the selected text.
        if (IsCopyGesture(e) && FocusManager?.GetFocusedElement() is not TextBox)
        {
            _ = CopySelectedNameAsync();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private bool IsCopyGesture(KeyEventArgs e)
    {
        var gestures = PlatformSettings?.HotkeyConfiguration.Copy;
        return gestures is { Count: > 0 }
            ? gestures.Any(g => g.Matches(e))
            : e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control;
    }

    private async void OnOptionsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || _store is null)
            return;

        var options = new OptionsViewModel(_store);

        // Fires when a scan ends — including a cancelled one, which still keeps everything captured
        // before the cancel — so the gallery is current the moment the dialog is dismissed.
        options.CatalogChanged += (_, _) => viewModel.LoadCatalog();

        var window = new OptionsWindow { DataContext = options };
        await window.ShowDialog(this);

        viewModel.LoadCatalog();
    }
}
