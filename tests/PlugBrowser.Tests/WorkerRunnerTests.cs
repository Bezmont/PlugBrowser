using PlugBrowser.Core.Probing;
using Xunit;

namespace PlugBrowser.Tests;

public class WorkerSelectionTests
{
    /// <summary>
    /// The native worker wins whenever it has been built.
    /// </summary>
    /// <remarks>
    /// Not a preference but a capability difference: the managed worker cannot load PACE/iLok-protected
    /// plugins at all — Plugin Alliance's bx_* titles, elysia, SPL and Knifonium spin at 100% CPU inside
    /// the loader and never return, while loading in seconds in a native host. Picking the wrong one
    /// silently costs a chunk of the catalog, so the choice is worth pinning.
    /// </remarks>
    [Fact]
    public void NativeWorkerIsPreferred_WhenBothArePresent()
    {
        using var tree = new TempTree();
        tree.File(WorkerRunner.NativeWorkerFileName, "native");
        tree.File(WorkerRunner.ManagedWorkerFileName, "managed");

        string chosen = WorkerRunner.ResolveWorkerPath(tree.Root);

        Assert.Equal(WorkerRunner.NativeWorkerFileName, Path.GetFileName(chosen));
        Assert.True(new WorkerRunner(chosen).IsNative);
    }

    /// <summary>Building the native worker needs a C++ toolchain, so its absence must degrade rather
    /// than break: the app still works, just without the protected plugins.</summary>
    [Fact]
    public void FallsBackToTheManagedWorker_WhenTheNativeOneIsNotBuilt()
    {
        using var tree = new TempTree();
        tree.File(WorkerRunner.ManagedWorkerFileName, "managed");

        string chosen = WorkerRunner.ResolveWorkerPath(tree.Root);

        Assert.Equal(WorkerRunner.ManagedWorkerFileName, Path.GetFileName(chosen));
        Assert.False(new WorkerRunner(chosen).IsNative);
    }

    /// <summary>With neither present the path still resolves, and IsAvailable is what reports the
    /// problem — callers check that and fall back to discovery-only scanning.</summary>
    [Fact]
    public void ReportsUnavailable_WhenNoWorkerExists()
    {
        using var tree = new TempTree();

        var runner = new WorkerRunner(WorkerRunner.ResolveWorkerPath(tree.Root));

        Assert.False(runner.IsAvailable);
    }

    [Fact]
    public async Task RunAsync_ReportsNotRun_WhenTheWorkerIsMissing()
    {
        using var tree = new TempTree();
        var runner = new WorkerRunner(Path.Combine(tree.Root, "absent.exe"));

        var run = await runner.RunAsync(@"C:\x\Thing.vst3", tree.Dir("out"));

        Assert.Equal(WorkerOutcome.NotRun, run.Outcome);
        Assert.Null(run.Result);
        Assert.NotNull(run.Detail);
    }
}
