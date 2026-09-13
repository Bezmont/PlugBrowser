using PlugBrowser.App.ViewModels;
using Xunit;

namespace PlugBrowser.Tests;

/// <summary>
/// While selected plugins are reloaded, the status bar counts against the selection — not against the
/// whole catalog, which made a five-plugin reload read like progress through hundreds.
/// </summary>
public class ReloadStatusTests
{
    [Theory]
    [InlineData(0, 5, "1 of 5 selected")]   // first plugin in progress
    [InlineData(3, 5, "4 of 5 selected")]
    [InlineData(4, 5, "5 of 5 selected")]   // last plugin in progress
    [InlineData(5, 5, "5 of 5 selected")]   // the final report: all done, never "6 of 5"
    [InlineData(0, 1, "1 of 1 selected")]
    public void ReloadCountLabel_CountsTheCurrentPluginAgainstTheSelection(int completed, int total, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.ReloadCountLabel(completed, total));
    }
}
