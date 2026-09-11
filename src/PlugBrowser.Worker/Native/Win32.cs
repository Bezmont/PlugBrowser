using System.Runtime.InteropServices;

namespace PlugBrowser.Worker.Native;

/// <summary>The Win32 surface needed to host a plugin editor off-screen and photograph it.</summary>
internal static partial class Win32
{
    public const int WS_CHILD = unchecked((int)0x4000_0000);
    public const int WS_VISIBLE = 0x1000_0000;
    public const int WS_CLIPCHILDREN = 0x0200_0000;
    public const int WS_POPUP = unchecked((int)0x8000_0000);

    public const int WS_EX_TOOLWINDOW = 0x0000_0080;
    public const int WS_EX_NOACTIVATE = 0x0800_0000;

    /// <summary>Render the full window content, including layers DWM composes.</summary>
    /// <remarks>Without this flag PrintWindow only replays the window's GDI paint, which misses almost
    /// every modern plugin editor.</remarks>
    public const uint PW_RENDERFULLCONTENT = 0x0000_0002;

    public const uint PM_REMOVE = 0x0001;

    /// <summary>
    /// Far off the left of any real monitor. The host window is created here so it is never visible and
    /// can never steal focus, while remaining a genuine composited window that PrintWindow can capture.
    /// </summary>
    public const int OffscreenX = -32000;
    public const int OffscreenY = -32000;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    public static partial IntPtr CreateWindowEx(int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height,
        [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax,
        uint removeMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Msg msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(in Msg msg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "EnumThreadWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr param);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    /// <summary>
    /// Runs the message loop for <paramref name="duration"/>.
    /// </summary>
    /// <remarks>
    /// Plugin editors paint asynchronously — they answer WM_PAINT, run animation timers, and some show a
    /// splash before their real UI. Without a real pump the window never draws and every capture is
    /// blank, so this is load-bearing rather than a politeness.
    /// </remarks>
    public static void PumpMessages(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(in msg);
                DispatchMessage(in msg);
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>Counts visible top-level windows owned by this thread, excluding those we created.</summary>
    /// <remarks>An unexpected window almost always means the plugin popped an authorization or trial
    /// dialog (iLok, Waves, NI). Capturing that would file a picture of a licence prompt as the
    /// plugin's UI, so it is worth detecting and reporting instead.</remarks>
    public static int CountForeignWindows(IReadOnlySet<IntPtr> ours)
    {
        int count = 0;
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            if (!ours.Contains(hwnd) && IsWindowVisible(hwnd))
                count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }
}
