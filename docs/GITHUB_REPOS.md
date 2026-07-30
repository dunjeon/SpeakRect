# Repository layout

**Remote:** [github.com/dunjeon/SpeakRect](https://github.com/dunjeon/SpeakRect) (public, GPLv2)

| In the clone | Purpose |
|--------------|---------|
| `*.cs`, `SpeakRect.csproj`, `SpeakRect.sln` | Application source |
| `tests/` | Unit tests + optional smokes |
| `docs/` | Architecture, speak-path, smoke runbook |
| `scripts/Fetch-LocalLlm.ps1` | Optional Local-LLM bootstrap for developers |
| `scripts/Publish-Release.ps1` | Maintainer complete-zip packaging |
| `koboldcpp/ocr.kcpps` | Small host config sample |
| `.github/workflows/ci.yml` | Pure unit-test CI |
| `LICENSE`, `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, … | Project meta |

| Not in git (local only) | Purpose |
|-------------------------|---------|
| `koboldcpp/*.gguf`, `koboldcpp.exe` | Multi-GB Local-LLM payload |
| `bin/`, `obj/`, `_debug_view/` | Build / debug outputs |
| `publish/*.zip` | Release packaging output |

**Users** download the complete zip from [Releases](https://github.com/dunjeon/SpeakRect/releases).  
**Contributors** clone source, then add Local-LLM files if they need live recognition (see [CONTRIBUTING.md](../CONTRIBUTING.md)).
