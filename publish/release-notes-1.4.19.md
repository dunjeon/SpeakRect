## SpeakRect 1.4.19

Complete Windows x64 package (single zip):

- `SpeakRect.exe` (closed source — no source in this repository)
- KoboldCpp host + vision GGUF model files (from `ocr.kcpps`)
- LICENSE, README, third-party notices

### What’s new in 1.4.19

#### Balloons / Image / Analytics — one shared pipe (full resolution)

- Live OCR, **Settings → Balloons / Image** “last capture,” and **Analytics** pipeline images now use the **same full-resolution** capture and prep path.
- Fixed a long-standing mismatch where Balloons re-detected on a **downscaled Analytics thumbnail** (long edge capped at 1280), so green boxes could look better or worse than live even with identical knobs.
- Analytics no longer re-samples pipeline frames for storage; gallery thumbs are UI-only. Double-click enlarge shows the real pipe pixels.

#### Balloons — detect fog visible in preview

- Preview base image is the **detect view** (gray fog when on — what WinOCR sees), not the clear OCR tone alone.
- Fog strength and on/off update the preview; when boxes are locked, fog still refreshes the background without wiping your regions.

#### Balloons — default fog strength 0.35

- Built-in default **Fog strength** is **0.35** (Reset defaults / new profiles). Existing saved profiles keep their stored value until you change or reset them.

#### Image + Balloons — themed progress strip

- A thin dark/orange **progress strip** above the status line animates while prep, detect, speak, or snap is running (fits the ink UI theme).

#### Speech — strip VL “uchar” junk

- Noise rules drop model/programming garbage like **uchar** / **unsigned char** so it is not spoken as dialogue.

#### Also in recent 1.4.x builds

##### Balloons / Comic Book — preview matches live regions (1.4.17)

- Balloons detect preview and live Comic Book share one region pipeline.
- Dead-island drops logo tokens on non-balloon art (e.g. **cream**).

##### Image prep — Default and Comic Book share the same pipeline (1.4.17)

- Both modes use letterbox → upscale → gray → **tone**. Mode only changes OCR strategy.

##### Speech — title-case ALL CAPS words (1.4.16)

- **Settings → Speech → Text rules → Title-case ALL CAPS words** (default **off**).
- Mutually exclusive with **Force lowercase**.

##### Balloons — merge overlapping islands honors Crop pad (1.4.16)

- Overlap test uses **Grow X/Y** and **Crop pad**, matching green boxes / OCR crops.

##### OCR prompts — common-word spelling (1.4.15)

- Default full/crop/simple/recovery prompts correct common English spelling.

##### Comic Book — mega-island full-frame rescue (1.4.14)

- Single near-full-frame island prefers full-frame OCR when crops under-read.

##### Speech — mid-word hyphen rejoin (1.4.13)

- Syllable breaks like `sophisti-cated` rejoin for TTS.

##### Balloons — merge overlapping islands (1.4.13)

- **Settings → Balloons → Merge overlapping islands** (default on).

##### Settings — Snap region (1.4.12)

- Snap active F1–F8 region into Image/Balloons preview (no OCR/TTS).

##### Balloons — reading order LTR (1.4.11)

- Same-row balloons ordered left → right, rows top → bottom.

##### Comic Book — sequential regions (1.4.10)

- Sequential regions on by default; short balloons not dropped by page-wide speak-dedupe.

##### Stop speech hotkey (1.4.10)

- Global **Stop TTS** (default **Ctrl+Shift+S**). Remap under **Settings → Key Map**.

### Install

1. Download **`SpeakRect-1.4.19-win-x64.zip`**
2. Extract (about **2.5 GB free** disk recommended)
3. Run **`SpeakRect.exe`**

**Source code is not included and is not open source.**

### Verify (optional)

```
SHA-256: 761CE59296CF0BE7C3784D31F3D2E7AEE7FB67FDC7990064E33D024AD1AECC3D
```

```powershell
Get-FileHash -Algorithm SHA256 .\SpeakRect-1.4.19-win-x64.zip
```

### Windows SmartScreen / “Unknown publisher”

Windows may block or warn when you first run **SpeakRect.exe** (or open the zip). Common messages:

- **“Windows protected your PC”** / SmartScreen
- **“Unknown publisher”**
- Browser download warnings for an unsigned app

**That is expected.** SpeakRect is free and distributed as an unsigned zip. It is **not** code-signed with a paid certificate, so Windows cannot show a verified publisher name. The warning means “Microsoft doesn’t know this publisher,” **not** that the file was found to be malware.

**How to run it anyway**

1. On the SmartScreen window, click **More info**.
2. Click **Run anyway**.

If Explorer still treats the file as blocked after download:

1. Right-click **SpeakRect.exe** (or the zip) → **Properties**.
2. If you see **Unblock** at the bottom, check it → **OK**.
3. Run the app again.

Or in PowerShell, from the folder you extracted to:

```powershell
Unblock-File -Path .\SpeakRect.exe
Get-ChildItem -Recurse | Unblock-File
```

Only download from the official Releases page on this repository.
