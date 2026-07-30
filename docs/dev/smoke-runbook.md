# Smoke runbook

**Product:** SpeakRect 1.4.19+  
**Baseline commit:** `64192263a0010c819f0ff61132cbc876b75e493a`  
**Last baseline run:** _(fill after first pass)_

## Requirements

| Smoke | Needs GPU / models? | CI gate? |
|-------|---------------------|----------|
| RegionSmoke | No | Manual / local |
| SettingsSmoke | No (WinForms session) | Nightly / manual only |
| ModeSmoke (pure checks) | No for early asserts | Manual |
| ModeSmoke (live OCR matrix) | **Yes** (Local-LLM + VRAM) | **Never** required for PR |

From repo root (`SpeakRect.sln` directory):

## Build

```powershell
dotnet build SpeakRect.sln -c Debug
```

Expected: `Build succeeded` (0 errors). Warnings may exist — do not treat as pass/fail for baseline.

## RegionSmoke (INI / region serialization)

```powershell
dotnet run --project tests/RegionSmoke/RegionSmoke.csproj -c Debug --no-build
# or with build:
dotnet run --project tests/RegionSmoke/RegionSmoke.csproj -c Debug
```

Expected: console ends with overall PASS (or equivalent success exit code 0).  
Covers: `RegionSlotData` rect/oval/lasso, legacy lasso separators.

## SettingsSmoke (WinForms settings shell)

```powershell
dotnet run --project tests/SettingsSmoke/SettingsSmoke.csproj -c Debug
```

Expected: opens Settings UI paths, captures layout screenshots under debug dirs, exits 0.  
May fail headless or without an interactive session.

## ModeSmoke

```powershell
dotnet run --project tests/ModeSmoke/ModeSmoke.csproj -c Debug
```

- **Early pure checks** (speech cleaner, geometry helpers, JSON shape) run without a healthy model.
- **Live matrix** waits for Local-LLM API (`koboldcpp` folder + GGUFs). If host not ready, live section aborts after pure checks — treat live as SKIP if models missing.

### Common failures

| Symptom | Likely cause |
|---------|----------------|
| `koboldcpp folder not found` | Run from wrong cwd or missing payload next to repo/bin |
| API ready timeout | Model load / insufficient VRAM / Vulkan |
| JSON shape fail | Obfuscation / JsonObject builder regression |
| Balloons full-res fail | DevCaptureCache vs Analytics thumb path |

## Pure unit tests (Phase 6 — required for CI)

```powershell
dotnet test tests/SpeakRect.Tests/SpeakRect.Tests.csproj -c Debug
# or:
dotnet test SpeakRect.sln -c Debug --filter "FullyQualifiedName~SpeakRect.Tests"
```

No Local-LLM models required. GitHub Actions: `.github/workflows/ci.yml`.

## Recording a baseline

| Date | RegionSmoke | SettingsSmoke | ModeSmoke pure | ModeSmoke live | Notes |
|------|-------------|---------------|----------------|----------------|-------|
| 2026-07-30 | **PASS** (all) | not run (UI session) | **PASS** (JSON + sanitizer early checks) | SKIP (killed before live) | Phase 0/1 start; sanitizer two-axis |
