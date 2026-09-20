// Off-screen editor hosting and screenshot capture.
//
// Same algorithm as the managed worker this replaces: create a top-level window far off any monitor,
// parent the plug-in's editor into a child of it, pump until the drawing settles, then PrintWindow.
// When that comes back blank, the window is brought on screen and captured again -- see MoveOnScreen.
#pragma once

#include <string>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace plugbrowser {

/// Starts and stops GDI+, which is used only to encode PNGs.
class GdiPlusSession {
public:
    GdiPlusSession();
    ~GdiPlusSession();
    GdiPlusSession(const GdiPlusSession&) = delete;
    GdiPlusSession& operator=(const GdiPlusSession&) = delete;

private:
    ULONG_PTR token_ = 0;
};

struct CaptureResult {
    bool        ok = false;
    int         width = 0;
    int         height = 0;
    bool        blank = false;  ///< Captured, but empty -- the case another rung of the ladder can fix.
    std::string error;    ///< Why it failed, when it did.
    std::string method;   ///< Which rung of the ladder produced it.
};

/// The pair of windows a VST3 editor needs in order to exist, positioned where nobody can see them.
class EditorHostWindow {
public:
    ~EditorHostWindow();

    /// Creates the windows. The plug-in's editor is attached separately, by the caller.
    bool Create(int width, int height);

    /// Resizes both windows, for when the plug-in reports its real size only after attaching.
    void Resize(int width, int height);

    /// Brings the host window onto the primary monitor, for editors that draw nothing off screen.
    ///
    /// OpenGL may skip drawing any pixel that is not on a monitor -- the spec's "pixel ownership test".
    /// Whether a driver actually does depends on the GPU setup: on a hybrid laptop the frame is drawn off
    /// screen and copied in whole, so a window parked at -32000 captures fine, but a desktop GPU driving
    /// the display directly skips every pixel and PrintWindow returns an empty window. Every Arturia
    /// editor draws through OpenGL, and every one came back blank that way on a desktop machine.
    ///
    /// When |visible| is false the window is a click-through layered window at 1/255 opacity: on screen
    /// as far as the driver is concerned, invisible as far as the user is. PrintWindow reads the window's
    /// own content, not the blended result, so the opacity does not reach the capture. When true it is
    /// fully opaque -- the last resort, for a driver that also skips drawing a transparent window.
    void MoveOnScreen(bool visible);

    /// Runs the message loop for the given duration.
    static void PumpMessages(DWORD milliseconds);

    /// Pumps until two consecutive captures are identical, or the budget runs out.
    void WaitUntilStable(DWORD minimumMs, DWORD maximumMs);

    /// True when a visible top-level window appeared that we did not create — an authorisation or
    /// trial dialog, most often.
    bool ForeignWindowAppeared() const;

    HWND TopLevel() const { return topLevel_; }
    HWND Child() const { return child_; }

private:
    HWND topLevel_ = nullptr;
    HWND child_ = nullptr;
};

/// Captures a window with PrintWindow and writes it as a PNG, plus a downscaled thumbnail.
/// Rejects blank results, which is how a GPU-rendered editor fails. |method| names the rung for the report.
CaptureResult CaptureWindowToPng(HWND window, const std::wstring& pngPath, const char* method);

}  // namespace plugbrowser
