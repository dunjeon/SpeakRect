## SpeakRect 1.4.43

**Source release only** — the complete Windows x64 zip is **not** ready yet. Do not attach or announce a `SpeakRect-1.4.43-win-x64.zip` until the owner provides the ship archive (see `publish/AGENT_RELEASE.md`).

### Highlights
- **Remove page-named one-offs** — drop single-case comic/OCR patches (uchar noise, coocoo gibberish gate, cream logo dead-island, Emplate mega full-frame branch, Storm non-Latin / trunc escape, afternoon crop superset, jazzed-hero stack continuity, short-stem speak-dedupe keep, etc.) so real bugs surface for category-level fixes.
- **Kill-list policy** — `docs/dev/one-off-kill-list.md` + checklist note: no new heuristics justified only by a single panel.
- **Tests** — ModeSmoke / unit tests updated; panel-named fixtures de-personalized where kept (JSON unwrap).

**Source:** full application source is in this repository under **GPLv2**.  
**Host/models:** third-party terms — see `THIRD_PARTY_NOTICES.md`.
