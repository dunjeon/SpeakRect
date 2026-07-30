# Phase 2 hygiene log

**Date:** 2026-07-30  
**Scope:** Dead code, dead constants, unused wrappers, comment hygiene (ban-list).  
**No algorithm / threshold changes.**

## Scan results

| Ban-list item | Result |
|---------------|--------|
| `#if false` / `#if 0` | **None found** |
| Multi-line commented-out methods | **None found** |
| Dead private methods | **Removed** (see below) |
| Dead private consts | **Removed** (inflate floor leftovers) |
| Legacy CustomActionKind | **Kept** — still execute for old INI; documented |
| Empty `catch { /* ignore */ }` on Dispose | **Kept** — intentional safe teardown |
| Archive 2026-07-* “why” comments | **Kept** (product regression notes) |

## Removed dead code

| Item | File | Notes |
|------|------|-------|
| `OpenHotkeyMap`, `OpenRegionsMap`, `OpenVoiceSettings`, `OpenAnalytics`, `OpenHelp` | `frm_SpeakRect.cs` | Unused wrappers; Settings opened via `OpenSettings(tab)` / tray |
| `OpenFollowSettings` | kept | Still used (sidebar FOLLOW → settings) |
| `BytesToBitmap` | `OcrProcessor.cs` | Never called |
| `SortComicReadingOrderByBands` | `OcrProcessor.cs` | Dead alias of `SortComicReadingOrderByRows` |
| `ActiveWinOcrDetectSecondPass` | `OcrProcessor.cs` | Always `true`, never read |
| `FullPipelineMinWinOcrWords` | `OcrProcessor.cs` | Unused after short-circuit removal |
| `RegionInflateMinPxX/Y`, `RegionInflateSmallMinPx`, `RegionInflateDenseMinPxX/Y` | `OcrProcessor.cs` | Superseded by “no fixed floor” grow logic |

## Comment / detail-string hygiene

- Host lifecycle comments: Kobold → Local-LLM (type name deferred to Phase 5)
- Sequential region detail: under-read / fallback messages use OCR + Local-LLM wording
- DetectedTextRegion docs: detect vs recognize roles clarified
- `CustomHotkey` legacy enum: note that SystemInput still runs them for old profiles
- `frm_HotkeyMap` ctor params: documented as reserved/unused (call-site compat)

## Intentionally not removed

| Item | Why |
|------|-----|
| Legacy `CustomActionKind` values + `SystemInput` cases | Old profiles still fire them |
| `frm_HotkeyMap` unused profile callbacks | Call-site signature compat |
| `Paddle*` / `Kobold*` method identifiers | Phase 5 mechanical rename |
| Debug pipeline detail with `winocr` tokens | Sanitized in UI; full rename later |
| Large comic consensus code | Complex by design — extract Phase 4, not delete |

## Verification

```text
dotnet build SpeakRect.sln -c Debug
dotnet run --project tests/RegionSmoke/RegionSmoke.csproj -c Debug
```

## Follow-ups (later phases)

- Phase 3: complete speak-path checklist with method traces  
- Phase 5: `KoboldCppHost` → `LocalLlmHost`, `Paddle*` renames  
- Optional: IDE analyzer pass (IDE0051 unused members) in CI after xUnit project exists  
