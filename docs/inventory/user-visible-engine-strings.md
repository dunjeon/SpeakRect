# User-visible engine string inventory

**Baseline:** SpeakRect 1.4.19 · commit `64192263a0010c819f0ff61132cbc876b75e493a` · 2026-07-30  
**Glossary:** detect → **OCR**; vision host → **Local-LLM** (never brand Kobold/WinOCR in UI)  
**Action codes:** `RENAME_UI` | `RENAME_INTERNAL_LATER` | `DOCS_ONLY` | `KEEP` | `DONE`

## Summary (pre–Phase 1)

| Surface | Hits | Action |
|---------|------|--------|
| Static Balloons UI (`frm_ComicRegions`) | ~12 string literals | RENAME_UI |
| `RegionRefineSurface` empty paint | 1 | RENAME_UI |
| `UiTheme.SanitizeUiEngineNames` | maps Kobold→OCR (**bug**) | RENAME_UI (two-axis map) |
| `AppSettings` INI comments | 3 | RENAME_UI (user can open .ini) |
| README / public-repo README | several KoboldCpp | RENAME_UI / DOCS_ONLY (third-party credit keeps real name) |
| `frm_Help` | "Local LLM" spaced | KEEP (prose OK) or Local-LLM |
| Identifiers (`KoboldCppHost`, `WinOcrText`, …) | hundreds | RENAME_INTERNAL_LATER |
| Debug / pipeline detail strings | many | sanitized for UI; internal later |

## Static UI literals (must change in Phase 1)

| File | Approx | Kind | Current | Target | Action |
|------|--------|------|---------|--------|--------|
| `frm_ComicRegions.cs` | intro hint | UI-literal | Tune how **WinOCR** finds… | Tune how **OCR** finds… | RENAME_UI |
| `frm_ComicRegions.cs` | checkbox | UI-literal | Gray fog for **WinOCR** detect | Gray fog for **OCR** detect | RENAME_UI |
| `frm_ComicRegions.cs` | merge hint | UI-literal | where **WinOCR** splits… | where **OCR** splits… | RENAME_UI |
| `frm_ComicRegions.cs` | empty label | UI-literal | seed **WinOCR** boxes | seed **OCR** boxes | RENAME_UI |
| `frm_ComicRegions.cs` | status strings | UI-literal | Re-detect… **WinOCR** | Re-detect… **OCR** | RENAME_UI |
| `frm_ComicRegions.cs` | seed status | UI-literal | **WinOCR** seeded… / No **WinOCR** islands | **OCR** seeded… / No **OCR** islands | RENAME_UI |
| `frm_ComicRegions.cs` | speak status | UI-literal | auto **WinOCR** / auto **WinOCR** island(s) | auto **OCR** / auto **OCR** island(s) | RENAME_UI |
| `RegionRefineSurface.cs` | paint | UI-literal | seed **WinOCR** boxes | seed **OCR** boxes | RENAME_UI |
| `UiTheme.cs` | sanitizer | code | Kobold→OCR / OCR engine | Kobold→**Local-LLM**; WinOCR→**OCR** | RENAME_UI |
| `AppSettings.cs` | INI write | UI-literal (file) | `; … WinOCR …` | `; … OCR …` | RENAME_UI |
| `README.md` | highlight | docs | KoboldCpp + GLM-OCR | Local-LLM host + GLM-OCR | RENAME_UI |
| `README.md` | credits table | docs | Local LLM host **KoboldCpp** | keep project name in credits; prose Local-LLM | DOCS_ONLY / partial |
| `public-repo/README.md` | same as root | docs | same | same | RENAME_UI |
| `frm_Help.cs` | subtitle | UI-literal | Local LLM · Windows speech… | OK as spaced; optional Local-LLM | KEEP |

## Identifier debt (Phase 5 — do not churn in Phase 1)

| Symbol | File | Later target |
|--------|------|----------------|
| `KoboldCppHost` | `KoboldCppHost.cs` | `LocalLlmHost` |
| `ExtractTextWithPaddleAsync` | `OcrProcessor.cs` | `ExtractTextWithLocalLlmAsync` |
| `PrepareForPaddleOcr` | `OcrProcessor.cs` | role-matched name |
| `GetWinOcrEngine` / `RunWinOcrPassAsync` / `WinOcrText` | `OcrProcessor.cs` | optional `BalloonOcr*` |
| `UseWinOcr` / `SkipWinOcrSendFullFrameOnly` | `AppSettings` load only | obsolete keys — KEEP for migration |

## Verification commands (post Phase 1)

```powershell
rg -n '"[^"]*WinOCR[^"]*"' --glob "*.cs"
rg -n '"[^"]*Kobold[^"]*"' --glob "frm_*.cs" --glob "UiTheme.cs" --glob "RegionRefineSurface.cs"
```

Identifiers in `OcrProcessor.cs` / `KoboldCppHost.cs` may still contain WinOCR/Kobold until Phase 5.
