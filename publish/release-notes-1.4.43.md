## SpeakRect 1.4.43

Complete Windows x64 package (**owner-provided** archive — do not rebuild):

- `SpeakRect.exe` (single-file self-contained; not obfuscated)
- Local-LLM host + **Q8_0** GLM-OCR model files under `koboldcpp\`
- LICENSE (GPLv2 app source), README, third-party notices

### Highlights
- **Remove page-named one-offs** — drop single-case comic/OCR patches (uchar noise, coocoo gibberish gate, cream logo dead-island, Emplate mega full-frame branch, Storm non-Latin / trunc escape, afternoon crop superset, jazzed-hero stack continuity, short-stem speak-dedupe keep, etc.) so real bugs surface for category-level fixes.
- **Kill-list policy** — `docs/dev/one-off-kill-list.md` + checklist note: no new heuristics justified only by a single panel.
- **Tests** — ModeSmoke / unit tests updated; panel-named fixtures de-personalized where kept (JSON unwrap).

### Install
1. Download `SpeakRect-1.4.43-win-x64.zip`
2. Extract (about **2.5 GB** free disk recommended)
3. Run `SpeakRect.exe`

**Source:** full application source is in this repository under **GPLv2**.  
**Host/models:** third-party terms — see `THIRD_PARTY_NOTICES.md`.

### Windows SmartScreen / “Unknown publisher”
Unsigned build — **More info** → **Run anyway**. Full note in README.
