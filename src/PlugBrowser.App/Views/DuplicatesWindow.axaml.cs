using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PlugBrowser.App.ViewModels;

namespace PlugBrowser.App.Views;

public partial class DuplicatesWindow : Window
{
    public DuplicatesWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Win11Chrome.Attach(this, titleBarHeight: 32);
    }

    /// <summary>Dims the copy in the format worth keeping, so the removable ones stand out.</summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is DuplicateRowViewModel row)
            e.Row.Classes.Set("preferred", row.IsPreferredFormat);
    }

    /// <summary>Puts the grid on the clipboard as TSV, which pastes straight into a spreadsheet.</summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DuplicatesViewModel viewModel || Clipboard is null)
            return;

        await Clipboard.SetTextAsync(viewModel.ToTabSeparated());
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
