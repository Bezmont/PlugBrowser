using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PlugBrowser.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow() => AvaloniaXamlLoader.Load(this);
}
