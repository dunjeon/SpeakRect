# Contributing to SpeakRect

Thanks for interest in SpeakRect.

## Prerequisites

- Windows 10/11 **x64**
- .NET **10** SDK (TFM `net10.0-windows10.0.26100.0`)
- Optional: GPU + Local-LLM payload under `koboldcpp\` for live recognition testing

## Build

```powershell
git clone https://github.com/dunjeon/SpeakRect.git
cd SpeakRect
dotnet build SpeakRect.sln -c Debug
dotnet run --project SpeakRect.csproj -c Debug
```

Large Local-LLM binaries are **not** in git. Use a release zip extract or `scripts/Fetch-LocalLlm.ps1` when configured. On-disk folder remains `koboldcpp\` for install compatibility. See [`docs/GITHUB_REPOS.md`](docs/GITHUB_REPOS.md).

## Tests / smokes

See [`docs/dev/smoke-runbook.md`](docs/dev/smoke-runbook.md).

- Pure unit tests: `dotnet test tests/SpeakRect.Tests`
- Live ModeSmoke is **optional** and needs GPU + models

## Product language (UI)

| Concept | User-facing term |
|---------|------------------|
| Windows.Media.Ocr balloon detect | **OCR** |
| Bundled vision host + model | **Local-LLM** |

Do not put **WinOCR** or **Kobold** / **KoboldCpp** in user-visible strings. On-disk folder `koboldcpp\` and third-party credit names may remain.

## Docs

- Design / architecture: [`docs/SPEAKRECT_ARCHITECTURE_OSS.md`](docs/SPEAKRECT_ARCHITECTURE_OSS.md)
- Speak path: [`docs/architecture/speak-path-checklist.md`](docs/architecture/speak-path-checklist.md)
- Git / releases: [`docs/GITHUB_REPOS.md`](docs/GITHUB_REPOS.md)

## Pull request expectations

1. Prefer small PRs.
2. Extract/refactor PRs: **no algorithm or threshold changes** unless that is the point of the PR.
3. Do not commit `SpeakRect.ini`, profiles, `Mapping.txt`, GGUF/exe dumps, or personal paths.
4. Run relevant smokes for the area you touch.

## License

SpeakRect application source is **GPLv2** — see [LICENSE](LICENSE). By contributing, you agree your contributions are licensed under the same terms.

## Code of conduct / DCO

- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)  
- **DCO:** commits should be signed off (`git commit -s`) affirming the Developer Certificate of Origin  

## Security

See [`SECURITY.md`](SECURITY.md).
