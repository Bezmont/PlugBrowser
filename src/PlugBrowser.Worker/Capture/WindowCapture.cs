using System.Drawing;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using PlugBrowser.Worker.Native;

namespace PlugBrowser.Worker.Capture;

/// <summary>Outcome of one capture attempt.</summary>
/// <param name="Bitmap">The captured image, or null on failure. Caller owns and must dispose it.</param>
/// <param name="Method">Which rung of the ladder produced it.</param>
/// <param name="Error">Why it failed, when it did.</param>
internal sealed record CaptureOutcome(Bitmap? Bitmap, string Method, string? Error)
{
    public bool Success => Bitmap is not null;

    public static CaptureOutcome Failed(string method, string error) => new(null, method, error);
}

/// <summary>
/// Photographs a window that is never shown on screen.
/// </summary>
/// <remarks>
/// <para><b>PrintWindow with PW_RENDERFULLCONTENT</b> is the first and best rung: it asks DWM for the
/// window's composed content, works while the window sits off-desktop, and costs nothing.</para>
/// <para>It does not work for everything. A plugin editor drawing through OpenGL or Direct3D — which
/// describes a large share of modern plugins — typically returns a <em>solid black image and a success
/// return code</em>. There is no error to check, so the only way to detect the failure is to look at the
/// pixels, which is what <see cref="IsBlank"/> exists for. Anything that fails that test needs a
/// different capture path (Windows.Graphics.Capture, then an on-screen grab), which is why the outcome
/// records the method rather than just the image.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowCapture
{
    public const string MethodPrintWindow = "PrintWindow";

    /// <summary>Captures a window with PrintWindow, rejecting blank results.</summary>
    public static CaptureOutcome TryPrintWindow(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return CaptureOutcome.Failed(MethodPrintWindow, "Window has no measurable size.");

        // A plugin reporting an absurd editor size is corrupt or confused; refuse rather than trying to
        // allocate a bitmap of that size.
        if (rect.Width > 8000 || rect.Height > 8000)
            return CaptureOutcome.Failed(MethodPrintWindow,
                $"Editor reported an implausible size ({rect.Width}x{rect.Height}).");

        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr hdc = graphics.GetHdc();
                try
                {
                    if (!Win32.PrintWindow(hwnd, hdc, Win32.PW_RENDERFULLCONTENT))
                    {
                        bitmap.Dispose();
                        return CaptureOutcome.Failed(MethodPrintWindow, "PrintWindow returned false.");
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            if (IsBlank(bitmap))
            {
                bitmap.Dispose();
                return CaptureOutcome.Failed(MethodPrintWindow,
                    "Captured image was blank — the editor most likely renders through OpenGL or Direct3D.");
            }

            return new CaptureOutcome(bitmap, MethodPrintWindow, null);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or ArgumentException)
        {
            bitmap.Dispose();
            return CaptureOutcome.Failed(MethodPrintWindow, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// True when an image carries no real content.
    /// </summary>
    /// <remarks>
    /// This is the detector for the silent PrintWindow failure described above, so it has to distinguish
    /// "solid black" from "a dark plugin UI" — and plugin UIs are overwhelmingly dark. Counting distinct
    /// colours over a sparse grid does that: a flat fill yields one or two, whereas even the plainest
    /// real interface yields dozens once text and borders are included. The grid is strided rather than
    /// exhaustive because this runs on every capture and per-pixel GetPixel is slow.
    /// </remarks>
    public static bool IsBlank(Bitmap bitmap)
    {
        const int MinimumDistinctColours = 4;

        int strideX = Math.Max(1, bitmap.Width / 40);
        int strideY = Math.Max(1, bitmap.Height / 40);

        var colours = new HashSet<int>();
        for (int x = 0; x < bitmap.Width; x += strideX)
        {
            for (int y = 0; y < bitmap.Height; y += strideY)
            {
                colours.Add(bitmap.GetPixel(x, y).ToArgb());
                if (colours.Count >= MinimumDistinctColours)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Trims uniform bands from the edges of a capture.
    /// </summary>
    /// <remarks>
    /// A plugin's reported editor size is not always the size it draws. Arturia's Comp FET-76, for
    /// instance, reports 960x508 — the height it needs with its "Advanced" panel expanded — but paints
    /// only the top ~295px while that panel is collapsed, leaving a dead band of host window background
    /// underneath. Cropping edge rows and columns that are entirely one colour removes it.
    /// <para>Only fully uniform bands are removed, and only from the outside in, so a legitimately flat
    /// background <em>within</em> the interface is never touched. If the whole image is uniform it is
    /// left alone — that is a blank capture, which <see cref="IsBlank"/> is responsible for, not this.</para>
    /// </remarks>
    public static Bitmap TrimUniformEdges(Bitmap source)
    {
        int left = 0, top = 0, right = source.Width - 1, bottom = source.Height - 1;

        while (top < bottom && IsUniformRow(source, top, left, right)) top++;
        while (bottom > top && IsUniformRow(source, bottom, left, right)) bottom--;
        while (left < right && IsUniformColumn(source, left, top, bottom)) left++;
        while (right > left && IsUniformColumn(source, right, top, bottom)) right--;

        int width = right - left + 1;
        int height = bottom - top + 1;

        // Nothing to do, or the image was uniform end to end.
        if (width < 8 || height < 8 || (width == source.Width && height == source.Height))
            return source;

        var cropped = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(cropped))
            graphics.DrawImage(source, new Rectangle(0, 0, width, height),
                new Rectangle(left, top, width, height), GraphicsUnit.Pixel);

        source.Dispose();
        return cropped;
    }

    private static bool IsUniformRow(Bitmap bitmap, int y, int left, int right)
    {
        int first = bitmap.GetPixel(left, y).ToArgb();
        for (int x = left + 1; x <= right; x++)
        {
            if (bitmap.GetPixel(x, y).ToArgb() != first)
                return false;
        }
        return true;
    }

    private static bool IsUniformColumn(Bitmap bitmap, int x, int top, int bottom)
    {
        int first = bitmap.GetPixel(x, top).ToArgb();
        for (int y = top + 1; y <= bottom; y++)
        {
            if (bitmap.GetPixel(x, y).ToArgb() != first)
                return false;
        }
        return true;
    }

    /// <summary>Saves a PNG, plus a downscaled thumbnail beside it.</summary>
    /// <returns>The full-size image path.</returns>
    public static string Save(Bitmap bitmap, string directory, string baseName)
    {
        Directory.CreateDirectory(directory);

        string fullPath = Path.Combine(directory, baseName + ".png");
        bitmap.Save(fullPath, ImageFormat.Png);

        // The gallery draws hundreds of cards at once; decoding full-size editor images for each would
        // cost far more memory than the grid needs.
        const int ThumbnailWidth = 320;
        if (bitmap.Width > ThumbnailWidth)
        {
            int height = Math.Max(1, bitmap.Height * ThumbnailWidth / bitmap.Width);
            using var thumbnail = new Bitmap(bitmap, new Size(ThumbnailWidth, height));
            thumbnail.Save(Path.Combine(directory, baseName + ".thumb.png"), ImageFormat.Png);
        }

        return fullPath;
    }
}
