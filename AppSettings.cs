using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SpeakRect
{

    /// <summary>
    /// App settings from SpeakRect.ini (next to the exe).
    /// ComicBook mode + OCR prompt strings (edit in the ini, or leave blank for hard-coded defaults).
    /// </summary>
    public sealed class AppSettings
    {
        public static AppSettings Current { get; } = new AppSettings();

        /// <summary>Directory of SpeakRect.exe (works for single-file publish).</summary>
        public static string AppDir { get; } =
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        public static string IniPath { get; } = Path.Combine(AppDir, "SpeakRect.ini");

        /// <summary>Folder next to the exe for named profile .ini files.</summary>
        public static string ProfilesDir { get; } = Path.Combine(AppDir, "Profiles");

        /// <summary>
        /// Last loaded/saved profile label (also stored in SpeakRect.ini [PROFILE]).
        /// Does not auto-load a profile file at startup — SpeakRect.ini is the live config.
        /// </summary>
        public string ActiveProfileName { get; set; } = "Default";

        /// <summary>
        /// Last Settings UI tab (enum name: KeyMap, Regions, Follow, …).
        /// Restored when opening Settings without a specific destination.
        /// </summary>
        public string LastSettingsTab { get; set; } = "Help";

        /// <summary>Sanitize / default a LastSettingsTab string for ini persistence.</summary>
        public static string NormalizeLastSettingsTab(string? raw)
        {
            // Empty / invalid → Help (matches stock ini LastSettingsTab default).
            if (string.IsNullOrWhiteSpace(raw))
                return "Help";
            string t = raw.Trim();
            // Allow only simple identifiers (enum-style names).
            foreach (char c in t)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                    return "Help";
            }
            return t.Length == 0 ? "Help" : t;
        }

        /// <summary>Remember the active Settings tab and write SpeakRect.ini.</summary>
        public void RememberSettingsTab(string tabName)
        {
            LastSettingsTab = NormalizeLastSettingsTab(tabName);
            try { Save(); } catch { /* keep in-memory */ }
        }

        // -----------------------------------------------------------------------
        // Hard-coded OCR prompt default (used when the ini key is missing or blank)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Single built-in VL instruction for every Local-LLM OCR path
        /// (Default, Comic full-frame, crops, POI, recovery retries).
        /// </summary>
        public const string DefaultOcrPrompt =
            "As an OCR, Extract all english text. Do not export html or markdown.";

        /// <summary>
        /// <b>OFF</b> (product default = Default mode): shared Image prep → one full-frame OCR.
        /// <b>ON</b>: Comic Book pipeline — fog detect, balloons, crops / POI / sequential.
        /// </summary>
        public bool ComicBook { get; set; } = false;

        /// <summary>
        /// Sole OCR prompt override. Blank → <see cref="DefaultOcrPrompt"/>.
        /// Edit under Speech → Prompts.
        /// </summary>
        public string OcrPrompt { get; set; } = "";

        // -----------------------------------------------------------------------
        // TTS voice — [VOICE] section
        // Windows = Windows.Media.SpeechSynthesis (UWP/OneCore)
        // Sapi    = System.Speech SAPI 5 (adapters / classic voices)
        // -----------------------------------------------------------------------

        /// <summary>
        /// TTS backend: <c>Windows</c> (UWP) or <c>Sapi</c> (SAPI 5).
        /// </summary>
        public string TtsEngine { get; set; } = "Windows";

        /// <summary>
        /// UWP voice Id from <c>VoiceInformation.Id</c> when engine is Windows.
        /// Blank → system default voice.
        /// </summary>
        public string VoiceId { get; set; } = "";

        /// <summary>
        /// SAPI 5 voice <c>VoiceInfo.Name</c> when engine is Sapi.
        /// Blank → system default SAPI voice.
        /// </summary>
        public string SapiVoiceName { get; set; } = "";

        /// <summary>
        /// Speaking rate multiplier (Windows range 0.5–6.0). Default 1.0.
        /// Mapped into SAPI Rate (-10..10) when using SAPI.
        /// </summary>
        public double VoiceSpeakingRate { get; set; } = 1.0;

        /// <summary>
        /// Pitch multiplier (Windows range 0.0–2.0). Default 1.0.
        /// On SAPI applied via SSML prosody when not 1.0.
        /// </summary>
        public double VoicePitch { get; set; } = 1.0;

        /// <summary>
        /// TTS utterance volume (Windows range 0.0–1.0). Default 1.0.
        /// </summary>
        public double VoiceVolume { get; set; } = 1.0;

        /// <summary>
        /// Silence after each utterance: <c>Default</c> or <c>Min</c>
        /// (<see cref="Windows.Media.SpeechSynthesis.SpeechAppendedSilence"/>).
        /// UWP only — ignored for SAPI.
        /// </summary>
        public string VoiceAppendedSilence { get; set; } = "Default";

        /// <summary>
        /// Silence after punctuation: <c>Default</c> or <c>Min</c>
        /// (<see cref="Windows.Media.SpeechSynthesis.SpeechPunctuationSilence"/>).
        /// UWP only — ignored for SAPI.
        /// </summary>
        public string VoicePunctuationSilence { get; set; } = "Default";

        // Speak-unit pause delays (Task.Delay between cleaned OCR pieces).
        // Applied after typed pause marks — engine-agnostic (Windows + SAPI).

        // Defaults match stock SpeakRect.ini [VOICE] (Settings → Voice):
        // comma ≈ 0.10 s, end-of-sentence ≈ 0.50 s, light mid-break ≈ 0.05 s,
        // balloon / speaker turn ≈ 0.75 s.
        public const int DefaultCommaPauseMs = 102;
        public const int DefaultSentencePauseMs = 502;   // . ! ?
        public const int DefaultOtherPauseMs = 52;
        public const int DefaultBubblePauseMs = 752;
        public const int MinSpeakPauseMs = 0;
        public const int MaxSpeakPauseMs = 3000;

        /// <summary>Pause after a comma clause break (ms). Default 102.</summary>
        public int VoiceCommaPauseMs { get; set; } = DefaultCommaPauseMs;

        /// <summary>Pause after <c>.</c> <c>!</c> <c>?</c> (ms). Default 502.</summary>
        public int VoiceSentencePauseMs { get; set; } = DefaultSentencePauseMs;

        /// <summary>Pause for other typed speak breaks (ms). Default 52.</summary>
        public int VoiceOtherPauseMs { get; set; } = DefaultOtherPauseMs;

        /// <summary>
        /// Pause between comic balloons / blank-line / dash-balloon splits (ms).
        /// Default 752.
        /// </summary>
        public int VoiceBubblePauseMs { get; set; } = DefaultBubblePauseMs;

        /// <summary>
        /// When true (default), OCR punctuation is encoded into typed pause marks
        /// and SpeakRect inserts Task.Delay gaps (comma / sentence / other / balloon).
        /// When false, punctuation stays for the TTS engine; no custom pause marks
        /// or typed delays (sliders are ignored until re-enabled).
        /// </summary>
        public bool VoiceUseCustomPauseEncodings { get; set; } = true;

        // -----------------------------------------------------------------------
        // Comic Book balloon / region detect — [COMIC_REGIONS] section
        // Used when ComicBook=true. Defaults match prior hard-coded pipeline.
        // -----------------------------------------------------------------------

        public const float DefaultComicDetectFogAmount = 0.35f;
        /// <summary>Dyn-fog linear search start (stock 0.00). 0.01 steps up to max.</summary>
        public const float DefaultComicDynamicFogMin = 0f;
        /// <summary>Dyn-fog linear search end inclusive (stock 1.00 = 100%).</summary>
        public const float DefaultComicDynamicFogMax = 1f;
        public const double DefaultComicClusterGapX = 1.05;
        public const double DefaultComicClusterGapY = 1.15;
        public const double DefaultComicInflateFracX = 0.22;
        public const double DefaultComicInflateFracY = 0.28;
        public const int DefaultComicRegionPadding = 16;
        /// <summary>0 = dense-page milder pad off for all modes (user can enable in Balloons).</summary>
        public const int DefaultComicDenseIslandCount = 0;
        public const int DefaultComicOrphanRecoverPasses = 6;
        public const int DefaultComicMinIslandAlnum = 4;

        /// <summary>Gray fog on the image OCR uses for balloon detect (Local-LLM reads clear tone).</summary>
        public bool ComicDetectFog { get; set; } = true;

        /// <summary>Fog blend 0..1 (default 0.35). Higher softens art so ink plates stand out.
        /// Used only when <see cref="ComicDynamicFog"/> is off (fixed fog). Dyn search ignores this.</summary>
        public float ComicDetectFogAmount { get; set; } = DefaultComicDetectFogAmount;

        /// <summary>
        /// Auto fog for OCR detect: climb from
        /// <see cref="ComicDynamicFogMin"/> to <see cref="ComicDynamicFogMax"/> in 0.01
        /// steps (no early stop). At each tick score island count + total area; keep the
        /// peak (most boxes first, then largest area; lower fog on ties). After the
        /// climb, final detect uses that peak amount. Merge-overlap off during search.
        /// Shared by live + Balloons preview. Fixed Fog strength unused while this is on.
        /// </summary>
        public bool ComicDynamicFog { get; set; } = true;

        /// <summary>
        /// Dyn-fog search floor 0..1 (stock 0.00). Inclusive start of the 0.01 grind.
        /// Raise to skip low fog if you know it never helps (saves CPU).
        /// </summary>
        public float ComicDynamicFogMin { get; set; } = DefaultComicDynamicFogMin;

        /// <summary>
        /// Dyn-fog search ceiling 0..1 (stock 1.00). Inclusive end of the 0.01 grind.
        /// Lower to skip heavy fog if wash-out never helps (saves CPU).
        /// </summary>
        public float ComicDynamicFogMax { get; set; } = DefaultComicDynamicFogMax;

        /// <summary>
        /// Line-cluster horizontal gap factor (median line height × this).
        /// Lower = keep side-by-side balloons separate; higher = glue more lines.
        /// </summary>
        public double ComicClusterGapX { get; set; } = DefaultComicClusterGapX;

        /// <summary>
        /// Line-cluster vertical gap factor. Lower = keep stacked balloons separate;
        /// higher = merge multi-line speech into one island.
        /// </summary>
        public double ComicClusterGapY { get; set; } = DefaultComicClusterGapY;

        /// <summary>Grow each island by this fraction of its width (each side).</summary>
        public double ComicInflateFracX { get; set; } = DefaultComicInflateFracX;

        /// <summary>Grow each island by this fraction of its height (each side).</summary>
        public double ComicInflateFracY { get; set; } = DefaultComicInflateFracY;

        /// <summary>Extra crop padding around islands (px), clamped vs neighbors.</summary>
        public int ComicRegionPadding { get; set; } = DefaultComicRegionPadding;

        /// <summary>
        /// When detect finds this many islands (or more), use milder pad so dense
        /// pages stay separable. 0 = never switch to dense pad.
        /// </summary>
        public int ComicDenseIslandCount { get; set; } = DefaultComicDenseIslandCount;

        /// <summary>
        /// Re-detect inside huge full-width / large-area islands (caption + row globs).
        /// </summary>
        public bool ComicSplitLargeRegions { get; set; } = true;

        /// <summary>
        /// When true (default): after Grow X/Y and Crop pad, any islands whose
        /// effective boxes overlap are merged into one union rectangle (covers all
        /// text; no crop cutoff). Helps dense OCR island scenes where close
        /// balloons inflate/pad into each other.
        /// When false: overlapping grow-inflated islands are nudged apart instead.
        /// </summary>
        public bool ComicMergeOverlappingIslands { get; set; } = true;

        /// <summary>
        /// Max bright-blob orphan balloons to re-OCR after full-frame detect misses.
        /// 0 = no orphan recovery.
        /// </summary>
        public int ComicOrphanRecoverPasses { get; set; } = DefaultComicOrphanRecoverPasses;

        /// <summary>
        /// Drop detect islands with fewer alphanumeric characters than this
        /// (costume art, scrap glyphs). <b>0 = off</b> (do not filter by letter count).
        /// </summary>
        public int ComicMinIslandAlnum { get; set; } = DefaultComicMinIslandAlnum;

        /// <summary>
        /// When true: OCR each balloon alone, speak it, wait for TTS, then the
        /// next region. Isolates balloons so cross-region word reuse never hits
        /// global speak-dedupe (e.g. "Really?" after "it's really…").
        /// When false: vertical crop-stack (one OCR image) + global unit plan.
        /// Balloons §9 · SPEAK PATH — on by default for all modes.
        /// </summary>
        public bool ComicSequentialRegions { get; set; } = true;

        /// <summary>
        /// Comic Book alternate: tone + green region boxes (± outside fog map).
        /// Stock: <see cref="ComicPoiAutoStack"/> on → each island orange canvas VL
        /// one at a time. Stack off/fail multi → §9 sequential or crop-stack.
        /// 1 island + stack off → full-page guide VL. Forces Comic Book on.
        /// Preview is full-page edit map (not always VL input).
        /// Product Default mode / fresh ini: off. Comic Book starting defaults
        /// (first MODE enter, Balloons reset while in Comic Book): on.
        /// </summary>
        public bool ComicPoiMarkers { get; set; } = false;

        /// <summary>
        /// When POI markers are on: thick gray fog over everything outside the
        /// island boxes so Local-LLM / TTS stay on speech text, not art or chrome.
        /// No effect unless <see cref="ComicPoiMarkers"/> is on.
        /// </summary>
        public bool ComicPoiFogOutside { get; set; } = true;

        /// <summary>
        /// When POI is on: lift each island onto its own orange canvas and send to
        /// Local-LLM one at a time (not one multi-strip image). Preview stays full page.
        /// Default on for Comic Book starting defaults.
        /// </summary>
        public bool ComicPoiAutoStack { get; set; } = true;

        /// <summary>
        /// Gap (px) between strips when composing a multi-strip canvas (non-POI
        /// crop-stack / legacy multi-strip). Per-island AutoStack send ignores this
        /// (one island per canvas). Default 8.
        /// </summary>
        public int ComicPoiAutoStackGapPx { get; set; } =
            ComicPoiGuide.DefaultAutoStackGapPx;

        /// <summary>
        /// Outer margin (px) on top/left/right/bottom of each Local-LLM island canvas.
        /// Default 12.
        /// </summary>
        public int ComicPoiAutoStackMarginPx { get; set; } =
            ComicPoiGuide.LlmSendStackMarginPx;

        /// <summary>
        /// Extra canvas size vs stacked content (0 = stock tight; 0.33 = ⅓ larger).
        /// Pads only — lettering not scaled. A/B for Local-LLM vision headroom.
        /// </summary>
        public double ComicPoiStackBeefExtra { get; set; } =
            ComicPoiGuide.DefaultStackBeefExtra;

        /// <summary>
        /// Share of vertical canvas beef placed below the content (0.5 = centered,
        /// 0.85 = bottom-heavy). Horizontal pad stays centered.
        /// </summary>
        public double ComicPoiStackBottomPadShare { get; set; } =
            ComicPoiGuide.DefaultStackBottomPadShare;

        /// <summary>Clamp comic-region detect options to safe ranges.</summary>
        public void NormalizeComicRegionSettings()
        {
            ComicDetectFogAmount = Math.Clamp(ComicDetectFogAmount, 0f, 1f);
            ComicDynamicFogMin = MathF.Round(Math.Clamp(ComicDynamicFogMin, 0f, 1f), 2);
            ComicDynamicFogMax = MathF.Round(Math.Clamp(ComicDynamicFogMax, 0f, 1f), 2);
            if (ComicDynamicFogMin > ComicDynamicFogMax)
            {
                // Swap so floor ≤ ceiling after any manual/ini edit.
                (ComicDynamicFogMin, ComicDynamicFogMax) =
                    (ComicDynamicFogMax, ComicDynamicFogMin);
            }
            ComicClusterGapX = Math.Clamp(ComicClusterGapX, 0.25, 3.0);
            ComicClusterGapY = Math.Clamp(ComicClusterGapY, 0.25, 3.0);
            ComicInflateFracX = Math.Clamp(ComicInflateFracX, 0.0, 0.80);
            ComicInflateFracY = Math.Clamp(ComicInflateFracY, 0.0, 0.80);
            ComicRegionPadding = Math.Clamp(ComicRegionPadding, 0, 64);
            ComicDenseIslandCount = Math.Clamp(ComicDenseIslandCount, 0, 20);
            ComicOrphanRecoverPasses = Math.Clamp(ComicOrphanRecoverPasses, 0, 16);
            // 0 = disabled (keep all non-junk islands regardless of alnum count)
            ComicMinIslandAlnum = Math.Clamp(ComicMinIslandAlnum, 0, 40);
            ComicPoiAutoStackGapPx = Math.Clamp(ComicPoiAutoStackGapPx, 0, 64);
            ComicPoiAutoStackMarginPx = Math.Clamp(ComicPoiAutoStackMarginPx, 0, 64);
            ComicPoiStackBeefExtra = Math.Clamp(ComicPoiStackBeefExtra, 0.0, 1.5);
            ComicPoiStackBottomPadShare =
                Math.Clamp(ComicPoiStackBottomPadShare, 0.0, 1.0);
        }

        /// <summary>
        /// Restore built-in comic balloon detect defaults.
        /// <list type="bullet">
        /// <item><paramref name="asComicBookMode"/> false (default): product Default mode —
        /// ComicBook off, POI off. Used for fresh SpeakRect.ini and full built-in reset.</item>
        /// <item><paramref name="asComicBookMode"/> true: Comic Book mode stock —
        /// ComicBook on + POI on (same feature set as
        /// <see cref="ApplyComicBookModeStartingDefaults"/>). Used by Balloons
        /// "Reset defaults" while already in Comic Book mode.</item>
        /// </list>
        /// </summary>
        public void ResetComicRegionSettingsToDefaults(bool asComicBookMode = false)
        {
            ComicDetectFog = true;
            ComicDetectFogAmount = DefaultComicDetectFogAmount;
            ComicDynamicFog = true;
            ComicDynamicFogMin = DefaultComicDynamicFogMin;
            ComicDynamicFogMax = DefaultComicDynamicFogMax;
            ComicClusterGapX = DefaultComicClusterGapX;
            ComicClusterGapY = DefaultComicClusterGapY;
            ComicInflateFracX = DefaultComicInflateFracX;
            ComicInflateFracY = DefaultComicInflateFracY;
            ComicRegionPadding = DefaultComicRegionPadding;
            ComicDenseIslandCount = DefaultComicDenseIslandCount;
            ComicSplitLargeRegions = true;
            ComicMergeOverlappingIslands = true;
            ComicOrphanRecoverPasses = DefaultComicOrphanRecoverPasses;
            ComicMinIslandAlnum = DefaultComicMinIslandAlnum;
            ComicSequentialRegions = true;
            ComicPoiFogOutside = true;
            ComicPoiAutoStack = true;
            ComicPoiAutoStackGapPx = ComicPoiGuide.DefaultAutoStackGapPx;
            ComicPoiAutoStackMarginPx = ComicPoiGuide.LlmSendStackMarginPx;
            ComicPoiStackBeefExtra = ComicPoiGuide.DefaultStackBeefExtra;
            ComicPoiStackBottomPadShare = ComicPoiGuide.DefaultStackBottomPadShare;
            if (asComicBookMode)
            {
                // Comic Book stock (fresh Comic Book path / Balloons reset in-mode).
                ComicBook = true;
                ComicPoiMarkers = true;
                // Send long-edge cap stays available on Image tab; default off.
                ImageLlmSendDownscale = false;
                ImageLlmSendMaxLongEdge = DefaultImageLlmSendMaxLongEdge;
            }
            else
            {
                // Product Default mode — POI off so Default stays the shipped default.
                ComicBook = false;
                ComicPoiMarkers = false;
            }
            ClearComicOnlyModeStash();
            NormalizeComicRegionSettings();
        }

        // -----------------------------------------------------------------------
        // Image prep pipeline — [IMAGE_PREP] section (letterbox / upscale / tone)
        // Shared by Default + Comic Book capture paths. Profile-backed.
        // -----------------------------------------------------------------------

        public const int DefaultImageUpscaleLongSide = 900;
        public const int DefaultImageLlmSendMaxLongEdge = 640;
        public const float DefaultImageInkGrayWeight = 0.25f;
        public const int DefaultImageDenoiseRadius = 1;
        public const float DefaultImageDenoiseSigma = 22f;
        public const double DefaultImageAutoLevelsLow = 1.0;
        public const double DefaultImageAutoLevelsHigh = 99.0;
        public const int DefaultImageAutoLevelsMinRange = 48;
        public const float DefaultImageSharpenAmount = 0.55f;
        public const int DefaultImageSharpenPasses = 1;
        public const int DefaultImageLetterboxBlack = 80;
        public const int DefaultImageLetterboxWhite = 150;
        public const int DefaultImageLetterboxPad = 3;

        /// <summary>
        /// Master switch for capture image prep (letterbox / scale / gray / tone).
        /// Off = OCR uses the raw snap (no prep). Profile-backed.
        /// </summary>
        public bool ImagePrepEnabled { get; set; } = true;

        /// <summary>Trim black/white bars before upscale. Off = use full snap.</summary>
        public bool ImageLetterbox { get; set; } = true;

        /// <summary>Pad kept around letterbox content (px).</summary>
        public int ImageLetterboxPad { get; set; } = DefaultImageLetterboxPad;

        /// <summary>Dark bar threshold (0–255). Higher = more aggressive dark trim.</summary>
        public int ImageLetterboxBlack { get; set; } = DefaultImageLetterboxBlack;

        /// <summary>Light bar threshold (0–255). Lower = more aggressive light trim.</summary>
        public int ImageLetterboxWhite { get; set; } = DefaultImageLetterboxWhite;

        /// <summary>Content long-edge after letterbox (px). Typical 1280–2560.</summary>
        public int ImageUpscaleLongSide { get; set; } = DefaultImageUpscaleLongSide;

        /// <summary>
        /// After all prep (and optional island stack), downscale the Local-LLM
        /// payload so its long edge is at most <see cref="ImageLlmSendMaxLongEdge"/>.
        /// Default <b>off</b> for all modes (toggle remains on Image tab). Detect/boxes
        /// stay at prep size either way.
        /// </summary>
        public bool ImageLlmSendDownscale { get; set; } = false;

        /// <summary>
        /// Max long edge (px) for Local-LLM send when
        /// <see cref="ImageLlmSendDownscale"/> is on. Default 640.
        /// </summary>
        public int ImageLlmSendMaxLongEdge { get; set; } = DefaultImageLlmSendMaxLongEdge;

        /// <summary>Ink-preserving grayscale after upscale (before tone).</summary>
        public bool ImageGrayscale { get; set; } = true;

        /// <summary>
        /// Blend weight for min(R,G,B) vs luminance in ink-gray (0–1).
        /// Higher keeps colored SFX darker.
        /// </summary>
        public float ImageInkGrayWeight { get; set; } = DefaultImageInkGrayWeight;

        /// <summary>
        /// Bilateral denoise spatial radius (0 = off, 1 = 3×3, 2 = 5×5).
        /// Comic Book tone path only (Default skips tone).
        /// </summary>
        public int ImageDenoiseRadius { get; set; } = DefaultImageDenoiseRadius;

        /// <summary>Bilateral range sigma in gray levels (edge protection).</summary>
        public float ImageDenoiseSigma { get; set; } = DefaultImageDenoiseSigma;

        /// <summary>Percentile auto-levels after denoise. Off = skip stretch.</summary>
        public bool ImageAutoLevels { get; set; } = true;

        /// <summary>Low percentile for auto-levels (0–20).</summary>
        public double ImageAutoLevelsLow { get; set; } = DefaultImageAutoLevelsLow;

        /// <summary>High percentile for auto-levels (80–100).</summary>
        public double ImageAutoLevelsHigh { get; set; } = DefaultImageAutoLevelsHigh;

        /// <summary>Skip auto-levels when input already spans this many levels.</summary>
        public int ImageAutoLevelsMinRange { get; set; } = DefaultImageAutoLevelsMinRange;

        /// <summary>Unsharp amount after levels (0 = off).</summary>
        public float ImageSharpenAmount { get; set; } = DefaultImageSharpenAmount;

        /// <summary>Unsharp pass count (0 = off).</summary>
        public int ImageSharpenPasses { get; set; } = DefaultImageSharpenPasses;

        public void NormalizeImagePrepSettings()
        {
            ImageLetterboxPad = Math.Clamp(ImageLetterboxPad, 0, 32);
            // Dark / light bar thresholds are independent (not a low/high pair).
            // Higher black = more aggressive dark trim; lower white = more aggressive light trim.
            ImageLetterboxBlack = Math.Clamp(ImageLetterboxBlack, 0, 255);
            ImageLetterboxWhite = Math.Clamp(ImageLetterboxWhite, 0, 255);
            ImageUpscaleLongSide = Math.Clamp(ImageUpscaleLongSide, 640, 4096);
            ImageLlmSendMaxLongEdge = Math.Clamp(ImageLlmSendMaxLongEdge, 256, 4096);
            ImageInkGrayWeight = Math.Clamp(ImageInkGrayWeight, 0f, 1f);
            ImageDenoiseRadius = Math.Clamp(ImageDenoiseRadius, 0, 4);
            ImageDenoiseSigma = Math.Clamp(ImageDenoiseSigma, 1f, 80f);
            ImageAutoLevelsLow = Math.Clamp(ImageAutoLevelsLow, 0.0, 20.0);
            ImageAutoLevelsHigh = Math.Clamp(ImageAutoLevelsHigh, 80.0, 100.0);
            if (ImageAutoLevelsHigh <= ImageAutoLevelsLow + 1)
                ImageAutoLevelsHigh = Math.Min(100.0, ImageAutoLevelsLow + 10);
            ImageAutoLevelsMinRange = Math.Clamp(ImageAutoLevelsMinRange, 8, 200);
            ImageSharpenAmount = Math.Clamp(ImageSharpenAmount, 0f, 2.0f);
            ImageSharpenPasses = Math.Clamp(ImageSharpenPasses, 0, 4);
        }

        public void ResetImagePrepSettingsToDefaults()
        {
            ImagePrepEnabled = true;
            ImageLetterbox = true;
            ImageLetterboxPad = DefaultImageLetterboxPad;
            ImageLetterboxBlack = DefaultImageLetterboxBlack;
            ImageLetterboxWhite = DefaultImageLetterboxWhite;
            ImageUpscaleLongSide = DefaultImageUpscaleLongSide;
            ImageLlmSendDownscale = false;
            ImageLlmSendMaxLongEdge = DefaultImageLlmSendMaxLongEdge;
            ImageGrayscale = true;
            ImageInkGrayWeight = DefaultImageInkGrayWeight;
            ImageDenoiseRadius = DefaultImageDenoiseRadius;
            ImageDenoiseSigma = DefaultImageDenoiseSigma;
            ImageAutoLevels = true;
            ImageAutoLevelsLow = DefaultImageAutoLevelsLow;
            ImageAutoLevelsHigh = DefaultImageAutoLevelsHigh;
            ImageAutoLevelsMinRange = DefaultImageAutoLevelsMinRange;
            ImageSharpenAmount = DefaultImageSharpenAmount;
            ImageSharpenPasses = DefaultImageSharpenPasses;
            NormalizeImagePrepSettings();
        }

        // -----------------------------------------------------------------------
        // Speech rules — [SPEECH_RULES] section (user pre-TTS substitutions)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Ordered user word/phrase replacements applied after built-in abbrev expand.
        /// Empty replace = strip. Profile-backed with the rest of settings.
        /// </summary>
        public List<SpeechRule> SpeechRules { get; private set; } = new();

        private bool _speechTitleCaseAllCaps;
        private bool _speechForceLowercase = true;

        /// <summary>
        /// When true, <c>CleanForSpeech</c> title-cases words that are entirely
        /// uppercase (HELLO → Hello) after noise strip. Mixed-case words and
        /// single-letter tokens (I, A) are left alone. Default false.
        /// Mutually exclusive with <see cref="SpeechForceLowercase"/> — turning
        /// this on clears force-lowercase.
        /// </summary>
        public bool SpeechTitleCaseAllCaps
        {
            get => _speechTitleCaseAllCaps;
            set
            {
                _speechTitleCaseAllCaps = value;
                if (value)
                    _speechForceLowercase = false;
            }
        }

        /// <summary>
        /// When true, <c>CleanForSpeech</c> lowercases the stream after noise
        /// strip — normalizes ALL-CAPS comics for TTS. Default <c>true</c> for
        /// all modes (titles like Mr. still expand — Abbrev stage is case-insensitive).
        /// Mutually exclusive with <see cref="SpeechTitleCaseAllCaps"/> — turning
        /// this on clears title-case ALL CAPS.
        /// </summary>
        public bool SpeechForceLowercase
        {
            get => _speechForceLowercase;
            set
            {
                _speechForceLowercase = value;
                if (value)
                    _speechTitleCaseAllCaps = false;
            }
        }

        // -----------------------------------------------------------------------
        // Speech text rules — [SPEECH_TEXT_RULES] (pipeline regex, formerly hard-coded)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Ordered pipeline text rules (noise strip / abbrev / decorators).
        /// Defaults from <see cref="SpeechTextRulesCatalog"/>; profile-backed.
        /// </summary>
        public List<SpeechTextRule> SpeechTextRules { get; private set; } =
            SpeechTextRulesCatalog.CreateDefaults();

        /// <summary>True when TTS should use SAPI 5 (<see cref="System.Speech.Synthesis"/>).</summary>
        public bool IsSapiTtsEngine =>
            string.Equals(NormalizeTtsEngine(TtsEngine), "Sapi", StringComparison.Ordinal);

        /// <summary>Clamp voice option numbers to supported ranges.</summary>
        public void NormalizeVoiceSettings()
        {
            VoiceSpeakingRate = Math.Clamp(VoiceSpeakingRate, 0.5, 6.0);
            VoicePitch = Math.Clamp(VoicePitch, 0.0, 2.0);
            VoiceVolume = Math.Clamp(VoiceVolume, 0.0, 1.0);
            VoiceId = (VoiceId ?? "").Trim();
            SapiVoiceName = (SapiVoiceName ?? "").Trim();
            TtsEngine = NormalizeTtsEngine(TtsEngine);
            VoiceAppendedSilence = NormalizeSilenceName(VoiceAppendedSilence);
            VoicePunctuationSilence = NormalizeSilenceName(VoicePunctuationSilence);
            VoiceCommaPauseMs = Math.Clamp(VoiceCommaPauseMs, MinSpeakPauseMs, MaxSpeakPauseMs);
            VoiceSentencePauseMs = Math.Clamp(VoiceSentencePauseMs, MinSpeakPauseMs, MaxSpeakPauseMs);
            VoiceOtherPauseMs = Math.Clamp(VoiceOtherPauseMs, MinSpeakPauseMs, MaxSpeakPauseMs);
            VoiceBubblePauseMs = Math.Clamp(VoiceBubblePauseMs, MinSpeakPauseMs, MaxSpeakPauseMs);
            // VoiceUseCustomPauseEncodings is a plain bool — no clamp needed.
        }

        /// <summary>Canonical engine name: Windows | Sapi.</summary>
        public static string NormalizeTtsEngine(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "Windows";
            string t = raw.Trim();
            if (t.Equals("Sapi", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("SAPI", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Sapi5", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("SAPI5", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("System.Speech", StringComparison.OrdinalIgnoreCase))
                return "Sapi";
            return "Windows";
        }

        private static string NormalizeSilenceName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "Default";
            if (raw.Equals("Min", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("Minimum", StringComparison.OrdinalIgnoreCase))
                return "Min";
            return "Default";
        }

        // -----------------------------------------------------------------------
        // Mouse-follow capture (↑ on overlay) — [FOLLOW] section
        // -----------------------------------------------------------------------

        public const int DefaultFollowWidth = 600;
        public const int DefaultFollowHeight = 100;
        public const int DefaultFollowOffsetX = 20;
        public const int DefaultFollowOffsetY = -50;

        /// <summary>Width of the mouse-follow capture region (pixels).</summary>
        public int FollowWidth { get; set; } = DefaultFollowWidth;

        /// <summary>Height of the mouse-follow capture region (pixels).</summary>
        public int FollowHeight { get; set; } = DefaultFollowHeight;

        /// <summary>
        /// Shape of the follow region: <c>Rectangle</c> or <c>Ellipse</c> (oval).
        /// Lasso is not supported for fixed mouse-follow boxes.
        /// </summary>
        public string FollowShape { get; set; } = "Rectangle";

        /// <summary>X offset of the region from the cursor (positive = right).</summary>
        public int FollowOffsetX { get; set; } = DefaultFollowOffsetX;

        /// <summary>Y offset of the region from the cursor (positive = down).</summary>
        public int FollowOffsetY { get; set; } = DefaultFollowOffsetY;

        /// <summary>True when follow shape is oval/ellipse.</summary>
        public bool FollowIsEllipse =>
            FollowShape.Equals("Ellipse", StringComparison.OrdinalIgnoreCase) ||
            FollowShape.Equals("Oval", StringComparison.OrdinalIgnoreCase);

        public void NormalizeFollowSettings()
        {
            FollowWidth = Math.Clamp(FollowWidth, 40, 4000);
            FollowHeight = Math.Clamp(FollowHeight, 20, 3000);
            FollowOffsetX = Math.Clamp(FollowOffsetX, -2000, 2000);
            FollowOffsetY = Math.Clamp(FollowOffsetY, -2000, 2000);
            FollowShape = NormalizeFollowShape(FollowShape);
        }

        private static string NormalizeFollowShape(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "Rectangle";
            if (raw.Equals("Ellipse", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("Oval", StringComparison.OrdinalIgnoreCase))
                return "Ellipse";
            return "Rectangle";
        }

        // -----------------------------------------------------------------------
        // Saved capture regions + overlay shape (persisted under [REGIONS])
        // Profiles include these so switching profiles restores slot geometries.
        // -----------------------------------------------------------------------


        /// <summary>Eight region-slot geometries (index 0 = slot 1 / F1).</summary>
        public RegionSlotData[] RegionSlots { get; } =
            Enumerable.Range(0, 8).Select(_ => new RegionSlotData()).ToArray();

        /// <summary>Active region slot index 0..7 (F1..F8).</summary>
        public int ActiveRegionSlot { get; set; } = 0;

        /// <summary>Overlay shape tool: Rectangle, Ellipse, or Lasso.</summary>
        public string ShapeMode { get; set; } = "Rectangle";

        // -----------------------------------------------------------------------
        // Hotkeys (persisted under [HOTKEYS] in the ini)
        // -----------------------------------------------------------------------

        public static readonly HotkeyChord DefaultToggleOverlay =
            new(HotkeyChord.MOD_SHIFT, Keys.Tab);
        // Mode toggles use Ctrl+letter (not Shift+letter): Shift+letter would fire
        // while typing capitals when the hotkey is registered globally.
        /// <summary>Default mode (ComicBook off) — Ctrl+D.</summary>
        public static readonly HotkeyChord DefaultToggleDefaultMode =
            new(HotkeyChord.MOD_CONTROL, Keys.D);
        /// <summary>Comic Book mode — Ctrl+B.</summary>
        public static readonly HotkeyChord DefaultToggleComicBook =
            new(HotkeyChord.MOD_CONTROL, Keys.B);
        /// <summary>Abort in-progress TTS / OCR speak — Ctrl+Shift+S.</summary>
        public static readonly HotkeyChord DefaultStopTts =
            new(HotkeyChord.MOD_CONTROL | HotkeyChord.MOD_SHIFT, Keys.S);
        public static readonly HotkeyChord DefaultShapeRect = new(0, Keys.R);
        public static readonly HotkeyChord DefaultShapeOval = new(0, Keys.O);
        public static readonly HotkeyChord DefaultShapeLasso = new(0, Keys.L);

        /// <summary>Default speak hotkey for Follow / region 9 (mouse-float box).</summary>
        public static readonly HotkeyChord DefaultFollowRegion =
            new(HotkeyChord.MOD_SHIFT, Keys.F9);

        public static HotkeyChord DefaultRegion(int index) =>
            new(HotkeyChord.MOD_SHIFT, Keys.F1 + Math.Clamp(index, 0, 7));

        /// <summary>Show / hide overlay (and stop OCR when hiding).</summary>
        public HotkeyChord HotkeyToggleOverlay { get; set; } = DefaultToggleOverlay;

        /// <summary>Eight saved-region slots (logical F1..F8). Global RegisterHotKey.</summary>
        public HotkeyChord[] HotkeyRegions { get; private set; } = CreateDefaultRegions();

        /// <summary>
        /// Region 9 — Follow: mouse-relative capture (size/shape from [FOLLOW]).
        /// Speaks at the cursor; does not store a fixed slot geometry.
        /// </summary>
        public HotkeyChord HotkeyFollowRegion { get; set; } = DefaultFollowRegion;

        /// <summary>Toggle Default mode (ComicBook off / simple read).</summary>
        public HotkeyChord HotkeyToggleDefaultMode { get; set; } = DefaultToggleDefaultMode;

        /// <summary>Toggle ComicBook mode flag.</summary>
        public HotkeyChord HotkeyToggleComicBook { get; set; } = DefaultToggleComicBook;

        /// <summary>
        /// Abort in-progress TTS (region speak, Follow, Balloons refine, short announcements).
        /// Global <c>RegisterHotKey</c>.
        /// </summary>
        public HotkeyChord HotkeyStopTts { get; set; } = DefaultStopTts;

        /// <summary>Overlay-local: rectangle shape (not registered globally).</summary>
        public HotkeyChord HotkeyShapeRect { get; set; } = DefaultShapeRect;

        /// <summary>Overlay-local: oval shape.</summary>
        public HotkeyChord HotkeyShapeOval { get; set; } = DefaultShapeOval;

        /// <summary>Overlay-local: lasso shape.</summary>
        public HotkeyChord HotkeyShapeLasso { get; set; } = DefaultShapeLasso;

        // ------------------------------------------------------------------
        // Gamepad (XInput) — all default empty; opt-in only. Pad* ini keys
        // so they never collide with [HOTKEYS] when the flat ini map is read.
        // ------------------------------------------------------------------

        /// <summary>XInput user index 0..3 (default 0).</summary>
        public int GamepadControllerIndex { get; set; } = 0;

        public GamepadButton PadToggleOverlay { get; set; }
        public GamepadButton PadToggleDefaultMode { get; set; }
        public GamepadButton PadToggleComicBook { get; set; }
        /// <summary>Gamepad binding for abort TTS.</summary>
        public GamepadButton PadStopTts { get; set; }
        public GamepadButton[] PadRegions { get; private set; } = CreateEmptyPadRegions();
        /// <summary>Gamepad binding for Follow / region 9 speak.</summary>
        public GamepadButton PadFollowRegion { get; set; }
        public GamepadButton PadShapeRect { get; set; }
        public GamepadButton PadShapeOval { get; set; }
        public GamepadButton PadShapeLasso { get; set; }

        /// <summary>
        /// User-defined global bindings (mouse, keys, window, media…).
        /// Not SpeakRect OCR actions — see <see cref="CustomHotkeyBinding"/>.
        /// </summary>
        public List<CustomHotkeyBinding> CustomHotkeys { get; private set; } = new();

        private static HotkeyChord[] CreateDefaultRegions()
        {
            var a = new HotkeyChord[8];
            for (int i = 0; i < 8; i++)
                a[i] = DefaultRegion(i);
            return a;
        }

        private static GamepadButton[] CreateEmptyPadRegions() => new GamepadButton[8];

        public void ResetHotkeysToDefaults()
        {
            HotkeyToggleOverlay = DefaultToggleOverlay;
            HotkeyRegions = CreateDefaultRegions();
            HotkeyFollowRegion = DefaultFollowRegion;
            HotkeyToggleDefaultMode = DefaultToggleDefaultMode;
            HotkeyToggleComicBook = DefaultToggleComicBook;
            HotkeyStopTts = DefaultStopTts;
            HotkeyShapeRect = DefaultShapeRect;
            HotkeyShapeOval = DefaultShapeOval;
            HotkeyShapeLasso = DefaultShapeLasso;
        }

        /// <summary>Clear every gamepad binding (factory default: nothing mapped).</summary>
        public void ResetGamepadToDefaults()
        {
            GamepadControllerIndex = 0;
            PadToggleOverlay = default;
            PadToggleDefaultMode = default;
            PadToggleComicBook = default;
            PadStopTts = default;
            PadRegions = CreateEmptyPadRegions();
            PadFollowRegion = default;
            PadShapeRect = default;
            PadShapeOval = default;
            PadShapeLasso = default;
        }

        /// <summary>Remove all user custom global hotkeys.</summary>
        public void ClearCustomHotkeys() => CustomHotkeys.Clear();

        /// <summary>Remove all user speech substitution rules.</summary>
        public void ClearSpeechRules() => SpeechRules.Clear();

        /// <summary>
        /// Replace the in-memory speech rule list (clamped to max). Used by the Speech tab.
        /// Does not persist — call <see cref="PersistSpeechRules"/> or <see cref="Save"/>.
        /// </summary>
        public void SetSpeechRules(IEnumerable<SpeechRule>? rules)
        {
            SpeechRules.Clear();
            if (rules == null)
                return;
            foreach (var r in rules)
            {
                if (SpeechRules.Count >= SpeechRule.MaxRules)
                    break;
                if (r == null)
                    continue;
                if (!SpeechRule.TryNormalize(
                        r.Match, r.Replace, r.Kind, r.Enabled,
                        out SpeechRule clean, out _))
                    continue;
                SpeechRules.Add(clean);
            }
        }

        /// <summary>
        /// Replace pipeline text rules (clamped / validated). Used by the Speech tab.
        /// Does not persist — call <see cref="PersistSpeechRules"/> or <see cref="Save"/>.
        /// </summary>
        public void SetSpeechTextRules(IEnumerable<SpeechTextRule>? rules)
        {
            // Null → defaults. Empty sequence after filter also → defaults (never
            // leave CleanForSpeech with zero pipeline rules unless user Reset… no:
            // empty means “use catalog”; a deliberate partial list is non-empty).
            var list = rules?.Where(r => r != null).ToList();
            SpeechTextRules = list == null || list.Count == 0
                ? SpeechTextRulesCatalog.CreateDefaults()
                : SpeechTextRulesCatalog.MergeWithDefaults(list);
            SpeechTextRulesEngine.ClearCache();
        }

        /// <summary>Restore shipped pipeline text rules (abbrevs, noise regex, decorators).</summary>
        public void ResetSpeechTextRulesToDefaults()
        {
            SpeechTextRules = SpeechTextRulesCatalog.CreateDefaults();
            SpeechTextRulesEngine.ClearCache();
        }

        /// <summary>
        /// Persist speech rules (names + pipeline text) and prompts to SpeakRect.ini
        /// and the active named profile when present.
        /// </summary>
        public void PersistSpeechRules()
        {
            Save();
            SyncActiveProfileFile();
        }

        /// <summary>Clear custom OCR prompt so resolve uses the hard-coded default.</summary>
        public void ResetPromptsToDefaults()
        {
            OcrPrompt = "";
        }

        /// <summary>Set the sole OCR prompt (blank → use built-in default at resolve time).</summary>
        public void SetOcrPrompt(string? value)
        {
            OcrPrompt = PromptForIni(value, DefaultOcrPrompt);
        }

        /// <summary>Resolved OCR prompt text for UI / VL (never blank).</summary>
        public string ResolveOcrPrompt() =>
            NonEmpty(OcrPrompt) ?? DefaultOcrPrompt;

        /// <summary>True when the stored value is blank (using built-in default).</summary>
        public bool IsOcrPromptUsingDefault() =>
            string.IsNullOrWhiteSpace(OcrPrompt);

        /// <summary>True if any action has a non-empty gamepad binding.</summary>
        public bool HasAnyGamepadBinding()
        {
            if (!PadToggleOverlay.IsEmpty || !PadToggleDefaultMode.IsEmpty ||
                !PadToggleComicBook.IsEmpty ||
                !PadStopTts.IsEmpty ||
                !PadFollowRegion.IsEmpty ||
                !PadShapeRect.IsEmpty || !PadShapeOval.IsEmpty || !PadShapeLasso.IsEmpty)
                return true;
            for (int i = 0; i < PadRegions.Length; i++)
            {
                if (!PadRegions[i].IsEmpty)
                    return true;
            }
            foreach (var c in CustomHotkeys)
            {
                if (!c.Gamepad.IsEmpty || c.UsesAnalogStick)
                    return true;
            }
            return false;
        }

        /// <summary>Allocate next free CustomN id.</summary>
        public string NextCustomHotkeyId()
        {
            var used = new HashSet<string>(
                CustomHotkeys.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < CustomHotkeyBinding.MaxBindings * 4; i++)
            {
                string id = $"Custom{i}";
                if (!used.Contains(id))
                    return id;
            }
            return $"Custom{Guid.NewGuid():N}"[..16];
        }

        /// <summary>
        /// Add a custom binding (clamped to max). Returns the instance or null if full.
        /// </summary>
        public CustomHotkeyBinding? AddCustomHotkey(CustomHotkeyBinding binding)
        {
            if (CustomHotkeys.Count >= CustomHotkeyBinding.MaxBindings)
                return null;
            if (string.IsNullOrWhiteSpace(binding.Id))
                binding.Id = NextCustomHotkeyId();
            if (binding.UsesAnalogStick)
            {
                // Stick-mouse does not need a discrete button capture
                binding.Gamepad = default;
            }
            CustomHotkeys.Add(binding);
            return binding;
        }

        public bool RemoveCustomHotkey(string id)
        {
            int n = CustomHotkeys.RemoveAll(
                c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return n > 0;
        }

        public CustomHotkeyBinding? FindCustomHotkey(string id) =>
            CustomHotkeys.FirstOrDefault(
                c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Named hotkey rows for the map UI / ini. <paramref name="isGlobal"/> means
        /// <c>RegisterHotKey</c>; local keys only work while the overlay is focused.
        /// Gamepad getters/setters are optional extras (default empty).
        /// </summary>
        public readonly record struct HotkeyMapRow(
            string Id,
            string Label,
            string Group,
            bool IsGlobal,
            Func<AppSettings, HotkeyChord> Getter,
            Action<AppSettings, HotkeyChord> Setter,
            Func<AppSettings, GamepadButton> GamepadGetter,
            Action<AppSettings, GamepadButton> GamepadSetter);

        public static readonly HotkeyMapRow[] HotkeyMapRows =
        {
            new("ToggleOverlay", "Show / hide overlay", "Global", true,
                s => s.HotkeyToggleOverlay, (s, v) => s.HotkeyToggleOverlay = v,
                s => s.PadToggleOverlay, (s, v) => s.PadToggleOverlay = v),
            new("ToggleDefaultMode", "Default mode", "Global", true,
                s => s.HotkeyToggleDefaultMode, (s, v) => s.HotkeyToggleDefaultMode = v,
                s => s.PadToggleDefaultMode, (s, v) => s.PadToggleDefaultMode = v),
            new("ToggleComicBook", "Comic Book mode", "Global", true,
                s => s.HotkeyToggleComicBook, (s, v) => s.HotkeyToggleComicBook = v,
                s => s.PadToggleComicBook, (s, v) => s.PadToggleComicBook = v),
            new("StopTts", "Stop speech (abort TTS)", "Global", true,
                s => s.HotkeyStopTts, (s, v) => s.HotkeyStopTts = v,
                s => s.PadStopTts, (s, v) => s.PadStopTts = v),
            new("Region1", "Region slot 1", "Regions", true,
                s => s.HotkeyRegions[0], (s, v) => s.HotkeyRegions[0] = v,
                s => s.PadRegions[0], (s, v) => s.PadRegions[0] = v),
            new("Region2", "Region slot 2", "Regions", true,
                s => s.HotkeyRegions[1], (s, v) => s.HotkeyRegions[1] = v,
                s => s.PadRegions[1], (s, v) => s.PadRegions[1] = v),
            new("Region3", "Region slot 3", "Regions", true,
                s => s.HotkeyRegions[2], (s, v) => s.HotkeyRegions[2] = v,
                s => s.PadRegions[2], (s, v) => s.PadRegions[2] = v),
            new("Region4", "Region slot 4", "Regions", true,
                s => s.HotkeyRegions[3], (s, v) => s.HotkeyRegions[3] = v,
                s => s.PadRegions[3], (s, v) => s.PadRegions[3] = v),
            new("Region5", "Region slot 5", "Regions", true,
                s => s.HotkeyRegions[4], (s, v) => s.HotkeyRegions[4] = v,
                s => s.PadRegions[4], (s, v) => s.PadRegions[4] = v),
            new("Region6", "Region slot 6", "Regions", true,
                s => s.HotkeyRegions[5], (s, v) => s.HotkeyRegions[5] = v,
                s => s.PadRegions[5], (s, v) => s.PadRegions[5] = v),
            new("Region7", "Region slot 7", "Regions", true,
                s => s.HotkeyRegions[6], (s, v) => s.HotkeyRegions[6] = v,
                s => s.PadRegions[6], (s, v) => s.PadRegions[6] = v),
            new("Region8", "Region slot 8", "Regions", true,
                s => s.HotkeyRegions[7], (s, v) => s.HotkeyRegions[7] = v,
                s => s.PadRegions[7], (s, v) => s.PadRegions[7] = v),
            new("Region9", "Region slot 9 (Follow — speak at mouse)", "Regions", true,
                s => s.HotkeyFollowRegion, (s, v) => s.HotkeyFollowRegion = v,
                s => s.PadFollowRegion, (s, v) => s.PadFollowRegion = v),
            new("ShapeRect", "Shape: Rectangle", "Overlay", false,
                s => s.HotkeyShapeRect, (s, v) => s.HotkeyShapeRect = v,
                s => s.PadShapeRect, (s, v) => s.PadShapeRect = v),
            new("ShapeOval", "Shape: Oval", "Overlay", false,
                s => s.HotkeyShapeOval, (s, v) => s.HotkeyShapeOval = v,
                s => s.PadShapeOval, (s, v) => s.PadShapeOval = v),
            new("ShapeLasso", "Shape: Lasso", "Overlay", false,
                s => s.HotkeyShapeLasso, (s, v) => s.HotkeyShapeLasso = v,
                s => s.PadShapeLasso, (s, v) => s.PadShapeLasso = v),
        };

        /// <summary>
        /// If <paramref name="chord"/> collides with another row (same mods+key),
        /// returns that row's label; otherwise null.
        /// </summary>
        public string? FindHotkeyConflict(string rowId, HotkeyChord chord)
        {
            // Empty is allowed (user may unbind a hotkey entirely).
            if (chord.IsEmpty)
                return null;

            foreach (var row in HotkeyMapRows)
            {
                if (row.Id == rowId)
                    continue;
                if (row.Getter(this) == chord)
                    return row.Label;
            }
            foreach (var c in CustomHotkeys)
            {
                if (c.Id.Equals(rowId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (c.Keyboard == chord)
                    return c.DisplayLabel;
            }
            return null;
        }

        /// <summary>
        /// If <paramref name="button"/> collides with another gamepad row,
        /// returns that row's label; empty is allowed (unbound).
        /// </summary>
        public string? FindGamepadConflict(string rowId, GamepadButton button)
        {
            if (button.IsEmpty)
                return null;

            foreach (var row in HotkeyMapRows)
            {
                if (row.Id == rowId)
                    continue;
                if (row.GamepadGetter(this) == button)
                    return row.Label;
            }
            foreach (var c in CustomHotkeys)
            {
                if (c.Id.Equals(rowId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (c.UsesAnalogStick)
                    continue; // stick-mouse uses whole stick, not a discrete button
                if (c.Gamepad == button)
                    return c.DisplayLabel;
            }
            return null;
        }

        /// <summary>
        /// Prompts that may appear in model output (active + hard-coded default)
        /// so echo stripping still works if the user customized the ini.
        /// </summary>
        public IEnumerable<string> AllKnownPrompts()
        {
            yield return ResolveOcrPrompt();
            yield return DefaultOcrPrompt;
        }

        /// <summary>Overlay MODE rows: Default ↔ Comic Book.</summary>
        public readonly record struct FlagItem(
            string Section,
            string Label,
            Func<AppSettings, bool> Getter,
            Action<AppSettings, bool> Setter);

        /// <summary>
        /// Overlay MODE stack order. <b>DEFAULT</b> is lit when ComicBook is off
        /// (simple read). <b>COMIC BOOK</b> lights when ComicBook is on.
        /// Indices: 0=DEFAULT, 1=COMIC BOOK.
        /// </summary>
        public static readonly FlagItem[] Flags =
        {
            new(
                "MODE",
                "DEFAULT",
                // Default mode = not Comic Book (mutually exclusive on the overlay).
                s => !s.ComicBook,
                // Setter unused — SetFlag handles DEFAULT specially.
                (s, v) => { if (v) s.ComicBook = false; }),
            new(
                "MODE",
                "COMIC BOOK",
                s => s.ComicBook,
                (s, v) => s.ComicBook = v),
        };

        public const int FlagIndexDefault = 0;
        public const int FlagIndexComicBook = 1;

        public bool GetFlag(int index) => Flags[index].Getter(this);

        public void SetFlag(int index, bool value)
        {
            if (index < 0 || index >= Flags.Length)
                return;

            string label = Flags[index].Label;
            bool wasComic = ComicBook;

            if (label == "DEFAULT")
            {
                // DEFAULT on ⇒ simple read (Comic off).
                // DEFAULT off (toggle) ⇒ Comic Book on.
                ComicBook = !value;
            }
            else if (label == "COMIC BOOK")
            {
                ComicBook = value;
            }
            else
            {
                Flags[index].Setter(this, value);
            }

            // MODE change: leave → suspend comic-only features; enter → restore stash
            // or apply Comic Book's own starting defaults (not product Default mode).
            // Never force ComicBook back on just because POI was on.
            bool enteredComic = ComicBook && !wasComic;
            bool leftComic = !ComicBook && wasComic;

            if (leftComic)
            {
                NormalizeModeFlags(); // stash + turn off POI etc.
            }
            else if (enteredComic)
            {
                if (_comicOnlyStashValid)
                {
                    // Re-enter after a leave this session — restore last comic prefs.
                    NormalizeModeFlags();
                }
                else
                {
                    // Fresh enter (no session stash): Comic Book starting point.
                    ApplyComicBookModeStartingDefaults();
                }
            }
            else
            {
                NormalizeModeFlags();
            }

            // Live config + active named profile (if any) so ComicBook sticks.
            Save();
            SyncActiveProfileFile();
        }

        public void ToggleFlag(int index) => SetFlag(index, !GetFlag(index));

        // -------------------------------------------------------------------
        // Comic-only features suspended in Default mode, restored on re-entry.
        // In-memory only (session); saved ini/profile reflects active values.
        // -------------------------------------------------------------------
        private bool _comicOnlyStashValid;
        private bool _stashedPoiMarkers;
        private bool _stashedPoiFogOutside;
        private bool _stashedPoiAutoStack;

        /// <summary>
        /// Comic Book mode's own starting feature set when the user first enables
        /// Comic Book via MODE (no session stash). Profile/ini load does not call
        /// this — saved profiles keep the user's values.
        /// Same POI/Comic Book stock as
        /// <see cref="ResetComicRegionSettingsToDefaults"/>(asComicBookMode: true)
        /// for feature flags (POI on); does not force every detect numeric knob so
        /// a MODE toggle does not wipe user-tuned cluster/pad values.
        /// </summary>
        public void ApplyComicBookModeStartingDefaults()
        {
            // Comic Book attack path — POI on by default for a fresh MODE enter.
            ComicBook = true;
            ComicPoiMarkers = true;
            ComicPoiFogOutside = true;
            ComicPoiAutoStack = true;
            ComicPoiAutoStackGapPx = ComicPoiGuide.DefaultAutoStackGapPx;
            ComicPoiAutoStackMarginPx = ComicPoiGuide.LlmSendStackMarginPx;
            ComicPoiStackBeefExtra = ComicPoiGuide.DefaultStackBeefExtra;
            ComicPoiStackBottomPadShare = ComicPoiGuide.DefaultStackBottomPadShare;
            ImageLlmSendDownscale = false;
            ImageLlmSendMaxLongEdge = DefaultImageLlmSendMaxLongEdge;
            // Detect / island pipeline stock on-state (booleans only).
            ComicDetectFog = true;
            ComicDetectFogAmount = DefaultComicDetectFogAmount;
            ComicDynamicFog = true;
            ComicDynamicFogMin = DefaultComicDynamicFogMin;
            ComicDynamicFogMax = DefaultComicDynamicFogMax;
            ComicMergeOverlappingIslands = true;
            ComicSplitLargeRegions = true;
            ComicSequentialRegions = true;
            ClearComicOnlyModeStash();
            NormalizeComicRegionSettings();
        }

        /// <summary>
        /// Keep MODE coherent with features that only run in Comic Book.
        /// <list type="bullet">
        /// <item>Default (ComicBook off): stash + turn off comic-only features
        /// (POI, …) so the user can leave Comic Book even when POI was on.</item>
        /// <item>Comic Book on + session stash: restore what Default suspended.</item>
        /// <item>Comic Book on + no stash: do not invent features here — use
        /// <see cref="ApplyComicBookModeStartingDefaults"/> on user MODE enter
        /// (SetFlag), or leave values as loaded from profile/ini.</item>
        /// </list>
        /// </summary>
        public void NormalizeModeFlags()
        {
            if (!ComicBook)
            {
                // User chose Default — suspend anything that requires Comic Book.
                if (ComicPoiMarkers)
                {
                    _stashedPoiMarkers = true;
                    _stashedPoiFogOutside = ComicPoiFogOutside;
                    _stashedPoiAutoStack = ComicPoiAutoStack;
                    _comicOnlyStashValid = true;
                    ComicPoiMarkers = false;
                }
                // If POI was already off but a prior leave stashed it, keep stash;
                // stay off until Comic Book is selected again.
                return;
            }

            // Comic Book on — re-enable what Default suspended (if any).
            if (_comicOnlyStashValid)
            {
                // If the user already turned POI on (Balloons checkbox → force Comic),
                // keep their choice; only restore when POI is still off.
                if (!ComicPoiMarkers)
                {
                    ComicPoiMarkers = _stashedPoiMarkers;
                    ComicPoiFogOutside = _stashedPoiFogOutside;
                    ComicPoiAutoStack = _stashedPoiAutoStack;
                }
                _comicOnlyStashValid = false;
            }
        }

        /// <summary>Clear session stash (defaults / explicit resets).</summary>
        private void ClearComicOnlyModeStash()
        {
            _comicOnlyStashValid = false;
            _stashedPoiMarkers = false;
            _stashedPoiFogOutside = false;
            _stashedPoiAutoStack = false;
        }

        /// <summary>
        /// When the active named profile already exists on disk, rewrite it so
        /// mode flags (and the rest of the in-memory snapshot) stay in sync after
        /// toggles — without requiring an explicit Profile → Save.
        /// </summary>
        public void SyncActiveProfileFile()
        {
            try
            {
                if (!TryNormalizeProfileName(ActiveProfileName, out string clean, out _))
                    return;
                string path = GetProfilePath(clean);
                if (!File.Exists(path))
                    return;
                SaveTo(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] SyncActiveProfileFile failed: {ex.Message}");
            }
        }

        public void Load()
        {
            ResetToBuiltInDefaults();

            if (!File.Exists(IniPath))
            {
                Save();
                Debug.WriteLine($"[Settings] wrote defaults → {IniPath}");
                return;
            }

            try
            {
                ApplyFromMap(ReadIni(IniPath));
                // Rewrite so the file always has the full shape + comments
                Save();
                Debug.WriteLine(
                    $"[Settings] loaded {IniPath} profile={ActiveProfileName} " +
                    $"(ComicBook={ComicBook})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] load failed: {ex.Message}");
            }
        }

        private void ResetToBuiltInDefaults()
        {
            OcrPrompt = "";
            TtsEngine = "Windows";
            VoiceId = "";
            SapiVoiceName = "";
            VoiceSpeakingRate = 1.0;
            VoicePitch = 1.0;
            VoiceVolume = 1.0;
            VoiceAppendedSilence = "Default";
            VoicePunctuationSilence = "Default";
            VoiceCommaPauseMs = DefaultCommaPauseMs;
            VoiceSentencePauseMs = DefaultSentencePauseMs;
            VoiceOtherPauseMs = DefaultOtherPauseMs;
            VoiceBubblePauseMs = DefaultBubblePauseMs;
            VoiceUseCustomPauseEncodings = true;
            // Comic knobs + MODE: Default mode (ComicBook off, POI off).
            ResetComicRegionSettingsToDefaults();
            ResetImagePrepSettingsToDefaults();
            FollowWidth = DefaultFollowWidth;
            FollowHeight = DefaultFollowHeight;
            FollowShape = "Rectangle";
            FollowOffsetX = DefaultFollowOffsetX;
            FollowOffsetY = DefaultFollowOffsetY;
            ActiveProfileName = "Default";
            LastSettingsTab = "Help";
            ActiveRegionSlot = 0;
            ShapeMode = "Rectangle";
            ClearRegionSlots();
            ResetHotkeysToDefaults();
            ResetGamepadToDefaults();
            ClearCustomHotkeys();
            ClearSpeechRules();
            SpeechTitleCaseAllCaps = false;
            SpeechForceLowercase = true;
            ResetSpeechTextRulesToDefaults();
            ResetPromptsToDefaults();
        }

        /// <summary>
        /// Factory-restore every product setting (mode, image prep, voice, speech
        /// rules/names, prompts, hotkeys, gamepad, custom actions, follow, regions).
        /// Keeps the active profile name and last Settings tab so the current profile
        /// file is rewritten on save. Writes main ini + active profile when present.
        /// </summary>
        public void RestoreAllBuiltInDefaults()
        {
            string keepProfile = ActiveProfileName ?? "Default";
            string keepTab = LastSettingsTab ?? "Help";
            ClearComicOnlyModeStash();
            ResetToBuiltInDefaults();
            if (TryNormalizeProfileName(keepProfile, out string cleanProfile, out _))
                ActiveProfileName = cleanProfile;
            LastSettingsTab = string.IsNullOrWhiteSpace(keepTab) ? "Help" : keepTab.Trim();
            NormalizeModeFlags();
            NormalizeFollowSettings();
            NormalizeVoiceSettings();
            NormalizeComicRegionSettings();
            NormalizeImagePrepSettings();
            try { Save(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] RestoreAllBuiltInDefaults Save: {ex.Message}");
            }
            SyncActiveProfileFile();
        }

        private void ClearRegionSlots()
        {
            foreach (var slot in RegionSlots)
                slot.Clear();
        }

        private void ApplyFromMap(Dictionary<string, string> map)
        {
            // Profile/ini load must not keep a session mode-stash from before the load.
            ClearComicOnlyModeStash();

            if (map.TryGetValue("ActiveProfile", out string? profileRaw) &&
                !string.IsNullOrWhiteSpace(profileRaw))
            {
                ActiveProfileName = profileRaw.Trim();
            }

            if (map.TryGetValue("LastSettingsTab", out string? lastTabRaw) &&
                !string.IsNullOrWhiteSpace(lastTabRaw))
            {
                LastSettingsTab = lastTabRaw.Trim();
            }

            if (map.TryGetValue("ComicBook", out string? comicRaw) &&
                TryParseBool(comicRaw, out bool comic))
            {
                ComicBook = comic;
            }
            // One-time migration from older keys
            else if (map.TryGetValue("UseWinOcr", out string? useRaw) &&
                     TryParseBool(useRaw, out bool useWin))
            {
                ComicBook = useWin;
            }
            else if (map.TryGetValue("SkipWinOcrSendFullFrameOnly", out string? skipRaw) &&
                     TryParseBool(skipRaw, out bool skip))
            {
                ComicBook = !skip;
            }
            // Legacy FastComic / FasterComic keys are ignored (speed pipes removed).
            //
            // Do NOT NormalizeModeFlags here — POI keys load later in
            // LoadComicRegionSettingsFromMap. Early normalize after Reset defaults
            // (POI=true) + ComicBook=false would stash a fake "POI was on" and
            // re-enable POI on next Comic Book entry even when the file said off.

            // Single OCR prompt. Prefer OcrPrompt; else migrate SimplePrompt
            // (closest historical default). All multi-prompt stock strings clear
            // so ResolveOcrPrompt picks up the new built-in.
            string ocrStored = ReadPrompt(map, "OcrPrompt");
            if (string.IsNullOrWhiteSpace(ocrStored))
                ocrStored = ReadPrompt(map, "SimplePrompt");
            if (string.IsNullOrWhiteSpace(ocrStored))
                ocrStored = ReadPrompt(map, "FullPrompt");
            foreach (string legacy in LegacyOcrPromptDefaults)
                ocrStored = MigratePromptIfLegacy(ocrStored, legacy);
            OcrPrompt = PromptForIni(ocrStored, DefaultOcrPrompt);

            LoadHotkeysFromMap(map);
            LoadGamepadFromMap(map);
            LoadCustomHotkeysFromMap(map);
            LoadRegionsFromMap(map);
            LoadVoiceFromMap(map);
            LoadComicRegionSettingsFromMap(map);
            LoadImagePrepSettingsFromMap(map);
            LoadSpeechRulesFromMap(map);
            LoadSpeechTextRulesFromMap(map);
            LoadFollowFromMap(map);
        }

        private void LoadFollowFromMap(Dictionary<string, string> map)
        {
            if (map.TryGetValue("FollowWidth", out string? wRaw) &&
                int.TryParse(wRaw, out int w))
                FollowWidth = w;
            if (map.TryGetValue("FollowHeight", out string? hRaw) &&
                int.TryParse(hRaw, out int h))
                FollowHeight = h;
            if (map.TryGetValue("FollowShape", out string? shape) && shape != null)
                FollowShape = shape;
            if (map.TryGetValue("FollowOffsetX", out string? oxRaw) &&
                int.TryParse(oxRaw, out int ox))
                FollowOffsetX = ox;
            if (map.TryGetValue("FollowOffsetY", out string? oyRaw) &&
                int.TryParse(oyRaw, out int oy))
                FollowOffsetY = oy;
            // FollowIdleMs was for old auto-OCR-on-still-mouse; ignored if present.

            NormalizeFollowSettings();
        }

        private void LoadVoiceFromMap(Dictionary<string, string> map)
        {
            if (map.TryGetValue("TtsEngine", out string? eng) && eng != null)
                TtsEngine = eng.Trim();
            else if (map.TryGetValue("VoiceEngine", out string? eng2) && eng2 != null)
                TtsEngine = eng2.Trim();

            if (map.TryGetValue("VoiceId", out string? id) && id != null)
                VoiceId = id.Trim();

            if (map.TryGetValue("SapiVoiceName", out string? sapiName) && sapiName != null)
                SapiVoiceName = sapiName.Trim();
            else if (map.TryGetValue("SapiVoice", out string? sapiName2) && sapiName2 != null)
                SapiVoiceName = sapiName2.Trim();

            if (map.TryGetValue("SpeakingRate", out string? rateRaw) &&
                double.TryParse(rateRaw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double rate))
                VoiceSpeakingRate = rate;

            if (map.TryGetValue("Pitch", out string? pitchRaw) &&
                double.TryParse(pitchRaw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pitch))
                VoicePitch = pitch;

            if (map.TryGetValue("Volume", out string? volRaw) &&
                double.TryParse(volRaw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double vol))
                VoiceVolume = vol;

            if (map.TryGetValue("AppendedSilence", out string? appSil) && appSil != null)
                VoiceAppendedSilence = appSil;

            if (map.TryGetValue("PunctuationSilence", out string? punSil) && punSil != null)
                VoicePunctuationSilence = punSil;

            if (map.TryGetValue("CommaPauseMs", out string? commaRaw) &&
                int.TryParse(commaRaw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int commaMs))
                VoiceCommaPauseMs = commaMs;

            if (map.TryGetValue("SentencePauseMs", out string? sentRaw) &&
                int.TryParse(sentRaw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int sentMs))
                VoiceSentencePauseMs = sentMs;

            if (map.TryGetValue("OtherPauseMs", out string? otherRaw) &&
                int.TryParse(otherRaw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int otherMs))
                VoiceOtherPauseMs = otherMs;

            if (map.TryGetValue("BubblePauseMs", out string? bubbleRaw) &&
                int.TryParse(bubbleRaw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int bubbleMs))
                VoiceBubblePauseMs = bubbleMs;

            if (map.TryGetValue("UseCustomPauseEncodings", out string? encodeRaw) &&
                TryParseBool(encodeRaw, out bool useEncode))
                VoiceUseCustomPauseEncodings = useEncode;

            NormalizeVoiceSettings();
        }

        private void LoadComicRegionSettingsFromMap(Dictionary<string, string> map)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            if (map.TryGetValue("ComicDetectFog", out string? fogOnRaw) &&
                TryParseBool(fogOnRaw, out bool fogOn))
                ComicDetectFog = fogOn;

            if (map.TryGetValue("ComicDetectFogAmount", out string? fogAmtRaw) &&
                float.TryParse(fogAmtRaw, System.Globalization.NumberStyles.Float, inv, out float fogAmt))
                ComicDetectFogAmount = fogAmt;

            if (map.TryGetValue("ComicDynamicFog", out string? dynFogRaw) &&
                TryParseBool(dynFogRaw, out bool dynFog))
                ComicDynamicFog = dynFog;

            if (map.TryGetValue("ComicDynamicFogMin", out string? dynMinRaw) &&
                float.TryParse(dynMinRaw, System.Globalization.NumberStyles.Float, inv, out float dynMin))
                ComicDynamicFogMin = dynMin;

            if (map.TryGetValue("ComicDynamicFogMax", out string? dynMaxRaw) &&
                float.TryParse(dynMaxRaw, System.Globalization.NumberStyles.Float, inv, out float dynMax))
                ComicDynamicFogMax = dynMax;

            if (map.TryGetValue("ComicClusterGapX", out string? gapXRaw) &&
                double.TryParse(gapXRaw, System.Globalization.NumberStyles.Float, inv, out double gapX))
                ComicClusterGapX = gapX;

            if (map.TryGetValue("ComicClusterGapY", out string? gapYRaw) &&
                double.TryParse(gapYRaw, System.Globalization.NumberStyles.Float, inv, out double gapY))
                ComicClusterGapY = gapY;

            if (map.TryGetValue("ComicInflateFracX", out string? infXRaw) &&
                double.TryParse(infXRaw, System.Globalization.NumberStyles.Float, inv, out double infX))
                ComicInflateFracX = infX;

            if (map.TryGetValue("ComicInflateFracY", out string? infYRaw) &&
                double.TryParse(infYRaw, System.Globalization.NumberStyles.Float, inv, out double infY))
                ComicInflateFracY = infY;

            if (map.TryGetValue("ComicRegionPadding", out string? padRaw) &&
                int.TryParse(padRaw, System.Globalization.NumberStyles.Integer, inv, out int pad))
                ComicRegionPadding = pad;

            if (map.TryGetValue("ComicDenseIslandCount", out string? denseRaw) &&
                int.TryParse(denseRaw, System.Globalization.NumberStyles.Integer, inv, out int dense))
                ComicDenseIslandCount = dense;

            if (map.TryGetValue("ComicSplitLargeRegions", out string? splitRaw) &&
                TryParseBool(splitRaw, out bool split))
                ComicSplitLargeRegions = split;

            if (map.TryGetValue("ComicMergeOverlappingIslands", out string? mergeRaw) &&
                TryParseBool(mergeRaw, out bool merge))
                ComicMergeOverlappingIslands = merge;

            if (map.TryGetValue("ComicOrphanRecoverPasses", out string? orphanRaw) &&
                int.TryParse(orphanRaw, System.Globalization.NumberStyles.Integer, inv, out int orphan))
                ComicOrphanRecoverPasses = orphan;

            if (map.TryGetValue("ComicMinIslandAlnum", out string? minAlnumRaw) &&
                int.TryParse(minAlnumRaw, System.Globalization.NumberStyles.Integer, inv, out int minAlnum))
                ComicMinIslandAlnum = minAlnum;

            if (map.TryGetValue("ComicSequentialRegions", out string? seqRaw) &&
                TryParseBool(seqRaw, out bool seq))
                ComicSequentialRegions = seq;

            if (map.TryGetValue("ComicPoiMarkers", out string? poiRaw) &&
                TryParseBool(poiRaw, out bool poi))
                ComicPoiMarkers = poi;

            if (map.TryGetValue("ComicPoiFogOutside", out string? poiFogRaw) &&
                TryParseBool(poiFogRaw, out bool poiFog))
                ComicPoiFogOutside = poiFog;

            if (map.TryGetValue("ComicPoiAutoStack", out string? poiStackRaw) &&
                TryParseBool(poiStackRaw, out bool poiStack))
                ComicPoiAutoStack = poiStack;

            if (map.TryGetValue("ComicPoiAutoStackGapPx", out string? poiGapRaw) &&
                int.TryParse(poiGapRaw, System.Globalization.NumberStyles.Integer, inv, out int poiGap))
                ComicPoiAutoStackGapPx = poiGap;

            if (map.TryGetValue("ComicPoiAutoStackMarginPx", out string? poiMarRaw) &&
                int.TryParse(poiMarRaw, System.Globalization.NumberStyles.Integer, inv, out int poiMar))
                ComicPoiAutoStackMarginPx = poiMar;

            if (map.TryGetValue("ComicPoiStackBeefExtra", out string? beefRaw) &&
                double.TryParse(beefRaw, System.Globalization.NumberStyles.Float, inv, out double beef))
                ComicPoiStackBeefExtra = beef;

            if (map.TryGetValue("ComicPoiStackBottomPadShare", out string? botRaw) &&
                double.TryParse(botRaw, System.Globalization.NumberStyles.Float, inv, out double botShare))
                ComicPoiStackBottomPadShare = botShare;

            NormalizeComicRegionSettings();
            // After POI keys load: if file has Default mode + POI on, suspend POI
            // (same as a live mode switch — do not force Comic Book on).
            NormalizeModeFlags();
        }

        private void LoadImagePrepSettingsFromMap(Dictionary<string, string> map)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            if (map.TryGetValue("ImagePrepEnabled", out string? prepOn) &&
                TryParseBool(prepOn, out bool prep))
                ImagePrepEnabled = prep;

            if (map.TryGetValue("ImageLetterbox", out string? lbOn) &&
                TryParseBool(lbOn, out bool lb))
                ImageLetterbox = lb;

            if (map.TryGetValue("ImageLetterboxPad", out string? lbPad) &&
                int.TryParse(lbPad, System.Globalization.NumberStyles.Integer, inv, out int pad))
                ImageLetterboxPad = pad;

            if (map.TryGetValue("ImageLetterboxBlack", out string? lbBlk) &&
                int.TryParse(lbBlk, System.Globalization.NumberStyles.Integer, inv, out int blk))
                ImageLetterboxBlack = blk;

            if (map.TryGetValue("ImageLetterboxWhite", out string? lbWht) &&
                int.TryParse(lbWht, System.Globalization.NumberStyles.Integer, inv, out int wht))
                ImageLetterboxWhite = wht;

            if (map.TryGetValue("ImageUpscaleLongSide", out string? up) &&
                int.TryParse(up, System.Globalization.NumberStyles.Integer, inv, out int ups))
                ImageUpscaleLongSide = ups;

            if (map.TryGetValue("ImageLlmSendDownscale", out string? llmDownRaw) &&
                TryParseBool(llmDownRaw, out bool llmDown))
                ImageLlmSendDownscale = llmDown;

            if (map.TryGetValue("ImageLlmSendMaxLongEdge", out string? llmEdgeRaw) &&
                int.TryParse(llmEdgeRaw, System.Globalization.NumberStyles.Integer, inv, out int llmEdge))
                ImageLlmSendMaxLongEdge = llmEdge;

            if (map.TryGetValue("ImageGrayscale", out string? grayOn) &&
                TryParseBool(grayOn, out bool gray))
                ImageGrayscale = gray;

            if (map.TryGetValue("ImageInkGrayWeight", out string? ink) &&
                float.TryParse(ink, System.Globalization.NumberStyles.Float, inv, out float inkW))
                ImageInkGrayWeight = inkW;

            if (map.TryGetValue("ImageDenoiseRadius", out string? denR) &&
                int.TryParse(denR, System.Globalization.NumberStyles.Integer, inv, out int dR))
                ImageDenoiseRadius = dR;

            if (map.TryGetValue("ImageDenoiseSigma", out string? denS) &&
                float.TryParse(denS, System.Globalization.NumberStyles.Float, inv, out float dS))
                ImageDenoiseSigma = dS;

            if (map.TryGetValue("ImageAutoLevels", out string? alOn) &&
                TryParseBool(alOn, out bool al))
                ImageAutoLevels = al;

            if (map.TryGetValue("ImageAutoLevelsLow", out string? alLo) &&
                double.TryParse(alLo, System.Globalization.NumberStyles.Float, inv, out double lo))
                ImageAutoLevelsLow = lo;

            if (map.TryGetValue("ImageAutoLevelsHigh", out string? alHi) &&
                double.TryParse(alHi, System.Globalization.NumberStyles.Float, inv, out double hi))
                ImageAutoLevelsHigh = hi;

            if (map.TryGetValue("ImageAutoLevelsMinRange", out string? alMin) &&
                int.TryParse(alMin, System.Globalization.NumberStyles.Integer, inv, out int minR))
                ImageAutoLevelsMinRange = minR;

            if (map.TryGetValue("ImageSharpenAmount", out string? shA) &&
                float.TryParse(shA, System.Globalization.NumberStyles.Float, inv, out float sha))
                ImageSharpenAmount = sha;

            if (map.TryGetValue("ImageSharpenPasses", out string? shP) &&
                int.TryParse(shP, System.Globalization.NumberStyles.Integer, inv, out int shp))
                ImageSharpenPasses = shp;

            NormalizeImagePrepSettings();
        }

        // ------------------------------------------------------------------
        // Named profiles (full settings snapshots under Profiles/*.ini)
        // ------------------------------------------------------------------

        public static string GetProfilePath(string name) =>
            Path.Combine(ProfilesDir, SanitizeProfileFileName(name) + ".ini");

        /// <summary>Profile names on disk (no extension), sorted.</summary>
        public static string[] ListProfiles()
        {
            try
            {
                if (!Directory.Exists(ProfilesDir))
                    return Array.Empty<string>();

                return Directory.GetFiles(ProfilesDir, "*.ini")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] ListProfiles failed: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        public static bool ProfileExists(string name) =>
            TryNormalizeProfileName(name, out string clean, out _) &&
            File.Exists(GetProfilePath(clean));

        /// <summary>
        /// Validate / normalize a user-facing profile name.
        /// Rejects empty, path segments, and illegal filename characters.
        /// </summary>
        public static bool TryNormalizeProfileName(string? name, out string clean, out string? error)
        {
            clean = "";
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Profile name is empty.";
                return false;
            }

            clean = name.Trim();
            if (clean.Length > 64)
            {
                error = "Profile name is too long (max 64 characters).";
                return false;
            }
            if (clean is "." or "..")
            {
                error = "Invalid profile name.";
                return false;
            }
            if (clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "Profile name has illegal characters.";
                return false;
            }
            return true;
        }

        private static string SanitizeProfileFileName(string name) =>
            TryNormalizeProfileName(name, out string clean, out _) ? clean : "Default";

        /// <summary>
        /// Write the current in-memory settings to Profiles\{name}.ini and mark it active.
        /// Snapshot includes modes, hotkeys, gamepad, regions, prompts,
        /// voice (engine + UWP/SAPI voice + rate/pitch/volume/silence), and follow.
        /// Also refreshes SpeakRect.ini. Callers should push live overlay regions first.
        /// </summary>
        public bool SaveProfile(string name, out string? error)
        {
            error = null;
            if (!TryNormalizeProfileName(name, out string clean, out error))
                return false;

            try
            {
                Directory.CreateDirectory(ProfilesDir);
                ActiveProfileName = clean;
                NormalizeModeFlags();
                NormalizeFollowSettings();
                NormalizeVoiceSettings();
                NormalizeComicRegionSettings();
                NormalizeImagePrepSettings();
                SaveTo(GetProfilePath(clean));
                Save();
                Debug.WriteLine(
                    $"[Settings] saved profile → {GetProfilePath(clean)} " +
                    $"(ComicBook={ComicBook})");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"[Settings] SaveProfile failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Load Profiles\{name}.ini into memory, set it active, and write SpeakRect.ini.
        /// </summary>
        public bool LoadProfile(string name, out string? error)
        {
            error = null;
            if (!TryNormalizeProfileName(name, out string clean, out error))
                return false;

            string path = GetProfilePath(clean);
            if (!File.Exists(path))
            {
                error = $"Profile “{clean}” not found.";
                return false;
            }

            try
            {
                ResetToBuiltInDefaults();
                ApplyFromMap(ReadIni(path));
                ActiveProfileName = clean;
                Save();
                Debug.WriteLine($"[Settings] loaded profile ← {path}");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine($"[Settings] LoadProfile failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Delete a profile file. Does not change in-memory settings.</summary>
        public bool DeleteProfile(string name, out string? error)
        {
            error = null;
            if (!TryNormalizeProfileName(name, out string clean, out error))
                return false;

            string path = GetProfilePath(clean);
            if (!File.Exists(path))
            {
                error = $"Profile “{clean}” not found.";
                return false;
            }

            try
            {
                File.Delete(path);
                Debug.WriteLine($"[Settings] deleted profile {path}");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Old stock mode hotkeys were Shift+letter (e.g. Shift+D). Those steal
        /// capital letters while typing when registered globally. If a profile
        /// still has exactly that old stock chord, move it to the new Ctrl+letter
        /// default. Custom bindings are left alone.
        /// </summary>
        private static HotkeyChord MigrateAwayFromGlobalShiftLetter(
            HotkeyChord loaded, Keys letter, HotkeyChord replacement)
        {
            var legacy = new HotkeyChord(HotkeyChord.MOD_SHIFT, letter);
            return loaded == legacy ? replacement : loaded;
        }

        private void LoadHotkeysFromMap(Dictionary<string, string> map)
        {
            // Missing key → built-in default. Blank / None → unbound (no hotkey).
            HotkeyToggleOverlay = HotkeyChord.ParseFromIni(
                ReadMap(map, "ToggleOverlay"), DefaultToggleOverlay);
            HotkeyToggleDefaultMode = MigrateAwayFromGlobalShiftLetter(
                HotkeyChord.ParseFromIni(
                    ReadMap(map, "ToggleDefaultMode"), DefaultToggleDefaultMode),
                Keys.D, DefaultToggleDefaultMode);
            HotkeyToggleComicBook = MigrateAwayFromGlobalShiftLetter(
                HotkeyChord.ParseFromIni(
                    ReadMap(map, "ToggleComicBook"), DefaultToggleComicBook),
                Keys.B, DefaultToggleComicBook);

            // Missing key → default Ctrl+Shift+S. Blank / None → unbound.
            HotkeyStopTts = HotkeyChord.ParseFromIni(
                ReadMap(map, "StopTts"), DefaultStopTts);

            for (int i = 0; i < 8; i++)
            {
                HotkeyRegions[i] = HotkeyChord.ParseFromIni(
                    ReadMap(map, $"Region{i + 1}"), DefaultRegion(i));
            }

            // Region9 / Follow (also accept legacy key name FollowRegion)
            HotkeyFollowRegion = HotkeyChord.ParseFromIni(
                ReadMap(map, "Region9") ?? ReadMap(map, "FollowRegion"),
                DefaultFollowRegion);

            HotkeyShapeRect = HotkeyChord.ParseFromIni(
                ReadMap(map, "ShapeRect"), DefaultShapeRect);
            HotkeyShapeOval = HotkeyChord.ParseFromIni(
                ReadMap(map, "ShapeOval"), DefaultShapeOval);
            HotkeyShapeLasso = HotkeyChord.ParseFromIni(
                ReadMap(map, "ShapeLasso"), DefaultShapeLasso);
        }

        private void LoadGamepadFromMap(Dictionary<string, string> map)
        {
            if (map.TryGetValue("PadControllerIndex", out string? idxRaw) &&
                int.TryParse(idxRaw, out int idx))
            {
                GamepadControllerIndex = Math.Clamp(idx, 0, 3);
            }

            // Empty / missing / invalid → unbound (opt-in only).
            PadToggleOverlay = GamepadButton.ParseOrEmpty(ReadMap(map, "PadToggleOverlay"));
            PadToggleDefaultMode = GamepadButton.ParseOrEmpty(ReadMap(map, "PadToggleDefaultMode"));
            PadToggleComicBook = GamepadButton.ParseOrEmpty(ReadMap(map, "PadToggleComicBook"));
            PadStopTts = GamepadButton.ParseOrEmpty(ReadMap(map, "PadStopTts"));

            for (int i = 0; i < 8; i++)
                PadRegions[i] = GamepadButton.ParseOrEmpty(ReadMap(map, $"PadRegion{i + 1}"));

            PadFollowRegion = GamepadButton.ParseOrEmpty(
                ReadMap(map, "PadRegion9") ?? ReadMap(map, "PadFollowRegion"));

            PadShapeRect = GamepadButton.ParseOrEmpty(ReadMap(map, "PadShapeRect"));
            PadShapeOval = GamepadButton.ParseOrEmpty(ReadMap(map, "PadShapeOval"));
            PadShapeLasso = GamepadButton.ParseOrEmpty(ReadMap(map, "PadShapeLasso"));
        }

        private void LoadCustomHotkeysFromMap(Dictionary<string, string> map)
        {
            CustomHotkeys.Clear();

            // Preferred: CustomCount + CustomN.Action / .Keyboard / .Gamepad / .Arg / .Label
            int count = 0;
            if (map.TryGetValue("CustomCount", out string? countRaw) &&
                int.TryParse(countRaw, out int parsed))
            {
                count = Math.Clamp(parsed, 0, CustomHotkeyBinding.MaxBindings);
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    string prefix = $"Custom{i}";
                    string? actionRaw = ReadMap(map, $"{prefix}.Action")
                        ?? ReadMap(map, $"{prefix}Action");
                    if (!CustomActionCatalog.TryParse(actionRaw, out var action))
                        continue;

                    var b = new CustomHotkeyBinding
                    {
                        Id = prefix,
                        Action = action,
                        Label = ReadMap(map, $"{prefix}.Label")
                            ?? ReadMap(map, $"{prefix}Label")
                            ?? "",
                        Arg = ReadMap(map, $"{prefix}.Arg")
                            ?? ReadMap(map, $"{prefix}Arg")
                            ?? "",
                        Keyboard = HotkeyChord.ParseFromIni(
                            ReadMap(map, $"{prefix}.Keyboard")
                            ?? ReadMap(map, $"{prefix}Keyboard"),
                            default),
                        Gamepad = GamepadButton.ParseOrEmpty(
                            ReadMap(map, $"{prefix}.Gamepad")
                            ?? ReadMap(map, $"{prefix}Gamepad")),
                    };
                    if (b.UsesAnalogStick)
                        b.Gamepad = default;
                    EnsureDefaultMouseSpeedArg(b);
                    CustomHotkeys.Add(b);
                }
                return;
            }

            // Fallback: scan Custom0.Action … Custom31.Action without count
            for (int i = 0; i < CustomHotkeyBinding.MaxBindings; i++)
            {
                string prefix = $"Custom{i}";
                string? actionRaw = ReadMap(map, $"{prefix}.Action");
                if (actionRaw == null)
                    continue;
                if (!CustomActionCatalog.TryParse(actionRaw, out var action))
                    continue;
                var b = new CustomHotkeyBinding
                {
                    Id = prefix,
                    Action = action,
                    Label = ReadMap(map, $"{prefix}.Label") ?? "",
                    Arg = ReadMap(map, $"{prefix}.Arg") ?? "",
                    Keyboard = HotkeyChord.ParseFromIni(
                        ReadMap(map, $"{prefix}.Keyboard"), default),
                    Gamepad = GamepadButton.ParseOrEmpty(
                        ReadMap(map, $"{prefix}.Gamepad")),
                };
                if (b.UsesAnalogStick)
                    b.Gamepad = default;
                EnsureDefaultMouseSpeedArg(b);
                CustomHotkeys.Add(b);
            }
        }

        /// <summary>
        /// Mouse-move / stick rows need a speed Arg. Blank → current default (12).
        /// <para>
        /// Never rewrite a stored numeric speed (including 4 / 8 / 10). An older
        /// migration forced those legacy defaults to 12 on every load, so Key Map
        /// speed edits could not survive Save / profile load / restart.
        /// </para>
        /// </summary>
        private static void EnsureDefaultMouseSpeedArg(CustomHotkeyBinding b)
        {
            if (!CustomActionCatalog.HasEditableSpeed(b.Action))
                return;
            if (string.IsNullOrWhiteSpace(b.Arg))
                b.Arg = SystemInput.FormatSpeed(SystemInput.DefaultMouseSpeed);
        }

        /// <summary>
        /// Persist custom bindings (Arg/speed, chords, pads) to SpeakRect.ini and
        /// the active named profile file when it exists.
        /// </summary>
        public void PersistCustomHotkeys()
        {
            Save();
            SyncActiveProfileFile();
        }

        private void LoadSpeechRulesFromMap(Dictionary<string, string> map)
        {
            SpeechRules.Clear();

            // Pipeline options (same section as name rules).
            // Title-case ALL CAPS: missing → off.
            // Force lowercase: missing → on (product default for all modes).
            // Mutually exclusive: if both true in the file, force-lowercase wins.
            bool titleCase = false;
            if (map.TryGetValue("TitleCaseAllCaps", out string? tcRaw) &&
                TryParseBool(tcRaw, out bool tc))
                titleCase = tc;
            else if (map.TryGetValue("SpeechTitleCaseAllCaps", out string? tcRaw2) &&
                     TryParseBool(tcRaw2, out bool tc2))
                titleCase = tc2;

            bool forceLower = true;
            if (map.TryGetValue("ForceLowercase", out string? flRaw) &&
                TryParseBool(flRaw, out bool fl))
                forceLower = fl;
            else if (map.TryGetValue("SpeechForceLowercase", out string? flRaw2) &&
                     TryParseBool(flRaw2, out bool fl2))
                forceLower = fl2;

            if (forceLower && titleCase)
                titleCase = false;
            // Assign force first then title when only title is on (setters clear peer).
            SpeechForceLowercase = forceLower;
            SpeechTitleCaseAllCaps = titleCase;

            int count = 0;
            if (map.TryGetValue("SpeechRuleCount", out string? countRaw) &&
                int.TryParse(countRaw, out int parsed))
            {
                count = Math.Clamp(parsed, 0, SpeechRule.MaxRules);
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    string prefix = $"SpeechRule{i}";
                    string? match = ReadMap(map, $"{prefix}.Match")
                        ?? ReadMap(map, $"{prefix}Match");
                    if (string.IsNullOrWhiteSpace(match))
                        continue;

                    string replace = ReadMap(map, $"{prefix}.Replace")
                        ?? ReadMap(map, $"{prefix}Replace")
                        ?? "";
                    string? kindRaw = ReadMap(map, $"{prefix}.Kind")
                        ?? ReadMap(map, $"{prefix}Kind");
                    if (!SpeechRule.TryParseKind(kindRaw, out SpeechMatchKind kind))
                        kind = SpeechMatchKind.Word;

                    bool enabled = true;
                    string? enRaw = ReadMap(map, $"{prefix}.Enabled")
                        ?? ReadMap(map, $"{prefix}Enabled");
                    if (enRaw != null && TryParseBool(enRaw, out bool en))
                        enabled = en;

                    if (SpeechRule.TryNormalize(
                            match, replace, kind, enabled,
                            out SpeechRule rule, out _))
                    {
                        SpeechRules.Add(rule);
                    }
                }
                return;
            }

            // Fallback: scan SpeechRule0.Match … without count
            for (int i = 0; i < SpeechRule.MaxRules; i++)
            {
                string prefix = $"SpeechRule{i}";
                string? match = ReadMap(map, $"{prefix}.Match");
                if (match == null)
                    continue;
                if (string.IsNullOrWhiteSpace(match))
                    continue;

                string replace = ReadMap(map, $"{prefix}.Replace") ?? "";
                if (!SpeechRule.TryParseKind(
                        ReadMap(map, $"{prefix}.Kind"), out SpeechMatchKind kind))
                    kind = SpeechMatchKind.Word;
                bool enabled = true;
                if (TryParseBool(ReadMap(map, $"{prefix}.Enabled") ?? "true", out bool en))
                    enabled = en;

                if (SpeechRule.TryNormalize(
                        match, replace, kind, enabled,
                        out SpeechRule rule, out _))
                {
                    SpeechRules.Add(rule);
                }
            }
        }

        private void LoadSpeechTextRulesFromMap(Dictionary<string, string> map)
        {
            // Missing section / count → keep catalog defaults.
            if (!map.TryGetValue("SpeechTextRuleCount", out string? countRaw) ||
                !int.TryParse(countRaw, out int parsed) ||
                parsed <= 0)
            {
                var sparse = new List<SpeechTextRule>();
                for (int i = 0; i < SpeechTextRule.MaxRules; i++)
                {
                    string prefix = $"SpeechTextRule{i}";
                    if (ReadMap(map, $"{prefix}.Id") == null &&
                        ReadMap(map, $"{prefix}.Pattern") == null)
                        continue;
                    if (TryReadSpeechTextRule(map, prefix, out SpeechTextRule? r) && r != null)
                        sparse.Add(r);
                }
                if (sparse.Count > 0)
                    SpeechTextRules = SpeechTextRulesCatalog.MergeWithDefaults(sparse);
                else
                    SpeechTextRules = SpeechTextRulesCatalog.CreateDefaults();
                SpeechTextRulesEngine.ClearCache();
                return;
            }

            int count = Math.Clamp(parsed, 0, SpeechTextRule.MaxRules);
            var loaded = new List<SpeechTextRule>(count);
            for (int i = 0; i < count; i++)
            {
                if (TryReadSpeechTextRule(map, $"SpeechTextRule{i}", out SpeechTextRule? r) &&
                    r != null)
                    loaded.Add(r);
            }

            SpeechTextRules = SpeechTextRulesCatalog.MergeWithDefaults(loaded);
            SpeechTextRulesEngine.ClearCache();
        }

        private static bool TryReadSpeechTextRule(
            Dictionary<string, string> map, string prefix, out SpeechTextRule? rule)
        {
            rule = null;
            string? idRaw = ReadMap(map, $"{prefix}.Id");
            string? nameRaw = ReadMap(map, $"{prefix}.Name");
            string? stageRaw = ReadMap(map, $"{prefix}.Stage");
            string? patRaw = ReadMap(map, $"{prefix}.Pattern");
            string? replRaw = ReadMap(map, $"{prefix}.Replace");
            string? enRaw = ReadMap(map, $"{prefix}.Enabled");
            string? icRaw = ReadMap(map, $"{prefix}.IgnoreCase");
            string? biRaw = ReadMap(map, $"{prefix}.BuiltIn");

            if (string.IsNullOrWhiteSpace(patRaw) && string.IsNullOrWhiteSpace(idRaw))
                return false;

            if (!SpeechTextRule.TryParseStage(stageRaw, out SpeechTextRuleStage stage))
                stage = SpeechTextRuleStage.Abbrev;
            bool enabled = true;
            if (!string.IsNullOrWhiteSpace(enRaw) && TryParseBool(enRaw, out bool en))
                enabled = en;
            bool ignoreCase = false;
            if (!string.IsNullOrWhiteSpace(icRaw) && TryParseBool(icRaw, out bool ic))
                ignoreCase = ic;
            bool builtIn = false;
            if (!string.IsNullOrWhiteSpace(biRaw) && TryParseBool(biRaw, out bool bi))
                builtIn = bi;

            string id = SpeechTextRule.SanitizeId(idRaw);
            if (id.Length == 0)
                id = SpeechTextRule.NewCustomId();

            if (!SpeechTextRule.TryNormalize(
                    id, nameRaw, stage, patRaw, replRaw ?? "",
                    enabled, ignoreCase, builtIn,
                    out SpeechTextRule clean, out _))
                return false;

            rule = clean;
            return true;
        }

        private void LoadRegionsFromMap(Dictionary<string, string> map)
        {
            ClearRegionSlots();

            if (map.TryGetValue("ActiveSlot", out string? slotRaw) &&
                int.TryParse(slotRaw, out int slot1Based))
            {
                // Accept 1..8 (user-facing) or 0..7
                if (slot1Based is >= 1 and <= 8)
                    ActiveRegionSlot = slot1Based - 1;
                else if (slot1Based is >= 0 and <= 7)
                    ActiveRegionSlot = slot1Based;
            }

            if (map.TryGetValue("ShapeMode", out string? shapeRaw) &&
                !string.IsNullOrWhiteSpace(shapeRaw))
            {
                ShapeMode = NormalizeShapeMode(shapeRaw);
            }

            for (int i = 0; i < 8; i++)
            {
                // Prefer SlotN; also accept RegionGeomN for clarity in hand-edited files
                string? raw = ReadMap(map, $"Slot{i + 1}")
                    ?? ReadMap(map, $"RegionGeom{i + 1}")
                    ?? ReadMap(map, $"RegionShape{i + 1}");
                var parsed = RegionSlotData.Parse(raw);
                RegionSlots[i].Mode = parsed.Mode;
                RegionSlots[i].X = parsed.X;
                RegionSlots[i].Y = parsed.Y;
                RegionSlots[i].W = parsed.W;
                RegionSlots[i].H = parsed.H;
                RegionSlots[i].Points = parsed.Points;
            }
        }

        private static string NormalizeShapeMode(string raw)
        {
            string t = raw.Trim();
            if (t.Equals("Oval", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Ellipse", StringComparison.OrdinalIgnoreCase))
                return "Ellipse";
            if (t.Equals("Lasso", StringComparison.OrdinalIgnoreCase))
                return "Lasso";
            return "Rectangle";
        }

        private static string? ReadMap(Dictionary<string, string> map, string key) =>
            map.TryGetValue(key, out string? v) ? v : null;

        public void Save() => SaveTo(IniPath);

        /// <summary>
        /// Load settings from an arbitrary ini path into memory.
        /// Used by profile load and by smoke tests. Does not write SpeakRect.ini
        /// unless the caller also calls <see cref="Save"/>.
        /// </summary>
        public void LoadFrom(string path, bool resetFirst = true)
        {
            if (resetFirst)
                ResetToBuiltInDefaults();
            ApplyFromMap(ReadIni(path));
        }

        public void SaveTo(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Always persist a coherent MODE block (pipes imply ComicBook).
                NormalizeModeFlags();

                // Prompt: only write a custom override. Blank = use hard-coded default
                // at ResolveOcrPrompt time (do not dump default text into the ini).
                string ocr = PromptForIni(OcrPrompt, DefaultOcrPrompt);
                OcrPrompt = ocr;

                string profileLabel = string.IsNullOrWhiteSpace(ActiveProfileName)
                    ? "Default"
                    : ActiveProfileName.Trim();

                var sb = new StringBuilder();
                sb.AppendLine("; SpeakRect settings");
                sb.AppendLine("; ComicBook=false (default) → Default mode: Image prep → single full-frame OCR");
                sb.AppendLine("; ComicBook=true → Comic Book: tone, fog detect, balloons/crops/POI");
                sb.AppendLine("; Named profiles (Profiles\\*.ini) store full snapshots:");
                sb.AppendLine(";   modes, hotkeys, gamepad, custom, regions, prompts, voice,");
                sb.AppendLine(";   speech rules, comic balloons, image prep, follow.");
                sb.AppendLine();
                sb.AppendLine("[PROFILE]");
                sb.AppendLine($"ActiveProfile={profileLabel}");
                sb.AppendLine("; Last Settings tab name (KeyMap, Regions, Follow, Voice, …).");
                sb.AppendLine($"LastSettingsTab={NormalizeLastSettingsTab(LastSettingsTab)}");
                sb.AppendLine();
                sb.AppendLine("[MODE]");
                sb.AppendLine($"ComicBook={ComicBook.ToString().ToLowerInvariant()}");
                sb.AppendLine();
                sb.AppendLine("[HOTKEYS]");
                sb.AppendLine("; Format: Ctrl / Alt / Shift / Win combined with + then the key.");
                sb.AppendLine("; Examples: Shift+Tab, Ctrl+D, Ctrl+B, F1, R");
                sb.AppendLine("; Prefer Ctrl (not Shift) with letter keys for global hotkeys");
                sb.AppendLine("; so typing capitals does not fire mode toggles.");
                sb.AppendLine("; Empty / None / Off = unbound (no hotkey for that action).");
                sb.AppendLine("; Global rows use RegisterHotKey (work from tray).");
                sb.AppendLine("; Overlay rows (Shape*) only apply while the overlay is open.");
                sb.AppendLine("; Remap in the overlay KEY MAP panel, or edit here and restart.");
                sb.AppendLine($"ToggleOverlay={HotkeyToggleOverlay.ToIniString()}");
                sb.AppendLine($"ToggleDefaultMode={HotkeyToggleDefaultMode.ToIniString()}");
                sb.AppendLine($"ToggleComicBook={HotkeyToggleComicBook.ToIniString()}");
                sb.AppendLine("; StopTts aborts in-progress speech (region / Follow / Balloons / announce).");
                sb.AppendLine($"StopTts={HotkeyStopTts.ToIniString()}");
                for (int i = 0; i < 8; i++)
                    sb.AppendLine($"Region{i + 1}={HotkeyRegions[i].ToIniString()}");
                sb.AppendLine("; Region9 = Follow (mouse-float box; size/shape from [FOLLOW]).");
                sb.AppendLine($"Region9={HotkeyFollowRegion.ToIniString()}");
                sb.AppendLine($"ShapeRect={HotkeyShapeRect.ToIniString()}");
                sb.AppendLine($"ShapeOval={HotkeyShapeOval.ToIniString()}");
                sb.AppendLine($"ShapeLasso={HotkeyShapeLasso.ToIniString()}");
                sb.AppendLine();
                sb.AppendLine("[GAMEPAD]");
                sb.AppendLine("; Optional XInput bindings. Empty = unbound (default).");
                sb.AppendLine("; Polling only runs when at least one Pad* binding is set.");
                sb.AppendLine("; Buttons: A B X Y LB RB Start Back LeftThumb RightThumb LT RT");
                sb.AppendLine("; D-pad:   DPadUp DPadDown DPadLeft DPadRight  (also Up/Down/Left/Right)");
                sb.AppendLine("; Sticks:  LSUp LSDown LSLeft LSRight  RSUp RSDown RSLeft RSRight");
                sb.AppendLine("; PadControllerIndex = 0..3 (which XInput slot).");
                sb.AppendLine("; Remap in the KEY MAP panel (Gamepad column), or edit here.");
                sb.AppendLine($"PadControllerIndex={Math.Clamp(GamepadControllerIndex, 0, 3)}");
                sb.AppendLine($"PadToggleOverlay={PadToggleOverlay.ToIniString()}");
                sb.AppendLine($"PadToggleDefaultMode={PadToggleDefaultMode.ToIniString()}");
                sb.AppendLine($"PadToggleComicBook={PadToggleComicBook.ToIniString()}");
                sb.AppendLine($"PadStopTts={PadStopTts.ToIniString()}");
                for (int i = 0; i < 8; i++)
                    sb.AppendLine($"PadRegion{i + 1}={PadRegions[i].ToIniString()}");
                sb.AppendLine($"PadRegion9={PadFollowRegion.ToIniString()}");
                sb.AppendLine($"PadShapeRect={PadShapeRect.ToIniString()}");
                sb.AppendLine($"PadShapeOval={PadShapeOval.ToIniString()}");
                sb.AppendLine($"PadShapeLasso={PadShapeLasso.ToIniString()}");
                sb.AppendLine();
                sb.AppendLine("[CUSTOM]");
                sb.AppendLine("; User-defined global actions (not SpeakRect OCR).");
                sb.AppendLine("; Map keyboard and/or gamepad → mouse OR any hotkey chord.");
                sb.AppendLine("; Actions: KeyTap (Arg = chord e.g. Win+=, Ctrl+C),");
                sb.AppendLine(";   MouseLeftClick, MouseRightClick, MouseMove*, Mouse*Stick, Scroll*.");
                sb.AppendLine("; Stick/move: Arg = optional speed (px/tick). Stick-mouse uses whole L/R stick.");
                sb.AppendLine("; Edit via KEY MAP → Add custom…, or hand-edit here.");
                sb.AppendLine($"CustomCount={CustomHotkeys.Count}");
                for (int i = 0; i < CustomHotkeys.Count; i++)
                {
                    var c = CustomHotkeys[i];
                    // Re-index ids on save so Custom0..N stay dense
                    c.Id = $"Custom{i}";
                    string prefix = c.Id;
                    sb.AppendLine($"{prefix}.Action={CustomActionCatalog.ToIniString(c.Action)}");
                    sb.AppendLine($"{prefix}.Label={EscapeIniValue(c.Label)}");
                    sb.AppendLine($"{prefix}.Keyboard={c.Keyboard.ToIniString()}");
                    sb.AppendLine($"{prefix}.Gamepad={c.Gamepad.ToIniString()}");
                    sb.AppendLine($"{prefix}.Arg={EscapeIniValue(c.Arg)}");
                }
                sb.AppendLine();
                sb.AppendLine("[REGIONS]");
                sb.AppendLine("; Saved capture geometries for region slots 1-8 (screen pixels).");
                sb.AppendLine("; Included in named profiles so Load restores shapes + modes.");
                sb.AppendLine("; Rect:x,y,w,h");
                sb.AppendLine("; Oval:x,y,w,h");
                sb.AppendLine("; Lasso:x1,y1|x2,y2|x3,y3|...   (use | between points, not ;)");
                sb.AppendLine("; Empty SlotN = no geometry for that slot.");
                sb.AppendLine("; ActiveSlot = 1..8 (which slot is selected).");
                sb.AppendLine("; ShapeMode = Rectangle | Ellipse | Lasso (drawing tool).");
                sb.AppendLine($"ActiveSlot={Math.Clamp(ActiveRegionSlot, 0, 7) + 1}");
                sb.AppendLine($"ShapeMode={NormalizeShapeMode(ShapeMode)}");
                for (int i = 0; i < 8; i++)
                    sb.AppendLine($"Slot{i + 1}={RegionSlots[i].ToIniString()}");
                sb.AppendLine();
                sb.AppendLine("[PROMPTS]");
                sb.AppendLine("; Leave blank to use the built-in default. Set a value only to override.");
                sb.AppendLine("; OcrPrompt = sole Local-LLM OCR instruction (all modes / paths).");
                sb.AppendLine($"OcrPrompt={ocr}");
                sb.AppendLine();
                NormalizeVoiceSettings();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                sb.AppendLine("[VOICE]");
                sb.AppendLine("; TtsEngine = Windows (default, UWP OneCore) | Sapi (optional SAPI 5 / adapters).");
                sb.AppendLine("; Leave TtsEngine=Windows for out-of-the-box speech. See README for optional SAPI setup.");
                sb.AppendLine("; VoiceId = UWP VoiceInformation.Id when TtsEngine=Windows (blank = default).");
                sb.AppendLine("; SapiVoiceName = SAPI VoiceInfo.Name when TtsEngine=Sapi (blank = default).");
                sb.AppendLine("; SpeakingRate = 0.5..6.0 (1.0 = normal). Pitch = 0.0..2.0 (1.0 = normal).");
                sb.AppendLine("; Volume = 0.0..1.0. AppendedSilence / PunctuationSilence = Default | Min (UWP only).");
                sb.AppendLine($"; Speak pauses (ms, 0..{MaxSpeakPauseMs}): Comma / Sentence (.!?) / Other / Bubble (balloons).");
                sb.AppendLine("; UseCustomPauseEncodings = true (default) inserts typed pause marks + Task.Delay gaps.");
                sb.AppendLine("; false keeps punctuation for TTS and ignores the pause sliders.");
                sb.AppendLine($"TtsEngine={NormalizeTtsEngine(TtsEngine)}");
                // Escape so long UWP VoiceIds / unusual SAPI names never break the line.
                sb.AppendLine($"VoiceId={EscapeIniValue(VoiceId)}");
                sb.AppendLine($"SapiVoiceName={EscapeIniValue(SapiVoiceName)}");
                sb.AppendLine($"SpeakingRate={VoiceSpeakingRate.ToString("0.###", inv)}");
                sb.AppendLine($"Pitch={VoicePitch.ToString("0.###", inv)}");
                sb.AppendLine($"Volume={VoiceVolume.ToString("0.###", inv)}");
                sb.AppendLine($"AppendedSilence={NormalizeSilenceName(VoiceAppendedSilence)}");
                sb.AppendLine($"PunctuationSilence={NormalizeSilenceName(VoicePunctuationSilence)}");
                sb.AppendLine($"UseCustomPauseEncodings={VoiceUseCustomPauseEncodings.ToString().ToLowerInvariant()}");
                sb.AppendLine($"CommaPauseMs={VoiceCommaPauseMs}");
                sb.AppendLine($"SentencePauseMs={VoiceSentencePauseMs}");
                sb.AppendLine($"OtherPauseMs={VoiceOtherPauseMs}");
                sb.AppendLine($"BubblePauseMs={VoiceBubblePauseMs}");
                sb.AppendLine();
                NormalizeComicRegionSettings();
                sb.AppendLine("[COMIC_REGIONS]");
                sb.AppendLine("; Comic Book mode only — balloon / OCR region detect tuning.");
                sb.AppendLine("; DetectFog softens art for OCR detect; Local-LLM still reads the clear tone image.");
                sb.AppendLine("; ClusterGap* = line-merge distance (lower = separate balloons).");
                sb.AppendLine("; InflateFrac* / RegionPadding = box pad around islands.");
                sb.AppendLine("; DenseIslandCount = use milder pad when this many islands (0 = off, stock).");
                sb.AppendLine("; SplitLargeRegions = re-detect inside mega caption/row globs.");
                sb.AppendLine("; MergeOverlappingIslands=true (default): union any islands whose boxes");
                sb.AppendLine(";   would overlap after Grow + Crop pad (covers all text).");
                sb.AppendLine(";   false = nudge grow-overlaps apart instead.");
                sb.AppendLine("; OrphanRecoverPasses = max missed balloon re-OCR attempts.");
                sb.AppendLine("; MinIslandAlnum = drop scrap islands below this letter count (0 = off).");
                sb.AppendLine("; SequentialRegions=true (default, Balloons §9): OCR+speak each balloon alone.");
                sb.AppendLine("; SequentialRegions=false: vertical crop-stack + global plan.");
                sb.AppendLine("; PoiMarkers=true: Comic Book alternate — tone + green region boxes. Forces ComicBook on.");
                sb.AppendLine(";   AutoStack on (stock): per-island orange canvas VL ×N (preview = full-page map only).");
                sb.AppendLine(";   AutoStack off + multi: §9 Sequential or crop-stack. 1 island + stack off: full-page VL.");
                sb.AppendLine("; PoiFogOutside=true: thick gray fog outside island boxes on the tone map canvas.");
                sb.AppendLine("; PoiAutoStack=true: each island → own orange canvas → Local-LLM one at a time");
                sb.AppendLine(";   (preview stays on full page for editing). Not one multi-strip image.");
                sb.AppendLine(";   Margin=outer pad on each canvas. Gap used by multi-strip crop-stack only.");
                sb.AppendLine("; StackBeefExtra=0 (stock: no extra canvas). Pad only — both POI + crop stacks.");
                sb.AppendLine("; StackBottomPadShare=0 (stock). Higher = more pad below content when beef>0.");
                sb.AppendLine("; PoiAutoStackGapPx=10 (multi-strip). PoiAutoStackMarginPx=12 (per canvas outer).");
                sb.AppendLine("; DynamicFog=true: auto fog — climb DynamicFogMin…Max @ 0.01 (stock 0…1).");
                sb.AppendLine(";   Peak = most islands, then largest area; go back to that amount.");
                sb.AppendLine(";   crop re-OCR each island when fog>0; empty/junk → drop (fog ghosts).");
                sb.AppendLine(";   merge off during search. DetectFogAmount unused while dyn on.");
                sb.AppendLine("; DynamicFogMin/Max = search range (raise min / lower max to save CPU).");
                sb.AppendLine($"ComicDetectFog={ComicDetectFog.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ComicDetectFogAmount={ComicDetectFogAmount.ToString("0.###", inv)}");
                sb.AppendLine($"ComicDynamicFog={ComicDynamicFog.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ComicDynamicFogMin={ComicDynamicFogMin.ToString("0.###", inv)}");
                sb.AppendLine($"ComicDynamicFogMax={ComicDynamicFogMax.ToString("0.###", inv)}");
                sb.AppendLine($"ComicClusterGapX={ComicClusterGapX.ToString("0.###", inv)}");
                sb.AppendLine($"ComicClusterGapY={ComicClusterGapY.ToString("0.###", inv)}");
                sb.AppendLine($"ComicInflateFracX={ComicInflateFracX.ToString("0.###", inv)}");
                sb.AppendLine($"ComicInflateFracY={ComicInflateFracY.ToString("0.###", inv)}");
                sb.AppendLine($"ComicRegionPadding={ComicRegionPadding}");
                sb.AppendLine($"ComicDenseIslandCount={ComicDenseIslandCount}");
                sb.AppendLine($"ComicSplitLargeRegions={ComicSplitLargeRegions.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ComicMergeOverlappingIslands={ComicMergeOverlappingIslands.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ComicOrphanRecoverPasses={ComicOrphanRecoverPasses}");
                sb.AppendLine($"ComicMinIslandAlnum={ComicMinIslandAlnum}");
                sb.AppendLine($"ComicSequentialRegions={ComicSequentialRegions.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ComicPoiMarkers={ComicPoiMarkers.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ComicPoiFogOutside={ComicPoiFogOutside.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ComicPoiAutoStack={ComicPoiAutoStack.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ComicPoiAutoStackGapPx={ComicPoiAutoStackGapPx}");
                sb.AppendLine($"ComicPoiAutoStackMarginPx={ComicPoiAutoStackMarginPx}");
                sb.AppendLine(
                    $"ComicPoiStackBeefExtra={ComicPoiStackBeefExtra.ToString("0.###", inv)}");
                sb.AppendLine(
                    $"ComicPoiStackBottomPadShare={ComicPoiStackBottomPadShare.ToString("0.###", inv)}");
                sb.AppendLine();
                NormalizeImagePrepSettings();
                sb.AppendLine("[IMAGE_PREP]");
                sb.AppendLine("; Capture image pipeline: letterbox → scale long-edge → gray → tone (denoise+levels+sharpen).");
                sb.AppendLine("; ImagePrepEnabled=false → raw snap (no prep). Comic Book uses full tone when on;");
                sb.AppendLine("; Default and ComicBook share letterbox+scale+gray+tone. Detect fog is under [COMIC_REGIONS].");
                sb.AppendLine("; LlmSendDownscale=false (default): send prep/stack size as-is.");
                sb.AppendLine("; LlmSendDownscale=true: cap Local-LLM payload long edge at LlmSendMaxLongEdge (640).");
                sb.AppendLine(";   Detect/boxes stay at prep size either way.");
                sb.AppendLine($"ImagePrepEnabled={ImagePrepEnabled.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ImageLetterbox={ImageLetterbox.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ImageLetterboxPad={ImageLetterboxPad}");
                sb.AppendLine($"ImageLetterboxBlack={ImageLetterboxBlack}");
                sb.AppendLine($"ImageLetterboxWhite={ImageLetterboxWhite}");
                sb.AppendLine($"ImageUpscaleLongSide={ImageUpscaleLongSide}");
                sb.AppendLine($"ImageLlmSendDownscale={ImageLlmSendDownscale.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ImageLlmSendMaxLongEdge={ImageLlmSendMaxLongEdge}");
                sb.AppendLine($"ImageGrayscale={ImageGrayscale.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ImageInkGrayWeight={ImageInkGrayWeight.ToString("0.###", inv)}");
                sb.AppendLine($"ImageDenoiseRadius={ImageDenoiseRadius}");
                sb.AppendLine($"ImageDenoiseSigma={ImageDenoiseSigma.ToString("0.###", inv)}");
                sb.AppendLine($"ImageAutoLevels={ImageAutoLevels.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ImageAutoLevelsLow={ImageAutoLevelsLow.ToString("0.###", inv)}");
                sb.AppendLine($"ImageAutoLevelsHigh={ImageAutoLevelsHigh.ToString("0.###", inv)}");
                sb.AppendLine($"ImageAutoLevelsMinRange={ImageAutoLevelsMinRange}");
                sb.AppendLine($"ImageSharpenAmount={ImageSharpenAmount.ToString("0.###", inv)}");
                sb.AppendLine($"ImageSharpenPasses={ImageSharpenPasses}");
                sb.AppendLine();
                sb.AppendLine("[SPEECH_RULES]");
                sb.AppendLine("; User speech rules (Settings → Speech). Profile-backed.");
                sb.AppendLine("; TitleCaseAllCaps=false (default): leave ALL CAPS words as OCR produced them.");
                sb.AppendLine("; true = HELLO → Hello (words of 2+ uppercase letters only; mixed case kept).");
                sb.AppendLine("; ForceLowercase=true (default): lowercase after noise strip (normalize ALL CAPS).");
                sb.AppendLine("; false = keep OCR casing for speech / preview. Abbrev still case-insensitive.");
                sb.AppendLine("; TitleCaseAllCaps and ForceLowercase are mutually exclusive (at most one true).");
                sb.AppendLine("; Store natural text (Match=X-Men, Replace=Ex-Men).");
                sb.AppendLine("; Engine maps Match to cleaned OCR; Replace is spoken as typed.");
                sb.AppendLine("; Kind = Word | Phrase. Empty Replace = never speak.");
                sb.AppendLine("; Order matters — rules run top to bottom.");
                sb.AppendLine($"TitleCaseAllCaps={SpeechTitleCaseAllCaps.ToString().ToLowerInvariant()}");
                sb.AppendLine($"ForceLowercase={SpeechForceLowercase.ToString().ToLowerInvariant()}");
                sb.AppendLine($"SpeechRuleCount={SpeechRules.Count}");
                for (int i = 0; i < SpeechRules.Count; i++)
                {
                    var r = SpeechRules[i];
                    string prefix = $"SpeechRule{i}";
                    sb.AppendLine($"{prefix}.Enabled={r.Enabled.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"{prefix}.Kind={SpeechRule.KindToIni(r.Kind)}");
                    sb.AppendLine($"{prefix}.Match={EscapeIniValue(r.Match)}");
                    sb.AppendLine($"{prefix}.Replace={EscapeIniValue(r.Replace)}");
                }
                sb.AppendLine();
                sb.AppendLine("[SPEECH_TEXT_RULES]");
                sb.AppendLine("; Pipeline text rules (Settings → Speech → Text rules). Profile-backed.");
                sb.AppendLine("; Stage = Noise | Abbrev | Decorators. Pattern = .NET regex.");
                sb.AppendLine("; Empty Replace = strip. Order within each stage is list order.");
                sb.AppendLine("; BuiltIn=true rows ship with SpeakRect; Reset defaults restores them.");
                sb.AppendLine($"SpeechTextRuleCount={SpeechTextRules.Count}");
                for (int i = 0; i < SpeechTextRules.Count; i++)
                {
                    var r = SpeechTextRules[i];
                    string prefix = $"SpeechTextRule{i}";
                    sb.AppendLine($"{prefix}.Id={EscapeIniValue(r.Id)}");
                    sb.AppendLine($"{prefix}.Name={EscapeIniValue(r.Name)}");
                    sb.AppendLine($"{prefix}.Stage={SpeechTextRule.StageToIni(r.Stage)}");
                    sb.AppendLine($"{prefix}.Enabled={r.Enabled.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"{prefix}.IgnoreCase={r.IgnoreCase.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"{prefix}.BuiltIn={r.IsBuiltIn.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"{prefix}.Pattern={EscapeIniValue(r.Pattern)}");
                    sb.AppendLine($"{prefix}.Replace={EscapeIniValue(r.Replace)}");
                }
                sb.AppendLine();
                NormalizeFollowSettings();
                sb.AppendLine("[FOLLOW]");
                sb.AppendLine("; Region 9 — mouse-float capture. Shape = Rectangle | Ellipse.");
                sb.AppendLine("; Size in pixels; Offset is region top-left relative to cursor.");
                sb.AppendLine("; Speak via Region9 hotkey (default Shift+F9). Enter = lock on overlay.");
                sb.AppendLine($"FollowWidth={FollowWidth}");
                sb.AppendLine($"FollowHeight={FollowHeight}");
                sb.AppendLine($"FollowShape={NormalizeFollowShape(FollowShape)}");
                sb.AppendLine($"FollowOffsetX={FollowOffsetX}");
                sb.AppendLine($"FollowOffsetY={FollowOffsetY}");
                sb.AppendLine();

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] save failed ({path}): {ex.Message}");
            }
        }

        private static string ReadPrompt(Dictionary<string, string> map, string key)
        {
            if (!map.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
                return "";
            return raw.Trim();
        }

        /// <summary>
        /// Known stock multi-prompt defaults from older builds. Matched on load so
        /// <see cref="ResolveOcrPrompt"/> picks up the single built-in text.
        /// Custom user prompts are left alone.
        /// </summary>
        private static readonly string[] LegacyOcrPromptDefaults =
        {
            // Pre-single-prompt full / crop / simple / recovery / POI stocks
            "Extract all readable text from this comic panel or image. " +
            "Read in Western comic order: top to bottom, and within each row left to right. " +
            "Finish a speech balloon or caption fully before moving to the next. " +
            "If balloons stack in a column, read that column top to bottom before jumping right. " +
            "Include every word. Output only the text in reading order, nothing else. OCR",

            "Extract all readable text from this comic panel or image. " +
            "Read in Western comic order: top to bottom, left to right. " +
            "Include SFX (big sound words like BRAP! CRASH! BAM!). " +
            "Do not stop after SFX — keep reading every balloon and caption. " +
            "Finish each balloon fully before the next. " +
            "Include every word. Output only the text in reading order, nothing else. OCR",

            "Extract all readable text from this comic panel or image. " +
            "Read in Western comic order: top to bottom, left to right. " +
            "Include SFX (big sound words like BRAP! CRASH! BAM!). " +
            "Do not stop after SFX — keep reading every balloon and caption. " +
            "Finish each balloon fully before the next. " +
            "Include every word. Output only the text in reading order, nothing else. " +
            "Read in columns top to bottom if the page looks split. " +
            "Correct the spelling of common english words. OCR",

            "Extract all readable text from this image crop. " +
            "Read top to bottom, left to right within each line. " +
            "Include every word in this crop. Output only the text, nothing else.",

            "Extract all readable text from this image crop. " +
            "Include SFX (BRAP! CRASH! etc). " +
            "Read top to bottom, left to right. " +
            "Include every word. Output only the text, nothing else.",

            "Extract all readable text from this image crop. " +
            "Include SFX (BRAP! CRASH! etc). " +
            "Read top to bottom, left to right. " +
            "Include every word. Output only the text, nothing else. " +
            "Correct the spelling of common english words.",

            "Extract all text.",
            "Extract all text. Include SFX (BRAP! CRASH!). Do not stop after SFX.",
            "As an OCR, Extract all english text.",
            "As an OCR, Extract all english text. Correct the spelling of common english words.",
            "OCR:",
            "OCR all text including SFX. Do not stop after the first word.",
            "OCR all text including SFX. Do not stop after the first word. " +
            "Correct the spelling of common english words.",

            "As an OCR, extract all English text inside each bright green rectangle. " +
            "Green rectangles mark speech / text regions of interest. " +
            "Read every word inside those rectangles. " +
            "Ignore art and UI outside the green rectangles. " +
            "Do not describe the rectangles themselves. " +
            "Correct the spelling of common english words.",
        };

        /// <summary>
        /// If the stored prompt is a known stock default, clear it so
        /// <see cref="ResolveOcrPrompt"/> picks up the new built-in text.
        /// Custom user prompts are left alone.
        /// </summary>
        private static string MigratePromptIfLegacy(string stored, string legacyDefault)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return "";
            if (string.Equals(
                    NormalizePromptCompare(stored),
                    NormalizePromptCompare(legacyDefault),
                    StringComparison.Ordinal))
                return "";
            return stored;
        }

        private static string NormalizePromptCompare(string s) =>
            Regex.Replace(s.Trim(), @"\s+", " ");

        private static string? NonEmpty(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        /// <summary>
        /// Value to write in the ini: blank when unset or identical to the
        /// hard-coded default (so the file stays free of stock prompt text).
        /// </summary>
        private static string PromptForIni(string? stored, string hardCodedDefault)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return "";
            string t = stored.Trim();
            if (string.Equals(
                    NormalizePromptCompare(t),
                    NormalizePromptCompare(hardCodedDefault),
                    StringComparison.Ordinal))
                return "";
            return t;
        }

        private static Dictionary<string, string> ReadIni(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(path))
            {
                string t = line.Trim();
                if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[')
                    continue;

                int eq = t.IndexOf('=');
                if (eq <= 0) continue;

                string key = t[..eq].Trim();
                string val = t[(eq + 1)..].Trim();
                // Trailing ; comments only on short settings (prompts stay intact).
                if (IsShortSettingKey(key))
                {
                    int semi = val.IndexOf(';');
                    if (semi >= 0) val = val[..semi].Trim();
                }
                map[key] = val;
            }
            return map;
        }

        /// <summary>
        /// Keys whose values may have trailing "; comment" stripped.
        /// Must NOT include Slot* — lasso geometry historically used ';' between points,
        /// and even with '|' we keep full slot values intact.
        /// </summary>
        private static bool IsShortSettingKey(string key) =>
            key is "ComicBook"
                or "UseWinOcr" or "SkipWinOcrSendFullFrameOnly"
                or "ToggleOverlay" or "ToggleComicBook"
                or "ShapeRect" or "ShapeOval" or "ShapeLasso"
                or "PadControllerIndex" or "ActiveProfile" or "LastSettingsTab"
                or "ActiveSlot" or "ShapeMode" or "CustomCount"
                or "TtsEngine" or "VoiceEngine" or "VoiceId" or "SapiVoiceName" or "SapiVoice"
                or "SpeakingRate" or "Pitch" or "Volume"
                or "AppendedSilence" or "PunctuationSilence"
                or "UseCustomPauseEncodings"
                or "CommaPauseMs" or "SentencePauseMs" or "OtherPauseMs" or "BubblePauseMs"
                or "ComicDetectFog" or "ComicDetectFogAmount" or "ComicDynamicFog"
                or "ComicDynamicFogMin" or "ComicDynamicFogMax"
                or "ComicClusterGapX" or "ComicClusterGapY"
                or "ComicInflateFracX" or "ComicInflateFracY"
                or "ComicRegionPadding" or "ComicDenseIslandCount"
                or "ComicSplitLargeRegions" or "ComicMergeOverlappingIslands"
                or "ComicOrphanRecoverPasses"
                or "ComicMinIslandAlnum" or "ComicSequentialRegions"
                or "ComicPoiMarkers" or "ComicPoiFogOutside"
                or "ComicPoiAutoStack" or "ComicPoiAutoStackGapPx" or "ComicPoiAutoStackMarginPx"
                or "ComicPoiStackBeefExtra" or "ComicPoiStackBottomPadShare"
                or "ImagePrepEnabled"
                or "ImageLlmSendDownscale" or "ImageLlmSendMaxLongEdge"
                or "ImageLetterbox" or "ImageLetterboxPad"
                or "ImageLetterboxBlack" or "ImageLetterboxWhite"
                or "ImageUpscaleLongSide" or "ImageGrayscale" or "ImageInkGrayWeight"
                or "ImageDenoiseRadius" or "ImageDenoiseSigma"
                or "ImageAutoLevels" or "ImageAutoLevelsLow" or "ImageAutoLevelsHigh"
                or "ImageAutoLevelsMinRange"
                or "ImageSharpenAmount" or "ImageSharpenPasses"
                or "SpeechRuleCount" or "SpeechTextRuleCount"
                or "FollowWidth" or "FollowHeight" or "FollowShape"
                or "FollowOffsetX" or "FollowOffsetY"
                // Region1..Region8 are hotkeys (short). Slot1..Slot8 are geometries (long) — excluded.
                || (key.StartsWith("Region", StringComparison.OrdinalIgnoreCase)
                    && !key.StartsWith("RegionGeom", StringComparison.OrdinalIgnoreCase)
                    && !key.StartsWith("RegionShape", StringComparison.OrdinalIgnoreCase))
                || (key.StartsWith("Pad", StringComparison.OrdinalIgnoreCase))
                || (key.StartsWith("Custom", StringComparison.OrdinalIgnoreCase))
                // SpeechRuleN.Enabled / .Kind are short; .Match / .Replace stay full (no ; strip).
                || (key.StartsWith("SpeechRule", StringComparison.OrdinalIgnoreCase)
                    && !key.StartsWith("SpeechTextRule", StringComparison.OrdinalIgnoreCase)
                    && (key.EndsWith(".Enabled", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith(".Kind", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith("Enabled", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith("Kind", StringComparison.OrdinalIgnoreCase)))
                // SpeechTextRuleN short fields only; Pattern/Replace/Name stay full.
                || (key.StartsWith("SpeechTextRule", StringComparison.OrdinalIgnoreCase)
                    && (key.EndsWith(".Enabled", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith(".Stage", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith(".IgnoreCase", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith(".BuiltIn", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith(".Id", StringComparison.OrdinalIgnoreCase)));

        /// <summary>Strip characters that would break one-line ini values.</summary>
        private static string EscapeIniValue(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static bool TryParseBool(string raw, out bool value)
        {
            value = false;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string s = raw.Trim();
            if (bool.TryParse(s, out value)) return true;
            if (s is "1" or "yes" or "y" or "on") { value = true; return true; }
            if (s is "0" or "no" or "n" or "off") { value = false; return true; }
            return false;
        }
    }
}
