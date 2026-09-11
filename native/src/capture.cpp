#include "capture.h"

#include <algorithm>
#include <string>
#include <vector>

// GDI+ header prerequisites, in this order:
//  - objidl.h declares IStream, which Gdiplus::Image's overloads take;
//  - the GDI+ headers call min/max unqualified, and capture.h defines NOMINMAX (needed so the Windows
//    macros do not collide with std::min/std::max elsewhere), so the std versions are pulled into
//    scope to satisfy them.
#include <objidl.h>
using std::max;
using std::min;
#include <gdiplus.h>

namespace plugbrowser {
namespace {

// Far off the left of any real monitor. The host window lives here so it is never visible and can
// never steal focus, while remaining a genuine composited window that PrintWindow can capture.
constexpr int kOffscreenX = -32000;
constexpr int kOffscreenY = -32000;

// Render the full window content, including layers DWM composes. Without this flag PrintWindow only
// replays the window's GDI paint, which misses almost every modern plug-in editor.
constexpr UINT kPwRenderFullContent = 0x00000002;

constexpr int kThumbnailWidth = 320;

/// An image is blank when it carries almost no colour variation.
///
/// This is the detector for PrintWindow's silent failure: an editor drawing through OpenGL or
/// Direct3D returns a solid black image *and* a success code, so the only way to notice is to look
/// at the pixels. It has to tell "solid fill" from "a dark plug-in UI", and plug-in UIs are
/// overwhelmingly dark — counting distinct colours over a sparse grid does that, because even the
/// plainest real interface yields dozens once text and borders are included.
bool IsBlank(const std::vector<BYTE>& pixels, int width, int height, int stride) {
    constexpr size_t kMinimumDistinctColours = 4;

    const int stepX = std::max(1, width / 40);
    const int stepY = std::max(1, height / 40);

    std::vector<COLORREF> seen;
    seen.reserve(kMinimumDistinctColours);

    for (int y = 0; y < height; y += stepY) {
        for (int x = 0; x < width; x += stepX) {
            const BYTE* p = pixels.data() + static_cast<size_t>(y) * stride + static_cast<size_t>(x) * 4;
            COLORREF colour = RGB(p[2], p[1], p[0]);

            if (std::find(seen.begin(), seen.end(), colour) == seen.end()) {
                seen.push_back(colour);
                if (seen.size() >= kMinimumDistinctColours) return false;
            }
        }
    }
    return true;
}

bool RowIsUniform(const std::vector<BYTE>& pixels, int stride, int y, int left, int right) {
    const BYTE* row = pixels.data() + static_cast<size_t>(y) * stride;
    const uint32_t first = *reinterpret_cast<const uint32_t*>(row + static_cast<size_t>(left) * 4);

    for (int x = left + 1; x <= right; x++) {
        if (*reinterpret_cast<const uint32_t*>(row + static_cast<size_t>(x) * 4) != first) return false;
    }
    return true;
}

bool ColumnIsUniform(const std::vector<BYTE>& pixels, int stride, int x, int top, int bottom) {
    const uint32_t first = *reinterpret_cast<const uint32_t*>(
        pixels.data() + static_cast<size_t>(top) * stride + static_cast<size_t>(x) * 4);

    for (int y = top + 1; y <= bottom; y++) {
        const BYTE* p = pixels.data() + static_cast<size_t>(y) * stride + static_cast<size_t>(x) * 4;
        if (*reinterpret_cast<const uint32_t*>(p) != first) return false;
    }
    return true;
}

/// Finds the bounds after trimming edge bands that are entirely one colour.
///
/// A plug-in's reported editor size is not always the size it draws. Arturia's Comp FET-76 reports
/// 960x508 -- the height it needs with its "Advanced" panel expanded -- but paints only the top 289px
/// while that panel is collapsed, leaving a dead band of host window background underneath.
///
/// Only fully uniform bands are removed, and only from the outside in, so a legitimately flat
/// background *within* the interface is never touched.
void ComputeTrim(const std::vector<BYTE>& pixels, int width, int height, int stride,
                 int& left, int& top, int& right, int& bottom) {
    left = 0;
    top = 0;
    right = width - 1;
    bottom = height - 1;

    while (top < bottom && RowIsUniform(pixels, stride, top, left, right)) top++;
    while (bottom > top && RowIsUniform(pixels, stride, bottom, left, right)) bottom--;
    while (left < right && ColumnIsUniform(pixels, stride, left, top, bottom)) left++;
    while (right > left && ColumnIsUniform(pixels, stride, right, top, bottom)) right--;

    // Refuse a degenerate trim; a uniform image is IsBlank's business, not this function's.
    if (right - left + 1 < 8 || bottom - top + 1 < 8) {
        left = 0;
        top = 0;
        right = width - 1;
        bottom = height - 1;
    }
}

bool GetPngEncoderClsid(CLSID& clsid) {
    UINT count = 0;
    UINT bytes = 0;
    if (Gdiplus::GetImageEncodersSize(&count, &bytes) != Gdiplus::Ok || bytes == 0) return false;

    std::vector<BYTE> buffer(bytes);
    auto* codecs = reinterpret_cast<Gdiplus::ImageCodecInfo*>(buffer.data());
    if (Gdiplus::GetImageEncoders(count, bytes, codecs) != Gdiplus::Ok) return false;

    for (UINT i = 0; i < count; i++) {
        if (wcscmp(codecs[i].MimeType, L"image/png") == 0) {
            clsid = codecs[i].Clsid;
            return true;
        }
    }
    return false;
}

/// Reads a window's pixels into a top-down 32bpp buffer via PrintWindow.
bool GrabPixels(HWND window, std::vector<BYTE>& pixels, int& width, int& height, std::string& error) {
    RECT rect{};
    if (!::GetWindowRect(window, &rect)) {
        error = "GetWindowRect failed.";
        return false;
    }

    width = rect.right - rect.left;
    height = rect.bottom - rect.top;
    if (width <= 0 || height <= 0) {
        error = "Window has no measurable size.";
        return false;
    }
    if (width > 8000 || height > 8000) {
        error = "Editor reported an implausible size.";
        return false;
    }

    HDC screen = ::GetDC(nullptr);
    HDC memory = ::CreateCompatibleDC(screen);

    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height;  // negative: top-down, so row 0 is the top of the image
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;

    void* bits = nullptr;
    HBITMAP bitmap = ::CreateDIBSection(memory, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!bitmap || !bits) {
        if (bitmap) ::DeleteObject(bitmap);
        ::DeleteDC(memory);
        ::ReleaseDC(nullptr, screen);
        error = "Could not allocate a capture bitmap.";
        return false;
    }

    HGDIOBJ previous = ::SelectObject(memory, bitmap);
    BOOL printed = ::PrintWindow(window, memory, kPwRenderFullContent);
    ::GdiFlush();

    if (printed) {
        const size_t stride = static_cast<size_t>(width) * 4;
        pixels.assign(static_cast<BYTE*>(bits), static_cast<BYTE*>(bits) + stride * height);
    } else {
        error = "PrintWindow returned false.";
    }

    ::SelectObject(memory, previous);
    ::DeleteObject(bitmap);
    ::DeleteDC(memory);
    ::ReleaseDC(nullptr, screen);
    return printed != FALSE;
}

/// Saves a cropped region of a pixel buffer as a PNG.
bool SavePng(const std::vector<BYTE>& pixels, int stride, int left, int top, int width, int height,
             const std::wstring& path, int scaleToWidth) {
    CLSID encoder{};
    if (!GetPngEncoderClsid(encoder)) return false;

    Gdiplus::Bitmap source(width, height, PixelFormat32bppRGB);
    Gdiplus::BitmapData data{};
    Gdiplus::Rect area(0, 0, width, height);
    if (source.LockBits(&area, Gdiplus::ImageLockModeWrite, PixelFormat32bppRGB, &data) != Gdiplus::Ok)
        return false;

    for (int y = 0; y < height; y++) {
        const BYTE* src = pixels.data() + static_cast<size_t>(top + y) * stride +
                          static_cast<size_t>(left) * 4;
        BYTE* dst = static_cast<BYTE*>(data.Scan0) + static_cast<size_t>(y) * data.Stride;
        memcpy(dst, src, static_cast<size_t>(width) * 4);
    }
    source.UnlockBits(&data);

    if (scaleToWidth > 0 && width > scaleToWidth) {
        int scaledHeight = std::max(1, height * scaleToWidth / width);
        Gdiplus::Bitmap thumbnail(scaleToWidth, scaledHeight, PixelFormat32bppRGB);
        Gdiplus::Graphics graphics(&thumbnail);
        graphics.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBicubic);
        graphics.DrawImage(&source, 0, 0, scaleToWidth, scaledHeight);
        return thumbnail.Save(path.c_str(), &encoder, nullptr) == Gdiplus::Ok;
    }

    return source.Save(path.c_str(), &encoder, nullptr) == Gdiplus::Ok;
}

/// A cheap signature of what a window currently looks like, for the settle check.
std::string Fingerprint(HWND window) {
    std::vector<BYTE> pixels;
    int width = 0;
    int height = 0;
    std::string error;
    if (!GrabPixels(window, pixels, width, height, error)) return {};

    // A sparse grid is enough to notice a fade or a splash giving way to the real UI, and avoids
    // hashing a multi-megapixel buffer several times per plug-in.
    const int stride = width * 4;
    const int stepX = std::max(1, width / 16);
    const int stepY = std::max(1, height / 16);

    std::string signature;
    signature.reserve(256);
    for (int y = 0; y < height; y += stepY) {
        for (int x = 0; x < width; x += stepX) {
            const BYTE* p = pixels.data() + static_cast<size_t>(y) * stride + static_cast<size_t>(x) * 4;
            signature += static_cast<char>('a' + (p[0] >> 4));
            signature += static_cast<char>('a' + (p[1] >> 4));
            signature += static_cast<char>('a' + (p[2] >> 4));
        }
    }
    return signature;
}

struct ForeignWindowSearch {
    HWND ours[2];
    bool found;
};

BOOL CALLBACK ForeignWindowProc(HWND window, LPARAM param) {
    auto* search = reinterpret_cast<ForeignWindowSearch*>(param);
    if (window != search->ours[0] && window != search->ours[1] && ::IsWindowVisible(window)) {
        search->found = true;
        return FALSE;
    }
    return TRUE;
}

}  // namespace

GdiPlusSession::GdiPlusSession() {
    Gdiplus::GdiplusStartupInput input;
    Gdiplus::GdiplusStartup(&token_, &input, nullptr);
}

GdiPlusSession::~GdiPlusSession() {
    if (token_) Gdiplus::GdiplusShutdown(token_);
}

EditorHostWindow::~EditorHostWindow() {
    if (child_) ::DestroyWindow(child_);
    if (topLevel_) ::DestroyWindow(topLevel_);
}

bool EditorHostWindow::Create(int width, int height) {
    // WS_EX_TOOLWINDOW and WS_EX_NOACTIVATE keep it out of the taskbar and stop it taking focus,
    // which matters when a scan opens a couple of hundred of these. "STATIC" is a pre-registered
    // class, so no window class of our own is needed just to own a rectangle.
    topLevel_ = ::CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, L"STATIC",
                                  L"PlugBrowser capture host",
                                  WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN,
                                  kOffscreenX, kOffscreenY, width, height,
                                  nullptr, nullptr, nullptr, nullptr);
    if (!topLevel_) return false;

    child_ = ::CreateWindowExW(0, L"STATIC", nullptr, WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                               0, 0, width, height, topLevel_, nullptr, nullptr, nullptr);
    return child_ != nullptr;
}

void EditorHostWindow::Resize(int width, int height) {
    if (topLevel_) ::MoveWindow(topLevel_, kOffscreenX, kOffscreenY, width, height, TRUE);
    if (child_) ::MoveWindow(child_, 0, 0, width, height, TRUE);
}

void EditorHostWindow::PumpMessages(DWORD milliseconds) {
    // Editors paint asynchronously -- they answer WM_PAINT, run animation timers, and some show a
    // splash before their real UI -- so without a real pump the window never draws and every capture
    // comes back blank. This is load-bearing, not a politeness.
    const DWORD deadline = ::GetTickCount() + milliseconds;
    MSG message;

    while (::GetTickCount() < deadline) {
        while (::PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            ::TranslateMessage(&message);
            ::DispatchMessageW(&message);
        }
        ::Sleep(10);
    }
}

void EditorHostWindow::WaitUntilStable(DWORD minimumMs, DWORD maximumMs) {
    // Fixed delays fit this badly: a simple effect draws in well under a tenth of a second, while a
    // large instrument may fade in or show a splash for over a second. Waiting for two consecutive
    // identical captures adapts to both, and never photographs a plug-in mid-fade.
    PumpMessages(minimumMs);

    const DWORD deadline = ::GetTickCount() + maximumMs;
    std::string previous;

    while (::GetTickCount() < deadline) {
        std::string current = Fingerprint(topLevel_);
        if (!current.empty() && current == previous) return;

        previous = std::move(current);
        PumpMessages(120);
    }
}

bool EditorHostWindow::ForeignWindowAppeared() const {
    ForeignWindowSearch search{{topLevel_, child_}, false};
    ::EnumThreadWindows(::GetCurrentThreadId(), ForeignWindowProc, reinterpret_cast<LPARAM>(&search));
    return search.found;
}

CaptureResult CaptureWindowToPng(HWND window, const std::wstring& pngPath) {
    CaptureResult result;
    result.method = "PrintWindow";

    std::vector<BYTE> pixels;
    int width = 0;
    int height = 0;
    if (!GrabPixels(window, pixels, width, height, result.error)) return result;

    const int stride = width * 4;
    if (IsBlank(pixels, width, height, stride)) {
        result.error = "Captured image was blank - the editor most likely renders through OpenGL or Direct3D.";
        return result;
    }

    int left = 0;
    int top = 0;
    int right = width - 1;
    int bottom = height - 1;
    ComputeTrim(pixels, width, height, stride, left, top, right, bottom);

    const int croppedWidth = right - left + 1;
    const int croppedHeight = bottom - top + 1;

    if (!SavePng(pixels, stride, left, top, croppedWidth, croppedHeight, pngPath, 0)) {
        result.error = "Could not encode the PNG.";
        return result;
    }

    // The gallery draws hundreds of cards at once, so a small copy sits beside every capture.
    std::wstring thumbnailPath = pngPath;
    const size_t extension = thumbnailPath.rfind(L".png");
    if (extension != std::wstring::npos) thumbnailPath.replace(extension, 4, L".thumb.png");
    SavePng(pixels, stride, left, top, croppedWidth, croppedHeight, thumbnailPath, kThumbnailWidth);

    result.ok = true;
    result.width = croppedWidth;
    result.height = croppedHeight;
    return result;
}

}  // namespace plugbrowser
