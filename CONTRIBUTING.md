# Contributing to SpeakRect

## Prerequisites

- Windows 10/11 **x64**
- .NET **10** SDK (`net10.0-windows10.0.26100.0`)
- Optional: GPU + Local-LLM files under `koboldcpp\` for live recognition

## Clone and build

```powershell
git clone https://github.com/dunjeon/SpeakRect.git
cd SpeakRect
dotnet build SpeakRect.sln -c Debug
dotnet run --project SpeakRect.csproj -c Debug
```

Large Local-LLM binaries are **not** in git. For live recognition, extract a [release zip](https://github.com/dunjeon/SpeakRect/releases) so `koboldcpp\` (host + GGUFs) is available next to the app / repo.

On-disk folder name stays `koboldcpp\`. Small config `koboldcpp/ocr.kcpps` is in the repo.

## Tests

```powershell
dotnet test tests/SpeakRect.Tests
```

Smokes (optional): see [`docs/dev/smoke-runbook.md`](docs/dev/smoke-runbook.md).  
Live ModeSmoke needs GPU + models and is **not** a PR gate.

## Product language (UI)

| Concept | Term |
|---------|------|
| Balloon / region detect | **OCR** |
| Vision host + model | **Local-LLM** |

Do not put **WinOCR** or **Kobold** / **KoboldCpp** in user-visible strings.

## Docs map

| Doc | Use |
|-----|-----|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | As-built overview |
| [`docs/architecture/speak-path-checklist.md`](docs/architecture/speak-path-checklist.md) | Capture → speak call graph |
| [`docs/GITHUB_REPOS.md`](docs/GITHUB_REPOS.md) | What is / isn’t in git |
| [`docs/dev/smoke-runbook.md`](docs/dev/smoke-runbook.md) | How to run smokes |

## Pull requests

1. Prefer small PRs.
2. Pure extracts: **no algorithm or threshold changes**.
3. Do not commit profiles, `SpeakRect.ini`, GGUF/exe dumps, or personal paths.
4. Run unit tests (and relevant smokes) for the area you touch.

## License

**GPLv2** — see [LICENSE](LICENSE). Contributions are under the same terms.

## Code of conduct / DCO

- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
- Sign off commits: `git commit -s` (Developer Certificate of Origin)

## Security

See [SECURITY.md](SECURITY.md).
