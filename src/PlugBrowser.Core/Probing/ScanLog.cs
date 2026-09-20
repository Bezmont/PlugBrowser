using System.Text;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Storage;

namespace PlugBrowser.Core.Probing;

/// <summary>
/// A plain-text record of what a scan did with every bundle, and why each screenshot was or was not taken.
/// </summary>
/// <remarks>
/// <para>The catalog keeps only the outcome — probed, failed, timed out — and the worker's per-class
/// <see cref="InspectedClass.CaptureError"/> never reaches it. So a plugin that loaded fine but produced no
/// picture looked identical to one that was never tried, and a scan that failed on another machine left
/// nothing to diagnose. This file is that evidence, readable without the app.</para>
/// <para>Appended to rather than replaced, so a reload of a few plugins does not wipe the record of the
/// full scan before it. It is rotated to <c>scan.previous.log</c> once it grows past
/// <see cref="MaxBytes"/>.</para>
/// <para>Every write is flushed immediately: the point of the file is to survive the scans that go wrong.</para>
/// </remarks>
public sealed class ScanLog : IDisposable
{
    /// <summary>Size past which the log is rotated when a new scan starts.</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    /// <summary>Worker console output kept per bundle. Plugins can be chatty; the tail is what matters.</summary>
    private const int MaxWorkerOutput = 4000;

    public static string DefaultPath => Path.Combine(CatalogStore.DefaultDirectory, "scan.log");

    private readonly StreamWriter? _writer;
    private readonly Lock _gate = new();

    public string FilePath { get; }

    private ScanLog(string path, StreamWriter? writer)
    {
        FilePath = path;
        _writer = writer;
    }

    /// <summary>Opens the log for appending. Never throws: a log that cannot be written must not stop a scan.</summary>
    public static ScanLog Open(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Rotate(path);

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            return new ScanLog(path, new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ScanLog(path, null);
        }
    }

    private static void Rotate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes)
            return;

        string previous = Path.Combine(info.DirectoryName!,
            Path.GetFileNameWithoutExtension(path) + ".previous" + info.Extension);
        File.Move(path, previous, overwrite: true);
    }

    /// <summary>Header for one scan: when, what kind, and with which worker and settings.</summary>
    public void BeginScan(string kind, WorkerRunner worker, IEnumerable<string> settings)
    {
        var text = new StringBuilder()
            .AppendLine()
            .AppendLine(new string('=', 100))
            .AppendLine($"{kind} started {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"  Machine   {Environment.MachineName} · {Environment.OSVersion.VersionString}")
            .AppendLine($"  App       {typeof(ScanLog).Assembly.GetName().Version}")
            .AppendLine($"  Worker    {worker.WorkerPath} ({(worker.IsNative ? "native" : "managed")}" +
                        $"{(worker.IsAvailable ? "" : ", NOT FOUND")})")
            .AppendLine($"  Timeout   {worker.Timeout.TotalSeconds:0}s per bundle · " +
                        $"capture {(worker.Capture ? "on" : "off")}");

        foreach (string setting in settings)
            text.AppendLine($"  {setting}");

        Write(text.ToString());
    }

    public void Line(string message) => Write($"{Stamp()} {message}{Environment.NewLine}");

    /// <summary>Records a bundle the probing phase will not load, and why.</summary>
    public void NotProbed(PluginEntry entry, string reason) =>
        Line($"NOT PROBED  {entry.Path}  — {reason}");

    /// <summary>Records one worker run in full: outcome, every class, every capture error, and console output.</summary>
    public void Probed(PluginEntry entry, WorkerRun run, PluginEntry merged)
    {
        var text = new StringBuilder();
        text.AppendLine($"{Stamp()} {Label(run, merged)}  {entry.Path}  ({run.Duration.TotalSeconds:0.0}s)");

        if (!string.IsNullOrWhiteSpace(run.Detail))
            text.AppendLine($"      detail: {run.Detail}");

        if (run.Result is { } result)
        {
            if (!string.IsNullOrWhiteSpace(result.Error) && result.Error != run.Detail)
                text.AppendLine($"      error: {result.Error}");

            if (result.Loaded && !result.Enumerated && result.Classes.Count == 0)
                text.AppendLine("      no classes reported (worker stopped before enumerating any)");
            else if (result.NoClassesExposed)
                text.AppendLine("      bundle exposes no classes");

            foreach (var c in result.Classes)
                text.Append(DescribeClass(c));
        }
        else
        {
            text.AppendLine("      no report was produced");
        }

        if (run.Output is { Length: > 0 } output)
        {
            text.AppendLine("      worker output:");
            foreach (string line in Tail(output).Split('\n'))
            {
                if (line.Trim().Length > 0)
                    text.AppendLine($"        | {line.TrimEnd('\r')}");
            }
        }

        Write(text.ToString());
    }

    public void EndScan(ScanSummary summary) =>
        Line($"FINISHED  {summary}");

    private static string Label(WorkerRun run, PluginEntry merged) => run.Outcome switch
    {
        WorkerOutcome.TimedOut => "TIMED OUT ",
        WorkerOutcome.Crashed => "CRASHED   ",
        WorkerOutcome.NotRun => "NOT RUN   ",
        _ when merged.State != ProbeState.Probed => "FAILED    ",
        _ when run.Result?.Classes.Any(c => c.ImagePath is null && c.HasEditor) == true => "NO IMAGE  ",
        _ => "OK        ",
    };

    private static string DescribeClass(InspectedClass c)
    {
        var text = new StringBuilder($"      [{c.Index}] {c.Name}");
        if (c.Vendor is not null)
            text.Append($" ({c.Vendor})");
        if (c.Category is not null)
            text.Append($" · {c.Category}");
        text.AppendLine();

        if (!c.Instantiated)
        {
            text.AppendLine($"          not instantiated: {c.Error ?? "no reason given"}");
            return text.ToString();
        }

        text.Append($"          params {c.ParameterCount} · editor {(c.HasEditor ? "yes" : "no")}");
        if (c.EditorWidth > 0 || c.EditorHeight > 0)
            text.Append($" · {c.EditorWidth}x{c.EditorHeight}");
        if (c.CaptureMethod is not null)
            text.Append($" · via {c.CaptureMethod}");
        text.AppendLine();

        if (c.ImagePath is not null)
            text.AppendLine($"          image: {c.ImagePath}{(File.Exists(c.ImagePath) ? "" : "  (FILE MISSING)")}");
        if (c.CaptureError is not null)
            text.AppendLine($"          capture failed: {c.CaptureError}");
        if (c.Error is not null)
            text.AppendLine($"          error: {c.Error}");

        return text.ToString();
    }

    private static string Tail(string output)
    {
        output = output.Trim();
        return output.Length <= MaxWorkerOutput
            ? output
            : "…" + output[^MaxWorkerOutput..];
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss");

    private void Write(string text)
    {
        if (_writer is null)
            return;

        lock (_gate)
        {
            try { _writer.Write(text); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Losing a log line is acceptable; failing the scan over one is not.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _writer?.Dispose();
    }
}
