using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PlugBrowser.App.ViewModels;

namespace PlugBrowser.App.Views;

public partial class PluginListWindow : Window
{
    public PluginListWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Win11Chrome.Attach(this, titleBarHeight: 32);
    }

    /// <summary>The rows as the grid shows them.</summary>
    /// <remarks>Read from the grid's own view rather than the view model's rows, so an export follows
    /// whatever column the user has sorted by.</remarks>
    private List<PluginRowViewModel>? DisplayedRows() =>
        this.FindControl<DataGrid>("Grid")?.CollectionView?.Cast<PluginRowViewModel>().ToList();

    /// <summary>Puts the visible rows on the clipboard as TSV, which pastes straight into a spreadsheet.</summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PluginListViewModel viewModel || Clipboard is null)
            return;

        await Clipboard.SetTextAsync(viewModel.ToTabSeparated(DisplayedRows()));
    }

    /// <summary>Saves the visible rows as a CSV file.</summary>
    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PluginListViewModel viewModel)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export plugins as CSV",
            SuggestedFileName = $"PlugBrowser plugins {DateTime.Now:yyyy-MM-dd}.csv",
            DefaultExtension = "csv",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("CSV file") { Patterns = ["*.csv"] }],
        });
        if (file is null)
            return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);

            // With a BOM, because Excel otherwise reads the file as ANSI and mangles accented names.
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await writer.WriteAsync(viewModel.ToCsv(DisplayedRows()));
            viewModel.ExportStatus = $"Saved {file.Name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            viewModel.ExportStatus = $"Could not save: {ex.Message}";
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
