# Catalog export format

PlugBrowser writes its catalog to a plain JSON file so other programs can use it without
reading PlugBrowser's internal SQLite database. That database is a cache: PlugBrowser drops
and rebuilds its tables whenever its schema version changes. The export is the stable
interface.

The first consumer is [AltraPlugHost](https://github.com/Bezmont/AltraPlugHost), which uses it
to show a screenshot browser for picking plugins to host.

## Location

```
%LOCALAPPDATA%\PlugBrowser\export\plugins.json
```

## When it is written

- At the end of every scan and every reload of selected plugins, including cancelled ones.
- When PlugBrowser starts, if the export is missing or older than the catalog.

The file is written to a temporary file in the same folder and then moved into place, so a reader
never sees a half-written file. Readers should still treat a file that fails to parse as absent and
try again later.

## Compatibility rules

- `version` is the format version, currently **1**. It changes only for a change that would break
  an existing reader: removing or renaming a field, or changing its meaning.
- Adding a field is not a breaking change. Readers must ignore fields they don't know.
- Readers must accept enum strings they don't know and treat them as `unknown`.
- A reader that finds a `version` it doesn't support should fall back to working without the
  catalog.

## Structure

```jsonc
{
  "format": "PlugBrowser.CatalogExport",   // always this string
  "version": 1,
  "generatedUtc": "2026-11-19T20:15:00Z",  // ISO 8601, UTC
  "generator": "PlugBrowser 1.2.0",
  "plugins": [ /* one per plugin file */ ]
}
```

### Plugin (one per plugin file or bundle)

| Field | Type | Notes |
|---|---|---|
| `path` | string | Absolute path to the `.vst3` bundle or file, or VST2 `.dll`. Pass this to the host to load it. |
| `format` | `"vst3"` \| `"vst2"` \| `"unknown"` | |
| `architecture` | `"x64"` \| `"x86"` \| `"arm64"` \| `"unknown"` | |
| `state` | string | See below. |
| `stateDetail` | string \| null | Why the plugin is in a failure state. |
| `displayName` | string | Best name available, even if the plugin was never loaded. |
| `displayVendor` | string \| null | |
| `fileVersion` | string \| null | From the binary's version resource. |
| `classes` | array | The plugins inside this file. Empty if it was never loaded. |

`state` is one of:

| Value | Meaning |
|---|---|
| `"discovered"` | Found on disk but never loaded, so only file details are known. |
| `"probed"` | Loaded successfully during the scan. |
| `"failed"` | Refused to load or crashed during the scan. |
| `"timedOut"` | Stopped responding during the scan. |
| `"blacklisted"` | The user told PlugBrowser never to load it. |
| `"unsupported"` | Can't be loaded by a 64-bit host (for example, a 32-bit plugin). |

`failed` and `timedOut` are not always permanent; copy-protected plugins in particular fail
intermittently. A host should warn rather than refuse.

### Class (one per plugin inside a file)

A VST3 file can contain several plugins (VST3 calls them classes). Only actual plugins are
exported, not the controller and other helper classes a VST3 factory also lists.

| Field | Type | Notes |
|---|---|---|
| `cid` | string \| null | VST3 class ID, 32 hex characters. Null when not known. |
| `index` | number | Position in the plugin's factory. Prefer `cid` or `name` to identify a class; the index can change between versions. |
| `name` | string | |
| `vendor` | string \| null | |
| `category` | string \| null | Raw VST3 subcategory string, such as `"Fx\|EQ"` or `"Instrument\|Synth"`. |
| `tags` | string[] | `category` split on `\|`. |
| `kind` | `"effect"` \| `"instrument"` \| `"unknown"` | |
| `version` | string \| null | |
| `parameterCount` | number | 0 if not known. |
| `image` | object \| null | Best picture of the plugin's editor. |

### Image

| Field | Type | Notes |
|---|---|---|
| `path` | string | Absolute path to a full-size PNG. |
| `thumbnailPath` | string \| null | Absolute path to a smaller PNG (320 px wide) for grids. Null if there isn't one; use `path` instead. |
| `width`, `height` | number | Size of the full image in pixels. |
| `source` | `"snapshot"` \| `"screenshot"` | `snapshot` is a picture shipped by the vendor; `screenshot` was captured by PlugBrowser. |

Image files can be deleted or replaced at any time by a later scan. Readers must cope with a
missing file.
