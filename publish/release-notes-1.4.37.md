## SpeakRect 1.4.37

Complete Windows x64 package (owner-provided archive):

- `SpeakRect.exe` (single-file self-contained; not obfuscated)
- Local-LLM host + **Q8_0** GLM-OCR model files under `koboldcpp\`
- LICENSE (GPLv2 app source), README, third-party notices

### Highlights
- **Speech symbols** — residual marks (`<>[]{}()*#^&…`) strip for TTS; mid-word hyphens kept (`well-known`, `X-Men`)
- **No HTML-tag strip** — comic radio lettering like `<WHERE ARE YOU, COUSIN?>` no longer eaten as fake tags; words stay, brackets go as symbols
- **1.4.34** — dense page pad off by default for all modes
- **1.4.33** — speak-run settings freeze, multi-monitor overlay, POI cancel safety

### Install
1. Download `SpeakRect-1.4.37-win-x64.zip`
2. Extract (about **2.5 GB** free disk recommended)
3. Run `SpeakRect.exe`

**Source:** full application source is in this repository under **GPLv2**.  
**Host/models:** third-party terms — see `THIRD_PARTY_NOTICES.md`.

### Windows SmartScreen / “Unknown publisher”
Unsigned build — **More info** → **Run anyway**. Full note in README.
