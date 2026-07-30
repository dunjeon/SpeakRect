# Agent / maintainer release instructions

**Audience:** future agents and humans shipping SpeakRect builds.  
**Public repo:** [dunjeon/SpeakRect](https://github.com/dunjeon/SpeakRect) (full **GPLv2** source + Releases).  
**Last updated:** 2026-07-30

---

## Hard rules (do not violate)

1. **Never build your own release zip for distribution** unless the product owner explicitly asks for a *new* version build.  
   If a complete zip already exists under `publish\` (for example `SpeakRect-1.4.23-win-x64.zip`), **that archive is the ship artifact**. Upload **that** file.
2. **Do not** re-run `dotnet publish` / `Publish-Release.ps1` just to “refresh” a zip that the owner already provided. Rebuilding can change bits (R2R, timestamps, host layout) and is not the approved release.
3. **Do not** put multi‑GB models or `koboldcpp.exe` into the **git** tree for public clones. Source is public; binaries ship only via **GitHub Releases** (or external host if over 2 GiB) and optional `scripts/Fetch-LocalLlm.ps1` for developers.
4. **Do not** use the private repo `dunjeon/SpeakRect-src` — it is retired. Source of truth is **`dunjeon/SpeakRect`**.
5. **Do not** invent a second “public face” repo or push only `public-repo\`. Full source lives on the single public remote.

---

## Current provided archive (1.4.23)

| Item | Path / value |
|------|----------------|
| Ship zip (owner-provided) | `publish/SpeakRect-1.4.23-win-x64.zip` |
| Approx size | ~1.95 GiB (under GitHub’s **2 GiB** release-asset limit as of measurement) |
| Expected tag | `v1.4.23` |
| Notes template | Prefer a short `publish/release-notes-1.4.23.md` if present; otherwise use the checklist below |

Verify before upload:

```powershell
Get-Item .\publish\SpeakRect-1.4.23-win-x64.zip |
  Select-Object FullName, Length, LastWriteTime
# Must be < 2147483648 bytes (2 GiB) for GitHub asset attach
```

---

## How to publish a release (agent checklist)

### A. Upload the **provided** zip (normal path)

From repo root, authenticated as the owner (`gh auth status`):

```powershell
$ver  = '1.4.23'
$tag  = "v$ver"
$zip  = ".\publish\SpeakRect-$ver-win-x64.zip"
$notes = @"
## SpeakRect $ver

Complete Windows x64 package (**owner-provided** archive — do not rebuild):

- ``SpeakRect.exe`` (single-file self-contained; not obfuscated)
- Local-LLM host + **Q8_0** GLM-OCR model files under ``koboldcpp\``
- LICENSE (GPLv2 app source), README, third-party notices

### Install
1. Download ``SpeakRect-$ver-win-x64.zip``
2. Extract (about **2.5 GB** free disk recommended)
3. Run ``SpeakRect.exe``

**Source:** full application source is in this repository under **GPLv2**.  
**Host/models:** third-party terms — see ``THIRD_PARTY_NOTICES.md``.

### Windows SmartScreen / “Unknown publisher”
Unsigned build — **More info** → **Run anyway**. Full note in README.
"@

if (-not (Test-Path -LiteralPath $zip)) {
  throw "Missing owner-provided zip: $zip — do not invent a replacement without explicit owner approval."
}
if ((Get-Item -LiteralPath $zip).Length -ge 2GB) {
  throw "Zip >= 2 GiB; host externally and link from release notes (do not attach)."
}

# Create release + attach the provided archive (no local rebuild)
gh release create $tag $zip `
  --repo dunjeon/SpeakRect `
  --title "SpeakRect $ver" `
  --notes $notes
```

If the tag/release already exists:

```powershell
gh release upload $tag $zip --repo dunjeon/SpeakRect --clobber
```

### B. When (and only when) the owner asks for a **new** version zip

1. Confirm version bump in `SpeakRect.csproj` (`Version` / `InformationalVersion`).
2. Confirm Local-LLM payload is present under `koboldcpp\` on disk (not from git).
3. Run:

   ```powershell
   .\scripts\Publish-Release.ps1
   ```

4. Place the resulting `publish\SpeakRect-<ver>-win-x64.zip` where the owner can validate it.
5. **Owner validation** before any GitHub upload.
6. Then follow section A using **that** validated zip only.

`Publish-Release.ps1` remains a **packaging tool for humans/owners**, not the default agent path for shipping an already-provided archive.

### C. Source push (every code change)

```powershell
git remote -v   # origin → https://github.com/dunjeon/SpeakRect.git
git push origin main
```

Never push release zips or GGUF/exe into git (see `.gitignore`).

---

## GitHub 2 GiB limit

| Zip size | Action |
|----------|--------|
| **&lt; 2 GiB** | Attach to GitHub Release (preferred when the provided zip qualifies) |
| **≥ 2 GiB** | Host zip on itch.io / R2 / etc.; GitHub Release = tag + notes + **download link only** |

Do **not** split or rebuild to “fix” size without owner direction.

---

## Repo topology (current)

| Remote | Role |
|--------|------|
| **[dunjeon/SpeakRect](https://github.com/dunjeon/SpeakRect)** | **Only** source of truth: full source, issues, PRs, Releases |
| `dunjeon/SpeakRect-src` | **Retired** — do not push or open PRs there |

`public-repo\` is a **legacy** docs-only tree (historical). Do not treat it as the product remote.

More detail: [`docs/GITHUB_REPOS.md`](../docs/GITHUB_REPOS.md).

---

## Security / packaging checklist

- [ ] Uploading the **owner-provided** zip path, not a freshly built substitute
- [ ] Zip has no secrets, PDBs (unless intentional), or personal paths
- [ ] App remains **not** obfuscated
- [ ] Release notes mention GPLv2 source + third-party host notices
- [ ] Tag matches product version (`v1.4.23` ↔ `1.4.23`)

---

## What not to do

| Don’t | Why |
|-------|-----|
| `dotnet publish` then ship without owner ask | Replaces validated bits |
| Attach a zip you rebuilt “to be sure” | Diverges from provided archive |
| Commit `publish/*.zip` or GGUFs | Breaks public clone policy |
| Push to private `SpeakRect-src` | Retired |
| Force-push `main` without owner ask | History rewrite risk after public launch |
