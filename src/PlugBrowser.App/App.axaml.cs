using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PlugBrowser.App.ViewModels;
using PlugBrowser.App.Views;
using PlugBrowser.Core.Storage;

namespace PlugBrowser.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The store is owned for the life of the app: it holds the SQLite connection, and opening it
            // per-operation would fight WAL mode for no benefit.
            var store = CatalogStore.Open();
            var viewModel = new MainWindowViewModel(store);

            // Loaded before the window is constructed so its bindings attach to a populated view model.
            // Restoring filters afterwards would fight the controls, which write their own defaults back
            // as they bind.
            viewModel.LoadCatalog();
            viewModel.RestoreState();

            desktop.MainWindow = new MainWindow(store) { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => store.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
