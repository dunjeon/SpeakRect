# SpeakRect architecture (as-built)

**Product:** SpeakRect 1.4.43 · **License:** GPLv2 · **Platform:** Windows x64 (WinForms + WPF)

For the full speak/capture call graph, see [`architecture/speak-path-checklist.md`](architecture/speak-path-checklist.md).  
For build/test steps, see [`../CONTRIBUTING.md`](../CONTRIBUTING.md) and [`dev/smoke-runbook.md`](dev/smoke-runbook.md).

## What it does

Users draw or save screen regions; SpeakRect captures those pixels, recognizes text with a **local** vision model (Local-LLM host + GLM-OCR GGUFs under `koboldcpp\`), and speaks via **Windows TTS** (default) or optional SAPI 5.

| Mode | Path |
|------|------|
| **Default** (Comic Book off) | Image prep → one full-frame Local-LLM call → speech clean → TTS |
| **Comic Book on** | Image prep → **OCR** balloon detect (optional gray fog on detect only) → per-island VL when islands found → speech clean → TTS |
| **Comic Book + POI** (Balloons → POI guide) | Same detect → green **edit map** on tone (± outside fog; not VL when island canvases on) → stock island canvases: orange canvas VL ×N → TTS; canvas off/fail multi → tone crop VL; 1 island + canvas off → full-page guide VL |

No cloud recognition or telemetry. Local-LLM HTTP is **loopback only** (`127.0.0.1`).

## Product language

| User-facing | Means |
|-------------|--------|
| **OCR** | Windows.Media.Ocr balloon/region detect |
| **Local-LLM** | Bundled host + vision model |
| On disk `koboldcpp\` | Install folder name (keep for compatibility) |

Do not put WinOCR / Kobold brand names in UI strings. Credits may name third-party projects.

## Process model

| Process | Role |
|---------|------|
| `SpeakRect.exe` | UI, capture, client, TTS |
| `koboldcpp\koboldcpp.exe` | Local vision server; started by `LocalLlmHost`; Job Object kill-on-close |

Single-instance mutex: `Global\SpeakRect_SingleInstance_2026`.

## Main source map

| Area | Types / files |
|------|----------------|
| Entry | `Program.cs` |
| Overlay / tray | `frm_SpeakRect.cs`, `LowLevelInputHooks.cs`, `OverlaySidebarChromeForm.cs` |
| Capture → recognize → TTS | `OcrProcessor.cs` |
| Comic fusion / geometry | `ComicBestOfFusion`, `ComicConsensus`, `ComicRegionGeometry`, `BalloonOcrDetect`, `ComicDetectTonePair` |
| Host / HTTP | `LocalLlmHost.cs`, `LocalLlmClient.cs` |
| Speech clean | `SpeechCleaner.cs` |
| Settings / slots | `AppSettings.cs`, `RegionSlotData.cs`, settings forms `frm_*.cs` |
| Theme / UI sanitize | `UiTheme.cs` |
| Input | `HotkeyChord.cs`, `GamepadButton.cs`, `XInputPoller.cs`, `SystemInput.cs` |

## Tests

| Project | Role |
|---------|------|
| `tests/SpeakRect.Tests` | Pure xUnit (CI) |
| `tests/RegionSmoke` | Region INI / geometry console smoke |
| `tests/SettingsSmoke` | Settings shell (manual / local) |
| `tests/ModeSmoke` | Pure asserts + optional live GPU matrix |

```powershell
dotnet test tests/SpeakRect.Tests
```

## Local-LLM payload (`koboldcpp\`)

Tracked in git via **Git LFS**:

| File | Role |
|------|------|
| `koboldcpp.exe` | Local inference host |
| `glmocr-Q8_0.gguf` | Vision / OCR model |
| `mmproj-glmocr-Q8_0.gguf` | Multimodal projector |
| `ocr.kcpps` | Host config (normal git) |

Clone with `git lfs install` first. Ignored: `*.log`, `ocr.runtime.kcpps`.

App-only publish (no payload copy): `dotnet publish -p:SkipKoboldPayload=true`. Official releases ship **unobfuscated** single-file + ReadyToRun.

## Third-party

See [`../THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md) (KoboldCpp AGPL host, GLM-OCR weights, .NET / NuGet).
