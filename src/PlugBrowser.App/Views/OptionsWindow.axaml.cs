using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PlugBrowser.App.ViewModels;

namespace PlugBrowser.App.Views;

public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Win11Chrome.Attach(this, titleBarHeight: 32);
    }

    /// <summary>Picks a folder to add as a scan root.</summary>
    /// <remarks>The picker lives in the view rather than the view model because it needs a parent
    /// window to be modal against; the view model exposes only the resulting path.</remarks>
    private async void OnAddFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel viewModel)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to scan for plugins",
            AllowMultiple = false,
        });

        if (folders.Count == 0)
            return;

        string? path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            viewModel.AddRoot(path);
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    /// <summary>Opens the grid of every detected plugin.</summary>
    private async void OnPluginListClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel viewModel)
            return;

        var window = new PluginListWindow { DataContext = viewModel.CreatePluginListView() };
        await window.ShowDialog(this);
    }
}
