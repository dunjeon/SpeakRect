# SpeakRect

**Draw regions on your screen. Hear the text read aloud.**

SpeakRect is a Windows accessibility tool for people with **visual impairments**. It reads text from whatever is on your screen — retro games, modern games, comic books, browsers, menus, subtitles, and more — using a **local LLM** for recognition and Windows speech for playback.

A core strength is **multiple saved regions**: set up to **eight fixed capture areas** on screen (plus a ninth “follow the mouse” reader), each on its own hotkey. Map dialogue here, a menu there, subtitles below — then speak any of them instantly without redrawing.

Nothing is sent to a cloud AI service. Recognition runs on your PC.

If Windows can show it in a normal (non-exclusive) window or on the desktop, SpeakRect can try to read it.

---

## Short description

> Accessibility screen reader for Windows: local LLM OCR + TTS for games, comics, and any non-exclusive on-screen text.

---


## Download

**Closed-source product.** This repository has **no application source code** — only docs and [Releases](https://github.com/dunjeon/SpeakRect/releases).

| | |
|--|--|
| **Latest build** | [Releases](https://github.com/dunjeon/SpeakRect/releases/latest) |
| **Package** | One zip: obfuscated `SpeakRect.exe` + KoboldCpp host + **Q8_0** model files |
| **Source** | Not published (private) |

1. Download **`SpeakRect-<version>-win-x64.zip`** from the latest release.
2. Extract the zip to a folder (needs **~2.5 GB free**).
3. Run **`SpeakRect.exe`**.

No separate model download is required for the complete release package.

### Windows SmartScreen / “Unknown publisher”

Windows may block or warn when you first run **`SpeakRect.exe`** (or open the zip). Common messages:

- **“Windows protected your PC”** / SmartScreen
- **“Unknown publisher”**
- Browser download warnings for an unsigned app

**That is expected.** SpeakRect is free and distributed as an unsigned zip from GitHub Releases. It is **not** code-signed with a paid certificate, so Windows cannot show a verified publisher name. The warning means “Microsoft doesn’t know this publisher,” **not** that the file was found to be malware.

#### How to run it anyway

1. On the blue/red SmartScreen window, click **More info**.
2. Click **Run anyway**.

If Explorer still treats the file as blocked after download:

1. Right-click **`SpeakRect.exe`** (or the zip) → **Properties**.
2. If you see **Unblock** at the bottom, check it → **OK**.
3. Run the app again.

Or in PowerShell, from the folder you extracted to:

```powershell
Unblock-File -Path .\SpeakRect.exe
Get-ChildItem -Recurse | Unblock-File
```

#### What is in the package (and why Windows is cautious)

The release zip is a full local install: **`SpeakRect.exe`**, a **KoboldCpp** host, and large **on-device model files**. Nothing is required to call a cloud AI service for recognition — work stays on your PC. Screen capture and hotkeys are normal for this kind of accessibility tool; Windows still treats new unsigned downloads carefully.

Only download from the official [Releases](https://github.com/dunjeon/SpeakRect/releases) page on this repository. If a release lists a **SHA-256** checksum, you can verify the zip before extracting:

```powershell
Get-FileHash -Algorithm SHA256 .\SpeakRect-*-win-x64.zip
```

Code signing may come later; until then, SmartScreen warnings are a normal side effect of shipping free, unsigned software.

---

## Why it exists

Many games and apps never ship with a proper screen reader. Dialogue, menus, HUD text, subtitles, and comic balloons can be hard or impossible to enlarge. SpeakRect lets you mark one or many parts of the screen and hear what they say — on demand, from hotkeys or a gamepad — without needing the app to support accessibility APIs, and without uploading screenshots to a remote service.

**One region** is enough for a quick read. **Several regions** are what make SpeakRect practical in real games and apps: keep separate boxes for speaker text, choices, quest log, minimap labels, comic balloons you revisit, and so on, each one keypress away.

---

## What it works on

| Target | Notes |
|--------|--------|
| **Retro games** | Emulators, classic ports, pixel UI and dialogue |
| **Modern games** | As long as the game is **not** exclusive fullscreen |
| **Comic books / manga** | Turn on **Comic Book** mode for panels and balloons |
| **Anything else on screen** | Browsers, documents, chat, apps, subtitles |

**Important:** SpeakRect captures the desktop composite. Games in **exclusive fullscreen** often cannot be read. Prefer **borderless windowed** or **windowed** mode.

---

## Requirements

- **Windows 10 / 11** (x64)
- A **local LLM** runtime and model files next to the app (see below)
- **GPU strongly recommended** for usable speed

### Disk space (local LLM payload)

Approximate sizes from a full install (bundled **Q8_0** GGUF pair):

| Item | Size | Role |
|------|------|------|
| Vision model (`glmocr-Q8_0.gguf`) | **~906 MB** | Local LLM weights |
| Vision projector (`mmproj-glmocr-Q8_0.gguf`) | **~462 MB** | Image understanding support |
| Local LLM host (`.exe`) | **~608 MB** | Runs the model on your machine |
| Config files | under 10 KB | Launch settings |
| **Runtime folder total** | **~2.0 GB** | Ships next to SpeakRect |

Plan on **about 2.5+ GB free** for a complete install. The SpeakRect app itself is small beside the model files.

### VRAM (GPU memory)

Default setup loads the full local model on the GPU.

| | Guidance |
|--|----------|
| Model files on disk | **~1.4 GB** (Q8_0 weights + projector) |
| Free VRAM to load and run | **~3 GB minimum** |
| Sharing GPU with a game | **4–6 GB+ free** recommended |
| Comfortable gaming + reading | **8 GB+** total card VRAM |

CPU-only is usually too slow for interactive use. Integrated or very low-VRAM GPUs may not load the default model pair.

---

## Quick start

1. Install / extract SpeakRect so the app and its **local LLM** folder sit together.
2. Run **SpeakRect**. It appears in the **system tray**. The local LLM may take a short time to load the first time.
3. Press **Shift+Tab** (default) to show the overlay — or double-click the tray icon / choose **Show Overlay**.
4. **Draw** a box around the text you care about (starts on **region 1**).
5. Press **Enter** to **speak** that region.
6. To add another area: press **Shift+F2** (still with the overlay open) → draw region 2 → **Enter** again.
7. Press **Escape** to hide the overlay. Later, **Shift+F1** / **Shift+F2** / … speak those saved spots without opening the overlay.

For games, use **borderless windowed** so capture works.

Step-by-step detail: [Set a region, speak it, set another](#set-a-region-speak-it-set-another).

---

## How to use

### System tray

Right-click the tray icon:

| Menu | What it does |
|------|----------------|
| **Show Overlay** | Opens the selection overlay (same as the overlay hotkey) |
| **Settings…** | Profiles, Key Map, Voice, and Follow in one window |
| **Profiles** | Load / save named setups (hotkeys, modes, regions, etc.) |
| **Exit** | Quit SpeakRect and stop the local LLM |

Double-click the tray icon to show the overlay.

Only one instance of SpeakRect runs at a time.

---

### Overlay basics

The overlay is a dim full-screen layer so you can still see the app or game underneath while you draw. The **left sidebar** has tools; the rest of the screen is for drawing.

| Action | How |
|--------|-----|
| Show / hide overlay | **Shift+Tab** (default), tray, or gamepad if bound |
| Draw a region | Click and drag on the screen (not on the sidebar) |
| Read the current region | **Enter** |
| Cancel / hide overlay | **Escape** (saves the current slot, stops speech, hides to tray) |
| Clear active region slot | **Delete** |
| Make overlay **more transparent** | **← Left** arrow |
| Make overlay **more opaque** | **→ Right** arrow |

While **Settings** is open, drawing on the overlay is paused.

#### Overlay opacity (Left / Right arrows)

The overlay tint can hide faint UI, dark game HUDs, or small subtitle text. Use the arrow keys **while the overlay is open**:

| Key | Effect |
|-----|--------|
| **← Left** | Lower opacity (see more of the screen underneath). Stops at a light minimum so the overlay stays usable. |
| **→ Right** | Raise opacity (stronger dim / easier to see your drawn box). Up to fully opaque. |

**Why you may need this**

- **See what you’re selecting** — turn opacity down so dim dialogue, gray subtitles, or dark menus stay readable while you aim the box.
- **See your region chrome** — turn it up if the selection outline is hard to spot over a bright or busy scene.
- **Different content** — comics on white pages often need less dim; dark games often need more transparency.

Opacity is only for drawing/setup. SpeakRect briefly clears the overlay tint when it snaps the region so the capture is not darkened by the overlay.

---

### Shape tools (how you select)

Sidebar buttons or overlay keys (defaults):

| Shape | Default key | Use when |
|-------|-------------|----------|
| **RECT** | **R** | Most text boxes, panels, UI |
| **OVAL** | **O** | Circular / rounded areas |
| **LASSO** | **L** | Irregular shapes; freehand outline |

Shape keys work while the overlay is focused (they are not global by default).

**How to draw**

1. Pick RECT, OVAL, or LASSO (sidebar button or **R** / **O** / **L**).
2. The **left sidebar hides** so you can see and select text on the left edge of the screen too.
3. Drag on the content (release to finish a rect/oval; lasso closes when you finish the stroke).
4. When the shape is committed, the **sidebar comes back**.
5. Press **Enter** to speak.

While the sidebar is hidden, a small “Draw … · Esc = tools” hint stays in the corner. **Esc** brings the sidebar back without leaving the overlay (press **Esc** again to hide to the tray).

---

### Set a region, speak it, set another

SpeakRect keeps **8 fixed region slots** (plus Follow as slot 9). Each slot remembers where on the screen to look. Defaults:

| Slot | Default hotkey | Typical use |
|------|----------------|-------------|
| **1** | **Shift+F1** | Main dialogue |
| **2** | **Shift+F2** | Choices / secondary box |
| **3**–**8** | **Shift+F3** … **Shift+F8** | Extra UI, logs, captions… |
| **9 (Follow)** | **Shift+F9** | Whatever is under the mouse (not a fixed box) |

Hotkeys are remappable in **Key Map**.

#### 1) Set region 1 (first area)

1. Show the overlay (**Shift+Tab** or tray **Show Overlay**).
2. You start on **region 1** by default. (If you were on another slot, press **Shift+F1** once with the overlay still open to select slot 1 — it switches the active slot; it does not speak while the overlay is open.)
3. Pick a shape if needed: **RECT** (default), **OVAL**, or **LASSO** (sidebar or **R** / **O** / **L**).
4. **Click and drag** on the screen over the text (avoid the left sidebar).
5. Release the mouse. That rectangle (or oval/lasso) is now **region 1**.
6. Optional: press **Enter** to hear it right away and confirm the box is good.

Your drawing is stored on that slot automatically when you finish drawing, press **Enter**, switch slots, or hide the overlay (**Escape**).

#### 2) Speak region 1

**While the overlay is open**

- Make sure region 1 is active (its shape is showing).
- Press **Enter** → SpeakRect captures that area and reads it aloud.

**With the overlay closed** (normal play)

- Press **Shift+F1** from anywhere → SpeakRect speaks **saved region 1** immediately.  
  No need to open the overlay or draw again.

Same idea for every slot: **Shift+F2** speaks region 2, and so on.

#### 3) Set another region (region 2, 3, …)

Keep the overlay open after setting region 1 (or open it again).

1. Press the **next slot’s hotkey** with the overlay **still open** — e.g. **Shift+F2** for region 2.  
   - This **switches** to that slot (and saves region 1 first).  
   - It does **not** start speaking while the overlay is visible.
2. Draw the new area for that slot (same as step 1: drag a box over different text/UI).
3. Press **Enter** if you want to test-speak the new region.
4. Repeat with **Shift+F3**, **Shift+F4**, … for more slots (up to 8 fixed regions).

When you are done laying out regions, press **Escape** to hide the overlay. Your set of regions stays available:

| You want to… | Do this |
|--------------|---------|
| Speak region 1 | **Shift+F1** (overlay closed) |
| Speak region 2 | **Shift+F2** |
| Speak region *n* | **Shift+F*n*** |
| Change a region’s size/place | Overlay on → press that slot’s hotkey → draw again |
| Clear a region | Overlay on → select that slot → **Delete** |
| Redraw region 1 without losing region 2 | Overlay on → **Shift+F1** → draw; slot 2 is untouched |

**Example (game)**  
Region 1 = character dialogue · Region 2 = multiple-choice answers · Region 3 = quest tracker.  
Play in borderless windowed mode. Whenever dialogue appears, **Shift+F1**; when you need the choices, **Shift+F2** — no re-selecting every time.

**Example (comic)**  
Region 1 = full page or main panel (Comic Book on).  
Or several fixed panels if the layout is stable across pages.

Regions are saved in settings and can be stored in **Profiles** (e.g. one profile per game).

---

### Follow (region 9 — read under the mouse)

**Follow** is a movable capture box that tracks the cursor (or can be locked in place). It does **not** overwrite slots 1–8.

| Default | Action |
|---------|--------|
| **Shift+F9** | Speak at the **current mouse** using Follow size/shape/offset |
| **Up** (overlay) | Arm Follow floating preview |
| **Down** (overlay) | Turn Follow off (overlay stays open) |
| **Enter** (while Follow is on) | **Lock** the box in place / unlock back to floating — does **not** speak |
| Sidebar **FOLLOW** | Click = on/off; **Ctrl+click** = Settings → Follow tab |
| Sidebar **SETTINGS** | Opens Settings (profiles, Key Map, Voice, Follow) |

**Follow settings** (size, rectangle vs oval, X/Y offset from the cursor): open with **Ctrl+click FOLLOW**, tray **Settings…** → **Follow** tab, or overlay **SETTINGS**.

Typical use: point at dialogue or a subtitle line → **Shift+F9** → hear it. Adjust width/height/offset so the box covers the text cleanly.

---

### Reading modes (sidebar flags)

Under **MODE** on the overlay, one primary mode is always selected (also toggleable with global hotkeys):

| Mode | Default hotkey | When to use |
|------|----------------|-------------|
| **Default** | **Shift+D** | Games, menus, subtitles, plain UI — simple full-region read |
| **Comic Book** | **Shift+B** | Comic / manga panels, balloons, multi-caption pages |
| **Fast** | **Shift+N** | With Comic Book: quicker reads |
| **Faster** | **Shift+M** | With Comic Book: snappiest option |

- **Default** and **Comic Book** are opposites: only one is on at a time. **Shift+D** / the DEFAULT button turns Default on and clears Comic / Fast / Faster. Comic (or Fast / Faster) turns Default off.
- **Fast** / **Faster** only apply with Comic Book; they are mutually exclusive. Enabling either also turns Comic Book on (Default off).
- Remap any of these in **Settings → Key Map** (keyboard and optional gamepad).
- When the overlay is hidden, mode hotkeys are announced with a short TTS phrase so you know what changed.

---

### Settings window

Open **Settings…** from the tray or the overlay **SETTINGS** button. Profile **Load / Save / Save As / Delete** sit at the top of the window. Tabs:

| Tab | What it does |
|-----|----------------|
| **Key Map** | Remap keyboard + gamepad; custom actions |
| **Voice** | Windows TTS voice, rate, pitch, volume, silence |
| **Follow** | Size, shape, and offset for the mouse-follow reader |

Mode toggles (Default / Comic / Fast / Faster) and the Follow **on/off** control stay on the overlay sidebar.

#### Voice (TTS)

| Control | Effect |
|---------|--------|
| **Voice** | Installed Windows voices (blank = system default) |
| **Rate** | Speaking speed |
| **Pitch** | Voice pitch |
| **Volume** | Speech volume |
| **Silence** options | Gap after phrases / around punctuation |
| **Preview** | Hear a sample with current settings |

Changes save for the next read.

#### Key Map (keyboard + gamepad)

You can rebind:

- Show / hide overlay  
- Comic Book / Fast / Faster  
- Region slots 1–8 and Follow (slot 9)  
- Shape tools (overlay-local by default)  
- **Gamepad** buttons for any of the above (off by default — opt in)  
- **Custom** bindings: mouse clicks/moves/scroll, send arbitrary key chords, stick-as-mouse, etc.

**Tips**

- Global bindings work even when another app is focused (when Windows allows).
- Overlay-only shape keys work when the overlay is up.
- Avoid conflicts with game controls; use profiles per game if needed.
- Gamepad uses **XInput** (controller index configurable).

---

### Profiles

Save different setups (hotkeys, modes, regions, voice-related prefs as stored, Follow size, etc.):

1. Tray → **Profiles** → **Save current…** or **Save as…**, or use the profile bar at the top of **Settings**.
2. Load a profile from the tray menu or the Settings profile list.

Use one profile for “comics,” another for a game’s UI hotkeys, etc.

---

### Default hotkey cheat sheet

| Action | Default |
|--------|---------|
| Show / hide overlay | **Shift+Tab** |
| Default mode | **Shift+D** |
| Comic Book mode | **Shift+B** |
| Fast | **Shift+N** |
| Faster | **Shift+M** |
| Speak region 1–8 | **Shift+F1** … **Shift+F8** |
| Speak Follow (at mouse) | **Shift+F9** |
| Shape: Rectangle | **R** (overlay) |
| Shape: Oval | **O** (overlay) |
| Shape: Lasso | **L** (overlay) |
| Speak current selection | **Enter** (overlay; regions 1–8) |
| Lock / unlock Follow box | **Enter** (overlay; when Follow is on) |
| Hide to tray | **Escape** |
| Clear active region | **Delete** |
| Follow preview on / off | **Up** / **Down** (overlay) |
| Overlay more transparent / more opaque | **← Left** / **→ Right** (see [opacity](#overlay-opacity-left--right-arrows)) |

All of these can be changed in **Settings → Key Map**.

---

### Suggested workflows

**Game with several UI spots (best use of multi-region)**  
1. Borderless windowed game; Comic Book **off**.  
2. Overlay → draw dialogue → (optional **Enter** to test) → **Shift+F2** → draw choices → **Shift+F3** → draw anything else you need.  
3. **Escape** to hide. In play: **Shift+F1** / **F2** / **F3** to speak each saved area.

**One-shot read (no need to keep a slot)**  
Overlay → draw → **Enter**. Hide with **Escape** when done.

**Subtitles / HUD under the cursor**  
1. Open Follow settings; size the box to a subtitle line.  
2. Point the mouse → **Shift+F9**.

**Comic page**  
1. Comic Book **on** (optionally Fast/Faster).  
2. Draw the panel or whole page on a region → **Enter**.  
3. Reuse that region’s hotkey on the next page if the panel sits in the same place.

**Controller-only**  
1. In Settings → Key Map, bind overlay + each region slot you use to the pad.  
2. Optionally add custom mouse/stick bindings for aiming the cursor / Follow.

---

## Accuracy — what to expect

SpeakRect is meant to be **usable**, not perfect. Real session logs from development (debug archives) give a sense of the ballpark.

### Measured on comic sessions (debug archive)

| Metric | Result |
|--------|--------|
| Sessions logged | **31** complete reads (mostly **Comic Book** + **Faster**) |
| Empty / failed reads | **0 / 31** (every run produced spoken text) |
| Spot-check set | **14** panels / pages compared to the on-screen wording |
| Word match (recall) | **~99.7%** of ground-truth words present in the spoken output |
| Word match (precision) | **~100%** on that dialogue-heavy set (few invented words) |

**What that means in practice:** clean English comic balloons, captions, and prose recap pages were usually spoken **correctly end-to-end**. Typical slips were small (e.g. dropping a leading “I” in “I mean…”, odd handling of a logo or “©”, a rare credit-line glitch). You should expect **high-nineties** accuracy on clear comic text in English when the region is well drawn.

### What can lower accuracy

| Situation | Expectation |
|-----------|-------------|
| Clear comic balloons / print | Excellent (as above) |
| Dense credits, tiny legal lines, fancy logos | More mistakes |
| Stylized SFX, heavy art behind text | Occasional miss or garble |
| Game UI, subtitles, low contrast, motion blur | Varies — often good, not as consistent as clean comics |
| Exclusive fullscreen / wrong region / partial crop | Missed or incomplete read |
| Non-English (depending on model & fonts) | Not validated in these logs |

These numbers come from **internal debug runs** (English comics), not a formal published benchmark. Your hardware, region size, and content type matter. If a read is wrong, redraw a tighter box or try again; saved regions help you retry the same spot quickly.

---

## Privacy

Recognition uses a **local LLM** on your machine. Screen captures are not sent to a cloud AI API by SpeakRect. Speech uses Windows TTS on the same PC.

---

## Credits & third-party software

SpeakRect ships a **local** vision LLM stack for on-device OCR. Huge thanks to the projects that make that possible:

| Component | Project | Links |
|-----------|---------|--------|
| Local LLM host | **KoboldCpp** (LostRuins) | [GitHub](https://github.com/LostRuins/koboldcpp) · [Releases](https://github.com/LostRuins/koboldcpp/releases/latest) |
| Vision OCR model | **GLM-OCR** (Z.ai / zai-org) | [GitHub](https://github.com/zai-org/GLM-OCR) · [Hugging Face](https://huggingface.co/zai-org/GLM-OCR) |

SpeakRect bundles a **Q8_0** GGUF build of GLM-OCR (`glmocr-Q8_0.gguf` + `mmproj-glmocr-Q8_0.gguf`) and runs it through KoboldCpp on `127.0.0.1`. Their licenses and terms apply to those components; see each project for details and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

## License

**SpeakRect** (the app) is **proprietary**. Source is closed. You may use the software **free of charge for personal and non-commercial purposes** under [LICENSE](LICENSE). Commercial use or redistribution of SpeakRect requires permission.

Third-party components (KoboldCpp, GLM-OCR, etc.) keep **their own** licenses — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
