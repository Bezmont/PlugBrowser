using System.Runtime.Versioning;
using NetPlugHost;
using PlugBrowser.Core.Probing;
using PlugBrowser.Worker.Capture;
using PlugBrowser.Worker.Native;

namespace PlugBrowser.Worker;

/// <summary>
/// Inspects exactly one plugin bundle and exits.
/// </summary>
/// <remarks>
/// <para>This runs as a separate process for one reason: third-party plugin code is not trustworthy in
/// this context. A plugin may crash on load, hang forever waiting on a licence server, or leave the
/// process in a state no managed exception handler can recover. Isolating it means the worst outcome is
/// one bundle recorded as a failure rather than a dead catalog or a dead UI.</para>
/// <para>The parent enforces the real timeout by killing the process tree. The internal watchdog here is
/// a backstop for running the worker directly.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBadArguments = 1;
    private const int ExitTimedOut = 3;

    /// <summary>Cap on parameters recorded per class. Some plugins expose thousands and the catalog
    /// only shows a list; carrying them all would bloat every report for no gain.</summary>
    private const int MaxParameters = 512;

    [STAThread]
    public static int Main(string[] args)
    {
        var options = WorkerOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(WorkerOptions.Usage);
            return ExitBadArguments;
        }

        StartWatchdog(options.Timeout, options);
        Directory.CreateDirectory(options.OutputDirectory);

        var result = Inspect(options);
        Write(result, options.OutputDirectory);
        return ExitOk;
    }

    /// <summary>Kills the process if inspection overruns, so a hung plugin cannot wedge the worker.</summary>
    /// <remarks>
    /// <see cref="Environment.Exit(int)"/> would run finalizers and could itself block inside plugin
    /// code, so this ends the process outright. A report is written first: FailFast exits with
    /// <c>0xC0000409</c>, which the parent cannot tell apart from a crash, so without this record every
    /// hung plugin would be filed as "failed to load" instead of "timed out".
    /// </remarks>
    private static void StartWatchdog(TimeSpan timeout, WorkerOptions options)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(timeout);

            try
            {
                Write(new InspectResult
                {
                    Path = options.BundlePath,
                    Loaded = true,
                    TimedOut = true,
                    Error = $"Gave up after {timeout.TotalSeconds:0.#}s — the plugin stopped responding.",
                }, options.OutputDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing more to try; the process is about to end regardless.
            }

            Environment.FailFast($"PlugBrowser.Worker exceeded its {timeout.TotalSeconds:0.#}s budget.");
        })
        {
            IsBackground = true,
            Name = "worker-watchdog",
        };
        watchdog.Start();
    }

    private static InspectResult Inspect(WorkerOptions options)
    {
        Vst3Module module;
        try
        {
            module = Vst3Module.Load(options.BundlePath);
        }
        catch (Exception ex)
        {
            return new InspectResult
            {
                Path = options.BundlePath,
                Loaded = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
            };
        }

        // Recorded before any class is touched. If a plugin hangs and the watchdog kills the process,
        // this is the only evidence that survives — without it a hang on the very first class leaves no
        // file at all, which is indistinguishable from the worker never having run.
        Write(new InspectResult { Path = options.BundlePath, Loaded = true }, options.OutputDirectory);

        var classes = new List<InspectedClass>();
        try
        {
            int count = module.ClassCount;
            for (int i = 0; i < count; i++)
            {
                if (options.ClassIndex is { } only && only != i)
                    continue;

                classes.Add(InspectClass(module, i, options));

                // Written after every class so a crash on class 2 does not discard classes 0 and 1.
                Write(new InspectResult { Path = options.BundlePath, Loaded = true, Classes = classes },
                    options.OutputDirectory);
            }
        }
        catch (Exception ex)
        {
            return new InspectResult
            {
                Path = options.BundlePath,
                Loaded = true,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                Classes = classes,
            };
        }
        finally
        {
            // Every plugin must be disposed before the module, or the ABI treats it as a use-after-free.
            // InspectClass guarantees that; this only unloads the bundle.
            try { module.Dispose(); }
            catch (InvalidOperationException) { /* a plugin outlived its scope; leave the bundle loaded */ }
        }

        return new InspectResult
        {
            Path = options.BundlePath,
            Loaded = true,
            Enumerated = true,
            Classes = classes,
        };
    }

    private static InspectedClass InspectClass(Vst3Module module, int index, WorkerOptions options)
    {
        Vst3ClassInfo info;
        try
        {
            info = module.GetClassInfo(index);
        }
        catch (Exception ex)
        {
            return new InspectedClass
            {
                Index = index,
                Name = $"(class {index})",
                Instantiated = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
            };
        }

        var described = new InspectedClass
        {
            Index = index,
            Name = info.Name,
            Vendor = NullIfBlank(info.Vendor),
            Category = NullIfBlank(info.Category),
            Version = NullIfBlank(info.Version),
        };

        Vst3Plugin plugin;
        try
        {
            plugin = module.CreatePlugin(index);
        }
        catch (Exception ex)
        {
            return described with { Instantiated = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
        }

        try
        {
            described = described with
            {
                Instantiated = true,
                Parameters = ReadParameters(plugin),
                ParameterCount = SafeParameterCount(plugin),
            };

            bool hasEditor;
            try { hasEditor = plugin.HasEditor; }
            catch (Vst3Exception) { hasEditor = false; }

            described = described with { HasEditor = hasEditor };

            if (!hasEditor)
                return described with { CaptureError = "Plugin exposes no editor view." };

            return options.Capture ? CaptureEditor(plugin, described, options) : described;
        }
        finally
        {
            plugin.Dispose();
        }
    }

    private static InspectedClass CaptureEditor(Vst3Plugin plugin, InspectedClass described,
        WorkerOptions options)
    {
        try
        {
            using var host = EditorHostWindow.Open(plugin);
            host.WaitUntilStable(options.MinimumSettle, options.MaximumSettle);

            described = described with { EditorWidth = host.Width, EditorHeight = host.Height };

            // A window we did not create means the plugin popped a dialog — an authorization or trial
            // prompt, most often. Photographing that would file a picture of a licence screen as the
            // plugin's interface.
            var ours = new HashSet<IntPtr> { host.TopLevel, host.Child };
            if (Win32.CountForeignWindows(ours) > 0)
            {
                return described with
                {
                    CaptureError = "Plugin opened its own window (likely an authorization or trial dialog).",
                };
            }

            var outcome = WindowCapture.TryPrintWindow(host.TopLevel);
            if (!outcome.Success)
                return described with { CaptureError = outcome.Error, CaptureMethod = outcome.Method };

            using var bitmap = WindowCapture.TrimUniformEdges(outcome.Bitmap!);
            string path = WindowCapture.Save(bitmap, options.OutputDirectory, $"class-{described.Index}");

            return described with
            {
                ImagePath = path,
                CaptureMethod = outcome.Method,
                EditorWidth = bitmap.Width,
                EditorHeight = bitmap.Height,
            };
        }
        catch (Exception ex)
        {
            return described with { CaptureError = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    private static int SafeParameterCount(Vst3Plugin plugin)
    {
        try { return plugin.ParameterCount; }
        catch (Vst3Exception) { return 0; }
    }

    private static IReadOnlyList<InspectedParameter> ReadParameters(Vst3Plugin plugin)
    {
        int count;
        try { count = plugin.ParameterCount; }
        catch (Vst3Exception) { return []; }

        var result = new List<InspectedParameter>(Math.Min(count, MaxParameters));
        for (int i = 0; i < Math.Min(count, MaxParameters); i++)
        {
            try
            {
                var info = plugin.GetParameterInfo(i);
                result.Add(new InspectedParameter
                {
                    Id = info.Id,
                    Title = info.Title,
                    Units = NullIfBlank(info.Units),
                    DefaultNormalized = info.DefaultNormalized,
                    StepCount = info.StepCount,
                    IsBypass = info.Flags.HasFlag(Vst3ParamFlags.IsBypass),
                    CanAutomate = info.Flags.HasFlag(Vst3ParamFlags.CanAutomate),
                });
            }
            catch (Vst3Exception)
            {
                // One unreadable parameter should not cost us the rest of them.
            }
        }
        return result;
    }

    private static void Write(InspectResult result, string directory)
    {
        // Written atomically: the parent may read the file while a later class is still being inspected,
        // and a half-written document would look like a parse failure.
        string finalPath = Path.Combine(directory, "result.json");
        string tempPath = finalPath + ".tmp";

        File.WriteAllText(tempPath, result.ToJson());
        File.Move(tempPath, finalPath, overwrite: true);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Parsed command line.</summary>
internal sealed record WorkerOptions
{
    public required string BundlePath { get; init; }
    public required string OutputDirectory { get; init; }

    /// <summary>Inspect only this class, used to isolate a class that crashed a whole-bundle run.</summary>
    public int? ClassIndex { get; init; }

    public bool Capture { get; init; } = true;

    public TimeSpan MinimumSettle { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaximumSettle { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public const string Usage = """
        PlugBrowser.Worker --inspect <bundle.vst3> --outdir <directory> [options]

          --class <n>        inspect only this class index
          --no-capture       read metadata only, do not open editors
          --settle-ms <n>    minimum settle before capture (default 250)
          --max-settle-ms    maximum settle while the editor keeps changing (default 3000)
          --timeout-ms <n>   hard self-kill budget (default 30000)
        """;

    public static WorkerOptions? Parse(string[] args)
    {
        string? bundle = null, outputDirectory = null;
        int? classIndex = null;
        bool capture = true;
        int settle = 250, maxSettle = 3000, timeout = 30_000;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--inspect" when i + 1 < args.Length: bundle = args[++i]; break;
                case "--outdir" when i + 1 < args.Length: outputDirectory = args[++i]; break;
                case "--class" when i + 1 < args.Length && int.TryParse(args[i + 1], out int c):
                    classIndex = c; i++; break;
                case "--no-capture": capture = false; break;
                case "--settle-ms" when i + 1 < args.Length && int.TryParse(args[i + 1], out int s):
                    settle = s; i++; break;
                case "--max-settle-ms" when i + 1 < args.Length && int.TryParse(args[i + 1], out int m):
                    maxSettle = m; i++; break;
                case "--timeout-ms" when i + 1 < args.Length && int.TryParse(args[i + 1], out int t):
                    timeout = t; i++; break;
                default: return null;
            }
        }

        if (string.IsNullOrWhiteSpace(bundle) || string.IsNullOrWhiteSpace(outputDirectory))
            return null;

        return new WorkerOptions
        {
            BundlePath = bundle,
            OutputDirectory = outputDirectory,
            ClassIndex = classIndex,
            Capture = capture,
            MinimumSettle = TimeSpan.FromMilliseconds(settle),
            MaximumSettle = TimeSpan.FromMilliseconds(maxSettle),
            Timeout = TimeSpan.FromMilliseconds(timeout),
        };
    }
}
