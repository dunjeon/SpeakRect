# Outbound HTTP inventory

**Verified:** 2026-07-30  
**Baseline commit:** `64192263a0010c819f0ff61132cbc876b75e493a`

## Result: no cloud telemetry

Application `HttpClient` usage is **localhost Local-LLM only**.

| Location | URL / use | Network |
|----------|-----------|---------|
| `KoboldCppHost.ApiBaseUrl` | `http://127.0.0.1:{port}/v1/` | loopback |
| `KoboldCppHost` Probe/Start health | `GET …/v1/models` via short-lived `HttpClient` | loopback |
| `OcrProcessor.CreateKoboldClient` | Shared client to Local-LLM OpenAI-compatible API | loopback |
| `OcrProcessor` SSML | `xmlns="http://www.w3.org/2001/10/synthesis"` | **not** a network call |
| `UiTheme.cs` | Comment link to learn.microsoft.com | docs comment only |
| `tests/ModeSmoke` | Fixture markdown with `http://x.test` | test string only |

## Policy

- Phase 0+ PRs must not add cloud `HttpClient` endpoints without a design update and privacy review.
- Re-run: `rg -n "HttpClient|https?://" --glob "*.cs"`
