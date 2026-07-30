# Contributing to SpeakRect

## Prerequisites

- Windows 10/11 **x64**
- .NET **10** SDK (`net10.0-windows10.0.26100.0`)
- [Git LFS](https://git-lfs.com/) — the Local-LLM host and GGUF models are **in this repository** under `koboldcpp\` (stored with LFS)
- GPU recommended for live recognition

## Clone and build

A normal clone (with LFS) includes everything needed to run recognition:

| Path | Role |
|------|------|
| `koboldcpp\koboldcpp.exe` | Local-LLM host |
| `koboldcpp\glmocr-Q8_0.gguf` | Vision model |
| `koboldcpp\mmproj-glmocr-Q8_0.gguf` | Multimodal projector |
| `koboldcpp\ocr.kcpps` | Host config |

```powershell
git lfs install
git clone https://github.com/dunjeon/SpeakRect.git
cd SpeakRect
dotnet build SpeakRect.sln -c Debug
dotnet run --project SpeakRect.csproj -c Debug
```

If files under `koboldcpp\` are tiny text stubs instead of real binaries, run:

```powershell
git lfs pull
```

You do **not** need a separate release zip just to build and run from source.

## Tests

```powershell
dotnet test tests/SpeakRect.Tests
```

Smokes (optional): see [`docs/dev/smoke-runbook.md`](docs/dev/smoke-runbook.md).  
Live ModeSmoke needs GPU and is **not** a PR gate. Unit tests do not require the model files.

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
| [`docs/GITHUB_REPOS.md`](docs/GITHUB_REPOS.md) | Repo layout (source + LFS payload) |
| [`docs/dev/smoke-runbook.md`](docs/dev/smoke-runbook.md) | How to run smokes |

## Pull requests

1. Prefer small PRs.
2. Pure extracts: **no algorithm or threshold changes**.
3. Do not commit personal profiles, `SpeakRect.ini`, or debug dumps (`_debug_view/`, smoke logs).
4. Leave `koboldcpp\` model/host updates to maintainers unless the PR is intentionally bumping the bundled payload (LFS).
5. Run unit tests (and relevant smokes) for the area you touch.

## License

**GPLv2** — see [LICENSE](LICENSE). Contributions are under the same terms.

## Code of conduct / DCO

- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
- Sign off commits: `git commit -s` (Developer Certificate of Origin)

## Security

See [SECURITY.md](SECURITY.md).
