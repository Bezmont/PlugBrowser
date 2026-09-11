// Off-screen editor hosting and screenshot capture.
//
// Same algorithm as the managed worker this replaces: create a top-level window far off any monitor,
// parent the plug-in's editor into a child of it, pump until the drawing settles, then PrintWindow.
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
/// Rejects blank results, which is how a GPU-rendered editor fails.
CaptureResult CaptureWindowToPng(HWND window, const std::wstring& pngPath);

}  // namespace plugbrowser
