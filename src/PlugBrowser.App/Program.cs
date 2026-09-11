using Avalonia;

namespace PlugBrowser.App;

internal static class Program
{
    // Avalonia must be initialised before anything touches its types, so keep this method free of
    // any other work and never reference visual types from it.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Also used by the Avalonia XAML previewer, which calls it by convention.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
