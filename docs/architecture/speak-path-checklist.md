# Speak-path checklist

Method-level map of capture → recognize → speak. Update this file when you change the call chain.

**Product:** SpeakRect 1.4.23+ · See also [`../ARCHITECTURE.md`](../ARCHITECTURE.md)

**Legend:** ✅ intended · ⚠️ edge · 📌 regression anchor

---

## 1. Lifecycle

| Step | Method / type | Intent |
|------|---------------|--------|
| Single-instance | `Program.Main` mutex `Global\SpeakRect_SingleInstance_2026` | Second instance exits |
| Dark chrome | `UiTheme.InitAppDarkMode` | Before first window |
| Settings | `AppSettings.Current.Load()` | INI / profiles before UI |
| Local-LLM start | `LocalLlmHost.Start()` | Non-blocking; Job Object; adopt healthy port |
| App run | `Application.Run(new frm_SpeakRect())` | Overlay is message pump |
| Exit | `LocalLlmHost.Stop()` from exit handlers + form close | Hide-to-tray does **not** stop host |

| Question | Answer |
|----------|--------|
| Double `Start` / `Stop`? | Safe under `lock` |
| Task Manager kill SpeakRect? | Job Object should kill child host |
| Missing `koboldcpp\`? | Start logs/skips; speak announces Local-LLM not ready |

---

## 2. Overlay / input → speak

| Action | Entry | Speaks? |
|--------|-------|---------|
| Toggle overlay | `HOTKEY_TOGGLE_OVERLAY` → `ToggleOverlayFromInput` | No |
| Draw RECT/OVAL/LASSO | mouse / paint | No until Enter |
| Enter (regions 1–8) | `ProcessDialogKey` → `StartSpeakKeepingOverlay` | Yes |
| Enter while Follow | lock/unlock only | **No** |
| Region hotkey (tray) | `ActivateRegionSlot` → `OcrProcessor.Start()` | Yes if geometry |
| Follow (region 9) | `SpeakFollowRegion` | Yes |
| Stop TTS | `HOTKEY_STOP_TTS` → `AbortTtsInProgress` | Stops speak |
| Gamepad | `XInputPoller` → same actions as hotkeys | Same |

```
StartSpeakKeepingOverlay(next)
  → PrepareForCapture / RestoreAfterCapture
  → next.Start() → Task.Run(CaptureAndRecognizeAsync)
```

---

## 3. Shared preamble (`CaptureAndRecognizeAsync`)

1. `LocalLlmHost` ready wait (announce if slow; attempt may continue)
2. `PrepareForCapture` → delay → snap (`CreateRectBitmap` / ellipse / lasso)
3. `RestoreAfterCapture` (always)
4. `DevCaptureCache.PublishLastCapture` — **full-res** for Balloons 📌
5. Branch Default vs Comic Book

---

## 4. Default path (Comic Book OFF)

```
RunComicBookOffPreparedSnapAsync
  → shared image prep (BuildImagePrepStages / tone)
  → one full-frame Local-LLM call
  → SpeechCleaner → TTS
```

Optional OCR fallback for very short text on busy art (settings-dependent).

---

## 5. Comic Book path (ON)

```
shared image prep → ComicDetectTonePair (fog on detect only; Local-LLM reads tone)
  → BalloonOcrDetect multi-pass / cluster
  → ComicRegionGeometry merge/sort/pad
  → [if ComicPoiMarkers] red bullseye POI (+ optional outside fog) → one full-frame Local-LLM
  → else sequential / crop-stack / best-of as configured
  → SpeechCleaner → TTS
```

**POI guide** (`AppSettings.ComicPoiMarkers`, Balloons tab): Comic Book alternate.
Forces Comic Book on. **Live + Balloons Speak share `RunComicPoiGuideAsync`.**
- **Canvas is always TONE** (detect fog is WinOCR-only; never POI base).  
- Preview seeds **tone** when POI is on; display boxes = grow + crop pad (final).  
- 1 island: `DrawRegionGuides` on tone → that bitmap is VL input + `poi_guide` + `last_poi_vl_input.png`.  
- 2+ islands: same guide published for analytics/preview map; **speak = sequential tone crops**  
  (stack debug PNG only; not VL input).  
- Balloons Speak publishes Analytics (`_runImages`) without clearing refine session.

**Regression anchors (ModeSmoke):**

- Balloons last capture == full-res live snap  
- Live and Balloons share `BuildComicReadingRegionsAsync` (dead-island / cream logo)  
- `SmokeVerifyKoboldJsonShape` for vision JSON  

---

## 6. Speech + TTS

| Stage | Owner |
|-------|--------|
| Pre-TTS clean | `SpeechCleaner` / `OcrProcessor.CleanForSpeech` |
| Windows Media TTS | default engine |
| SAPI 5 | optional |
| Abort | `AbortTtsInProgress` (default Ctrl+Shift+S) |

---

## 7. Network

Local-LLM client → `http://127.0.0.1:{port}/v1/` only. No cloud OCR/telemetry.

---

## 8. Extract / refactor rules

1. **No algorithm or threshold changes** in pure extract PRs.
2. Re-run `tests/SpeakRect.Tests` + relevant smokes.
3. Keep this checklist accurate in the same PR.
