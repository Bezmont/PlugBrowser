using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Microsoft.Win32;

namespace PlugBrowser.App.Views;

/// <summary>
/// Windows 11 chrome, matching the owner's other apps: Mica behind the whole window, a dark frame,
/// rounded corners, and the user's accent colour on the window border and nowhere else.
/// </summary>
/// <remarks>
/// <para>The content is extended into the title bar rather than leaving the system one in place. With
/// "Show accent colour on title bars" switched on in Settings, a system title bar is painted solid in
/// the accent; extending over it leaves the accent on the outline only, and lets Mica run through the
/// caption strip. The system's own caption buttons are kept, so snapping, the right-click system menu
/// and accessibility behave exactly as in any other window.</para>
/// <para>Anything drawn in the caption strip must have no background of its own where the window
/// should be draggable: Avalonia treats any hit-testable visual there as client area. Backgrounds go
/// on a separate, non-hit-testable layer underneath.</para>
/// </remarks>
public static class Win11Chrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    /// <summary>What shows when Mica is unavailable (Windows 10, or transparency effects switched off):
    /// the Windows 11 dark base colour, so the window still reads as the same app.</summary>
    private static readonly IBrush FallbackBackground = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Applies the chrome. Call from the window's constructor, before it is shown.</summary>
    /// <param name="titleBarHeight">Height of the caption strip the window draws itself.</param>
    public static void Attach(Window window, double titleBarHeight)
    {
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
        window.ExtendClientAreaTitleBarHeightHint = titleBarHeight;

        // Mica only shows through a window that paints no background of its own.
        window.TransparencyLevelHint = [WindowTransparencyLevel.Mica];
        window.Background = Brushes.Transparent;
        window.TransparencyBackgroundFallback = FallbackBackground;

        void OnColorsChanged(object? sender, PlatformColorValues e) => ApplyBorder(window);

        window.Opened += (_, _) =>
        {
            ApplyFrame(window);
            if (window.PlatformSettings is { } settings)
                settings.ColorValuesChanged += OnColorsChanged;
        };

        window.Closed += (_, _) =>
        {
            if (window.PlatformSettings is { } settings)
                settings.ColorValuesChanged -= OnColorsChanged;
        };
    }

    private static void ApplyFrame(Window window)
    {
        if (Handle(window) is not { } hwnd)
            return;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // Corners and border are left to DWM rather than drawn by the app: a border drawn inside the
        // window is a square rectangle that the rounded window region clips at the corners.
        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        ApplyBorder(window);
    }

    /// <summary>Paints the window border in the user's accent colour.</summary>
    private static void ApplyBorder(Window window)
    {
        if (Handle(window) is not { } hwnd)
            return;

        var accent = ReadAccent();

        // DWMWA_BORDER_COLOR takes a COLORREF, 0x00BBGGRR — the reverse of the ARGB used elsewhere.
        int colorRef = accent.R | (accent.G << 8) | (accent.B << 16);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }

    private static IntPtr? Handle(Window window) =>
        OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { Handle: var h } && h != IntPtr.Zero
            ? h
            : null;

    /// <summary>
    /// The accent chosen in Settings, as DWM stores it: a DWORD in 0xAABBGGRR order. Read from the
    /// registry rather than DwmGetColorizationColor, which returns the blended colourisation value and
    /// so would not match the border every other Windows 11 window draws. Falls back to Windows blue.
    /// </summary>
    private static Color ReadAccent()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key?.GetValue("AccentColor") is int stored)
                {
                    uint abgr = unchecked((uint)stored);
                    return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                          or IOException)
            {
                // Fall through to the default.
            }
        }
        return Color.FromRgb(0x00, 0x78, 0xD4);
    }
}
