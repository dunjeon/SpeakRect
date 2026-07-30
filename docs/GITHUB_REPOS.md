# SpeakRect on GitHub (public full source)

Owner: **[dunjeon](https://github.com/dunjeon)**

| Repo | Visibility | Purpose |
|------|------------|---------|
| **[dunjeon/SpeakRect](https://github.com/dunjeon/SpeakRect)** | **Public** | **Single source of truth:** full application source (GPLv2), docs, issues, PRs, **Releases** |
| `dunjeon/SpeakRect-src` | **Retired** | Former private source mirror — do not use; may be deleted |

App source is **GPLv2** and ships **without obfuscation**. Large Local-LLM binaries (**GGUF** + host exe) are **not** in public git clones. Users get them from the **release zip**; developers use the release zip or `scripts/Fetch-LocalLlm.ps1` when pins are filled.

```
dunjeon/SpeakRect  (public — only remote)
  *.cs, *.csproj, tests/, scripts/, docs/
  LICENSE (GPLv2), README, THIRD_PARTY_NOTICES
       │
       │  owner-validated zip under publish\
       ▼
  Releases → SpeakRect-<ver>-win-x64.zip
            (app + koboldcpp host + Q8_0 models)
```

## Clone / develop

```powershell
git clone https://github.com/dunjeon/SpeakRect.git
cd SpeakRect
dotnet build SpeakRect.sln -c Debug
dotnet test tests/SpeakRect.Tests
# Optional full Local-LLM stack on disk (not from git):
#   extract release zip, or scripts/Fetch-LocalLlm.ps1 when configured
dotnet run --project SpeakRect.csproj -c Debug
```

## Every release

1. Prefer an **owner-validated** zip already under `publish\` (e.g. `SpeakRect-1.4.23-win-x64.zip`).
2. **Do not** rebuild that zip for upload unless the owner requests a new version package.
3. Create/upload the GitHub Release on **`dunjeon/SpeakRect`** attaching that file when size **&lt; 2 GiB**.

`scripts/Publish-Release.ps1` is for **owner-directed new packs** only, not the default ship path for an existing archive.

### Single zip contents

| Path | Notes |
|------|--------|
| `SpeakRect.exe` | Single-file publish (not obfuscated) |
| `koboldcpp\koboldcpp.exe` | Local LLM host |
| `koboldcpp\glmocr-Q8_0.gguf` | Vision model |
| `koboldcpp\mmproj-glmocr-Q8_0.gguf` | Projector |
| `koboldcpp\ocr.kcpps` | Host config |
| `LICENSE`, `README.md`, `THIRD_PARTY_NOTICES.md`, `INSTALL.txt` | Docs |

**Never ship in git:** multi-GB models, host exe as required clone payload, secrets, release zips.

### GitHub 2 GiB limit

GitHub **rejects release assets ≥ 2 GiB**. If a provided zip is over the limit, host it externally and link from release notes. Do not put models into the git tree to work around the limit.

## Local git remote

```text
origin  https://github.com/dunjeon/SpeakRect.git
```

## Legacy notes

- **`public-repo\`** — historical docs-only tree used before full-source OSS. Not the product remote; safe to ignore for day-to-day work.
- **Private `SpeakRect-src` + dual-repo flow** — superseded. Init script dual-repo setup is obsolete for new work.
- **Large binaries** — public tree has **no** multi-GB GGUF/host objects; ship via Releases only.

## Security checklist

- [ ] Release zip has no secrets or local debug artifacts
- [ ] Models only in the release zip / Fetch path, not required for public source clone
- [ ] App is **not** obfuscated
- [ ] Upload **owner-validated** archives only (do not substitute a rebuild)
