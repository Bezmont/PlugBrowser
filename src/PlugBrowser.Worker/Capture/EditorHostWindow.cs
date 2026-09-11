using System.Runtime.Versioning;
using NetPlugHost;
using PlugBrowser.Worker.Native;

namespace PlugBrowser.Worker.Capture;

/// <summary>
/// The pair of windows a plugin editor needs in order to exist, positioned where nobody can see them.
/// </summary>
/// <remarks>
/// <para>A VST3 editor attaches itself as a child of an HWND the host supplies, so capturing one means
/// creating that HWND. Two windows are needed: an off-desktop top-level popup (a child window alone has
/// nothing to be composed into and cannot be captured), and inside it the plain child the plugin
/// actually parents its view to.</para>
/// <para>The recipe — a <c>STATIC</c> child with <c>WS_CLIPCHILDREN</c>, then re-querying the editor
/// size after attaching because many plugins only know it then — is lifted from StreamRecorder's
/// <c>Vst3EditorWindow</c>, which is the same code path in a visible window.</para>
/// <para><c>WS_EX_NOACTIVATE</c> and <c>WS_EX_TOOLWINDOW</c> keep the window out of the taskbar and
/// stop it stealing focus, which matters when a full scan opens a couple of hundred of these.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class EditorHostWindow : IDisposable
{
    private readonly Vst3Plugin _plugin;
    private bool _editorOpen;

    public IntPtr TopLevel { get; private set; }
    public IntPtr Child { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    private EditorHostWindow(Vst3Plugin plugin) => _plugin = plugin;

    /// <summary>Creates the windows and attaches <paramref name="plugin"/>'s editor to them.</summary>
    /// <exception cref="InvalidOperationException">If a window could not be created.</exception>
    public static EditorHostWindow Open(Vst3Plugin plugin)
    {
        var host = new EditorHostWindow(plugin);
        try
        {
            host.Create();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private void Create()
    {
        // Ask before attaching so the windows start close to the right size; many plugins answer with a
        // placeholder here and only report truthfully once the view is attached, hence the re-query below.
        (Width, Height) = QuerySize(fallbackWidth: 800, fallbackHeight: 600);

        // "STATIC" is a pre-registered window class, which avoids registering one of our own just to
        // own a rectangle.
        TopLevel = Win32.CreateWindowEx(
            Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
            "STATIC", "PlugBrowser capture host",
            Win32.WS_POPUP | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN,
            Win32.OffscreenX, Win32.OffscreenY, Width, Height,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (TopLevel == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the off-screen capture host window.");

        Child = Win32.CreateWindowEx(
            0, "STATIC", null,
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN,
            0, 0, Width, Height,
            TopLevel, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (Child == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the editor child window.");

        _plugin.OpenEditor(Child);
        _editorOpen = true;

        // Re-query: most plugins only know their real editor size once the view is attached.
        var (width, height) = QuerySize(Width, Height);
        if (width != Width || height != Height)
        {
            Width = width;
            Height = height;
            Win32.MoveWindow(TopLevel, Win32.OffscreenX, Win32.OffscreenY, Width, Height, repaint: true);
            Win32.MoveWindow(Child, 0, 0, Width, Height, repaint: true);
        }
    }

    /// <summary>Pumps messages until the editor stops changing, or the budget runs out.</summary>
    /// <remarks>
    /// Fixed delays are a poor fit here: a simple effect is drawn in well under a tenth of a second,
    /// while a large instrument may fade in or show a splash for a second or more. Waiting for two
    /// consecutive identical captures adapts to both — fast plugins finish early, slow ones get the time
    /// they need, and nothing is photographed mid-fade.
    /// </remarks>
    public void WaitUntilStable(TimeSpan minimum, TimeSpan maximum)
    {
        var pollInterval = TimeSpan.FromMilliseconds(120);

        Win32.PumpMessages(minimum);

        string? previous = null;
        var deadline = DateTime.UtcNow + maximum;
        while (DateTime.UtcNow < deadline)
        {
            string? current = Fingerprint();
            if (current is not null && current == previous)
                return;

            previous = current;
            Win32.PumpMessages(pollInterval);
        }
    }

    /// <summary>A cheap signature of what the window currently looks like.</summary>
    /// <returns>Null when the window cannot be captured yet, which counts as "not settled".</returns>
    private string? Fingerprint()
    {
        var outcome = WindowCapture.TryPrintWindow(TopLevel);
        if (!outcome.Success)
            return null;

        using var bitmap = outcome.Bitmap!;
        var builder = new System.Text.StringBuilder();

        // A sparse grid is enough to notice a fade or a splash screen giving way to the real UI, and
        // avoids hashing a multi-megapixel bitmap several times per plugin.
        int strideX = Math.Max(1, bitmap.Width / 16);
        int strideY = Math.Max(1, bitmap.Height / 16);
        for (int x = 0; x < bitmap.Width; x += strideX)
        {
            for (int y = 0; y < bitmap.Height; y += strideY)
                builder.Append(bitmap.GetPixel(x, y).ToArgb().ToString("X8"));
        }
        return builder.ToString();
    }

    private (int Width, int Height) QuerySize(int fallbackWidth, int fallbackHeight)
    {
        try
        {
            var (width, height) = _plugin.GetEditorSize();
            if (width > 0 && height > 0)
                return (Math.Clamp(width, 40, 8000), Math.Clamp(height, 40, 8000));
        }
        catch (Vst3Exception)
        {
            // Plugin declined to report a size; the fallback is only used to create a window it will
            // immediately resize anyway.
        }
        return (fallbackWidth, fallbackHeight);
    }

    public void Dispose()
    {
        if (_editorOpen)
        {
            // Best effort: a plugin that crashed during capture may already be unusable, and throwing
            // here would lose the results for every class after it.
            try { _plugin.CloseEditor(); }
            catch (Vst3Exception) { /* editor already gone */ }
            _editorOpen = false;
        }

        if (Child != IntPtr.Zero)
        {
            Win32.DestroyWindow(Child);
            Child = IntPtr.Zero;
        }

        if (TopLevel != IntPtr.Zero)
        {
            Win32.DestroyWindow(TopLevel);
            TopLevel = IntPtr.Zero;
        }
    }
}
