# PlugBrowser

A browsable, searchable catalog of the audio plugins installed on this machine — metadata, and
eventually a picture of each plugin's GUI.

## Status

**Discovery, catalog, and browser UI are complete.** Scanning finds 418 plugins on the development
machine (196 VST3, 222 VST2) in about 300 ms, without loading a single plugin.

**Capture works, at scale.** A full run over this machine's library with the native worker:

```
418 found · 194 loaded · 199 screenshots · 2 failed · 12 min
```

That is **99% of installed VST3 plugins photographed** — effects, instruments and copy-protected titles
alike — every one via `PrintWindow`; the GPU fallbacks have still never been needed, which is a better
result than expected for a library this heavy on OpenGL-based UIs.

For comparison, the managed worker on the same library managed `190 loaded · 189 screenshots · 15
failed` in 22 minutes. Most of that extra time was the 45-second timeout burned by each plugin it could
not load.

Two plugins still fail: KORG MS-20 V dies with an access violation, and VocalSynth 2 stops responding.
Both are contained by the sandbox and both are worth retrying — see the note on non-reproducibility below.

Those failures are also **not reproducible run to run**. The same `bx_*` plugin variously hangs until
the watchdog kills it, dies by native fail-fast, or fails cleanly in 270ms with "load failed" — which is
what licence-server interaction looks like from the outside. So a failed plugin is worth retrying; the
catalog keeps the state and the reason so a later scan can pick it up.

Distinguishing the two cases needed care. The worker's watchdog ends the process with
`Environment.FailFast`, and a plugin crashing via native `__fastfail` produces a *nearly identical*
exit code, so a hang and a crash were indistinguishable from the parent. The watchdog now writes an
explicit timed-out report before it pulls the plug, and the parent trusts that over the exit code.

**Scanning is wired into the app.** *Scan & Options* lists every scan root — conventional, registered
by an installer, or added by you — with per-root enable switches and plugin counts, and runs the scan
from the same dialog with live progress. Captured screenshots appear in the gallery at each plugin's
own aspect ratio.

Filters, the chosen vendor, and window geometry are remembered between runs.

## Browsing

- **Category facets** built from the subcategory tags the plugins themselves report — Delay, Dynamics,
  Reverb, Modulation and so on, each with a count. Ticking several asks for *any* of them, which is how
  a facet list reads; requiring all would return almost nothing, since a plugin rarely claims two.
- **Every facet count cross-filters.** Counts are computed against the rest of the active query, so
  typing "arturia" takes the sidebar from Delay (8) to Delay (3) and Drum (4) to Drum (0). Each group's
  count excludes its *own* selections — otherwise ticking Delay would show "Delay (1)" and zero beside
  every sibling, which is the opposite of what a facet list is for.
- **Result count** above the gallery, tracking the current filter.
- **Full-size view**: double-click a card, or click the detail pane's image. Click anywhere or press
  Escape to dismiss.
- **Clear filters** resets everything including the search; the search box carries its own ✕.
- **Unique only** hides plugins that are also installed in another format — see below.
- **Multi-select** with the usual Ctrl/Shift gestures. The detail pane then shows only traits the
  selection genuinely shares — a differing one reports "(3 different)" rather than quietly showing the
  first plugin's value, which would be a claim about plugins it does not describe.

### Duplicates across formats

A VST2 and a VST3 build of the same plugin are usually installed together by one installer, so most
catalogs contain each plugin twice. On the development machine that is **139 products, 278 files, and
2,733 MB of redundant VST2 copies** — 278 of 418 entries, leaving 140 unique.

*Scan & Options → Review duplicates* opens a sortable grid: plugin, format, which formats it is
installed as, type, vendor, version, architecture, size, whether it has a screenshot, and the binary's
location. The VST3 copy is dimmed as the keeper, "Show only the redundant copies" narrows to what could
go, and *Copy as spreadsheet* puts it on the clipboard as TSV. The footer lists the folders holding the
redundant copies with a file count each, since switching those off under Scan locations is the action
the whole view exists to inform.

The **Unique only** facet applies the same analysis in the main gallery.

Matching is by a normalised product name, because the same product is named inconsistently across its
own builds — "Addictive Drums 2" as VST3 against "Addictive Drums 2 x64" as VST2, "Comp FET-76" against
"Comp FET 76". The normalisation is deliberately conservative: a false duplicate would invite someone to
delete a plugin they still need, which is far worse than missing one. Audited against the real catalog,
every group it found was a genuine match.

### Reload & recapture

Some plugins show a registration or trial prompt in their editor, so the stored screenshot is a picture
of that dialog rather than the interface — every Audio Plugin Union plugin on the development machine
did this. Select those plugins and use **Reload & recapture**: it re-probes just that selection,
bypassing the unchanged-file check, since nothing about the file changed — only the licence state
outside it. A recapture that fails keeps the previous image rather than leaving the plugin with none.

Two VST3 subcategory groups are deliberately kept out of the category list. `Fx` and `Instrument`
duplicate the Type facet, and listing them twice lets the two controls be set into a contradiction that
silently returns nothing. `OnlyRT`, `NoOfflineProcess` and the channel-layout markers are processing
*capabilities* the spec packs into the same string as musical categories — real, but noise in a list
meant for picking "delay" or "reverb".

## Layout

```
src/PlugBrowser.Core/    discovery, PE parsing, SQLite catalog, scan orchestration
native/                  PlugBrowser.NativeWorker - the C++ loader/capture worker (preferred)
src/PlugBrowser.Worker/  the managed worker, used as a fallback
src/PlugBrowser.App/     Avalonia 11 browser UI
tests/PlugBrowser.Tests/ xUnit suite
```

### Building the native worker

```powershell
.uild-native.ps1          # builds NetPlugHost too if needed
dotnet build PlugBrowser.sln  # copies the worker next to the app
```

Needs Visual Studio 2022 (or Build Tools) with the C++ workload. Everything works without it — the app
falls back to the managed worker and reports the protected plugins as failures.

## How capture works

The app never loads a plugin itself. `PlugBrowser.Worker` is launched once per bundle and killed —
process tree and all — if it overruns its budget. Large sample-library and amp-sim plugins do hang on
load; out of process that costs one catalog row marked "timed out" instead of a frozen UI.

Inside the worker, capture is: create an off-desktop top-level window, parent the plugin's editor into a
child of it, pump messages until two consecutive captures come back identical (so splash screens and
fades finish), then `PrintWindow` with `PW_RENDERFULLCONTENT`.

Two details that are easy to get wrong:

- **Re-query the editor size after attaching.** Most plugins only know their real size then.
- **A blank capture is a silent failure, not an error.** An OpenGL or Direct3D editor returns solid
  black *and* a success code, so the only way to detect it is to inspect the pixels — hence the
  distinct-colour check that rejects flat images.

### Copy-protected plugins and the CLR

Plugin Alliance's `bx_*` titles, elysia, SPL and Knifonium hang forever in the .NET worker while loading
fine in any DAW. The cause is not the plugin or the ABI: NetPlugHost's own **native** C++ smoke host
loads every one of them in 2–5 seconds. The only difference is a CLR in the process.

During a hang the process burns a full core, has `WININET`/`CRYPT32`/`WS2_32` loaded and **no network
connections** — copy protection spinning in a local check loop, not waiting on a licence server. These
titles use PACE/iLok anti-tamper, which probes for debuggers and instrumentation; a CLR process presents
exactly that shape (vectored exception handlers, hardware exceptions for null checks, threads suspended
for GC), so the protection goes defensive. It also explains the nondeterminism — the same plugin
variously hangs, fails fast, or fails cleanly depending on how the race falls.

**Fixed** by `PlugBrowser.NativeWorker`, a C++ worker with no runtime in its process. All eight
previously-unloadable plugins now load and capture in 3–7 seconds:

```
bx_solo               Fx|Analyzer        7 params    380x114
bx_subfilter          Fx|EQ|Filter       8 params    481x211
bx_masterdesk Classic Fx|EQ|Dynamics    12 params    630x389
bx_console N          Fx|EQ|Dynamics    55 params    700x785
bx_oberhausen         Instrument|Synth 269 params    850x683
elysia niveau filter  Fx|EQ              5 params    455x174
SPL Free Ranger       Fx|EQ              8 params    308x450
Knif Audio Knifonium  Instrument|Synth 317 params   1200x541
```

The worker talks to plugins through NetPlugHost's flat C ABI rather than the VST3 SDK directly, so all
the hosting logic stays in one place and the worker stays a thin shell around windowing, capture and
JSON — no SDK headers, no submodule, three source files. It emits the same `result.json` the managed
worker does, so nothing downstream changed.

`WorkerRunner` picks the native worker whenever it is present and falls back to the managed one
otherwise, because building it needs a C++ toolchain.

### The host must supply an IPlugFrame

The VST3 spec requires `IPlugView::setFrame(IPlugFrame*)` **before** `attached()`. NetPlugHost never
called it, and the omission is not benign: plugins are entitled to call `plugFrame->resizeView()` from
inside `attached()` when their editor sizes or scales itself, and one that does not null-check its frame
dereferences null and takes the host process down.

That is what every Native Instruments plugin was doing. Battery 4 and Kontakt died inside `attached()`
with NI's own crash dialog; Super 8 produced a bare `0xC0000005` access violation. All of them load fine
in any DAW, because every DAW implements this interface. Fixed in NetPlugHost by implementing a minimal
`IPlugFrame` that resizes the parent window and forwards to `onSize()`.

One wrinkle: the SDK *declares* `IPlugFrame`'s IID but defines it in none of the translation units this
target compiles, so the host has to provide it with `DEF_CLASS_IID(IPlugFrame)`.

**Known limitation.** A few plugins draw an information or licence screen *inside* their own editor
rather than in a separate window — every Elektron Overbridge plugin does this — so the capture is of
that notice, not the interface. Foreign-window detection cannot catch it, because there is no foreign
window. These are visible in the gallery and can be recaptured individually.

Reported editor size is not always the size drawn: Arturia's Comp FET-76 reports 960x508, the height it
needs with its "Advanced" panel open, but paints only the top 289px while collapsed. Uniform edge bands
are cropped off, so stored images match the visible interface and the gallery can lay every card out at
the plugin's true proportions.

## Running

```powershell
dotnet test                                  # 37 tests
dotnet run --project src/PlugBrowser.App     # the browser
```

The catalog lives at `%LOCALAPPDATA%\PlugBrowser\catalog.db`. Deleting it forces a full rescan.

## How discovery works

No plugin is loaded. Everything shown today comes from the filesystem:

- **Traversal.** A `.vst3` entry is one plugin whether it is a file or a directory, and a `.vst3`
  directory is never descended into — doing so rediscovers its own `Contents/x86_64-win/Name.vst3`
  binary as a phantom second plugin.
- **PE version resource.** The dependable source of vendor and version. On this machine 147 of 151 flat
  VST3 files carry one, and VST2 DLLs carry product names too.
- **Exports.** `GetPluginFactory` marks VST3, `VSTPluginMain`/`main` marks VST2 — but the file extension
  must be part of the test, because every Arturia VST3 also exports `main`.
- **`moduleinfo.json`.** Rich when present (CIDs, categories, SDK version) but rare: 3 bundles of ~229.
  Note the SDK writes `"Sub Categories"` with a space and emits trailing commas.
- **Vendor snapshots.** `Contents/Resources/Snapshots/<CID>_snapshot.png` is part of the VST3 spec and
  would be the ideal plugin image — but zero plugins on this machine ship one, so it is a bonus path,
  not a foundation.

## The C++ toolchain is optional

The prebuilt `Vst3HostNative.dll` in NetPlugHost already exports everything capture needs, so **no
compiler is required** to load plugins or take screenshots.

A rebuild (VS 2022 Build Tools with *Desktop development with C++*, plus CMake) is only needed for the
ABI v2 additions — chiefly exposing each class's **CID**, so plugins can be addressed by identity
instead of by factory index. Index-based addressing silently re-points cached data at the wrong plugin
when a vendor ships an update that reorders classes. Worth fixing, but not blocking.

### Instruments are *not* invisible

An earlier reading of NetPlugHost suggested its `kVstAudioEffectClass` filter hid instruments, making
the rebuild urgent. That is wrong, and worth recording so nobody re-derives it: in VST3,
`kVstAudioEffectClass` is the component category for **every** audio processor. Instruments included.
The instrument/effect distinction lives in the *subcategory* string — `"Fx|Dynamics"` versus
`"Instrument|Synth"` — which the v1 ABI already reports. Instruments enumerate, instantiate and
screenshot today.

## Related repositories

PlugBrowser depends on **`D:\git\NetPlugHost`**, a C++ wrapper around the Steinberg
VST3 SDK with a C# P/Invoke layer. It already loads modules, instantiates plugins, and opens plugin
editors into a host-supplied HWND — which is the hard half of screenshot capture.

NetPlugHost is extended **additively**: its existing exports keep their exact v1 semantics because
`D:\git\StreamRecorder` depends on them. New exports sit alongside the old ones rather than
replacing them.

## Licensing

NetPlugHost is GPLv3 because it links the Steinberg VST3 SDK. Linking it makes PlugBrowser GPLv3 too.

## VST2

VST2 is catalogued but never loaded, and gets no screenshots. Steinberg no longer licenses the VST2 SDK,
so hosting it would require a clean-room header. The metadata available without loading turns out to be
good — vendor, product name, version, architecture — so VST2 entries are not second-class in the browser.
