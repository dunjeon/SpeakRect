# Speak-path checklist (Phase 3 gate)

| Field | Value |
|-------|--------|
| **Status** | Complete (method-level trace, 2026-07-30) |
| **Product** | SpeakRect 1.4.19+ |
| **Baseline** | Matches as-built code + `docs/SPEAKRECT_ARCHITECTURE_OSS.md` |
| **Gate** | Large extract PRs (LocalLlmClient, BalloonOcrDetect, geometry, SpeechCleaner) **must not merge** until this file exists and stays accurate |

> **Phase numbers vs PR numbers:** This artifact is **PR-05**. It is a capability gate for Phase 4 extracts, not a code change by itself.

---

## 0. How to use this document

1. Before extracting a module, re-read the section that owns those methods.
2. Extract PRs: **no algorithm / threshold changes**; re-run RegionSmoke + relevant ModeSmoke pure checks.
3. If you change a call chain, update this file in the same PR.
4. ModeSmoke names below are exact `Check("…")` strings (or section headers) from `tests/ModeSmoke/Program.cs`.

**Legend:** ✅ traced & intended · ⚠️ intentional edge · 🐛 fixed this phase · 📌 regression anchor

---

## 1. Lifecycle

| Step | Method / type | Intent | Status |
|------|---------------|--------|--------|
| Single-instance | `Program.Main` mutex `Global\SpeakRect_SingleInstance_2026` | Second instance MessageBox + exit | ✅ |
| Dark chrome | `UiTheme.InitAppDarkMode` | Before first window | ✅ |
| Settings | `AppSettings.Current.Load()` | INI / profiles before UI | ✅ |
| Local-LLM start | `LocalLlmHost.Start()` | Non-blocking; Job Object; adopt healthy port | ✅ |
| App run | `Application.Run(new frm_SpeakRect())` | Overlay form is message pump | ✅ |
| Exit handlers | `ApplicationExit` + `ProcessExit` → `LocalLlmHost.Stop()` | Plus `finally` Stop | ✅ |
| Form close Exit | `frm_SpeakRect.OnFormClosed` → `LocalLlmHost.Stop()` | Tray Exit closes form | ✅ |
| Hide-to-tray | `HideToTray` | **Does not** Stop host; optional Balloons refine speak | ✅ |

### Host lifecycle notes

| Question | Answer |
|----------|--------|
| Double `Start` safe? | Yes — `lock (Gate)`; reuses live process / healthy API |
| Double `Stop` safe? | Yes — kills owned process + sweeps bundled `koboldcpp.exe` |
| Task Manager kill SpeakRect? | Job Object `KILL_ON_JOB_CLOSE` should kill child; Start also sweeps orphans |
| Missing `koboldcpp\` | Start logs and skips; speak waits/announces Local-LLM not ready |

---

## 2. Overlay / input entry points

| Action | Entry | Speaks? | Status |
|--------|-------|---------|--------|
| Toggle overlay | `WndProc` `HOTKEY_TOGGLE_OVERLAY` → `ToggleOverlayFromInput` | No | ✅ |
| Draw RECT/OVAL/LASSO | mouse hook / paint; `SetMode` / `HideSidebarForDraw` | No until Enter | ✅ |
| Enter (regions 1–8) | `ProcessDialogKey` → `StartSpeakKeepingOverlay(new OcrProcessor(…))` | Yes | ✅ |
| Enter while Follow | lock/unlock only (`LockFollowAtCurrent` / `BeginFollowFloating`) | **No speak** | ✅ |
| Region hotkey overlay open | `ActivateRegionSlot` | Switches slot only | ✅ |
| Region hotkey tray (hidden) | `ActivateRegionSlot` → `OcrProcessor.Start()` | Yes if slot has geometry | ✅ |
| Follow (region 9) | `SpeakFollowRegion` → overlay: `StartSpeakKeepingOverlay`; tray: `Start()` | Yes | ✅ |
| Stop TTS | `HOTKEY_STOP_TTS` → `AbortTtsInProgress` | Stops OCR + Balloons + announcements | ✅ |
| Custom hotkeys | `ExecuteCustomActionAt` → `SystemInput.ExecuteOnce` | No (unless KeyTap) | ✅ |
| Gamepad | `XInputPoller` edges → same actions as hotkeys | Same as keyboard | ✅ |
| Settings open | `_settingsOpen` / `IsToolWindowOpen` | Draw paused; Enter not stolen by overlay | ✅ |

### Speak starters (all construct `OcrProcessor` then `Start` or `StartSpeakKeepingOverlay`)

```
StartSpeakKeepingOverlay(next)
  → next.PrepareForCapture / RestoreAfterCapture (dim chrome)
  → next.Start()
       → CancelBackgroundComicSpeak
       → CTS reset; RestoreAudio
       → Task.Run(CaptureAndRecognizeAsync)

Tray region / Follow:
  → Task.Run(() => _current.Start())  // no overlay dim callbacks unless set
```

---

## 3. Shared speak preamble (`CaptureAndRecognizeAsync`)

Every live region speak (Default or Comic):

| # | Method | Notes |
|---|--------|--------|
| 1 | `LocalLlmHost.Start` + `IsApiReady` / `WaitUntilReadyAsync` (≤3 min) | Announce if not ready; **still continues** attempt |
| 2 | `PrepareForCapture` → `Task.Delay(80)` | Overlay dim |
| 3 | `SnapCapture` → shape: `CreateRectBitmap` / `CreateEllipseMaskedBitmap` / `CreateMaskedBitmapFromLasso` | |
| 4 | `RestoreAfterCapture` | Always in `finally` |
| 5 | `DevCaptureCache.PublishLastCapture(rawSnap)` | **Full-res** for Balloons (not Analytics thumbs) 📌 |
| 6 | Branch `ComicBookOff` (`!AppSettings.Current.ComicBook`) | |

---

## 4. Default path (ComicBook OFF)

```
CaptureAndRecognizeAsync
  → RunComicBookOffPreparedSnapAsync
       → BuildImagePrepStages(rawSnap, buildTone: true)   // letterbox → upscale → gray → tone
       → CaptureAnalyticsImage stages
       → RunFullFrameKoboldOnBitmapAsync(tone)
            → PrepareForLocalLlmOcr          // encode-scale layer (often clone)
            → ExtractTextWithLocalLlmAsync   // → LocalLlmClient.ChatAsync
            → CleanForSpeech
       → SplitSpeakPieces → speak plan
       → if empty: TryWinOcrSpeakFallbackAsync(tone, existingRegions: null)
       → DuckOtherAudio + SpeakWithSystemAsync (Windows UWP or SAPI per IsSapiTtsEngine)
       → WriteLastOcrDebug / LastResult
```

| Check | Expected | Status |
|-------|----------|--------|
| Fog | **None** | ✅ |
| Balloon detect | **None** (except empty-ladder OCR fallback may run detect) | ✅ |
| Crop stack / sequential | **None** | ✅ |
| Full-frame Local-LLM | **One** primary call (`RunFullFrameKoboldOnBitmapAsync`) | ✅ |
| Prep shared with Comic | Same `BuildImagePrepStages` | ✅ 📌 ModeSmoke Image preview equality |

### Failure / cancel

| Condition | Behavior |
|-----------|----------|
| Snap null | return early |
| Host not ready | announcement; HTTP likely fails → unreadable / empty ladder |
| Empty after full-frame | OCR detect fallback TTS |
| Still empty | speak `"unreadable"` |
| `Stop()` / CTS cancel | `OperationCanceledException` ends run |

---

## 5. Comic Book ON (live)

```
CaptureAndRecognizeAsync
  → BuildImagePrepStages → tone = ocrImage
  → optional ApplyGrayFog → detectImage          // detect only; Local-LLM reads tone
  → QuickWinOcrWordCountAsync(detectImage)       // DIAGNOSTIC ONLY — does not branch strategy
  → BuildComicReadingRegionsAsync(detectImage)   // always when no override
  → if ComicSequentialRegions && regions.Count > 0:
        RunSequentialRegionsSpeakAsync(ocrImage, regions, speakNow: true)
     else:
        RunFullAndCropsBestOfAsync(...)
  → if sequential spoke nothing OR best-of empty:
        empty ladder (below)
  → else best-of path: ExpandToSpeakPieces → Dedupe → Coalesce → SpeakWithSystemAsync
```

### 5.1 Shared region pipeline (`BuildComicReadingRegionsAsync`)

**Single code path** for:

- Live Comic speak  
- Balloons **Preview** (`PreviewComicRegionsAsync`)  
- Balloons **Speak** (`RunComicSpeakFromBitmapCoreAsync`)

```
DetectTextRegionsAsync(detectImage)
  → multi-pass RunWinOcrPassAsync (+ orphan fill)
  → ImproveDetectedRegions (grow X/Y, merge or nudge)
  → FilterDeadDetectRegions
  → CoalesceIntoReadingBlocks
  → optional TryCollapseCompactCluster
  → SplitMegaReadingIslandsAsync
  → ApplyMergeOverlappingIslandsIfEnabled
  → SortComicReadingOrderRegions
```

| Invariant | Status |
|-----------|--------|
| Word-count **never** skips this method on Comic ON | ✅ (body + `QuickWinOcrWordCountAsync` XML) |
| Preview and live share this method | ✅ |

### 5.2 Sequential (default `ComicSequentialRegions = true`)

```
RunSequentialRegionsSpeakAsync
  → optional mega near-full-frame → RunFullFrameWithWideRescueAsync
  → per region:
       ReadOneRegionAsync / crop Local-LLM + consensus
       KoboldUnderReadsWinOcr → full-frame rescue when appropriate
       prefer richer OCR-detect text if still better
       ExpandToSpeakPieces → Dedupe → Coalesce
       SpeakWithSystemAsync each unit (bubble pause between islands)
```

- **No** global crop-stack  
- **No** global speak-dedupe bag across balloons  

### 5.3 Best-of (`ComicSequentialRegions = false`)

```
RunFullAndCropsBestOfAsync
  → RunFullFrameWithWideRescueAsync
  → BuildVerticalCropStack + RunCropStackKoboldAsync   // primary when islands exist
  → per-crop fallback if stack fails
  → PickBestOfFullVsCrops
```

### 5.4 Empty ladder (live + Balloons speak)

Runs when primary path yields nothing usable (including sequential with `spokenParts.Count == 0`):

1. `RunFullFrameKoboldOnBitmapAsync(…, promptOverride: SimpleExtractPrompt)` — **no** settings flip  
2. `TryWinOcrSpeakFallbackAsync` — OCR detect text as last-resort TTS  

### 5.5 Region override (Balloons refine)

```
RunComicSpeakFromBitmapCoreAsync(raw, token, regionOverride)
  → if override non-empty: RegionsFromOverride — skip DetectTextRegionsAsync
  → else: BuildComicReadingRegionsAsync
  → same sequential / best-of / empty ladder
```

Session: `ComicRegionOverrideSession` + `frm_ComicRegions` refine surface; armed speak on overlay hide via `TrySpeakOverrideOnOverlayHide`.

---

## 6. Two prep layers (do not confuse)

| Layer | Methods | Role |
|-------|---------|------|
| Pipeline tone | `BuildImagePrepStages` | Image-tab look; tone for Local-LLM; fog clone for detect only |
| Encode / crop scale | `PrepareForPaddleOcr`, `PrepareCropForPaddleOcr` | After tone, before HTTP |

There is **no** type `ImagePrepPipeline` today.

### Settings bridge (comic)

| `AppSettings` | `OcrProcessor` alias / read |
|---------------|------------------------------|
| `ComicDetectFog` | `EnableWinOcrDetectGrayFog` |
| `ComicDetectFogAmount` | `WinOcrDetectGrayFogAmount` |
| `ComicRegionPadding` | `TextRegionPadding` |
| `ComicSequentialRegions` | `AppSettings.Current.ComicSequentialRegions` |
| Cluster / inflate / dense / merge / orphans / min alnum | various `Active*` / direct settings |

---

## 7. Local-LLM HTTP boundary

| Method | Role |
|--------|------|
| `KoboldCppHost.ApiBaseUrl` / `ModelApiId` / `Port` | From `ocr.kcpps` |
| `ExtractTextWithPaddleAsync` | Vision chat request (legacy name) |
| `KoboldChatAsync` / `BuildKoboldChatRequestJson` / `BuildKoboldUserContent` | HTTP + JsonObject (obfuscation-safe) |
| `RunKoboldConsensusAsync` | Comic diversified temps |

**Network:** `127.0.0.1` only (no cloud OCR/telemetry).

---

## 8. TTS

| Engine | Gate | Methods |
|--------|------|---------|
| Windows Media (default) | `!IsSapiTtsEngine` | `SpeakWithSystemAsync` → `SpeechSynthesizer` + `MediaPlayer` |
| SAPI 5 | `IsSapiTtsEngine` | `SpeakWithSapiAsync` |
| Announcements | UI only | `SpeakAnnouncement` (no duck; cancel prior) |
| Ducking | long speak | `DuckOtherAudio` / `RestoreAudio` (NAudio sessions) |

Speech text cleaning: `CleanForSpeech` → `SpeechTextRulesEngine` / `SpeechRulesEngine` stages (abbrevs, noise, pause marks).

---

## 9. Settings forms (in / out)

| Form | In | Out |
|------|----|-----|
| `frm_Settings` | Tab shell | Hosts children; profile load/save |
| `frm_HotkeyMap` | Chords / customs | `AppSettings` + re-register hotkeys |
| `frm_RegionMap` | Slots geometry | `RegionSlots` → overlay apply |
| `frm_FollowSettings` | R9 box | Follow INI; live paint refresh |
| `frm_VoiceSettings` | TTS engine/voice | `NormalizeVoiceSettings` |
| `frm_ImagePrep` | Prep knobs | Shared prep; `DevCaptureCache` prep gen |
| `frm_ComicRegions` | Detect knobs + refine | Preview/Speak; override session |
| `frm_SpeechRules` | Catalog | Speech rules INI |
| `frm_Analytics` | `OcrProcessor.LastResult` | Read-only; `SanitizeUiEngineNames` |
| `frm_Help` | Static copy | README link |

**Preview-only (no Local-LLM):** Image prep stage preview; Balloons Preview (`PreviewComicRegionsAsync`).

---

## 10. ModeSmoke regression anchors

Exact names from `tests/ModeSmoke/Program.cs` (pure section runs without GPU):

### Required for extract gate

| Invariant | Check / section name |
|-----------|----------------------|
| Vision JSON wire shape | `Kobold JSON shape (obfuscation-safe)` → `SmokeVerifyKoboldJsonShape` |
| Balloons full-res cache | Section `--- Balloons last capture == full-res live snap ---` |
| | `Last capture width matches live snap (not 1280 cap)` |
| | `Last capture pixels == live snap (Balloons source identity)` |
| Dead-island parity | `Dead-island keeps one-word dialogue on balloon plate` |
| | `Dead-island drops single-token logo on non-balloon art (cream)` |
| | `Dead-island keeps multi-word dialogue island` |
| Shared prep Default vs Comic | `Default prep pixels == ComicBook prep pixels (shared pipeline)` |
| Sequential default | `ComicSequentialRegions defaults on (isolates balloons at speak time)` |
| Wordcount does not gate detect | **Code invariant** (no branch after `QuickWinOcrWordCountAsync`); ModeSmoke pure does not live-run Comic detect — protect with extract reviews + this checklist |

### Geometry / merge (Balloons knobs)

| Check name |
|------------|
| `Side-by-side: left balloon before elevated right (L→R)` |
| `2x2 grid is row-major (TL→TR→BL→BR)` |
| `Emplate-like mega region is near-full-frame` |
| `Kobold under-read vs WinOCR (29 vs 54 words)` |
| `Two overlapping islands → one union covering both` |
| `Near islands merge when Crop pad would bridge the gap` |
| `ComicMergeOverlappingIslands defaults on` |

### Speech cleaner (sample)

| Check name |
|------------|
| `Expand mr. → mister (title stays with name, no leftover period)` |
| `you're left intact (not expanded to you are)` |
| `Noise rules strip uchar from cleaned speech` |
| `JSON wrapper: does not speak key 'text something'` |

### Live (optional / GPU)

| Check name |
|------------|
| `Kobold API ready` |
| Default / Comic capture matrix (after host ready) |

---

## 11. Bugs / debt found during Phase 3 trace

| ID | Severity | Finding | Disposition |
|----|----------|---------|-------------|
| P3-1 | Minor UX | `SpeakAnnouncement` said “OCR is not ready” for Local-LLM host wait | **Fixed** → “Local-LLM is not ready…” |
| P3-2 | Stale docs | `QuickWinOcrWordCountAsync` XML said “Used to gate full detect/crops” | **Fixed** → diagnostic only |
| P3-3 | Design doc lag | Architecture doc still listed removed short-circuit comments / `FullPipelineMinWinOcrWords` as present | Note: Phase 2 removed const + fixed headers; design Correctness debt table is historical |
| P3-4 | Naming debt | `ExtractTextWithPaddleAsync` / `KoboldChatAsync` / `WinOcr*` identifiers | Phase 5 — not Phase 3 |
| P3-5 | Intentional | Legacy `CustomActionKind` still execute | Keep for old INI |
| P3-6 | Intentional | Host wait failure still continues speak attempt | May produce empty/unreadable; announcement already fired |

No algorithm bugs confirmed in this pass that required logic changes.

---

## 12. Manual smoke matrix (operator)

| # | Scenario | Pass criteria |
|---|----------|---------------|
| M1 | Overlay draw rect → Enter | Speaks; overlay stays; Analytics has images |
| M2 | Tray Shift+F1 with saved region | Speaks without overlay |
| M3 | Follow hotkey floating | Box tracks; speaks R9 geometry |
| M4 | Enter while Follow | Locks box; **no** speak |
| M5 | Comic ON sequential multi-balloon | One island at a time; no cross-balloon dedupe wipe |
| M6 | Comic OFF short “No!” on busy art | May use OCR fallback |
| M7 | Balloons Preview then Speak with refine | Same boxes; override skips detect |
| M8 | Stop TTS mid-speak | Silence; next speak works |
| M9 | Exit tray | `koboldcpp` process gone |
| M10 | Hide tray | Host **still** running |

---

## 13. Extract readiness map

| Extract target (Phase 4) | Status (2026-07-30) | Notes |
|--------------------------|---------------------|--------|
| `LocalLlmClient` | **Done** | `BuildUserContent`, `BuildChatRequestJson`, `ChatAsync`, JSON smoke |
| `SpeechCleaner` | **Facade done** | Public entry; body still `OcrProcessor.CleanForSpeech` until helper graph moves |
| `ComicRegionGeometry` | **Partial** | `RegionIsNearFullFrame`, under-read, `CountWords`; merge/sort still in `OcrProcessor` |
| `BalloonOcrDetect` | **Partial** | `GetEngine` only; multi-pass detect still in `OcrProcessor` |
| `DetectedTextRegion` | **Done** | Top-level public type |
| `RegionSlotData` file | **Done** | Un-nested from `AppSettings` |
| `HotkeyChord` / `GamepadButton` files | **Done** | File split only |

---

## 14. Related docs

| Doc | Role |
|-----|------|
| `docs/SPEAKRECT_ARCHITECTURE_OSS.md` | Full as-built design + PR plan |
| `docs/dev/smoke-runbook.md` | How to run smokes |
| `docs/GITHUB_REPOS.md` | Public git topology + release channel |

---

**Phase 3 exit criteria (met):**

- [x] Method-level Default / Comic sequential / best-of / empty ladder documented  
- [x] ModeSmoke names for JSON shape, Balloons full-res, dead-island mapped  
- [x] Bugs found: branding + stale QuickWinOcr XML fixed  
- [x] Artifact path: `docs/architecture/speak-path-checklist.md`  
