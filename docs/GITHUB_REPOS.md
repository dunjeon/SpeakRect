# Repository layout

**Remote:** [github.com/dunjeon/SpeakRect](https://github.com/dunjeon/SpeakRect) (public, GPLv2)

| In the clone | Purpose |
|--------------|---------|
| `*.cs`, `SpeakRect.csproj`, `SpeakRect.sln` | Application source |
| `tests/` | Unit tests + optional smokes |
| `docs/` | Architecture, speak-path, smoke runbook |
| `koboldcpp/` | Local-LLM host + GGUF models (**Git LFS**) + `ocr.kcpps` |
| `.github/workflows/ci.yml` | Pure unit-test CI |
| `LICENSE`, `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, … | Project meta |

| Not in git (local only) | Purpose |
|-------------------------|---------|
| `scripts/` | Maintainer packaging helpers |
| `publish/` | Release zip output |
| `bin/`, `obj/`, `_debug_view/` | Build / debug outputs |
| `koboldcpp/*.log`, `ocr.runtime.kcpps` | Host runtime noise |

**Clone requires [Git LFS](https://git-lfs.com/)** so `koboldcpp.exe` and the GGUFs download with the tree:

```powershell
git lfs install
git clone https://github.com/dunjeon/SpeakRect.git
```

**Users** can still download a ready-to-run zip from [Releases](https://github.com/dunjeon/SpeakRect/releases).
