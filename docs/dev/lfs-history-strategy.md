# LFS / large-binary history strategy

**Status:** **Executed for public OSS** — public tree is source-only (no multi-GB LFS clone requirement)  
**Date:** 2026-07-30  
**Owner:** product / maintainer (`docs/OSS_PRODUCT_DECISIONS.md` Q2 / LFS)

## Facts

| Source | Reality |
|--------|---------|
| Public remote | [`dunjeon/SpeakRect`](https://github.com/dunjeon/SpeakRect) — full source |
| Public policy | Multi-GB must **not** be required to clone source |
| On-disk `koboldcpp\` | Host + GGUFs may exist **locally** for run/pack; **gitignored** for public |
| Ship channel | Release zip under `publish\` (owner-provided for agents) |

## Decision (done)

**Option 2 — Public source without LFS history / without multi-GB blobs in git.**

1. Single public repo with application source + small config (`ocr.kcpps` as needed).  
2. Private `SpeakRect-src` dual-repo layout **retired**.  
3. Developers: release zip or `scripts/Fetch-LocalLlm.ps1` for host + GGUFs.  
4. Users: full install zip from GitHub Releases (or external host if ≥ 2 GiB).

### Rejected for public

| Option | Why not |
|--------|---------|
| Track GGUF/exe via Git LFS on public `main` | Clone/bandwidth trap |
| Dual private+public repos | Extra complexity once OSS is live |

## Public clone bootstrap

```text
git clone https://github.com/dunjeon/SpeakRect.git
dotnet build SpeakRect.sln
dotnet test tests/SpeakRect.Tests
# optional full stack:
#   extract SpeakRect-*-win-x64.zip next to dev tree, or Fetch-LocalLlm.ps1
dotnet run --project SpeakRect.csproj
```

## Agents

Do not commit LFS binaries or release zips. Ship binaries per [`publish/AGENT_RELEASE.md`](../../publish/AGENT_RELEASE.md).
