# Repository layout

**Remote:** [github.com/dunjeon/SpeakRect](https://github.com/dunjeon/SpeakRect) (public, GPLv2)

| In the clone | Purpose |
|--------------|---------|
| `*.cs`, `SpeakRect.csproj`, `SpeakRect.sln` | Application source |
| `tests/` | Unit tests + optional smokes |
| `docs/` | Architecture, speak-path, smoke runbook |
| `koboldcpp/ocr.kcpps` | Small host config sample |
| `.github/workflows/ci.yml` | Pure unit-test CI |
| `LICENSE`, `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, … | Project meta |

| Not in git (local only) | Purpose |
|-------------------------|---------|
| `koboldcpp/*.gguf`, `koboldcpp.exe` | Multi-GB Local-LLM payload |
| `scripts/` | Maintainer packaging / bootstrap helpers |
| `publish/` | Release zip output |
| `bin/`, `obj/`, `_debug_view/` | Build / debug outputs |

**Users** download the complete zip from [Releases](https://github.com/dunjeon/SpeakRect/releases).  
**Contributors** clone source, run unit tests without models; add `koboldcpp\` from a release zip only if they need live recognition.
