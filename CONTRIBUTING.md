# Contributing to SpeakRect

Thanks for interest in SpeakRect. This document is a **draft** for open-source readiness work. Contribution legal process (CLA vs DCO) is **not final** until the product owner answers open questions in `docs/SPEAKRECT_ARCHITECTURE_OSS.md`.

## Prerequisites

- Windows 10/11 **x64**
- .NET **10** SDK (TFM `net10.0-windows10.0.26100.0`)
- Optional: GPU + bundled Local-LLM payload under `koboldcpp\` for live OCR testing

## Build

```powershell
git clone <repo-url>
cd SpeakRect
dotnet build SpeakRect.sln -c Debug
dotnet run --project SpeakRect.csproj -c Debug
```

Local-LLM binaries/models: see `docs/dev/lfs-history-strategy.md` and (when available) `scripts/Fetch-LocalLlm.ps1`. Folder on disk remains `koboldcpp\` for install compatibility.

## Tests / smokes

See [`docs/dev/smoke-runbook.md`](docs/dev/smoke-runbook.md).

- Pure unit tests (when present): `dotnet test tests/SpeakRect.Tests`
- Live ModeSmoke is **optional** and needs GPU + models

## Product language (UI)

| Concept | User-facing term |
|---------|------------------|
| Windows.Media.Ocr balloon detect | **OCR** |
| Bundled vision host + model | **Local-LLM** |

Do not put **WinOCR** or **Kobold** / **KoboldCpp** in user-visible strings. On-disk folder `koboldcpp\` and third-party credit names may remain.

## Architecture & product decisions

- Design: [`docs/SPEAKRECT_ARCHITECTURE_OSS.md`](docs/SPEAKRECT_ARCHITECTURE_OSS.md)
- Git / releases: [`docs/GITHUB_REPOS.md`](docs/GITHUB_REPOS.md), [`publish/AGENT_RELEASE.md`](publish/AGENT_RELEASE.md)
- Product decisions: [`docs/OSS_PRODUCT_DECISIONS.md`](docs/OSS_PRODUCT_DECISIONS.md)

## Pull request expectations

1. Prefer small PRs (see design PR plan).
2. Extract/refactor PRs: **no algorithm or threshold changes**.
3. Do not commit `SpeakRect.ini`, profiles, `Mapping.txt`, GGUF/exe dumps, or personal paths.
4. Run relevant smokes for the area you touch.

## License

SpeakRect application source is **GPLv2** — see [LICENSE](LICENSE). By contributing, you agree your contributions are licensed under the same terms.

## Code of conduct / DCO

- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)  
- **DCO:** commits should be signed off (`git commit -s`) affirming the Developer Certificate of Origin  
- Product decisions for OSS flip: [docs/OSS_PRODUCT_DECISIONS.md](docs/OSS_PRODUCT_DECISIONS.md)

## Security

See [`SECURITY.md`](SECURITY.md).
