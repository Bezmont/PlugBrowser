using PlugBrowser.Core.Model;
using PlugBrowser.Core.Probing;
using Xunit;

namespace PlugBrowser.Tests;

public class ScanLogTests
{
    private static readonly PluginEntry Entry = new()
    {
        Path = @"C:\Plugins\Comp FET-76.vst3",
        Format = PluginFormat.Vst3,
        Architecture = PluginArchitecture.X64,
    };

    [Fact]
    public void Capture_errors_the_catalog_drops_are_written_to_the_log()
    {
        using var tree = new TempTree();
        string path = Path.Combine(tree.Root, "scan.log");

        var run = new WorkerRun(WorkerOutcome.Completed, new InspectResult
        {
            Path = Entry.Path,
            Loaded = true,
            Enumerated = true,
            Classes =
            [
                new InspectedClass
                {
                    Index = 0, Name = "Comp FET-76", Vendor = "Arturia", Instantiated = true, HasEditor = true,
                    EditorWidth = 960, EditorHeight = 508,
                    CaptureError = "Plugin opened its own window (likely an authorization or trial dialog).",
                },
            ],
        }, null, TimeSpan.FromSeconds(4), Output: "Arturia: license not found");

        using (var log = ScanLog.Open(path))
            log.Probed(Entry, run, Entry with { State = ProbeState.Probed });

        string text = File.ReadAllText(path);
        Assert.Contains("NO IMAGE", text);
        Assert.Contains(Entry.Path, text);
        Assert.Contains("960x508", text);
        Assert.Contains("capture failed: Plugin opened its own window", text);
        Assert.Contains("| Arturia: license not found", text);
    }

    [Fact]
    public void A_new_scan_appends_until_the_log_is_too_large_then_rotates_it()
    {
        using var tree = new TempTree();
        string path = Path.Combine(tree.Root, "scan.log");

        using (var log = ScanLog.Open(path))
            log.Line("first");
        using (var log = ScanLog.Open(path))
            log.Line("second");

        Assert.Contains("first", File.ReadAllText(path));
        Assert.Contains("second", File.ReadAllText(path));

        File.AppendAllText(path, new string('x', (int)ScanLog.MaxBytes));
        using (var log = ScanLog.Open(path))
            log.Line("third");

        Assert.DoesNotContain("first", File.ReadAllText(path));
        Assert.Contains("first", File.ReadAllText(Path.Combine(tree.Root, "scan.previous.log")));
    }
}
