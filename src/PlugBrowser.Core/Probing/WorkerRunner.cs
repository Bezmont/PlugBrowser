using System.Diagnostics;

namespace PlugBrowser.Core.Probing;

/// <summary>How a worker run ended.</summary>
public enum WorkerOutcome
{
    /// <summary>The worker finished and produced a report.</summary>
    Completed,
    /// <summary>The worker overran its budget and its process tree was killed.</summary>
    TimedOut,
    /// <summary>The worker died — usually the plugin crashing the process.</summary>
    Crashed,
    /// <summary>The worker could not be started at all.</summary>
    NotRun,
}

/// <param name="Outcome">How the run ended.</param>
/// <param name="Result">The report, when one was produced. A timed-out run may still have a partial
/// report, because the worker writes after every class.</param>
/// <param name="Detail">Human-readable explanation for the failures list.</param>
/// <param name="Duration">How long the run took.</param>
public sealed record WorkerRun(
    WorkerOutcome Outcome, InspectResult? Result, string? Detail, TimeSpan Duration);

/// <summary>
/// Runs <c>PlugBrowser.Worker.exe</c> against one bundle and collects its report.
/// </summary>
/// <remarks>
/// <para>The isolation is the point. Testing against a real library showed large sample-library and
/// amp-sim plugins — Kontakt, Guitar Rig, some Plugin Alliance titles — hanging indefinitely on load.
/// In-process that is an unkillable UI freeze; out-of-process it is one catalog row marked as timed out.</para>
/// <para>Killing the <em>tree</em> rather than the process matters: plugins spawn helper processes
/// (licence daemons, content scanners) that keep handles alive and can outlive a bare
/// <see cref="Process.Kill()"/>.</para>
/// </remarks>
public sealed class WorkerRunner
{
    private readonly string _workerPath;

    /// <summary>Budget per bundle. Generous because a large instrument legitimately takes many seconds
    /// to load its content before its editor can be drawn.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Whether to open editors and capture screenshots, or read metadata only.</summary>
    public bool Capture { get; init; } = true;

    public WorkerRunner(string workerPath) => _workerPath = workerPath;

    /// <summary>True if the worker executable is actually present.</summary>
    public bool IsAvailable => File.Exists(_workerPath);

    /// <summary>Native worker, preferred whenever it has been built.</summary>
    public const string NativeWorkerFileName = "PlugBrowser.NativeWorker.exe";

    /// <summary>Managed worker, used when the native one is absent.</summary>
    public const string ManagedWorkerFileName = "PlugBrowser.Worker.exe";

    /// <summary>Locates the worker beside the running application.</summary>
    public static string DefaultWorkerPath => ResolveWorkerPath(AppContext.BaseDirectory);

    /// <summary>
    /// Picks the native worker when present, falling back to the managed one.
    /// </summary>
    /// <remarks>
    /// <para>The native worker is preferred because the managed one cannot load PACE/iLok-protected
    /// plugins <em>at all</em>: Plugin Alliance's <c>bx_*</c> titles, elysia, SPL and Knifonium spin at
    /// 100% CPU inside the loader and never return, while loading in seconds in any DAW and in a native
    /// host. Their anti-tamper probes for debuggers and instrumentation, and a CLR process presents
    /// exactly that shape, so the protection goes defensive. Taking the runtime out of the process
    /// removes the trigger.</para>
    /// <para>The managed worker remains the fallback because building the native one needs a C++
    /// toolchain. Without it the app still works, just without those plugins.</para>
    /// </remarks>
    public static string ResolveWorkerPath(string baseDirectory)
    {
        string native = Path.Combine(baseDirectory, NativeWorkerFileName);
        return File.Exists(native) ? native : Path.Combine(baseDirectory, ManagedWorkerFileName);
    }

    /// <summary>True when the worker in use is the native build.</summary>
    public bool IsNative =>
        Path.GetFileName(_workerPath).Equals(NativeWorkerFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Inspects one bundle, writing any images into <paramref name="outputDirectory"/>.</summary>
    public async Task<WorkerRun> RunAsync(string bundlePath, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();

        if (!IsAvailable)
            return new WorkerRun(WorkerOutcome.NotRun, null,
                $"Worker not found at {_workerPath}.", started.Elapsed);

        Directory.CreateDirectory(outputDirectory);
        string resultPath = Path.Combine(outputDirectory, "result.json");

        // A stale report from a previous run would otherwise be mistaken for this one's output.
        try { File.Delete(resultPath); }
        catch (IOException) { /* locked; the worker overwrites it atomically anyway */ }

        var startInfo = new ProcessStartInfo
        {
            FileName = _workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Plugins write freely to stdout and stderr while loading. Redirecting keeps that noise out
            // of the host's console; the actual report travels by file.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("--inspect");
        startInfo.ArgumentList.Add(bundlePath);
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--timeout-ms");
        // The worker's own watchdog fires slightly first, giving it a chance to exit cleanly and leave a
        // report behind rather than being killed from outside.
        startInfo.ArgumentList.Add(((int)Timeout.TotalMilliseconds - 2000).ToString());

        if (!Capture)
            startInfo.ArgumentList.Add("--no-capture");

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
                return new WorkerRun(WorkerOutcome.NotRun, null, "Worker failed to start.", started.Elapsed);
        }
        catch (Exception ex)
        {
            return new WorkerRun(WorkerOutcome.NotRun, null,
                $"{ex.GetType().Name}: {ex.Message}", started.Elapsed);
        }

        // Drained on background tasks: a plugin that fills the pipe buffer would otherwise block
        // forever on its own write, and look to us like a hang.
        var drainOut = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var drainError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(Timeout);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            KillTree(process);

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
        }

        await Task.WhenAny(Task.WhenAll(drainOut, drainError), Task.Delay(500, CancellationToken.None))
            .ConfigureAwait(false);

        var result = InspectResult.FromJsonFile(resultPath);

        if (timedOut)
        {
            return new WorkerRun(WorkerOutcome.TimedOut, result,
                $"Exceeded its {Timeout.TotalSeconds:0}s budget and was terminated.", started.Elapsed);
        }

        // A non-zero exit with no report means the plugin took the process down with it.
        int exitCode = SafeExitCode(process);
        if (result is null)
        {
            return new WorkerRun(WorkerOutcome.Crashed, null,
                $"Worker exited with code {exitCode} without producing a report.", started.Elapsed);
        }

        if (!result.Loaded)
            return new WorkerRun(WorkerOutcome.Completed, result, result.Error, started.Elapsed);

        // The worker's own watchdog got there first. Trust its word over the exit code: it ends the
        // process with FailFast, whose 0xC0000409 is indistinguishable from a genuine crash.
        if (result.TimedOut)
            return new WorkerRun(WorkerOutcome.TimedOut, result, result.Error, started.Elapsed);

        // A report that never finished enumerating means the worker died partway through.
        if (!result.Enumerated)
        {
            return new WorkerRun(WorkerOutcome.Crashed, result,
                $"Worker stopped partway through (exit code {exitCode}).", started.Elapsed);
        }

        return new WorkerRun(WorkerOutcome.Completed, result, null, started.Elapsed);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone, or the OS refused. Either way there is nothing further to do.
        }
    }
}
