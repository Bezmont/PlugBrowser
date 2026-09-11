// PlugBrowser.NativeWorker - inspects exactly one plug-in bundle and exits.
//
// This is a native process on purpose, and that is the whole reason it exists.
//
// The managed worker it replaces could not load PACE/iLok-protected plug-ins at all: Plugin Alliance's
// bx_* titles, elysia, SPL and Knifonium would spin at 100% CPU inside the loader and never return,
// while loading in 2-5 seconds in any DAW -- and in NetPlugHost's own native smoke host. The anti-tamper
// in those plug-ins probes for debuggers and instrumentation, and a CLR process presents exactly that
// shape (vectored exception handlers, hardware exceptions for null checks, threads suspended for GC),
// so the protection goes defensive. Removing the runtime from the process removes the trigger.
//
// It talks to plug-ins through NetPlugHost's flat C ABI rather than the VST3 SDK directly, so the host
// logic stays in one place and this stays a thin shell around windowing, capture and JSON.

#include <algorithm>
#include <string>
#include <vector>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <shlwapi.h>

#include "capture.h"
#include "json_writer.h"
#include "vst3_host_c.h"

namespace {

using plugbrowser::JsonWriter;
using plugbrowser::Utf8From;

constexpr int kExitOk = 0;
constexpr int kExitBadArguments = 1;
constexpr int kExitTimedOut = 3;

/// Cap on parameters recorded per class. Some plug-ins expose thousands -- Comp FET-76 reports 2195 --
/// and the catalog only shows a list, so carrying them all would bloat every report for no gain.
constexpr int kMaxParameters = 512;

struct Options {
    std::wstring bundlePath;
    std::wstring outputDirectory;
    int  classIndex = -1;       ///< -1 means every class.
    bool capture = true;
    DWORD minimumSettleMs = 250;
    DWORD maximumSettleMs = 3000;
    DWORD timeoutMs = 45000;
};

// The watchdog needs these after main's locals are unreachable.
std::wstring g_outputDirectory;
std::wstring g_bundlePath;
DWORD        g_timeoutMs = 0;

std::wstring Combine(const std::wstring& directory, const std::wstring& leaf) {
    if (directory.empty()) return leaf;
    if (directory.back() == L'\\' || directory.back() == L'/') return directory + leaf;
    return directory + L"\\" + leaf;
}

std::string Utf8(const std::wstring& text) {
    if (text.empty()) return {};
    int bytes = ::WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                                      nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(bytes), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                          out.data(), bytes, nullptr, nullptr);
    return out;
}

/// Writes the report atomically.
///
/// The parent may read the file while a later class is still being inspected, and a half-written
/// document would look like a parse failure rather than work in progress.
void WriteReport(const std::string& json) {
    const std::wstring finalPath = Combine(g_outputDirectory, L"result.json");
    const std::wstring tempPath = finalPath + L".tmp";

    HANDLE file = ::CreateFileW(tempPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                                FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;

    DWORD written = 0;
    ::WriteFile(file, json.data(), static_cast<DWORD>(json.size()), &written, nullptr);
    ::CloseHandle(file);

    ::MoveFileExW(tempPath.c_str(), finalPath.c_str(), MOVEFILE_REPLACE_EXISTING);
}

/// A report for a bundle that never got off the ground.
std::string SimpleReport(bool loaded, bool timedOut, const std::string& error) {
    JsonWriter json;
    json.BeginObject();
    json.Write("Path", Utf8(g_bundlePath));
    json.Write("Loaded", loaded);
    json.Write("Enumerated", false);
    json.Write("TimedOut", timedOut);
    json.WriteOrNull("Error", error);
    json.BeginArray("Classes");
    json.EndArray();
    json.EndObject();
    return json.Text();
}

/// Kills the process if inspection overruns, so a hung plug-in cannot wedge the worker.
///
/// A report is written first. The parent cannot tell an abrupt exit from a crash by its code alone, so
/// without this record every hung plug-in would be filed as "failed to load" rather than "timed out" --
/// and a timeout is worth retrying with a longer budget, where a crash is not.
DWORD WINAPI WatchdogProc(LPVOID) {
    ::Sleep(g_timeoutMs);

    char message[160];
    _snprintf_s(message, sizeof(message), _TRUNCATE,
                "Gave up after %.0fs - the plugin stopped responding.", g_timeoutMs / 1000.0);
    WriteReport(SimpleReport(/*loaded*/ true, /*timedOut*/ true, message));

    // TerminateProcess rather than exit(): the hang is inside third-party code that may hold locks,
    // and running destructors or atexit handlers could block forever right here.
    ::TerminateProcess(::GetCurrentProcess(), kExitTimedOut);
    return 0;
}

bool ParseOptions(int argc, wchar_t** argv, Options& options) {
    for (int i = 1; i < argc; i++) {
        std::wstring arg = argv[i];

        auto next = [&](std::wstring& target) {
            if (i + 1 >= argc) return false;
            target = argv[++i];
            return true;
        };
        auto nextNumber = [&](DWORD& target) {
            if (i + 1 >= argc) return false;
            target = static_cast<DWORD>(_wtoi(argv[++i]));
            return true;
        };

        if (arg == L"--inspect") {
            if (!next(options.bundlePath)) return false;
        } else if (arg == L"--outdir") {
            if (!next(options.outputDirectory)) return false;
        } else if (arg == L"--class") {
            if (i + 1 >= argc) return false;
            options.classIndex = _wtoi(argv[++i]);
        } else if (arg == L"--no-capture") {
            options.capture = false;
        } else if (arg == L"--settle-ms") {
            if (!nextNumber(options.minimumSettleMs)) return false;
        } else if (arg == L"--max-settle-ms") {
            if (!nextNumber(options.maximumSettleMs)) return false;
        } else if (arg == L"--timeout-ms") {
            if (!nextNumber(options.timeoutMs)) return false;
        } else {
            return false;
        }
    }

    return !options.bundlePath.empty() && !options.outputDirectory.empty();
}

/// Everything learned about one class, accumulated so the report can be rewritten after each.
struct ClassReport {
    int         index = 0;
    std::string name;
    std::string vendor;
    std::string category;
    std::string version;
    bool        instantiated = false;
    std::string error;
    int         parameterCount = 0;
    bool        hasEditor = false;
    int         editorWidth = 0;
    int         editorHeight = 0;
    std::string imagePath;
    std::string captureMethod;
    std::string captureError;

    struct Parameter {
        unsigned int id = 0;
        std::string  title;
        std::string  units;
        double       defaultNormalized = 0;
        int          stepCount = 0;
        bool         isBypass = false;
        bool         canAutomate = false;
    };
    std::vector<Parameter> parameters;
};

std::string BuildReport(const std::vector<ClassReport>& classes, bool enumerated) {
    JsonWriter json;
    json.BeginObject();
    json.Write("Path", Utf8(g_bundlePath));
    json.Write("Loaded", true);
    json.Write("Enumerated", enumerated);
    json.Write("TimedOut", false);
    json.WriteNull("Error");

    json.BeginArray("Classes");
    for (const auto& c : classes) {
        json.BeginObject();
        json.Write("Index", c.index);
        json.Write("Name", c.name);
        json.WriteOrNull("Vendor", c.vendor);
        json.WriteOrNull("Category", c.category);
        json.WriteOrNull("Version", c.version);
        json.Write("Instantiated", c.instantiated);
        json.WriteOrNull("Error", c.error);
        json.Write("ParameterCount", c.parameterCount);

        json.BeginArray("Parameters");
        for (const auto& p : c.parameters) {
            json.BeginObject();
            json.Write("Id", p.id);
            json.Write("Title", p.title);
            json.WriteOrNull("Units", p.units);
            json.Write("DefaultNormalized", p.defaultNormalized);
            json.Write("StepCount", p.stepCount);
            json.Write("IsBypass", p.isBypass);
            json.Write("CanAutomate", p.canAutomate);
            json.EndObject();
        }
        json.EndArray();

        json.Write("HasEditor", c.hasEditor);
        json.Write("EditorWidth", c.editorWidth);
        json.Write("EditorHeight", c.editorHeight);
        json.WriteOrNull("ImagePath", c.imagePath);
        json.WriteOrNull("CaptureMethod", c.captureMethod);
        json.WriteOrNull("CaptureError", c.captureError);
        json.EndObject();
    }
    json.EndArray();
    json.EndObject();
    return json.Text();
}

void ReadParameters(Vst3PluginHandle plugin, ClassReport& report) {
    const int count = vst3_plugin_param_count(plugin);
    if (count <= 0) return;

    report.parameterCount = count;
    const int wanted = std::min(count, kMaxParameters);

    for (int i = 0; i < wanted; i++) {
        Vst3ParamInfo info{};
        if (vst3_plugin_param_info(plugin, i, &info) != VST3_OK) continue;

        ClassReport::Parameter parameter;
        parameter.id = info.id;
        parameter.title = Utf8From(info.title, 128);
        parameter.units = Utf8From(info.units, 128);
        parameter.defaultNormalized = info.defaultNormalized;
        parameter.stepCount = info.stepCount;
        parameter.canAutomate = (info.flags & 1) != 0;
        parameter.isBypass = (info.flags & 2) != 0;
        report.parameters.push_back(std::move(parameter));
    }
}

void CaptureEditor(Vst3PluginHandle plugin, ClassReport& report, const Options& options) {
    int width = 0;
    int height = 0;
    if (vst3_plugin_get_editor_size(plugin, &width, &height) != VST3_OK || width <= 0 || height <= 0) {
        width = 800;
        height = 600;
    }

    plugbrowser::EditorHostWindow host;
    if (!host.Create(width, height)) {
        report.captureError = "Could not create the off-screen capture host window.";
        return;
    }

    if (vst3_plugin_open_editor(plugin, host.Child()) != VST3_OK) {
        report.captureError = "The plugin refused to open its editor.";
        return;
    }

    // Re-query now that the view is attached: most plug-ins only know their real size then.
    int attachedWidth = 0;
    int attachedHeight = 0;
    if (vst3_plugin_get_editor_size(plugin, &attachedWidth, &attachedHeight) == VST3_OK &&
        attachedWidth > 0 && attachedHeight > 0 &&
        (attachedWidth != width || attachedHeight != height)) {
        width = attachedWidth;
        height = attachedHeight;
        host.Resize(width, height);
    }

    report.editorWidth = width;
    report.editorHeight = height;

    host.WaitUntilStable(options.minimumSettleMs, options.maximumSettleMs);

    // A window we did not create means the plug-in popped a dialog -- an authorisation or trial prompt,
    // most often. Photographing that would file a picture of a licence screen as the plug-in's UI.
    if (host.ForeignWindowAppeared()) {
        report.captureError = "Plugin opened its own window (likely an authorization or trial dialog).";
        vst3_plugin_close_editor(plugin);
        return;
    }

    wchar_t leaf[64];
    _snwprintf_s(leaf, _TRUNCATE, L"class-%d.png", report.index);
    const std::wstring pngPath = Combine(options.outputDirectory, leaf);

    auto result = plugbrowser::CaptureWindowToPng(host.TopLevel(), pngPath);
    report.captureMethod = result.method;

    if (result.ok) {
        report.imagePath = Utf8(pngPath);
        report.editorWidth = result.width;
        report.editorHeight = result.height;
    } else {
        report.captureError = result.error;
    }

    vst3_plugin_close_editor(plugin);
}

int Run(const Options& options) {
    Vst3ModuleHandle module = nullptr;
    if (vst3_module_load(options.bundlePath.c_str(), &module) != VST3_OK || !module) {
        WriteReport(SimpleReport(/*loaded*/ false, /*timedOut*/ false,
                                 "The plugin could not be loaded."));
        return kExitOk;
    }

    // Recorded before any class is touched. If a plug-in hangs and the watchdog kills the process, a
    // hang on the very first class would otherwise leave no file at all -- indistinguishable from the
    // worker never having run.
    WriteReport(SimpleReport(/*loaded*/ true, /*timedOut*/ false, {}));

    std::vector<ClassReport> classes;
    const int count = vst3_module_class_count(module);

    for (int i = 0; i < count; i++) {
        if (options.classIndex >= 0 && options.classIndex != i) continue;

        ClassReport report;
        report.index = i;

        Vst3ClassInfo info{};
        if (vst3_module_class_info(module, i, &info) == VST3_OK) {
            report.name = Utf8From(info.name, 128);
            report.vendor = Utf8From(info.vendor, 128);
            report.category = Utf8From(info.category, 128);
            report.version = Utf8From(info.version, 64);
        }
        if (report.name.empty()) report.name = "(class " + std::to_string(i) + ")";

        Vst3PluginHandle plugin = nullptr;
        if (vst3_plugin_create(module, i, &plugin) != VST3_OK || !plugin) {
            report.error = "The plugin class could not be instantiated.";
            classes.push_back(std::move(report));
            WriteReport(BuildReport(classes, /*enumerated*/ false));
            continue;
        }

        report.instantiated = true;
        ReadParameters(plugin, report);
        report.hasEditor = vst3_plugin_has_editor(plugin) == 1;

        if (!report.hasEditor) {
            report.captureError = "Plugin exposes no editor view.";
        } else if (options.capture) {
            CaptureEditor(plugin, report, options);
        }

        vst3_plugin_destroy(plugin);

        classes.push_back(std::move(report));

        // Written after every class so a crash on class 2 does not discard classes 0 and 1.
        WriteReport(BuildReport(classes, /*enumerated*/ false));
    }

    WriteReport(BuildReport(classes, /*enumerated*/ true));

    // Every plugin was destroyed above, so the bundle can be unloaded.
    vst3_module_free(module);
    return kExitOk;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    Options options;
    if (!ParseOptions(argc, argv, options)) {
        ::fwprintf(stderr,
                   L"PlugBrowser.NativeWorker --inspect <bundle.vst3> --outdir <directory> [options]\n"
                   L"  --class <n>         inspect only this class index\n"
                   L"  --no-capture        read metadata only, do not open editors\n"
                   L"  --settle-ms <n>     minimum settle before capture (default 250)\n"
                   L"  --max-settle-ms <n> maximum settle while the editor keeps changing (default 3000)\n"
                   L"  --timeout-ms <n>    hard self-kill budget (default 45000)\n");
        return kExitBadArguments;
    }

    g_outputDirectory = options.outputDirectory;
    g_bundlePath = options.bundlePath;
    g_timeoutMs = options.timeoutMs;

    ::CreateDirectoryW(options.outputDirectory.c_str(), nullptr);

    // STA with a message pump, as VST3 editors require (ABI_SPEC.md section 6.1).
    ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    ::SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    plugbrowser::GdiPlusSession gdiplus;

    DWORD watchdogId = 0;
    HANDLE watchdog = ::CreateThread(nullptr, 0, WatchdogProc, nullptr, 0, &watchdogId);

    const int result = Run(options);

    if (watchdog) ::CloseHandle(watchdog);
    ::CoUninitialize();
    return result;
}
