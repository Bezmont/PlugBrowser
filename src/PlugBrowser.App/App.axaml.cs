using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PlugBrowser.App.ViewModels;
using PlugBrowser.App.Views;
using PlugBrowser.Core.Storage;

namespace PlugBrowser.App;

public partial class App : Application
{
    /// <summary>Shortest time the splash stays up, so a fast start does not flash it on and off.</summary>
    private static readonly TimeSpan MinimumSplashTime = TimeSpan.FromSeconds(1);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The splash is the main window until the real one is ready; the lifetime shows it as soon
            // as this method returns, before any of the loading below has started.
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            StartAsync(desktop, splash);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Loads the catalog behind the splash, then swaps the splash for the main window.
    /// </summary>
    /// <remarks>
    /// <para>async void on purpose: this is the app's startup, and a failure here (an unopenable
    /// database, say) must surface as the crash it always was, not vanish into an unobserved task
    /// and leave the splash up forever.</para>
    /// <para>The catalog is loaded before the main window is constructed, so its bindings attach to a
    /// populated view model — restoring filters afterwards would fight the controls, which write their
    /// own defaults back as they bind. Loading off the UI thread is safe because nothing is bound to
    /// the view model yet.</para>
    /// </remarks>
    private static async void StartAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        var shown = Stopwatch.StartNew();

        // Closing the splash (Alt+F4) before loading finishes means "don't start after all".
        bool abandoned = false;
        bool handedOver = false;
        splash.Closed += (_, _) => abandoned = !handedOver;

        var (store, viewModel) = await Task.Run(() =>
        {
            // The store is owned for the life of the app: it holds the SQLite connection, and opening
            // it per-operation would fight WAL mode for no benefit.
            var store = CatalogStore.Open();
            var viewModel = new MainWindowViewModel(store);
            viewModel.LoadCatalog();
            viewModel.RestoreState();
            return (store, viewModel);
        });

        var remaining = MinimumSplashTime - shown.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);

        if (abandoned)
        {
            store.Dispose();
            return;
        }

        var main = new MainWindow(store) { DataContext = viewModel };
        desktop.MainWindow = main;
        desktop.ShutdownRequested += (_, _) => store.Dispose();

        // The splash goes only once the main window has laid out and drawn its first frame, so the
        // hand-over never shows an empty window. Background priority runs after layout and render.
        main.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            handedOver = true;
            splash.Close();
            main.Activate();
        }, DispatcherPriority.Background);

        main.Show();
    }
}
