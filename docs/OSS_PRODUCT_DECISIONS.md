# OSS product decisions (recorded)

**Date:** 2026-07-30  
**Status:** Open-source **live** — single public source repo; private dual-repo retired.  
**Override:** Product owner may change any row; update this file + LICENSE/docs in the same PR.

| # | Question | Decision recorded | Rationale |
|---|----------|-------------------|-----------|
| Q1 | App license | **GPLv2** — root `LICENSE` | Copyleft app license; third-party host remains AGPL |
| Q2 | Repo topology | **B — single public source-of-truth** [`dunjeon/SpeakRect`](https://github.com/dunjeon/SpeakRect); private `SpeakRect-src` **retired / nuke** | One place for source, issues, PRs, Releases |
| Q3 | Trademark / fork rename | **None** | No trademark program yet |
| Q4 | Models install | **Always-bundle for user zips** + **Fetch-LocalLlm.ps1 for devs** | Current ship model; external host if GitHub 2 GiB blocks |
| Q5 | Obfuscation | **Removed entirely** (1.4.23) | Open source; contributor trust |
| Q6 | Telemetry | **Forever none** | No outbound app telemetry |
| Q7 | Arm64 | **Document win-x64 only** for now | TFM/publish already x64-focused |
| Q8 | Min OS | **Document TFM floor** `10.0.26100` in README requirements | Honesty over marketing |
| Q9 | Glossary | **Local-LLM + OCR** | Implemented in UI |
| Q10 | CLA vs DCO | **DCO** (sign-off) | Lightweight |
| Q11 | SECURITY contact | **GitHub Security Advisories** on `dunjeon/SpeakRect`; owner **dunjeon** | |
| Q12 | Historical proprietary | **Keep prior LICENSE texts in git history** where retained; public tree may be orphan “new start” without private LFS history | Clean public clones |
| Q13 | Agent release builds | **Never rebuild ship zips** — use owner-provided archive under `publish\` | Bit-stability; see `publish/AGENT_RELEASE.md` |

## LFS / large binaries

**Choice: public source without multi-GB LFS objects.**  
Developers obtain host + GGUFs via release zip or `scripts/Fetch-LocalLlm.ps1`.  
See `docs/dev/lfs-history-strategy.md` and `docs/GITHUB_REPOS.md`.

## Release packaging

- User-facing binary: complete `SpeakRect-<ver>-win-x64.zip` (app + Local-LLM payload).
- Agents: follow **`publish/AGENT_RELEASE.md`** — upload the provided archive; do not invent a new zip.

## Legal note

GPLv2 app source is applied. Counsel on AGPL KoboldCpp auto-start/bundling (design K5) remains optional but recommended for redistribution claims. Host AGPL source-offer obligations still apply when shipping the host binary in a release zip.
