## SpeakRect 1.4.38

Complete Windows x64 package (owner-provided archive):

- `SpeakRect.exe` (single-file self-contained; not obfuscated)
- Local-LLM host + **Q8_0** GLM-OCR model files under `koboldcpp\`
- LICENSE (GPLv2 app source), README, third-party notices

### Highlights
- **Restore all defaults** — Settings → Help: factory-reset mode, Image, Voice, Speech, Key Map, Follow, and regions (Yes/No confirm first; keeps active profile name; saves to disk)
- **Ink weight default 0.25** — shared Image prep default for all modes (was 0.55)
- **Speech cleaner** — residual symbol strip (keep hyphens); no HTML-tag strip so comic `<…>` lettering keeps the words

### Install
1. Download `SpeakRect-1.4.38-win-x64.zip`
2. Extract (about **2.5 GB** free disk recommended)
3. Run `SpeakRect.exe`

**Source:** full application source is in this repository under **GPLv2**.  
**Host/models:** third-party terms — see `THIRD_PARTY_NOTICES.md`.

### Windows SmartScreen / “Unknown publisher”
Unsigned build — **More info** → **Run anyway**. Full note in README.
