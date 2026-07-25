# SpeakRect

**Draw regions on your screen. Hear the text read aloud.**

SpeakRect is a free Windows accessibility app for people with **visual impairments**. It captures text from whatever is on your screen — games, comics, browsers, menus, subtitles, and more — recognizes it with a **local LLM** on your PC, and reads it aloud with **Windows speech by default** (optional SAPI 5 for advanced setups).

No cloud AI is required for recognition. Speech works offline with the built-in Windows engine.

---

## Highlights

- **Up to 8 saved regions** — pin dialogue, menus, choices, captions, or panels each to its own hotkey
- **Follow mode** — a ninth reader that tracks the mouse (or locks in place)
- **Local recognition** — KoboldCpp + GLM-OCR (Q8_0) bundled in the release zip
- **Windows TTS by default** — works out of the box; optional **SAPI 5** for classic voices and third-party engines
- **Keyboard + gamepad** — remappable bindings, optional custom actions
- **Profiles** — save layouts, hotkeys, modes, and voice (including TTS engine) per game
- **Comic Book mode** — better handling of panels and balloons (with Fast / Faster options)

If Windows can show it in a normal window or on the desktop, SpeakRect can try to read it. Prefer **borderless windowed** or **windowed** mode — exclusive fullscreen often cannot be captured.

---

## Download

This public repository is **docs and releases only**. Application source is not published.

| | |
|--|--|
| **Latest build** | [Releases](https://github.com/dunjeon/SpeakRect/releases/latest) |
| **Package** | One zip: `SpeakRect.exe` + local LLM host + **Q8_0** model files |
| **Source** | Closed / private |

### Install

1. Download **`SpeakRect-<version>-win-x64.zip`** from the [latest release](https://github.com/dunjeon/SpeakRect/releases/latest).
2. Extract it to a folder (about **2.5 GB free** disk recommended).
3. Run **`SpeakRect.exe`**.

No separate model download is required for the complete package.

### Windows SmartScreen / “Unknown publisher”

Windows may block or warn on first run. Common messages:

- **“Windows protected your PC”** / SmartScreen  
- **“Unknown publisher”**  
- Browser warnings for an unsigned download  

**That is expected.** SpeakRect is free and distributed as an **unsigned** zip from GitHub Releases. The warning means Microsoft does not recognize the publisher — **not** that the file was found to be malware.

**To run anyway**

1. On the SmartScreen window, click **More info**.
2. Click **Run anyway**.

If the file stays blocked after download:

1. Right-click **`SpeakRect.exe`** (or the zip) → **Properties**.
2. Check **Unblock** if shown → **OK**.
3. Run the app again.

Or from PowerShell in the extract folder:

```powershell
Unblock-File -Path .\SpeakRect.exe
Get-ChildItem -Recurse | Unblock-File
```

Only download from the official [Releases](https://github.com/dunjeon/SpeakRect/releases) page. If a release lists a SHA-256 checksum:

```powershell
Get-FileHash -Algorithm SHA256 .\SpeakRect-*-win-x64.zip
```

---

## Requirements

| | |
|--|--|
| **OS** | Windows 10 or 11, **x64** |
| **Disk** | ~**2.5 GB+** free for a full install |
| **GPU** | Strongly recommended for usable speed |

### Disk (bundled local LLM)

| Item | Approx. size | Role |
|------|--------------|------|
| Vision model (`glmocr-Q8_0.gguf`) | ~906 MB | Local LLM weights |
| Vision projector (`mmproj-glmocr-Q8_0.gguf`) | ~462 MB | Image understanding |
| Local LLM host (`koboldcpp.exe`) | ~608 MB | Runs the model |
| Config | under 10 KB | Launch settings |
| **Runtime folder total** | **~2.0 GB** | Ships next to SpeakRect |

The SpeakRect app itself is small next to the model files.

### VRAM

Default setup loads the model on the GPU (Vulkan).

| | Guidance |
|--|----------|
| Model files on disk | ~**1.4 GB** (weights + projector) |
| Free VRAM to load and run | ~**3 GB** minimum |
| Sharing GPU with a game | **4–6 GB+** free recommended |
| Comfortable gaming + reading | **8 GB+** total card VRAM |

CPU-only is usually too slow for interactive use. Integrated or very low-VRAM GPUs may not load the default model pair.

### Local LLM defaults (1.2.1+)

The bundled host is configured for broader hardware compatibility:

- **Vulkan** is the default GPU backend (instead of CUDA)
- **2 CPU threads** are used by default, to leave more cores free for games and other apps

Advanced users can edit `koboldcpp\ocr.kcpps` if they need different host settings.

---

## Quick start

1. Extract the release so `SpeakRect.exe` and the `koboldcpp` folder stay together.
2. Run **SpeakRect**. It appears in the **system tray**. The local LLM may take a short time to load the first time.
3. Press **Shift+Tab** (default) to show the overlay — or double-click the tray icon.
4. **Draw** a box around the text you care about (starts on **region 1**).
5. Press **Enter** to **speak** that region.
6. To add another area: **Shift+F2** (overlay still open) → draw region 2 → **Enter** again.
7. Press **Escape** to hide the overlay. Later, **Shift+F1** / **Shift+F2** / … speak those saved spots without opening the overlay.

For games, use **borderless windowed** so capture works.

---

## How to use

### System tray

| Menu | What it does |
|------|----------------|
| **Show Overlay** | Opens the selection overlay |
| **Settings…** | Profiles, Key Map, Regions, Voice, Follow, Analytics, Help |
| **Profiles** | Load / save named setups |
| **Exit** | Quit SpeakRect and stop the local LLM |

Double-click the tray icon to show the overlay. Only one instance of SpeakRect runs at a time.

### Overlay basics

The overlay is a dim full-screen layer so you can still see the app or game underneath while you draw. Tools sit in the **left sidebar**.

| Action | How |
|--------|-----|
| Show / hide overlay | **Shift+Tab** (default), tray, or gamepad if bound |
| Draw a region | Click and drag (not on the sidebar) |
| Read the current region | **Enter** |
| Cancel / hide overlay | **Escape** (saves the current slot, stops speech, hides to tray) |
| Clear active region slot | **Delete** |
| Overlay more transparent | **← Left** arrow |
| Overlay more opaque | **→ Right** arrow |

While **Settings** is open, drawing on the overlay is paused.

#### Overlay opacity

Use the arrow keys **while the overlay is open**:

| Key | Effect |
|-----|--------|
| **← Left** | Lower opacity (see more of the screen). Stops at a light minimum. |
| **→ Right** | Raise opacity (stronger dim / easier to see your box). Up to fully opaque. |

Turn opacity **down** when dim dialogue or dark HUDs are hard to aim at. Turn it **up** when the selection outline is hard to spot on a bright scene. SpeakRect briefly clears the overlay tint when it captures so the snapshot is not darkened.

### Shape tools

| Shape | Default key | Use when |
|-------|-------------|----------|
| **RECT** | **R** | Most text boxes, panels, UI |
| **OVAL** | **O** | Circular / rounded areas |
| **LASSO** | **L** | Irregular freehand outlines |

1. Pick RECT, OVAL, or LASSO (sidebar or **R** / **O** / **L**).
2. The **left sidebar hides** so you can select text near the left edge.
3. Drag on the content (release to finish a rect/oval; lasso closes when you finish the stroke).
4. When the shape is committed, the **sidebar returns**.
5. Press **Enter** to speak.

While the sidebar is hidden, a small corner hint remains. **Esc** brings the sidebar back without leaving the overlay (press **Esc** again to hide to the tray).

### Regions (slots 1–8)

SpeakRect keeps **8 fixed region slots**, each with its own hotkey. Defaults:

| Slot | Default hotkey | Typical use |
|------|----------------|-------------|
| **1** | **Shift+F1** | Main dialogue |
| **2** | **Shift+F2** | Choices / secondary box |
| **3**–**8** | **Shift+F3** … **Shift+F8** | Extra UI, logs, captions… |
| **9 (Follow)** | **Shift+F9** | Under the mouse (not a fixed box) |

All hotkeys are remappable in **Settings → Key Map**.

#### Set region 1

1. Show the overlay (**Shift+Tab** or tray **Show Overlay**).
2. You start on **region 1**. (To switch slots while the overlay is open, press that slot’s hotkey — it selects the slot; it does **not** speak.)
3. Pick a shape if needed, then **click and drag** over the text.
4. Optional: press **Enter** to test-speak.

Drawings save when you finish drawing, press **Enter**, switch slots, or hide the overlay (**Escape**).

#### Speak a region

| Situation | Action |
|-----------|--------|
| Overlay open | Select the slot → **Enter** |
| Overlay closed (normal play) | **Shift+F1** … **Shift+F8** from anywhere |

#### Add more regions

1. Keep the overlay open (or open it again).
2. Press the next slot’s hotkey (e.g. **Shift+F2**). This switches slots and saves the previous one; it does not speak while the overlay is visible.
3. Draw the new area → optional **Enter** to test.
4. Repeat up to 8 fixed regions, then **Escape** to hide.

| You want to… | Do this |
|--------------|---------|
| Speak region *n* | **Shift+F*n*** (overlay closed) |
| Move or resize a region | Overlay on → that slot’s hotkey → draw again |
| Clear a region | Overlay on → select slot → **Delete** |
| Redraw region 1 without losing region 2 | Overlay on → **Shift+F1** → draw; slot 2 is untouched |

**Game example:** Region 1 = dialogue · Region 2 = choices · Region 3 = quest tracker. Play borderless; press the matching hotkey when that UI appears.

**Comic example:** One region over a panel or page with Comic Book mode on — reuse the same slot on the next page if layout is stable.

Regions are stored in settings and can be saved in **Profiles**.

### Follow (region 9 — under the mouse)

Follow is a movable capture box that tracks the cursor (or can lock in place). It does **not** overwrite slots 1–8.

| Default | Action |
|---------|--------|
| **Shift+F9** | Speak at the **current mouse** using Follow size/shape/offset |
| **Up** (overlay) | Arm Follow floating preview |
| **Down** (overlay) | Turn Follow off (overlay stays open) |
| **Enter** (Follow on) | **Lock** / unlock the box — does **not** speak |
| Sidebar **FOLLOW** | Click = on/off; **Ctrl+click** = Follow settings |
| Sidebar **SETTINGS** | Opens the full Settings window |

Typical use: size the Follow box to a subtitle line → point the mouse → **Shift+F9**.

### Reading modes

One primary mode is always selected (also toggleable with global hotkeys):

| Mode | Default hotkey | When to use |
|------|----------------|-------------|
| **Default** | **Ctrl+D** | Games, menus, subtitles, plain UI |
| **Comic Book** | **Ctrl+B** | Panels, balloons, multi-caption pages |
| **Fast** | **Ctrl+N** | With Comic Book: quicker reads |
| **Faster** | **Ctrl+M** | With Comic Book: snappiest option |

Mode toggles use **Ctrl+letter**, not Shift+letter: a global **Shift+D** (etc.) would fire while typing capitals.

- **Default** and **Comic Book** are opposites — only one primary style at a time.
- **Fast** / **Faster** only apply with Comic Book and are mutually exclusive; enabling either also turns Comic Book on.
- When the overlay is hidden, mode hotkeys are announced with a short TTS phrase.

### Settings

Open **Settings…** from the tray or the overlay **SETTINGS** button. Profile **Load / Save / Save As / Delete** sit at the top.

| Tab | What it does |
|-----|----------------|
| **Key Map** | Keyboard + gamepad bindings; custom actions |
| **Regions** | Map of slots 1–8: position, hotkey, shape; clear a slot |
| **Voice** | TTS: Windows (default) or optional SAPI 5; voice, rate, pitch, volume |
| **Follow** | Size, shape, and offset for the mouse-follow reader |
| **Analytics** | Most recent OCR/speak result: text, pipeline images (capture/prep/regions/crops), timings |
| **Help** | Getting started, features, default hotkeys, open README |

#### Voice (speech)

**Default: Windows (UWP / OneCore).** Fresh installs and **Reset** use this engine so SpeakRect speaks immediately with the voices Windows already provides. You do **not** need SAPI, Narrator packs, or any adapter for a normal setup.

| Control | Effect |
|---------|--------|
| **Engine** | **Windows** (default) or **SAPI 5** (optional) |
| **Voice** | Voices for the selected engine (blank = that engine’s system default) |
| **Rate** / **Pitch** / **Volume** | Speaking style |
| **Silence** options | End / punctuation gaps (**Windows** engine only) |
| **Preview** | Sample with current settings |

**More Windows voices (still on the default engine)**  
**Settings → Time & language → Speech** (or Language packs) → download additional speech voices, then reopen SpeakRect’s Voice tab.

##### Optional: SAPI 5 (natural / adapter voices)

SAPI 5 is for people who want **classic Control Panel voices** or a **third-party SAPI engine**. Microsoft’s **Narrator “Natural” voices** are *not* exposed to normal apps by Windows; SpeakRect cannot list them on the Windows engine. Some community tools register those (or similar) voices as **SAPI 5** so apps can use them.

**Example path — NaturalVoiceSAPIAdapter (unofficial)**

1. Install and test [NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter) from its [Releases](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter/releases) (run **Installer.exe** as admin; install **both 32-bit and 64-bit** on 64-bit Windows if you want every app covered).  
2. Enable **local Narrator voices** in the adapter if that is what you want; follow the project README / [wiki](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter/wiki) (some newer Narrator packs need a [last working voice download](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter/wiki/Narrator-natural-voice-download-links)).  
3. Confirm voices appear in Windows **Control Panel → Speech Recognition → Text to Speech**, or the adapter’s test app.  
4. In SpeakRect: **Settings → Voice → Engine → SAPI 5 (classic + adapters)** → pick a voice → **Preview**.  
5. **Save** your **profile** so `TtsEngine` and the SAPI voice name are stored (profiles include the full Voice section).

**Important**

| | |
|--|--|
| **Default** | Windows engine — always the out-of-the-box path |
| **Adapter tools** | Third-party, not shipped with SpeakRect, not endorsed by Microsoft |
| **Stability** | Adapters can break after Windows updates; switch Engine back to **Windows** if speech fails |
| **Online adapter options** | Edge/Azure voices via an adapter may need the network; local Narrator models stay offline when configured that way |
| **Profiles** | Engine + voice + rate/pitch/volume are saved with the profile |

SpeakRect does **not** bundle or install SAPI adapters. If SAPI is selected but no usable voice is registered, switch back to **Windows**.

#### Key Map

Rebind overlay, modes, region slots, shape tools, and optional **gamepad** buttons. Custom actions can send clicks, key chords, stick-as-mouse, and more.

Tips:

- Global bindings work when another app is focused (when Windows allows).
- Overlay-only shape keys work when the overlay is up.
- Avoid conflicts with game controls; use **Profiles** per game if needed.
- Gamepad uses **XInput** (controller index is configurable).

### Profiles

Save hotkeys, modes, regions, Follow size, **voice (engine + voice + rate/pitch/volume)**, and related prefs:

1. Tray → **Profiles** → **Save current…** / **Save as…**, or use the profile bar in **Settings**.
2. Load from the tray menu or the Settings profile list.

If you switch to **SAPI 5** or change the spoken voice, **Save** the profile again so that setup is restored next time.

### Default hotkey cheat sheet

| Action | Default |
|--------|---------|
| Show / hide overlay | **Shift+Tab** |
| Default mode | **Ctrl+D** |
| Comic Book mode | **Ctrl+B** |
| Fast | **Ctrl+N** |
| Faster | **Ctrl+M** |
| Speak region 1–8 | **Shift+F1** … **Shift+F8** |
| Speak Follow (at mouse) | **Shift+F9** |
| Shape: Rectangle / Oval / Lasso | **R** / **O** / **L** (overlay) |
| Speak current selection | **Enter** (overlay; regions 1–8) |
| Lock / unlock Follow box | **Enter** (overlay; Follow on) |
| Hide to tray | **Escape** |
| Clear active region | **Delete** |
| Follow preview on / off | **Up** / **Down** (overlay) |
| Overlay more transparent / opaque | **←** / **→** |

Change any of these in **Settings → Key Map**.

### Suggested workflows

**Game with several UI spots**  
Borderless windowed · Comic Book **off** · overlay → draw dialogue → **Shift+F2** → draw choices → more slots as needed → **Escape**. In play: **Shift+F1** / **F2** / …

**One-shot read**  
Overlay → draw → **Enter**. Hide with **Escape** when done.

**Subtitles under the cursor**  
Follow settings → size the box → point → **Shift+F9**.

**Comic page**  
Comic Book **on** (optional Fast/Faster) → draw panel or page → **Enter**. Reuse the same region hotkey if layout stays put.

**Controller-only**  
Key Map → bind overlay and region slots to the pad; optionally add stick/mouse custom actions.

---

## What it works on

| Target | Notes |
|--------|--------|
| **Retro games** | Emulators, classic ports, pixel UI and dialogue |
| **Modern games** | As long as the game is **not** exclusive fullscreen |
| **Comic books / manga** | Use **Comic Book** mode for panels and balloons |
| **Anything else on screen** | Browsers, documents, chat, apps, subtitles |

**Important:** SpeakRect captures the desktop composite. Exclusive fullscreen often cannot be read. Prefer **borderless windowed** or **windowed**.

---

## Accuracy

SpeakRect aims to be **usable**, not perfect. Internal debug sessions (mostly English comics with Comic Book + Faster) give a ballpark:

| Metric | Result |
|--------|--------|
| Sessions logged | **31** complete reads |
| Empty / failed reads | **0 / 31** |
| Spot-check set | **14** panels / pages vs on-screen wording |
| Word match (recall) | **~99.7%** of ground-truth words present |
| Word match (precision) | **~100%** on that dialogue-heavy set |

Clean English comic balloons and captions were usually spoken correctly end-to-end. Small slips (dropped leading words, logos, rare credit lines) still happen.

| Situation | Expectation |
|-----------|-------------|
| Clear comic balloons / print | Excellent |
| Dense credits, tiny legal lines, fancy logos | More mistakes |
| Stylized SFX, heavy art behind text | Occasional miss or garble |
| Game UI, subtitles, low contrast, motion blur | Often good, less consistent than clean comics |
| Exclusive fullscreen / wrong region / partial crop | Missed or incomplete read |
| Non-English | Not validated in these logs |

These numbers are from internal runs, not a formal benchmark. Hardware, region size, and content matter. If a read is wrong, redraw a tighter box or retry; saved regions make that easy.

---

## Privacy

Recognition uses a **local LLM** on your machine. SpeakRect does not send screen captures to a cloud AI API. Speech uses **on-device** TTS (Windows UWP by default, or optional SAPI 5 / a registered local engine). If you enable an adapter’s *online* voices, that path is outside SpeakRect and may use the network — keep the Windows engine or local-only SAPI voices for fully offline speech.

---

## Credits & third-party software

| Component | Project | Links |
|-----------|---------|--------|
| Local LLM host | **KoboldCpp** (LostRuins) | [GitHub](https://github.com/LostRuins/koboldcpp) · [Releases](https://github.com/LostRuins/koboldcpp/releases/latest) |
| Vision OCR model | **GLM-OCR** (Z.ai / zai-org) | [GitHub](https://github.com/zai-org/GLM-OCR) · [Hugging Face](https://huggingface.co/zai-org/GLM-OCR) |

SpeakRect bundles a **Q8_0** GGUF build of GLM-OCR (`glmocr-Q8_0.gguf` + `mmproj-glmocr-Q8_0.gguf`) and runs it through KoboldCpp on `127.0.0.1`. Their licenses apply to those components — see each project and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

## License

**SpeakRect** (the app) is **proprietary**. Source is closed. You may use the software **free of charge for personal and non-commercial purposes** under [LICENSE](LICENSE). Commercial use or redistribution of SpeakRect requires permission.

Third-party components (KoboldCpp, GLM-OCR, and others) keep **their own** licenses — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
