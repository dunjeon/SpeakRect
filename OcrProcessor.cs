using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Ocr;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using WinSpeech = Windows.Media.SpeechSynthesis;
using SapiSpeech = System.Speech.Synthesis;

namespace SpeakRect
{
    /// <summary>
    /// One live pipeline image from the most recent OCR run (PNG bytes for Analytics).
    /// Stored at full pipeline resolution — same pixels live used (no analytics downscale).
    /// Gallery thumbs are UI-only; double-click enlarge shows these exact pixels.
    /// </summary>
    public sealed class OcrResultImage
    {
        public string Key { get; init; } = "";
        public string Title { get; init; } = "";
        /// <summary>Pixel size of the stored PNG (always full pipeline resolution).</summary>
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>Same as <see cref="Width"/> / <see cref="Height"/> (legacy fields; kept for UI).</summary>
        public int SourceWidth { get; init; }
        public int SourceHeight { get; init; }
        public byte[] PngBytes { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Snapshot of the most recent OCR/speak run for Settings → Analytics.
    /// Always recorded in memory (Release and Debug); debug file dumps are separate.
    /// </summary>
    public sealed class OcrLastResult
    {
        public DateTime CompletedLocal { get; init; }
        public Rectangle CaptureBounds { get; init; }
        public string Shape { get; init; } = "Rectangle";
        public string SpokenText { get; init; } = "";
        public string Detail { get; init; } = "";
        public bool Unreadable { get; init; }
        public IReadOnlyList<OcrResultImage> Images { get; init; } = Array.Empty<OcrResultImage>();

        /// <summary>
        /// Detect fog amount used for this run (fixed fog strength, or 0 when fog off).
        /// </summary>
        public float FogAmountUsed { get; init; }
    }

    /// <summary>
    /// Settings → Balloons preview: numbered green boxes on the prepared panel.
    /// Caller owns and must dispose <see cref="Overlay"/> and <see cref="BaseImage"/>.
    /// </summary>
    public sealed class ComicRegionPreviewResult : IDisposable
    {
        /// <summary>
        /// Detect-view image with numbered green boxes (fog when enabled — what OCR detect sees).
        /// Settable so callers can take ownership.
        /// </summary>
        public Bitmap? Overlay { get; set; }

        /// <summary>
        /// Clean detect-view base without boxes (fog when on, tone when off).
        /// Same pixel size as crop geometry; shows the image OCR detect uses.
        /// </summary>
        public Bitmap? BaseImage { get; set; }

        /// <summary>
        /// Display-final reading islands (grow + crop pad already applied) for Balloons
        /// preview / Speak override. Live path expands cores once via
        /// <c>ActiveCropPadPx</c>; override sets pad to 0 so these boxes are not padded again.
        /// Order = reading / crop-stack order.
        /// </summary>
        public IReadOnlyList<Rectangle> Regions { get; init; } = Array.Empty<Rectangle>();

        public int PipelineWidth { get; init; }
        public int PipelineHeight { get; init; }

        /// <summary>How many reading islands after improve / coalesce / mega-split.</summary>
        public int RegionCount { get; init; }

        /// <summary>
        /// Fog amount used for detect (fixed strength, or 0 when fog off).
        /// </summary>
        public float FogAmountUsed { get; init; }

        /// <summary>Pipeline log (prep + detect summary).</summary>
        public string Detail { get; init; } = "";

        public void Dispose()
        {
            try { Overlay?.Dispose(); } catch { /* ignore */ }
            Overlay = null;
            try { BaseImage?.Dispose(); } catch { /* ignore */ }
            BaseImage = null;
        }
    }

    /// <summary>
    /// Settings → Balloons Speak test: full Comic Book OCR + TTS on a still image.
    /// Caller owns and must dispose <see cref="Overlay"/>.
    /// </summary>
    public sealed class ComicRegionSpeakResult : IDisposable
    {
        public Bitmap? Overlay { get; set; }
        public int RegionCount { get; init; }
        public string SpokenText { get; init; } = "";
        public string Detail { get; init; } = "";
        public bool Unreadable { get; init; }

        public void Dispose()
        {
            try { Overlay?.Dispose(); } catch { /* ignore */ }
            Overlay = null;
        }
    }

    /// <summary>
    /// Settings → Image prep stage preview. Caller owns and must dispose
    /// <see cref="Display"/> (and any non-null stage bitmaps not transferred).
    /// </summary>
    public sealed class ImagePrepPreviewResult : IDisposable
    {
        /// <summary>Stage selected for display (clone of that stage).</summary>
        public Bitmap? Display { get; set; }

        public string StageName { get; init; } = "";
        public string Detail { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }

        public void Dispose()
        {
            try { Display?.Dispose(); } catch { /* ignore */ }
            Display = null;
        }
    }

    /// <summary>
    /// Shared still image for Settings → Balloons / Image live previews.
    /// Thread-safe clone in / clone out. Falls back to a built-in sample panel.
    /// </summary>
    public static class DevCaptureCache
    {
        public const string SampleLabel = "sample panel";

        private static readonly object Gate = new();
        private static Bitmap? _image;
        private static string _label = "";
        /// <summary>
        /// Bumped on every <see cref="Set"/> / last-capture publish so Balloons
        /// refine overrides know the frame identity changed.
        /// </summary>
        private static long _frameSerial;
        /// <summary>
        /// Bumped when Image-tab prep knobs change so Balloons (and others) know
        /// to re-run preview with the new prep (e.g. grayscale on/off).
        /// </summary>
        private static int _prepGeneration;

        /// <summary>Monotonic id for the shared still frame (refine session key).</summary>
        public static long FrameSerial
        {
            get { lock (Gate) return _frameSerial; }
        }

        public static string Label
        {
            get { lock (Gate) return _label; }
        }

        public static bool HasImage
        {
            get { lock (Gate) return _image != null; }
        }

        public static bool IsSample
        {
            get
            {
                lock (Gate)
                    return string.Equals(_label, SampleLabel, StringComparison.Ordinal);
            }
        }

        /// <summary>Monotonic counter for shared image-prep settings changes.</summary>
        public static int PrepGeneration
        {
            get { lock (Gate) return _prepGeneration; }
        }

        /// <summary>Call after Image prep knobs are written to <see cref="AppSettings"/>.</summary>
        public static void NotifyPrepSettingsChanged()
        {
            lock (Gate)
                _prepGeneration++;
        }

        /// <summary>
        /// Compact signature of live prep knobs (for UI to detect stale previews).
        /// </summary>
        public static string PrepSettingsSignature()
        {
            var s = AppSettings.Current;
            s.NormalizeImagePrepSettings();
            return string.Join("|",
                s.ImagePrepEnabled ? "1" : "0",
                s.ImageLetterbox ? "1" : "0",
                s.ImageLetterboxPad,
                s.ImageLetterboxBlack,
                s.ImageLetterboxWhite,
                s.ImageUpscaleLongSide,
                s.ImageGrayscale ? "1" : "0",
                s.ImageInkGrayWeight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                s.ImageDenoiseRadius,
                s.ImageDenoiseSigma.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                s.ImageAutoLevels ? "1" : "0",
                s.ImageAutoLevelsLow.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                s.ImageAutoLevelsHigh.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                s.ImageAutoLevelsMinRange,
                s.ImageSharpenAmount.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                s.ImageSharpenPasses);
        }

        /// <summary>Store a copy of <paramref name="source"/> for other settings tabs.</summary>
        public static void Set(Bitmap source, string label)
        {
            if (source == null) return;
            string lab = string.IsNullOrWhiteSpace(label) ? "image" : label.Trim();
            lock (Gate)
            {
                try { _image?.Dispose(); } catch { /* ignore */ }
                _image = new Bitmap(source);
                _label = lab;
                _frameSerial++;
            }
            // Note: Balloons refine flush is explicit (new OCR capture / open image /
            // different last-capture stamp) — not on every cache Set, so tab switches
            // can restore locked regions.
        }

        /// <summary>Clone of the cached image, or null.</summary>
        public static Bitmap? CloneOrNull()
        {
            lock (Gate)
            {
                if (_image == null) return null;
                return new Bitmap(_image);
            }
        }

        /// <summary>
        /// Prefer <b>full-res last OCR capture</b> → other shared cache → built-in sample.
        /// Settings Image/Balloons always try last capture first so previews track
        /// the latest speak at the same resolution live used. Caller owns the bitmap.
        /// </summary>
        public static Bitmap GetOrCreatePreviewSource(out string label)
        {
            try
            {
                var fromRun = TryLoadLastCaptureOnly();
                if (fromRun != null)
                {
                    // Keep cache aligned with full-res snap (may already be set by live OCR).
                    Set(fromRun, LastCaptureLabel);
                    label = LastCaptureLabel;
                    // Caller owns fromRun; cache has its own clone via Set.
                    return fromRun;
                }
            }
            catch { /* fall through */ }

            var cached = CloneOrNull();
            if (cached != null)
            {
                label = Label;
                return cached;
            }

            var sample = CreateBuiltinSamplePanel();
            Set(sample, SampleLabel);
            label = SampleLabel;
            return sample;
        }

        /// <summary>
        /// Publish the live raw snap for Balloons/Image "last capture" previews.
        /// Must be full resolution (never the Analytics long-edge thumbnail).
        /// </summary>
        public static void PublishLastCapture(Bitmap rawSnap)
        {
            if (rawSnap == null) return;
            try { Set(rawSnap, LastCaptureLabel); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DevCaptureCache] PublishLastCapture: {ex.Message}");
            }
        }

        /// <summary>
        /// Synthetic comic-like panel for Settings previews (no external file).
        /// Cream page, letterbox bars, white balloons with dark text strokes.
        /// </summary>
        public static Bitmap CreateBuiltinSamplePanel()
        {
            const int w = 960;
            const int h = 640;
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(255, 18, 18, 20)); // outer letterbox
                // Page
                var page = new Rectangle(48, 36, w - 96, h - 72);
                using (var pageBrush = new SolidBrush(Color.FromArgb(255, 236, 228, 210)))
                    g.FillRectangle(pageBrush, page);

                void Balloon(Rectangle r, params string[] lines)
                {
                    using (var white = new SolidBrush(Color.White))
                    using (var edge = new Pen(Color.FromArgb(255, 40, 40, 44), 2.5f))
                    {
                        g.FillEllipse(white, r);
                        g.DrawEllipse(edge, r);
                    }
                    using var font = new Font("Segoe UI", 14f, FontStyle.Bold);
                    using var ink = new SolidBrush(Color.FromArgb(255, 28, 28, 32));
                    float y = r.Y + r.Height * 0.28f;
                    foreach (string line in lines)
                    {
                        var sz = g.MeasureString(line, font);
                        float x = r.X + (r.Width - sz.Width) / 2f;
                        g.DrawString(line, font, ink, x, y);
                        y += sz.Height + 2;
                    }
                }

                // Caption strip
                var cap = new Rectangle(page.X + 24, page.Y + 20, page.Width - 48, 56);
                using (var capBg = new SolidBrush(Color.FromArgb(255, 250, 250, 252)))
                using (var capEdge = new Pen(Color.FromArgb(255, 60, 60, 64), 2f))
                {
                    g.FillRectangle(capBg, cap);
                    g.DrawRectangle(capEdge, cap);
                }
                using (var font = new Font("Segoe UI", 13f, FontStyle.Bold))
                using (var ink = new SolidBrush(Color.FromArgb(255, 28, 28, 32)))
                    g.DrawString("SAMPLE PANEL — tune Image & Balloons here", font, ink,
                        cap.X + 16, cap.Y + 16);

                Balloon(new Rectangle(page.X + 40, page.Y + 100, 300, 160),
                    "HELLO THERE.", "NICE TO MEET YOU.");
                Balloon(new Rectangle(page.X + 400, page.Y + 120, 340, 180),
                    "THIS IS A TEST", "BALLOON FOR OCR.");
                Balloon(new Rectangle(page.X + 80, page.Y + 340, 280, 140),
                    "OPEN YOUR OWN", "IMAGE ANYTIME.");
                Balloon(new Rectangle(page.X + 420, page.Y + 360, 320, 150),
                    "CTRL+B FOR", "COMIC BOOK MODE.");
            }
            return bmp;
        }

        /// <summary>Last real OCR capture only (not the sample panel). Caller owns the bitmap.</summary>
        public static Bitmap? TryLoadLastOcrCapture() => TryLoadLastCaptureOnly();

        /// <summary>
        /// Label when live OCR publishes the raw snap into this cache.
        /// Balloons / Image previews load this so re-detect matches live geometry.
        /// </summary>
        public const string LastCaptureLabel = "last capture";

        /// <summary>
        /// Raw snap for Balloons/Image re-detect — same pixels live used.
        /// Priority: in-memory publish at snap → debug last_capture.png →
        /// Analytics "capture" (full pipeline res, never a re-prep stage).
        /// </summary>
        private static Bitmap? TryLoadLastCaptureOnly()
        {
            // 1) Live OCR publishes the exact rawSnap here before prep.
            try
            {
                lock (Gate)
                {
                    if (_image != null &&
                        string.Equals(_label, LastCaptureLabel, StringComparison.Ordinal))
                    {
                        return new Bitmap(_image);
                    }
                }
            }
            catch { /* ignore */ }

            // 2) Debug dump written at snap (exact live geometry).
            try
            {
                string path = Path.Combine(
                    AppContext.BaseDirectory, "debug_images", "last_capture.png");
                if (File.Exists(path))
                    return new Bitmap(path);
            }
            catch { /* ignore */ }

            // 3) Analytics "capture" slot — same full-res pipe snapshot as live.
            // Never use letterbox/upscale/ocr_prep here (would double-prep on re-detect).
            try
            {
                var images = OcrProcessor.LastResult?.Images;
                if (images != null)
                {
                    var hit = images.FirstOrDefault(i =>
                        string.Equals(i.Key, "capture", StringComparison.OrdinalIgnoreCase) &&
                        i.PngBytes != null &&
                        i.PngBytes.Length > 32);
                    if (hit != null)
                    {
                        using var ms = new MemoryStream(hit.PngBytes);
                        using var tmp = new Bitmap(ms);
                        return new Bitmap(tmp);
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        public static void Clear()
        {
            lock (Gate)
            {
                try { _image?.Dispose(); } catch { /* ignore */ }
                _image = null;
                _label = "";
            }
        }
    }

    /// <summary>
    /// Comic / on-screen text: snap → prep → detect balloons → Local-LLM recognize → TTS.
    /// <para>
    /// <b>PRODUCT PRIORITY (do not invert):</b><br/>
    /// 1) Find <b>all</b> readable text (every balloon / caption that is in the selection).<br/>
    /// 2) Read it <b>accurately</b> and in sensible reading order.<br/>
    /// 3) Speed is nice - lower latency when free - but <b>never</b> trade away
    ///    coverage or accuracy for a faster empty or wrong read. A quick miss is
    ///    a failed read. Optimize only after completeness is solid.
    /// </para>
    /// ComicBook ON: snap → same Image prep (letterbox/upscale/ink-gray/tone) →
    /// optional fog for OCR detect only → multi-pass boxes + crops + best-of.
    /// Local-LLM always reads pre-fog tone. ComicBook ON uses diversified consensus.
    /// ComicBook OFF (Default): <b>same Image prep</b> → one full-frame Local-LLM call
    /// (no fog/OCR detect/balloon crops; no consensus). Mode changes strategy only.
    /// Word-count is diagnostic only — it does <b>not</b> short-circuit the comic path.
    /// </summary>
    public class OcrProcessor : IDisposable
    {
        // -----------------------------------------------------------------------
        // PRIORITY: completeness + accuracy  -  wall-clock speed.
        // If a change makes the pipe faster but drops balloons or garbles text,
        // it is the wrong change. Prefer extra passes, orphan recovery, and
        // best-of full/crops over "fast path" that skips recovery.
        // -----------------------------------------------------------------------

        /// <summary>OpenAI-compatible base URL (port from koboldcpp/ocr.kcpps).</summary>
        private static string KoboldBaseUrl => LocalLlmHost.ApiBaseUrl;

        /// <summary>
        /// Must match KoboldCPP model id from GET /v1/models.
        /// Taken from <c>ocr.kcpps</c> model_param (see <see cref="LocalLlmHost.ModelApiId"/>).
        /// </summary>
        private static string KoboldModel => LocalLlmHost.ModelApiId;

        // Sole OCR prompt: SpeakRect.ini [PROMPTS] OcrPrompt, else AppSettings.DefaultOcrPrompt.
        // Same text for full-frame, crops, POI, recovery retries, and consensus passes.

        /// <summary>Active Local-LLM OCR prompt (all modes and paths).</summary>
        private static string LocalLlmTaskPrompt =>
            SpeakRunSettings.GetOcrPrompt();

        /// <summary>
        /// Full-frame path only: extra scale + unsharp before Kobold.
        /// Off — live Image prep (letterbox/upscale/gray/tone) already did this.
        /// </summary>
        private static readonly bool EnableFullFrameScaleAndSharpen = false;

        /// <summary>
        /// Region crops only: second-pass upscale + unsharp after the cut.
        /// Off — crops are plain snaps of the fully prepped tone image; Image prep
        /// already upscaled the panel and applied tone/sharpen. Re-doing it here
        /// double-processed lettering.
        /// </summary>
        private static readonly bool EnableCropScaleAndSharpen = false;

        /// <summary>Full-frame fit box (only if <see cref="EnableFullFrameScaleAndSharpen"/>).</summary>
        private const int OcrTargetWidth = 1280;
        private const int OcrTargetHeight = 720;

        /// <summary>
        /// Legacy per-crop fit box (only if <see cref="EnableCropScaleAndSharpen"/>).
        /// </summary>
        private const int CropTargetWidth = 1536;
        private const int CropTargetHeight = 1536;

        /// <summary>
        /// Legacy cap on crop second-pass upscale (only if
        /// <see cref="EnableCropScaleAndSharpen"/>).
        /// </summary>
        private const double MaxCropUpscale = 8.0;

        /// <summary>Unsharp for full-frame prep (if enabled).</summary>
        private const float LightSharpenAmount = 0.75f;
        private const int SharpenPasses = 2;

        /// <summary>
        /// Legacy crop second-pass unsharp (only if <see cref="EnableCropScaleAndSharpen"/>).
        /// Pipeline tone already sharpens once.
        /// </summary>
        private const float CropSharpenAmount = 0.85f;
        private const int CropSharpenPasses = 1;

        /// <summary>
        /// Active Local-LLM send long-edge cap from Image tab (or 0 if downscale off).
        /// </summary>
        private static int ActiveLlmSendMaxLongEdge =>
            SpeakRunSettings.GetImageLlmSendDownscale()
                ? Math.Clamp(SpeakRunSettings.GetImageLlmSendMaxLongEdge(), 256, 4096)
                : 0;

        /// <summary>
        /// Crops and other small bitmaps go to Kobold as PNG (no JPEG mush).
        /// Larger full-frames use JPEG at <see cref="KoboldFullFrameJpegQuality"/>.
        /// </summary>
        private const int KoboldPngMaxPixels = 900 * 900;

        /// <summary>Full-frame / large image encode quality (was 90).</summary>
        private const long KoboldFullFrameJpegQuality = 95L;

        /// <summary>
        /// max_tokens is a generation ceiling (model still stops at EOS).
        /// 16384 was wasteful: some backends pre-size for the max and OCR
        /// never needs novel-length output. Crops = one bubble; full page = denser.
        /// </summary>
        private const int CropMaxTokens = 512;
        private const int FullFrameMaxTokens = 2048;

        /// <summary>Primary OCR decode - deterministic for stable reads.</summary>
        private const double KoboldPrimaryTemperature = 0;

        /// <summary>
        /// ComicBook ON only: second diversified decode for 2-of-3 consensus.
        /// Low enough to stay near the mode; high enough to escape sticky dumps.
        /// </summary>
        private const double KoboldConsensusTemperature = 0.25;

        /// <summary>
        /// One-shot recovery when temp-0 is unusable / badly under-reads.
        /// Low enough to stay near the mode; high enough to escape sticky dumps.
        /// </summary>
        private const double KoboldRecoveryTemperature = 0.5;

        /// <summary>
        /// ComicBook ON: diversified Kobold consensus (T=0, T=0.25, optional
        /// recovery third) with 2-of-3 / quality pick before full vs crops merge.
        /// ComicBook OFF stays a single prepared send (same prep, one full-frame).
        /// </summary>
        private static readonly bool EnableComicBookDecodeConsensus = true;

        /// <summary>ComicBook ON always uses full-strength consensus when enabled.</summary>
        private static bool ActiveDecodeConsensus =>
            EnableComicBookDecodeConsensus;

        /// <summary>Wide dual-balloon L/R rescue (always on for comic path).</summary>
        private static bool ActiveWideStripRescue => true;

        /// <summary>
        /// Master switch for <c>debug_images/</c> (PNGs, last_ocr.txt, archive).
        /// <b>Debug builds only.</b> Release and publish never create or write this folder.
        /// </summary>
#if DEBUG
        private static bool EnableDebugArtifacts => true;
#else
        private static bool EnableDebugArtifacts => false;
#endif

        /// <summary>Heavy debug PNGs. Debug builds only.</summary>
        private static bool ActiveHeavyDebugImages =>
#if DEBUG
            true;
#else
            false;
#endif

        /// <summary>WinOCR detect prep PNG. Debug builds only.</summary>
        private static bool ActiveWinOcrDetectDebugPng => ActiveHeavyDebugImages;

        /// <summary>Any debug_images write. Debug only.</summary>
        private static bool ActiveAnyDebugArtifacts => EnableDebugArtifacts;

        /// <summary>How many archived debug runs to keep under debug_images/archive.</summary>
        private const int MaxDebugArchiveRuns = 30;

        /// <summary>
        /// Accuracy-first fast path: when primary T=0 is already a <b>strong</b> read,
        /// skip B/C (logs showed B almost always empty and C ≈ A). Short / weak A
        /// still runs full consensus (B has saved 3-word balloons).
        /// </summary>
        private static readonly bool EnableConsensusStrongAFastPath = true;

        /// <summary>Min words for strong-A skip (below this always multi-pass).</summary>
        private const int ConsensusStrongMinWords = 8;

        /// <summary>Min alphanumeric chars for strong-A skip.</summary>
        private const int ConsensusStrongMinAlnum = 28;

        /// <summary>Min OCR quality score for strong-A skip.</summary>
        private const int ConsensusStrongMinQuality = 36;

        // Speak-unit pause ms come from AppSettings (Voice tab / [VOICE] ini).
        // Stock defaults: comma 102, sentence 502, other 52, bubble 752.
        // When VoiceUseCustomPauseEncodings is false, encoding and delays are off.
        private static bool UseCustomPauseEncodings =>
            SpeakRunSettings.GetVoiceUseCustomPauseEncodings();

        private static int ClampSpeakPauseMs(int ms) =>
            Math.Clamp(ms, AppSettings.MinSpeakPauseMs, AppSettings.MaxSpeakPauseMs);

        private static int BubblePauseMs =>
            UseCustomPauseEncodings
                ? ClampSpeakPauseMs(SpeakRunSettings.GetVoiceBubblePauseMs())
                : 0;

        private static int SentencePauseMs =>
            UseCustomPauseEncodings
                ? ClampSpeakPauseMs(SpeakRunSettings.GetVoiceSentencePauseMs())
                : 0;

        private static int CommaPauseMs =>
            UseCustomPauseEncodings
                ? ClampSpeakPauseMs(SpeakRunSettings.GetVoiceCommaPauseMs())
                : 0;

        private static int OtherPauseMs =>
            UseCustomPauseEncodings
                ? ClampSpeakPauseMs(SpeakRunSettings.GetVoiceOtherPauseMs())
                : 0;


        /// <summary>
        /// Extra breath inside a long multi-sentence unit (SSML break).
        /// Helps tracking when one balloon has several sentences.
        /// Rare now that .!? get pause marks after them (unit splits); kept as a short fallback.
        /// </summary>
        private const int SentenceBreakMs = 450;

        /// <summary>
        /// Per-host override for <see cref="AppSettings.ComicRegionPadding"/> (not static —
        /// concurrent live speak must not see Balloons override pad=0).
        /// Balloons override boxes already bake pad; set to 0 so pad is not applied twice.
        /// Live leaves this null and uses settings pad.
        /// </summary>
        private int? _forcedCropPadPx;

        /// <summary>
        /// Settings crop pad (no per-run override). Static helpers / merge tests use this.
        /// </summary>
        private static int TextRegionPadding =>
            SpeakRunSettings.GetComicRegionPadding();

        /// <summary>
        /// Crop pad for this host run (settings unless Balloons override forced 0).
        /// </summary>
        private int ActiveCropPadPx =>
            _forcedCropPadPx ?? TextRegionPadding;

        /// <summary>
        /// Hard cap on regions/crops per snap (sanity bound only).
        /// Do not lower this to "go faster" if it starts dropping balloons.
        /// </summary>
        private const int MaxTextRegions = 20;

        /// <summary>Minimum line box size (px) to keep before clustering.</summary>
        /// <summary>Minimum clustered region size (px) after merge.</summary>
        /// <summary>
        /// Detect pass 1: pipeline is already upscaled; 1.0 = native pipeline pixels.
        /// </summary>
        private const double WinOcrDetectScale = 1.0;

        /// <summary>
        /// Detect pass 2: extra scale for small lettering. Always run (two-pass detect).
        /// </summary>
        private const double WinOcrDetectScaleRetry = 1.5;

        /// <summary>
        /// ComicBook panel long-edge after letterbox.
        /// From <see cref="AppSettings.ImageUpscaleLongSide"/>.
        /// </summary>
        private static int PipelineUpscaleLongSideComic =>
            SpeakRunSettings.GetImageUpscaleLongSide();

        private static int ActivePipelineUpscaleLongSide => PipelineUpscaleLongSideComic;

        /// <summary>
        /// Ink-preserving gray: blend weight for min(R,G,B) vs Rec.601 luminance.
        /// From <see cref="AppSettings.ImageInkGrayWeight"/>.
        /// </summary>
        private static float InkGrayMinWeight =>
            SpeakRunSettings.GetImageInkGrayWeight();

        /// <summary>Auto-levels low/high percentiles from settings.</summary>
        private static double AutoLevelsLowPercentile =>
            SpeakRunSettings.GetImageAutoLevelsLow();
        private static double AutoLevelsHighPercentile =>
            SpeakRunSettings.GetImageAutoLevelsHigh();

        private static int AutoLevelsMinRange =>
            SpeakRunSettings.GetImageAutoLevelsMinRange();

        private static bool EnableAutoLevels =>
            EnableImagePrep && SpeakRunSettings.GetImageAutoLevels();

        /// <summary>
        /// Edge-preserving denoise spatial radius (0 = off).
        /// From <see cref="AppSettings.ImageDenoiseRadius"/>.
        /// </summary>
        private static int DenoiseSpatialRadius =>
            EnableImagePrep ? SpeakRunSettings.GetImageDenoiseRadius() : 0;

        private static float DenoiseRangeSigma =>
            SpeakRunSettings.GetImageDenoiseSigma();

        private static float PipelineSharpenAmount =>
            EnableImagePrep ? SpeakRunSettings.GetImageSharpenAmount() : 0f;
        private static int PipelineSharpenPasses =>
            EnableImagePrep ? SpeakRunSettings.GetImageSharpenPasses() : 0;

        /// <summary>
        /// Master image-prep switch. From <see cref="AppSettings.ImagePrepEnabled"/>.
        /// Off = raw snap (no letterbox / scale / gray / tone).
        /// </summary>
        private static bool EnableImagePrep =>
            SpeakRunSettings.GetImagePrepEnabled();

        /// <summary>
        /// Convert to ink-preserving grayscale after upscale.
        /// From <see cref="AppSettings.ImageGrayscale"/> (and master prep on).
        /// </summary>
        private static bool EnablePipelineGrayscale =>
            EnableImagePrep && SpeakRunSettings.GetImageGrayscale();

        /// <summary>
        /// Gray fog after tone prep for <b>WinOCR detect only</b>. ComicBook ON only.
        /// From <see cref="AppSettings.ComicDetectFog"/>.
        /// </summary>
        private static bool EnableWinOcrDetectGrayFog =>
            SpeakRunSettings.GetComicDetectFog();

        /// <summary>
        /// ComicBook OFF (Default): same Image prep as ComicBook, then one full-frame
        /// Kobold call (no fog/detect/crops). ComicBook ON: full balloon pipeline.
        /// </summary>
        private static bool ComicBookOff => !SpeakRunSettings.GetComicBook();

        /// <summary>0 = no fog, 1 = solid gray. From settings (default 0.35).</summary>
        private static float WinOcrDetectGrayFogAmount =>
            SpeakRunSettings.GetComicDetectFogAmount();

        /// <summary>Gray level for the fog (128 = mid gray).</summary>
        private const byte WinOcrDetectGrayFogLevel = 128;

        /// <summary>
        /// Union overlapping inflated islands instead of nudging them apart.
        /// From <see cref="AppSettings.ComicMergeOverlappingIslands"/> (default on).
        /// </summary>
        private static bool EnableMergeOverlappingIslands =>
            SpeakRunSettings.GetComicMergeOverlappingIslands();

        /// <summary>
        /// Combined letterbox dark bar threshold.
        /// From <see cref="AppSettings.ImageLetterboxBlack"/>.
        /// </summary>
        private static int LetterboxBlackThreshold =>
            SpeakRunSettings.GetImageLetterboxBlack();

        /// <summary>
        /// Combined letterbox light bar threshold.
        /// From <see cref="AppSettings.ImageLetterboxWhite"/>.
        /// </summary>
        private static int LetterboxWhiteThreshold =>
            SpeakRunSettings.GetImageLetterboxWhite();

        /// <summary>
        /// Row/col needs at least this fraction of dark-content <b>and</b>
        /// light-content pixels to count as real content (not a uniform bar).
        /// Was 0.02 — too low: a few title-bar / taskbar chrome pixels (~20–30)
        /// made pure-black Kindle/ebook pillars look like content, so left trim
        /// stopped ~20px in and left a huge black side bar. 0.05 ignores that
        /// sparse UI noise while real panel columns still pass easily.
        /// </summary>
        private const double LetterboxMinContentFraction = 0.05;

        /// <summary>Soft pass dark bar floor (hard + 20, residual mid-dark rims).</summary>
        private static int LetterboxSoftBlackThreshold =>
            Math.Min(255, LetterboxBlackThreshold + 20);

        /// <summary>Soft pass light bar ceiling (hard − 20).</summary>
        private static int LetterboxSoftWhiteThreshold =>
            Math.Max(0, LetterboxWhiteThreshold - 20);

        /// <summary>Min content fraction for the soft pass (slightly above hard).</summary>
        private const double LetterboxSoftMinContentFraction = 0.06;

        /// <summary>Pad kept around detected content. From settings.</summary>
        private static int LetterboxContentPad =>
            SpeakRunSettings.GetImageLetterboxPad();

        private static bool EnableLetterbox =>
            EnableImagePrep && SpeakRunSettings.GetImageLetterbox();

        // Letterbox edge trim treats a row/col as a bar when it is predominantly
        // dark OR predominantly light. That handles black-only, white-only, and
        // sandwich [black][white][art][white][black] page margins. Mid-gray is
        // left alone (not treated as a bar) so art/panels are not over-cropped.
        // Scans are band-restricted and iterated so black pillar corners do not
        // block vertical white/black bar trim (and vice versa).

        /// <summary>
        /// Wide content strip: if full-frame OCR is short, split L/R and re-read
        /// (common miss: only the first balloon on a letterboxed dual-bubble panel).
        /// </summary>
        private const double WideStripMinAspect = 2.0;
        private const int WideStripMaxWordsBeforeSplit = 12;

        /// <summary>
        /// Minimum words for a speak unit to survive full-order / crop-primary merge.
        /// 1 keeps short openers and one-word balloons; SpeechCleaner.IsUnusableOcrText
        /// still applies. Detect scrap islands are separate.
        /// </summary>
        private const int MinSpeakUnitWords = 1;

        /// <summary>
        /// Grow every OCR island by this fraction of its own size on each side.
        /// From <see cref="AppSettings.ComicInflateFracX"/> / <see cref="AppSettings.ComicInflateFracY"/>.
        /// </summary>
        private static double RegionInflateFractionY =>
            SpeakRunSettings.GetComicInflateFracY();
        private static double RegionInflateFractionX =>
            SpeakRunSettings.GetComicInflateFracX();
        /// <summary>
        /// Below this max-side (px), islands with non-zero grow get a 2px
        /// minimum pad so lettering is not crop-starved. No hard 16px floor.
        /// </summary>
        private const int RegionInflateSmallMaxSide = 280;

        /// <summary>
        /// Wide/tall captures with this few regions are treated as low-confidence
        /// (likely missed balloons) ? higher-scale redetect + full-frame fill.
        /// </summary>
        private const int LowConfidenceMaxRegions = 1;
        private const int LowConfidenceMinCaptureWidth = 1000;
        private const int LowConfidenceMinCaptureArea = 400_000;

        /// <summary>
        /// Wide comic panels often hide a whole balloon at 1.75-. Treat this many
        /// islands (or fewer) as sparse so we retry harder + orphan-blob fill.
        /// </summary>
        private const int WidePanelSparseMaxRegions = 2;
        private const double WidePanelMinAspect = 1.55;

        /// <summary>
        /// Cap raw OCR line dumps in last_ocr detail (avoid huge logs).
        /// </summary>
        /// <summary>
        /// Map user refine rects (pipeline space, list = reading order) into detect regions.
        /// Clamps and drops degenerate boxes.
        /// </summary>
        private static List<DetectedTextRegion> RegionsFromOverride(
            IReadOnlyList<Rectangle> raw,
            int pipeW,
            int pipeH)
        {
            var list = new List<DetectedTextRegion>(raw.Count);
            foreach (var r0 in raw)
            {
                var r = r0;
                if (r.Width < 4 || r.Height < 4)
                    continue;
                int x = Math.Clamp(r.X, 0, Math.Max(0, pipeW - 1));
                int y = Math.Clamp(r.Y, 0, Math.Max(0, pipeH - 1));
                int w = Math.Clamp(r.Width, 4, pipeW - x);
                int h = Math.Clamp(r.Height, 4, pipeH - y);
                if (w < 4 || h < 4)
                    continue;
                list.Add(new DetectedTextRegion
                {
                    Bounds = new Rectangle(x, y, w, h),
                    WinOcrText = "",
                });
                if (list.Count >= MaxTextRegions)
                    break;
            }
            return list;
        }

        private sealed class DetectionResult
        {
            public List<DetectedTextRegion> Regions { get; init; } = new();
            public bool LowConfidence { get; init; }
            /// <summary>WinOCR boxes look like word scraps, not full balloons.</summary>
            public bool LooksFragmented { get; init; }
            public string Detail { get; init; } = "";
        }

        private readonly struct RegionReadResult
        {
            public string? Text { get; init; }
            public bool KoboldFailed { get; init; }
            public bool ExpandedRetry { get; init; }
        }

        /// <summary>Lazy OCR engine — see <see cref="BalloonOcrDetect.GetEngine"/>.</summary>
        private static OcrEngine? GetWinOcrEngine() => BalloonOcrDetect.GetEngine();

        public float DuckVolumeLevel { get; set; } = 0.15f;

        /// <summary>
        /// Optional UI hook: run just before the screen snap (e.g. dim/hide overlay).
        /// May be invoked off the UI thread — marshal as needed.
        /// </summary>
        public Action? PrepareForCapture { get; set; }

        /// <summary>
        /// Optional UI hook: run right after the screen snap (e.g. restore overlay).
        /// May be invoked off the UI thread — marshal as needed.
        /// </summary>
        public Action? RestoreAfterCapture { get; set; }

        // Duck state is process-wide so concurrent OcrProcessor hosts cannot each
        // sample already-ducked volume as "original" and leave apps stuck quiet.
        private static readonly object DuckLock = new();
        private static readonly List<(SimpleAudioVolume VolumeControl, float OriginalVolume)>
            DuckedSessions = new();

        private readonly HashSet<string> _excludedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "SpeakRect", "explorer", "shellexperiencehost", "searchapp",
            "dllhost", "smartscreen", "audiosrv", "conhost"
        };

        private readonly Rectangle _rect;
        private readonly List<Point>? _lassoPoints;
        private readonly bool _isEllipse;
        private string _lastText = "";

        private readonly WinSpeech.SpeechSynthesizer _synth;
        private readonly MediaPlayer _player;
        private readonly object _ttsLock = new();
        private readonly object _sapiLock = new();
        private SapiSpeech.SpeechSynthesizer? _sapiSynth;
        private CancellationTokenSource? _processingCts;

        /// <summary>
        /// Process-wide generation for live <see cref="Start"/> speaks. Stop/preempt
        /// bumps this so a cancelled host does not keep TTS after a new host starts.
        /// </summary>
        private static int LiveSpeakGeneration;
        private int _speakGeneration;

        private static readonly string DebugFolder = Path.Combine(
            AppContext.BaseDirectory, "debug_images");

        private static string DebugArchiveFolder => Path.Combine(DebugFolder, "archive");

        private static readonly object LastResultLock = new();

        /// <summary>Most recent completed (or mid-run plan) OCR result for Analytics UI.</summary>
        public static OcrLastResult? LastResult { get; private set; }

        /// <summary>
        /// Hard cap on images kept for one run (many balloons + stages).
        /// Each image is full pipeline resolution (no analytics downscale) so
        /// Preview / Live / Analytics show the same pipe pixels.
        /// </summary>
        private const int AnalyticsMaxImages = 48;

        /// <summary>In-flight image list for the current CaptureAndRecognizeAsync run.</summary>
        private List<OcrResultImage>? _runImages;

        /// <summary>Detect fog knobs for the current run (published into <see cref="OcrLastResult"/>).</summary>
        private float _runFogAmountUsed;

        /// <summary>Create debug_images/ — Debug builds only (no-op in Release/publish).</summary>
        private static void EnsureDebugFolder()
        {
#if DEBUG
            try
            {
                if (!Directory.Exists(DebugFolder))
                    Directory.CreateDirectory(DebugFolder);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] EnsureDebugFolder: {ex.Message}");
            }
#endif
        }

        public OcrProcessor(Rectangle rect, List<Point>? lassoPoints = null, bool isEllipse = false)
        {
            _rect = rect;
            _lassoPoints = (lassoPoints != null && lassoPoints.Count > 2) ? new List<Point>(lassoPoints) : null;
            _isEllipse = isEllipse;

            _synth = new WinSpeech.SpeechSynthesizer();
            ApplyVoiceSettings(_synth);

            _player = new MediaPlayer();
            // Do NOT RestoreAudio on MediaEnded - multi-block comic TTS ducks once
            // for the whole speak loop; unducking between balloons would be wrong.
            _player.MediaFailed += (_, e) =>
                Debug.WriteLine($"Media failed: {e.Error}");

            // Do not create debug_images/ in Release — only EnsureDebugFolder when dumping.
        }

        public void Start()
        {
            // New hotkey snap/speak preempts background Balloons refine speak (overlay hide).
            CancelBackgroundComicSpeak();

            CancellationToken token;
            lock (_ttsLock)
            {
                // Cancel any in-flight capture/TTS on this instance
                try { _processingCts?.Cancel(); } catch { /* ignore */ }
                try { _processingCts?.Dispose(); } catch { /* ignore */ }
                try
                {
                    _player.Pause();
                    _player.Source = null;
                }
                catch { }
                CancelSapiSpeech();

                _processingCts = new CancellationTokenSource();
                token = _processingCts.Token;
            }

            // Unduck previous session before a new snap (speak path ducks again if needed)
            RestoreAudio();
            // Bump generation so a Stop()'d host that still runs ignores late TTS.
            int gen = Interlocked.Increment(ref LiveSpeakGeneration);
            _speakGeneration = gen;
            Task.Run(async () =>
            {
                try
                {
                    await CaptureAndRecognizeAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on Stop / preempt.
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OCR] CaptureAndRecognizeAsync: {ex.Message}");
                }
            });
        }

        public void Stop()
        {
            // Invalidate this host's speak generation first so in-flight work
            // that only checks generation (not CTS) still bails.
            _speakGeneration = -1;
            lock (_ttsLock)
            {
                try { _processingCts?.Cancel(); } catch { /* ignore */ }
                try { _processingCts?.Dispose(); } catch { /* ignore */ }
                _processingCts = null;

                try
                {
                    _player.Pause();
                    _player.Source = null;
                }
                catch { }
                CancelSapiSpeech();
            }

            RestoreAudio();
        }

        /// <summary>
        /// True when this host is still the active live speak generation
        /// (not Stop()'d / not replaced by a newer Start).
        /// </summary>
        private bool IsLiveSpeakCurrent =>
            _speakGeneration > 0 && _speakGeneration == Volatile.Read(ref LiveSpeakGeneration);

        // ---- Background comic speak (Balloons refine on overlay hide) ----
        private static readonly object BgComicSpeakLock = new();
        private static CancellationTokenSource? _bgComicSpeakCts;
        private static OcrProcessor? _bgComicSpeakHost;

        /// <summary>
        /// Stop in-flight <see cref="SpeakComicFromBitmapAsync"/> TTS/OCR (e.g. refine
        /// speak after overlay hide) so a new hotkey snap/speak can take over.
        /// </summary>
        public static void CancelBackgroundComicSpeak()
        {
            CancellationTokenSource? cts;
            OcrProcessor? host;
            lock (BgComicSpeakLock)
            {
                cts = _bgComicSpeakCts;
                host = _bgComicSpeakHost;
                _bgComicSpeakCts = null;
                _bgComicSpeakHost = null;
            }
            try { cts?.Cancel(); } catch { /* ignore */ }
            try { host?.Stop(); } catch { /* ignore */ }
            try { cts?.Dispose(); } catch { /* ignore */ }
        }

        // OcrProcessor is not IDisposable historically; smoke tests use `using` for clarity.
        public void Dispose()
        {
            Stop();
            lock (_sapiLock)
            {
                try { _sapiSynth?.Dispose(); } catch { /* ignore */ }
                _sapiSynth = null;
            }
            // WinRT TTS + MediaPlayer must be released — SnapRegionOnly used to spin up
            // a full host per click and leak these, which eventually breaks screen snap.
            try { _player.Source = null; } catch { /* ignore */ }
            try { _player.Dispose(); } catch { /* ignore */ }
            try { _synth?.Dispose(); } catch { /* ignore */ }
        }

        /// <summary>
        /// Build the Balloons detect-view bitmap (prep + optional gray fog) without
        /// running WinOCR. Same pixels WinOCR would see. Caller owns the return.
        /// Used to refresh fog preview when boxes are locked (non-POI).
        /// </summary>
        public static Bitmap BuildComicDetectViewBitmap(Bitmap rawSnap)
        {
            if (rawSnap == null)
                throw new ArgumentNullException(nameof(rawSnap));

            AppSettings.Current.NormalizeComicRegionSettings();
            AppSettings.Current.NormalizeImagePrepSettings();

            using var stages = BuildImagePrepStages(
                rawSnap, buildTone: true, detail: null);
            Bitmap tone = stages.ToneOrPre;
            // Detect view only: fog never applied to Local-LLM tone path.
            using var pair = ComicDetectTonePair.Create(
                tone,
                EnableWinOcrDetectGrayFog,
                WinOcrDetectGrayFogAmount,
                WinOcrDetectGrayFogLevel,
                ApplyGrayFog);
            if (pair.DetectIsSeparateFog)
                return pair.ReleaseDetect(); // caller owns fog
            return new Bitmap(tone);
        }

        /// <summary>
        /// Build the Comic Book tone (Local-LLM / POI base) without WinOCR.
        /// Caller owns the return. Used when POI is on so locked-box fog refreshes
        /// never swap the refine surface onto detect fog (VL still reads tone).
        /// </summary>
        public static Bitmap BuildComicToneViewBitmap(Bitmap rawSnap)
        {
            if (rawSnap == null)
                throw new ArgumentNullException(nameof(rawSnap));

            AppSettings.Current.NormalizeImagePrepSettings();

            using var stages = BuildImagePrepStages(
                rawSnap, buildTone: true, detail: null);
            return new Bitmap(stages.ToneOrPre);
        }

        /// <summary>
        /// Run Comic Book prep + WinOCR detect + region improve (no Kobold) for Settings preview.
        /// Preview base is the detect view (fog when on). Uses live comic-region knobs.
        /// Caller disposes the result.
        /// </summary>
        public static async Task<ComicRegionPreviewResult> PreviewComicRegionsAsync(
            Bitmap rawSnap,
            CancellationToken token = default)
        {
            if (rawSnap == null)
                throw new ArgumentNullException(nameof(rawSnap));

            AppSettings.Current.NormalizeComicRegionSettings();
            AppSettings.Current.NormalizeImagePrepSettings();

            var detail = new StringBuilder();
            detail.AppendLine("preview=ComicBook detect (no TTS)");
            detail.AppendLine(
                $"prep={(EnableImagePrep ? "on" : "off")}" +
                $" gray={(EnablePipelineGrayscale ? "on" : "off")}" +
                $" fog={(EnableWinOcrDetectGrayFog ? "on" : "off")}" +
                $" amount={WinOcrDetectGrayFogAmount:0.###}" +
                $" grow={RegionInflateFractionX:0.##}/{RegionInflateFractionY:0.##}" +
                $" cropPad={TextRegionPadding}" +
                $" mergeOverlap={(EnableMergeOverlappingIslands ? "on" : "off")}");
            detail.AppendLine($"source {rawSnap.Width}x{rawSnap.Height}");

            ImagePrepStages? prepStages = null;
            Bitmap? fogOwned = null;
            Bitmap? overlay = null;

            try
            {
                // Same Image prep as live ComicBook + Settings → Image (tone for OCR).
                prepStages = BuildImagePrepStages(
                    rawSnap, buildTone: true, detail);
                Bitmap toneOwned = prepStages.ToneOrPre;

                token.ThrowIfCancellationRequested();

                // Same detect entry as live (fixed fog when enabled).
                using var host = new OcrProcessor(new Rectangle(0, 0, 2, 2));
                var (regions, _, _, detectImage, ownsDetect, fogUsed) =
                    await host.BuildComicRegionsSharedDetectAsync(
                        toneOwned, detail, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (ownsDetect)
                    fogOwned = detectImage;

                int pipeW = toneOwned.Width;
                int pipeH = toneOwned.Height;

                // Display boxes = grow cores + crop pad (what user sees / what speak crops).
                var previewBoxes = ExpandRegionsByCropPad(
                    regions, pipeW, pipeH, TextRegionPadding);
                var displayRects = previewBoxes.Select(r => r.Bounds).ToList();

                // POI: preview canvas = TONE (VL canvas). Non-POI: detect image so fog is visible
                bool poiMode = SpeakRunSettings.GetComicPoiMarkers();
                detail.AppendLine(
                    $"grow X/Y={RegionInflateFractionX:0.##}/{RegionInflateFractionY:0.##} " +
                    $"cropPad={TextRegionPadding}px " +
                    $"(display boxes = grow + pad; " +
                    $"preview base={(poiMode ? "TONE (POI=live VL canvas)" : "detect @ fog")}; " +
                    $"fogUsed={fogUsed:0.###})");

                Bitmap detectForOverlay = detectImage;
                overlay = BuildRegionsOverlayBitmap(
                    poiMode ? toneOwned : detectForOverlay, previewBoxes);

                Bitmap baseImage = new Bitmap(poiMode ? toneOwned : detectForOverlay);

                return new ComicRegionPreviewResult
                {
                    Overlay = overlay,
                    BaseImage = baseImage,
                    // Padded display rects — Speak override uses pad=0 (boxes already final).
                    Regions = displayRects,
                    PipelineWidth = pipeW,
                    PipelineHeight = pipeH,
                    RegionCount = regions.Count,
                    FogAmountUsed = fogUsed,
                    Detail = detail.ToString(),
                };
            }
            catch (OperationCanceledException)
            {
                try { overlay?.Dispose(); } catch { /* ignore */ }
                throw;
            }
            catch (Exception ex)
            {
                try { overlay?.Dispose(); } catch { /* ignore */ }
                detail.AppendLine($"preview failed: {ex.Message}");
                Debug.WriteLine($"[OCR] PreviewComicRegions: {ex.Message}");
                return new ComicRegionPreviewResult
                {
                    Overlay = null,
                    BaseImage = null,
                    Regions = Array.Empty<Rectangle>(),
                    RegionCount = 0,
                    Detail = detail.ToString(),
                };
            }
            finally
            {
                prepStages?.Dispose();
                try { fogOwned?.Dispose(); } catch { /* ignore */ }
                // overlay / BaseImage ownership transferred to result (or disposed on failure)
            }
        }

        /// <summary>
        /// Settings → Balloons: run Comic Book detect + crop-stack/full OCR + TTS on
        /// a still image (same knobs as live Comic Book). Always uses the comic
        /// pipeline — does <b>not</b> mutate <see cref="AppSettings.ComicBook"/> so a
        /// mid-speak mode toggle is not clobbered on finally.
        /// </summary>
        /// <param name="regionOverride">
        /// When non-null and non-empty: skip WinOCR detect and use these pipeline-space
        /// core rects (reading order = list order) for crop-stack. Crop pad still applied.
        /// </param>
        public static async Task<ComicRegionSpeakResult> SpeakComicFromBitmapAsync(
            Bitmap rawSnap,
            CancellationToken token = default,
            IReadOnlyList<Rectangle>? regionOverride = null)
        {
            if (rawSnap == null)
                throw new ArgumentNullException(nameof(rawSnap));

            AppSettings.Current.NormalizeComicRegionSettings();
            AppSettings.Current.NormalizeVoiceSettings();

            // One background comic speak at a time; also preempted by OcrProcessor.Start().
            CancelBackgroundComicSpeak();

            var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            var host = new OcrProcessor(new Rectangle(0, 0, 2, 2));
            lock (BgComicSpeakLock)
            {
                _bgComicSpeakCts = linked;
                _bgComicSpeakHost = host;
            }

            try
            {
                // Core is the Comic Book path regardless of MODE (no AppSettings flip).
                return await host.RunComicSpeakFromBitmapCoreAsync(
                        rawSnap, linked.Token, regionOverride)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ComicRegionSpeakResult
                {
                    Overlay = null,
                    RegionCount = regionOverride?.Count ?? 0,
                    SpokenText = "",
                    Detail = "cancelled (new speak/snap)",
                    Unreadable = true,
                };
            }
            finally
            {
                lock (BgComicSpeakLock)
                {
                    if (_bgComicSpeakHost == host)
                    {
                        _bgComicSpeakHost = null;
                        _bgComicSpeakCts = null;
                    }
                }
                try { host.Stop(); } catch { /* ignore */ }
                try { host.Dispose(); } catch { /* ignore */ }
                try { linked.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>Cancel in-flight Balloons-tab speak/preview TTS on a host instance.</summary>
        public void CancelSpeak()
        {
            Stop();
        }

        private async Task<ComicRegionSpeakResult> RunComicSpeakFromBitmapCoreAsync(
            Bitmap rawSnap,
            CancellationToken token,
            IReadOnlyList<Rectangle>? regionOverride = null)
        {
            // Freeze knobs for this Balloons still-image speak (same as live).
            using var _runSnap = SpeakRunSettings.Push(SpeakRunSettings.CaptureFromApp());

            var detail = new StringBuilder();
            detail.AppendLine("speak-test=ComicBook full path (no screen capture)");
            detail.AppendLine(
                $"settings: fog={(EnableWinOcrDetectGrayFog ? "on" : "off")}" +
                $" amount={WinOcrDetectGrayFogAmount:0.###}" +
                $" inflate={RegionInflateFractionX:0.##}/{RegionInflateFractionY:0.##}" +
                $" pad={TextRegionPadding}" +
                $" mergeOverlap={(EnableMergeOverlappingIslands ? "on" : "off")}");
            detail.AppendLine($"source {rawSnap.Width}x{rawSnap.Height}");
            bool useOverride = regionOverride != null && regionOverride.Count > 0;
            if (useOverride)
                detail.AppendLine($"region-override={regionOverride!.Count} (skip WinOCR detect; crop-pad baked into boxes)");

            ImagePrepStages? prepStages = null;
            Bitmap? fogOwned = null;
            Bitmap? overlay = null;
            var pipeTimer = new PipelineTimer();
            var totalSw = Stopwatch.StartNew();
            bool ducked = false;

            // Balloons solid boxes already include crop pad — do not pad again
            // (per-host; does not affect concurrent live CaptureAndRecognizeAsync).
            if (useOverride)
                _forcedCropPadPx = 0;

            // Analytics: same publish path as live (was a silent no-op before).
            _runImages = new List<OcrResultImage>(16);
            ClearRunFogAnalytics();

            try
            {
                // Ensure Local-LLM host is up for recognition
                try
                {
                    LocalLlmHost.Start();
                    if (!LocalLlmHost.IsApiReady())
                    {
                        detail.AppendLine("waiting for Local-LLM host…");
                        bool ready = await LocalLlmHost.WaitUntilReadyAsync(
                            TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
                        if (!ready)
                        {
                            detail.AppendLine("Local-LLM host not ready — recognition will likely fail");
                            SpeakAnnouncement(
                                "Local-LLM is not ready yet. Wait for the local model to finish loading.");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    detail.AppendLine($"OCR ready wait: {ex.Message}");
                }

                var sw = Stopwatch.StartNew();
                // Shared Image prep (same as live ComicBook + Settings → Image).
                prepStages = BuildImagePrepStages(
                    rawSnap, buildTone: true, detail);
                Bitmap toneOwned = prepStages.ToneOrPre;
                Bitmap letterboxOwned = prepStages.Letterbox;
                Bitmap? upscaleOwned = prepStages.Upscale;
                Bitmap? grayOwned = prepStages.Gray;
                pipeTimer.Mark("image-prep", sw);
                // Same Analytics stage keys as live CaptureAndRecognizeAsync.
                CaptureAnalyticsImage("capture", "Capture", rawSnap);
                if (letterboxOwned.Width != rawSnap.Width ||
                    letterboxOwned.Height != rawSnap.Height)
                {
                    CaptureAnalyticsImage("letterbox", "Letterbox", letterboxOwned);
                }
                if (upscaleOwned != null &&
                    (upscaleOwned.Width != letterboxOwned.Width ||
                     upscaleOwned.Height != letterboxOwned.Height))
                {
                    CaptureAnalyticsImage("upscale", "Upscale", upscaleOwned);
                }
                if (grayOwned != null)
                    CaptureAnalyticsImage("gray", "Ink gray", grayOwned);
                CaptureAnalyticsImage("ocr_prep", "OCR prep / tone", toneOwned);

                token.ThrowIfCancellationRequested();

                int pipeW = toneOwned.Width;
                int pipeH = toneOwned.Height;
                List<DetectedTextRegion> regions;
                bool fragmented;
                bool solidIslands;
                bool scrapDetect;
                Bitmap detectImage;
                float fogUsed = 0f;

                if (useOverride)
                {
                    // Override skips WinOCR; still build detect view for overlay/analytics.
                    using (var toneDetect = ComicDetectTonePair.Create(
                        toneOwned,
                        EnableWinOcrDetectGrayFog,
                        WinOcrDetectGrayFogAmount,
                        WinOcrDetectGrayFogLevel,
                        ApplyGrayFog))
                    {
                        if (toneDetect.DetectIsSeparateFog)
                        {
                            fogOwned = toneDetect.ReleaseDetect();
                            fogUsed = WinOcrDetectGrayFogAmount;
                            detail.AppendLine(
                                $"fog amount={fogUsed:0.###} (detect view only; region-override)");
                        }
                        else
                        {
                            detail.AppendLine("fog off (detect on tone; region-override)");
                        }
                    }
                    detectImage = fogOwned ?? toneOwned;
                    SetRunFogAnalytics(fogUsed);

                    sw.Restart();
                    regions = RegionsFromOverride(regionOverride!, pipeW, pipeH);
                    pipeTimer.Mark("region-override", sw);
                    detail.AppendLine($"reading-blocks={regions.Count} (user refine)");
                    fragmented = false;
                    solidIslands = HasWellSeparatedSolidIslands(regions, pipeW, pipeH);
                    scrapDetect = false;
                    detail.AppendLine(
                        $"(override solid={solidIslands} regions={regions.Count})");
                }
                else
                {
                    // Same shared detect as live + Balloons preview (fixed fog when on).
                    sw.Restart();
                    DetectionResult detection;
                    Bitmap detImg;
                    bool ownsDet;
                    (regions, detection, fragmented, detImg, ownsDet, fogUsed) =
                        await BuildComicRegionsSharedDetectAsync(
                            toneOwned, detail, token).ConfigureAwait(false);
                    if (ownsDet)
                        fogOwned = detImg;
                    detectImage = detImg;
                    SetRunFogAnalytics(fogUsed);
                    pipeTimer.Mark(
                        $"winocr-detect+regions (fog={fogUsed:0.00})",
                        sw);
                    solidIslands = HasWellSeparatedSolidIslands(
                        regions, pipeW, pipeH);
                    scrapDetect = LooksLikeScrapDetect(
                        regions, pipeW, pipeH, fragmented);
                    detail.AppendLine(
                        $"(lowConf={detection.LowConfidence} frag={fragmented} " +
                        $"scrap={scrapDetect} solid={solidIslands} regions={regions.Count} " +
                        $"fogUsed={fogUsed:0.###}" +
                        ")");
                }

                if (!ReferenceEquals(detectImage, toneOwned))
                    CaptureAnalyticsImage("detect", "Detect (fog)", detectImage);

                // Same green boxes as Settings → Balloons: grow cores + settings crop pad
                // on detect view (fog when on). Use settings pad even when override pad=0
                // so the returned overlay / Analytics match what the user tuned.
                int overlayPad = Math.Max(0, SpeakRunSettings.GetComicRegionPadding());
                // Override boxes from Balloons already include pad — do not expand again.
                var speakOverlayBoxes = useOverride
                    ? regions
                    : ExpandRegionsByCropPad(regions, pipeW, pipeH, overlayPad);
                overlay = BuildRegionsOverlayBitmap(detectImage, speakOverlayBoxes);
                // Publish for Analytics when speaking from Balloons / still image
                try
                {
                    CaptureAnalyticsImage(
                        "regions",
                        "WinOCR detect boxes (fog when on; not VL input)",
                        overlay);
                }
                catch { /* ignore */ }

                var spokenParts = new List<string>();
                string chosenTag;

                // Same strategy as live: POI (island canvases) or per-island speak.
                bool usePoi =
                    SpeakRunSettings.GetComicPoiMarkers() &&
                    regions.Count > 0;
                // Non-POI multi: per-island VL+TTS (no §9 toggle / crop-stack mode).
                bool usePerIsland = !usePoi && regions.Count > 0;

                if (usePoi)
                {
                    var (poiParts, poiTag, poiDucked) =
                        await RunComicPoiGuideAsync(
                            toneOwned, regions, pipeW, pipeH,
                            detail, pipeTimer, token,
                            speakNow: true, alreadyDucked: ducked)
                        .ConfigureAwait(false);
                    spokenParts = poiParts;
                    chosenTag = poiTag;
                    ducked = poiDucked;
                    detail.AppendLine(
                        $"speak-plan units={spokenParts.Count} tag={chosenTag}");
                }
                else if (usePerIsland)
                {
                    var (seqParts, seqTag, seqDucked) =
                        await RunSequentialRegionsSpeakAsync(
                            toneOwned, regions, detail, pipeTimer, token,
                            speakNow: true, alreadyDucked: ducked)
                        .ConfigureAwait(false);
                    spokenParts = seqParts;
                    chosenTag = seqTag;
                    ducked = seqDucked;
                    detail.AppendLine(
                        $"speak-plan units={spokenParts.Count} tag={chosenTag}");
                }
                else
                {
                    List<string> chosen;
                    {
                        var (fbChosen, fbTag) = await RunFullAndCropsBestOfAsync(
                            toneOwned, regions, scrapDetect, solidIslands,
                            detail, pipeTimer, token).ConfigureAwait(false);
                        chosen = fbChosen;
                        chosenTag = fbTag;
                    }

                    // Same fallback ladder as live Comic Book
                    if (chosen.Count == 0 || chosen.All(SpeechCleaner.IsUnusableOcrText))
                    {
                        sw.Restart();
                        string? offClean = await RunFullFrameKoboldOnBitmapAsync(
                            toneOwned,
                            detail,
                            token,
                            savePrep: false).ConfigureAwait(false);
                        pipeTimer.Mark("full-frame-retry", sw);
                        if (!SpeechCleaner.IsUnusableOcrText(offClean))
                        {
                            chosen = new List<string> { offClean! };
                            chosenTag = "full-frame-retry";
                            detail.AppendLine(
                                $"winner=full-frame-retry words={ComicRegionGeometry.CountWords(offClean!)}");
                        }
                    }

                    if (chosen.Count == 0 || chosen.All(SpeechCleaner.IsUnusableOcrText))
                    {
                        sw.Restart();
                        var winParts = await TryWinOcrSpeakFallbackAsync(
                            detectImage, regions, detail, token).ConfigureAwait(false);
                        pipeTimer.Mark("winocr-speak-fallback", sw);
                        if (winParts.Count > 0)
                        {
                            chosen = winParts;
                            chosenTag = "winocr-fallback";
                            detail.AppendLine(
                                $"winner=winocr-fallback parts={winParts.Count}");
                        }
                    }

                    var speakPieces = SpeechCleaner.ExpandToSpeakPieces(chosen);
                    if (speakPieces.Count >= 2)
                    {
                        int beforeDedup = speakPieces.Count;
                        speakPieces = SpeechCleaner.DedupeSpeakPiecesForTts(speakPieces, detail);
                        if (speakPieces.Count != beforeDedup)
                        {
                            detail.AppendLine(
                                $"speak-dedupe {beforeDedup} → {speakPieces.Count}");
                        }
                    }
                    if (speakPieces.Count >= 2)
                    {
                        int beforeCoal = speakPieces.Count;
                        speakPieces = SpeechCleaner.CoalesceFragmentSpeakPieces(speakPieces, detail);
                        if (speakPieces.Count != beforeCoal)
                        {
                            detail.AppendLine(
                                $"speak-coalesce {beforeCoal} → {speakPieces.Count}");
                        }
                    }

                    detail.AppendLine(
                        $"speak-plan units={speakPieces.Count} tag={chosenTag}");

                    if (speakPieces.Count > 0)
                    {
                        DuckOtherAudio();
                        ducked = true;
                        for (int pi = 0; pi < speakPieces.Count; pi++)
                        {
                            token.ThrowIfCancellationRequested();
                            string unit = speakPieces[pi].Text;
                            spokenParts.Add(unit);
                            detail.AppendLine(
                                $"speak[{chosenTag} {pi + 1}/{speakPieces.Count}]: {unit}");
                            sw.Restart();
                            await SpeakWithSystemAsync(unit, token).ConfigureAwait(false);
                            pipeTimer.Mark($"tts[{pi + 1}]", sw);

                            int pauseMs = speakPieces[pi].PauseAfterMs;
                            if (pauseMs > 0)
                                await Task.Delay(pauseMs, token).ConfigureAwait(false);
                        }
                    }
                }

                totalSw.Stop();
                pipeTimer.Mark("TOTAL wall-clock", totalSw);
                detail.AppendLine();
                detail.AppendLine("--- timings (ms) ---");
                detail.Append(pipeTimer.FormatReport());

                // Use platform newlines so WinForms multiline TextBoxes render
                // unit breaks (bare \n is often invisible until paste).
                string finalJoined = string.Join(
                    Environment.NewLine + Environment.NewLine, spokenParts);
                bool unreadable = spokenParts.Count == 0;
                if (unreadable)
                {
                    detail.AppendLine("speak-test: unreadable");
                    DuckOtherAudio();
                    ducked = true;
                    await SpeakWithSystemAsync("unreadable", token).ConfigureAwait(false);
                }

                // Publish to Analytics without clearing Balloons refine session.
                WriteLastOcrDebug(
                    unreadable ? "(unreadable)" : finalJoined,
                    detail,
                    notifyNewCapture: false);

                return new ComicRegionSpeakResult
                {
                    Overlay = overlay,
                    RegionCount = regions.Count,
                    SpokenText = finalJoined,
                    Detail = detail.ToString(),
                    Unreadable = unreadable,
                };
            }
            catch (OperationCanceledException)
            {
                try { overlay?.Dispose(); } catch { /* ignore */ }
                detail.AppendLine("speak-test cancelled");
                throw;
            }
            catch (Exception ex)
            {
                try { overlay?.Dispose(); } catch { /* ignore */ }
                detail.AppendLine($"speak-test failed: {ex.Message}");
                Debug.WriteLine($"[OCR] SpeakComicFromBitmap: {ex.Message}");
                return new ComicRegionSpeakResult
                {
                    Overlay = null,
                    RegionCount = 0,
                    SpokenText = "",
                    Detail = detail.ToString(),
                    Unreadable = true,
                };
            }
            finally
            {
                _forcedCropPadPx = null;
                if (ducked)
                    RestoreAudio();
                prepStages?.Dispose();
                try { fogOwned?.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Settings → Image: same letterbox → upscale → gray → tone as live.
        /// Default and ComicBook share this prep (mode only changes OCR strategy).
        /// No WinOCR / Kobold / detect fog (fog is Balloons-only). Default stage
        /// <c>tone</c> is live OCR input. Intermediate: source/letterbox/upscale/gray.
        /// </summary>
        public static ImagePrepPreviewResult PreviewImagePrep(
            Bitmap rawSnap,
            string stage = "tone")
        {
            if (rawSnap == null)
                throw new ArgumentNullException(nameof(rawSnap));

            AppSettings.Current.NormalizeImagePrepSettings();

            var detail = new StringBuilder();
            detail.AppendLine("preview=image prep (no OCR; fog is Balloons-only)");
            detail.AppendLine(
                $"prep={(EnableImagePrep ? "on" : "off")}" +
                $" letterbox={(EnableLetterbox ? "on" : "off")}" +
                $" pad={LetterboxContentPad}" +
                $" black={LetterboxBlackThreshold} white={LetterboxWhiteThreshold}" +
                $" upscale={ActivePipelineUpscaleLongSide}" +
                $" gray={(EnablePipelineGrayscale ? "on" : "off")}" +
                $" inkW={InkGrayMinWeight:0.##}" +
                $" denoiseR={DenoiseSpatialRadius} sigma={DenoiseRangeSigma:0.#}" +
                $" levels={(EnableAutoLevels ? "on" : "off")}" +
                $" lo/hi={AutoLevelsLowPercentile:0.#}/{AutoLevelsHighPercentile:0.#}" +
                $" sharp={PipelineSharpenAmount:0.##}×{PipelineSharpenPasses}" +
                " (Default+ComicBook same prep)");
            detail.AppendLine($"source {rawSnap.Width}x{rawSnap.Height}");

            string want = (stage ?? "tone").Trim().ToLowerInvariant();
            // Fog is Balloons detect-only — ignore legacy "fog"/"detect" stage requests.
            if (want is "fog" or "detect" or "live" or "ocr")
                want = "tone";

            try
            {
                using var stages = BuildImagePrepStages(
                    rawSnap, buildTone: true, detail);

                Bitmap pick;
                string stageName;
                if (want == "source")
                {
                    pick = rawSnap;
                    stageName = "source";
                }
                else if (want == "letterbox")
                {
                    pick = stages.Letterbox;
                    stageName = "letterbox";
                }
                else if (want == "upscale")
                {
                    pick = stages.Upscale;
                    stageName = "upscale";
                }
                else if (want == "gray")
                {
                    pick = stages.PreTone;
                    stageName = stages.Gray != null ? "gray" : "upscale (no gray)";
                }
                else
                {
                    // Full-pipeline end = live OCR input (both modes use tone).
                    pick = stages.LiveOcrInput;
                    stageName = stages.Tone != null
                        ? "tone (OCR input)"
                        : "pre-tone (OCR input)";
                }

                var display = new Bitmap(pick);
                detail.AppendLine(
                    $"display={stageName} {display.Width}x{display.Height}");

                return new ImagePrepPreviewResult
                {
                    Display = display,
                    StageName = stageName,
                    Detail = detail.ToString(),
                    Width = display.Width,
                    Height = display.Height,
                };
            }
            catch (Exception ex)
            {
                detail.AppendLine($"image-prep preview failed: {ex.Message}");
                Debug.WriteLine($"[OCR] PreviewImagePrep: {ex.Message}");
                return new ImagePrepPreviewResult
                {
                    Display = null,
                    StageName = want,
                    Detail = detail.ToString(),
                };
            }
        }

        /// <summary>
        /// Shared image-prep stages (letterbox → upscale → gray → optional tone).
        /// Used by Image preview, live ComicBook ON/OFF, and Balloons prep.
        /// Caller disposes the result.
        /// </summary>
        private sealed class ImagePrepStages : IDisposable
        {
            public Bitmap Letterbox { get; set; } = null!;
            public Bitmap Upscale { get; set; } = null!;
            public Bitmap? Gray { get; set; }
            public Bitmap? Tone { get; set; }
            public Rectangle ContentRect { get; set; }

            /// <summary>After gray (or upscale if gray off), before tone.</summary>
            public Bitmap PreTone => Gray ?? Upscale;

            /// <summary>After tone when built; else <see cref="PreTone"/>.</summary>
            public Bitmap ToneOrPre => Tone ?? PreTone;

            /// <summary>
            /// What live full-frame / crop OCR reads (no detect fog).
            /// Default and ComicBook share the same prep end (tone).
            /// </summary>
            public Bitmap LiveOcrInput => ToneOrPre;

            public void Dispose()
            {
                // Tone / Gray / Upscale / Letterbox may alias when prep is identity.
                var seen = new HashSet<Bitmap>();
                void Drop(Bitmap? b)
                {
                    if (b == null || !seen.Add(b)) return;
                    try { b.Dispose(); } catch { /* ignore */ }
                }
                Drop(Tone);
                Drop(Gray);
                Drop(Upscale);
                Drop(Letterbox);
                Tone = null;
                Gray = null;
                Upscale = null!;
                Letterbox = null!;
            }
        }

        /// <summary>
        /// Build letterbox → upscale → gray → tone with live Image settings.
        /// Same pixels for Image preview and live speak when settings match.
        /// </summary>
        private static ImagePrepStages BuildImagePrepStages(
            Bitmap rawSnap,
            bool buildTone,
            StringBuilder? detail)
        {
            if (rawSnap == null)
                throw new ArgumentNullException(nameof(rawSnap));

            AppSettings.Current.NormalizeImagePrepSettings();

            var letterbox = CropToContentOrClone(
                rawSnap, out var contentRect, detail: null);
            if (contentRect.Width != rawSnap.Width ||
                contentRect.Height != rawSnap.Height ||
                contentRect.X != 0 || contentRect.Y != 0)
            {
                detail?.AppendLine(
                    $"letterbox-trim {rawSnap.Width}x{rawSnap.Height} → " +
                    $"{letterbox.Width}x{letterbox.Height} " +
                    $"(content @({contentRect.X},{contentRect.Y}) " +
                    $"{contentRect.Width}x{contentRect.Height})");
            }
            else if (!EnableLetterbox)
            {
                detail?.AppendLine(
                    $"letterbox off — full frame {rawSnap.Width}x{rawSnap.Height}");
            }
            else
            {
                detail?.AppendLine(
                    $"letterbox-trim none ({rawSnap.Width}x{rawSnap.Height})");
            }

            int targetLongSide = ActivePipelineUpscaleLongSide;
            var upscale = ScaleMaintainAspectToLongSide(letterbox, targetLongSide);
            detail?.AppendLine(
                $"upscale {letterbox.Width}x{letterbox.Height} → " +
                $"{upscale.Width}x{upscale.Height} (long-edge {targetLongSide})");

            Bitmap? gray = null;
            Bitmap work = upscale;
            if (EnablePipelineGrayscale)
            {
                gray = ConvertToInkGrayscale(work);
                work = gray;
                detail?.AppendLine(
                    $"ink-gray → {work.Width}x{work.Height}");
            }
            else
            {
                detail?.AppendLine("ink-gray skipped (color path)");
            }

            Bitmap? tone = null;
            if (buildTone)
            {
                tone = ApplyPipelineTonePrep(work, skipDenoise: false);
                detail?.AppendLine(
                    $"tone → {tone.Width}x{tone.Height} " +
                    $"(denoise+levels+sharpen; prep={(EnableImagePrep ? "on" : "off")})");
            }

            return new ImagePrepStages
            {
                Letterbox = letterbox,
                Upscale = upscale,
                Gray = gray,
                Tone = tone,
                ContentRect = contentRect,
            };
        }

        /// <summary>
        /// Best-effort load of the live raw snap for Balloons / Image preview
        /// (same geometry live used). Uses DevCaptureCache / Analytics capture slot —
        /// never letterbox/upscale/tone stages (those would double-prep).
        /// </summary>
        public static Bitmap? TryLoadPreviewSourceBitmap()
        {
            try
            {
                var last = DevCaptureCache.TryLoadLastOcrCapture();
                if (last != null)
                    return last;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] TryLoadPreviewSource (last capture): {ex.Message}");
            }

            try
            {
                var cached = DevCaptureCache.CloneOrNull();
                if (cached != null)
                    return cached;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] TryLoadPreviewSource (cache): {ex.Message}");
            }

            return null;
        }

        private void CancelSapiSpeech()
        {
            lock (_sapiLock)
            {
                try { _sapiSynth?.SpeakAsyncCancelAll(); } catch { /* ignore */ }
            }
        }

        private void DuckOtherAudio()
        {
            // Process-wide: clear any prior duck first so originals stay correct.
            RestoreAudio();

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;

                uint currentPid = (uint)Process.GetCurrentProcess().Id;

                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.State != AudioSessionState.AudioSessionStateActive ||
                        session.IsSystemSoundsSession)
                        continue;

                    uint pid = session.GetProcessID;
                    if (pid == currentPid) continue;

                    string? procName = null;
                    try
                    {
                        using var p = Process.GetProcessById((int)pid);
                        procName = p.ProcessName;
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(procName) && _excludedProcesses.Contains(procName))
                        continue;

                    var vol = session.SimpleAudioVolume;
                    if (vol.Mute) continue;

                    float original = vol.Volume;
                    if (original < 0.01f) continue;

                    vol.Volume = DuckVolumeLevel;
                    lock (DuckLock)
                        DuckedSessions.Add((vol, original));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Ducking] Failed: {ex.Message}");
            }
        }

        private void RestoreAudio()
        {
            List<(SimpleAudioVolume, float)> toRestore;
            lock (DuckLock)
            {
                toRestore = new List<(SimpleAudioVolume, float)>(DuckedSessions);
                DuckedSessions.Clear();
            }

            foreach (var (ctrl, original) in toRestore)
                try { ctrl.Volume = original; } catch { }
        }

        private async Task CaptureAndRecognizeAsync(CancellationToken token)
        {
            // Freeze knobs for this run (MODE / pad / pauses / prompts / voice).
            using var _runSnap = SpeakRunSettings.Push(SpeakRunSettings.CaptureFromApp());
            // Fresh analytics image list for this run (published via WriteLastOcrDebug).
            _runImages = new List<OcrResultImage>(16);
            ClearRunFogAnalytics();
            try
            {
                // -------------------------------------------------------------
                // GOAL: do not miss text; read it correctly. Speed is secondary.
                // Timings in last_ocr.txt are for diagnosis - not a license to
                // strip recovery paths that find balloons or fix under-reads.
                // -------------------------------------------------------------
                // ComicBook OFF (Default): letterbox → upscale → ink-gray → tone →
                //     one full-frame Local-LLM call (no fog / detect / crops).
                // ComicBook ON + POI: tone + green boxes (± outside fog for map);
                //     AutoStack on (stock) → per-island orange canvas VL ×N;
                //     stack off/fail multi → §9 sequential or crop-stack;
                //     1 island + stack off → full-page guide VL.
                // ComicBook ON:
                //  0) Same Image prep → tone (Local-LLM) + optional fog (OCR detect only)
                //  1) Always run balloon OCR detect + region improve (when no override)
                //  2) Sequential regions or best-of (per settings) — not word-count gated
                // QuickWinOcrWordCountAsync is diagnostic / logging only (legacy threshold).
                var pipeTimer = new PipelineTimer();
                var totalSw = Stopwatch.StartNew();

                var sw = Stopwatch.StartNew();
                // Ensure Local-LLM host is up (auto-start can still be loading the GGUF).
                // Without this, ComicBook/Default both fail silently while the model warms.
                try
                {
                    LocalLlmHost.Start();
                    if (!LocalLlmHost.IsApiReady())
                    {
                        Debug.WriteLine("[OCR] Waiting for Local-LLM API (model load)…");
                        bool ready = await LocalLlmHost.WaitUntilReadyAsync(
                            TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
                        if (!ready)
                        {
                            Debug.WriteLine("[OCR] Local-LLM API not ready — recognition will likely fail.");
                            SpeakAnnouncement(
                                "Local-LLM is not ready yet. Wait for the local model to finish loading.");
                        }
                        else
                            Debug.WriteLine("[OCR] Local-LLM API ready.");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OCR] Local-LLM ready wait: {ex.Message}");
                }
                pipeTimer.Mark("ocr-engine-ready", sw);

                sw.Restart();
                // Let host dim/hide overlay chrome so the snap is clean (overlay stays open).
                try { PrepareForCapture?.Invoke(); } catch (Exception ex)
                {
                    Debug.WriteLine($"[OCR] PrepareForCapture: {ex.Message}");
                }
                await Task.Delay(80, token); // compositor settle after dim/hide
                pipeTimer.Mark("overlay-delay", sw);

                sw.Restart();
                Bitmap? snapped = null;
                try
                {
                    snapped = SnapCapture();
                }
                finally
                {
                    // Restore overlay even if snap fails — Enter does not close the UI.
                    try { RestoreAfterCapture?.Invoke(); } catch (Exception ex)
                    {
                        Debug.WriteLine($"[OCR] RestoreAfterCapture: {ex.Message}");
                    }
                }
                pipeTimer.Mark("snap-capture", sw);
                if (snapped == null)
                    return;
                using var rawSnap = snapped;

                // Full-res raw snap for Settings → Balloons/Image. Analytics PNGs are
                // long-edge capped — Balloons must not re-detect on those thumbnails
                // or green boxes diverge from live (same knobs, different pixels).
                DevCaptureCache.PublishLastCapture(rawSnap);

                Debug.WriteLine(
                    $"[OCR] capture {rawSnap.Width}x{rawSnap.Height}" +
                    (ComicBookOff ? " [ComicBook OFF / Default prep]" : " [ComicBook ON]"));

                // -- ComicBook OFF (Default): same prep → full-frame Kobold
                if (ComicBookOff)
                {
                    await RunComicBookOffPreparedSnapAsync(
                        rawSnap, pipeTimer, totalSw, token);
                    return;
                }

                ImagePrepStages? prepStages = null;
                Bitmap? letterboxOwned = null;
                Bitmap? upscaleOwned = null;
                Bitmap? grayOwned = null;
                Bitmap? toneOwned = null;
                Bitmap? fogOwned = null;
                // Same size always: ocrImage = tone (Kobold); detectImage = fog or tone (WinOCR).
                Bitmap ocrImage;
                Bitmap detectImage;
                try
                {
                    // Shared Image prep (same as Settings → Image preview).
                    sw.Restart();
                    prepStages = BuildImagePrepStages(
                        rawSnap, buildTone: true, detail: null);
                    letterboxOwned = prepStages.Letterbox;
                    upscaleOwned = prepStages.Upscale;
                    grayOwned = prepStages.Gray;
                    toneOwned = prepStages.Tone;
                    ocrImage = prepStages.LiveOcrInput; // tone (shared prep)
                    pipeTimer.Mark(
                        $"image-prep → {ocrImage.Width}x{ocrImage.Height}", sw);
                    Debug.WriteLine(
                        $"[OCR] image-prep letterbox {letterboxOwned.Width}x{letterboxOwned.Height} " +
                        $"upscale {upscaleOwned.Width}x{upscaleOwned.Height} " +
                        $"ocr {ocrImage.Width}x{ocrImage.Height}");

                    // Shared detect (fixed fog when on) — same entry as Balloons.
                    // Local-LLM always reads ocrImage/tone; WinOCR detect may use fog.
                    sw.Restart();
                    var detectLog = new StringBuilder();
                    List<DetectedTextRegion> regions;
                    DetectionResult detection;
                    bool fragmented;
                    float fogUsedLive;
                    {
                        var (regs, det, frag, detImg, ownsDet, fogAmt) =
                            await BuildComicRegionsSharedDetectAsync(
                                ocrImage, detectLog, token).ConfigureAwait(false);
                        if (ownsDet)
                            fogOwned = detImg;
                        detectImage = detImg;
                        regions = regs;
                        detection = det;
                        fragmented = frag;
                        fogUsedLive = fogAmt;
                        SetRunFogAnalytics(fogUsedLive);
                    }
                    pipeTimer.Mark(
                        $"detect-fog+regions (fog={fogUsedLive:0.00})",
                        sw);

                    sw.Restart();
                    // Analytics: one slot per real stage (no clones of the same pixels).
                    CaptureAnalyticsImage("capture", "Capture", rawSnap);
                    if (letterboxOwned.Width != rawSnap.Width ||
                        letterboxOwned.Height != rawSnap.Height)
                    {
                        CaptureAnalyticsImage("letterbox", "Letterbox", letterboxOwned);
                    }
                    if (upscaleOwned != null)
                        CaptureAnalyticsImage("upscale", "Upscale", upscaleOwned);
                    if (grayOwned != null)
                        CaptureAnalyticsImage("gray", "Ink gray", grayOwned);
                    CaptureAnalyticsImage("ocr_prep", "OCR prep / tone", ocrImage);
                    if (!ReferenceEquals(detectImage, ocrImage))
                        CaptureAnalyticsImage("detect", "Detect (fog)", detectImage);

                    if (ActiveAnyDebugArtifacts)
                    {
                        try
                        {
                            EnsureDebugFolder();
                            ClearStaleDebugArtifacts();
                            if (ActiveHeavyDebugImages)
                            {
                                rawSnap.Save(
                                    Path.Combine(DebugFolder, "last_capture.png"), ImageFormat.Png);
                                if (letterboxOwned.Width != rawSnap.Width ||
                                    letterboxOwned.Height != rawSnap.Height)
                                {
                                    letterboxOwned.Save(
                                        Path.Combine(DebugFolder, "last_letterbox.png"),
                                        ImageFormat.Png);
                                }
                                if (upscaleOwned != null)
                                {
                                    upscaleOwned.Save(
                                        Path.Combine(DebugFolder, "last_upscale.png"),
                                        ImageFormat.Png);
                                }
                                if (grayOwned != null)
                                {
                                    grayOwned.Save(
                                        Path.Combine(DebugFolder, "last_gray.png"),
                                        ImageFormat.Png);
                                }
                                ocrImage.Save(
                                    Path.Combine(DebugFolder, "last_ocr_prep.png"),
                                    ImageFormat.Png);
                                detectImage.Save(
                                    Path.Combine(DebugFolder, "last_detect_fog.png"),
                                    ImageFormat.Png);
                            }
                            else
                            {
                                ocrImage.Save(
                                    Path.Combine(DebugFolder, "last_ocr_prep.png"),
                                    ImageFormat.Png);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[OCR] debug image save failed: {ex.Message}");
                        }
                    }
                    pipeTimer.Mark("debug-image-save", sw);

                    var spokenParts = new List<string>();
                    var detail = new StringBuilder();
                    {
                        detail.Append(FormatRunHeader(
                            comicBookOn: true, detectUsesFog: fogOwned != null));
                        detail.AppendLine(
                            $"letterbox-trim → {letterboxOwned!.Width}x{letterboxOwned.Height}");
                        detail.AppendLine(
                            $"upscale-comic → {upscaleOwned!.Width}x{upscaleOwned.Height} " +
                            $"(long-edge {ActivePipelineUpscaleLongSide})");
                        var tags = new List<string> { "letterbox", "upscale-comic" };
                        if (grayOwned != null) tags.Add("gray");
                        tags.Add("tone");
                        if (fogOwned != null) tags.Add("fog(detect)");
                        detail.AppendLine(
                            $"pipeline={string.Join("+", tags)} " +
                            $"{ocrImage.Width}x{ocrImage.Height} " +
                            $"(from snap {rawSnap.Width}x{rawSnap.Height}; " +
                            "ComicBook ON - WinOCR detect on " +
                            (fogOwned != null ? "fog" : "tone") +
                            $", OCR full+crops on tone; fogUsed={fogUsedLive:0.###}; " +
                            "prep=Image tab shared pipeline)");
                        detail.Append(detectLog);
                    }

                    bool ducked = false;
                    try
                    {
                        List<string> chosen = new();
                        string chosenTag = "none";
                        // User's ComicBook setting for this capture (restored after any temp OFF).
                        bool userComicBook = SpeakRunSettings.GetComicBook();
                        // ComicBook ON: regions already ran WinOCR (reuse for TTS fallback).
                        List<DetectedTextRegion>? winOcrRegions = null;

                        // ComicBook ON pipeline (shared detect already ran above):
                        //   1) prep already done (tone = Kobold; optional fog = WinOCR detect)
                        //   2) regions from BuildComicRegionsSharedDetectAsync (dyn fog when on)
                        //   3) POI / sequential / best-of on ocrImage (tone)
                        {
                            int pipeW = ocrImage.Width;
                            int pipeH = ocrImage.Height;

                            // Same boxes as Balloons preview: post-grow cores + crop pad
                            // on the detect view (fog when on). Crops still use clear tone.
                            SaveRegionDebugOverlay(detectImage, regions);
                            winOcrRegions = regions;

                            bool solidIslands = HasWellSeparatedSolidIslands(
                                regions, pipeW, pipeH);
                            bool scrapDetect = LooksLikeScrapDetect(
                                regions, pipeW, pipeH, fragmented);

                            // POI guide: Comic Book alternate (shared with Balloons Speak).
                            bool usePoi =
                                SpeakRunSettings.GetComicPoiMarkers() &&
                                regions.Count > 0;
                            // Non-POI: per-island VL+TTS when detect found boxes.
                            bool usePerIsland = !usePoi && regions.Count > 0;

                            string strategyHint = usePoi
                                ? (SpeakRunSettings.GetComicPoiAutoStack()
                                    ? "detect+poi-canvas (per-island orange VL; multi fail → per-island)"
                                    : "detect+poi (no island-canvas; multi → per-island; 1 → full-page)")
                                : usePerIsland
                                    ? "detect+per-island"
                                    : "detect+crop-stack";
                            detail.AppendLine(
                                $"strategy={strategyHint} " +
                                $"(ComicBook; regions={regions.Count} fogUsed={fogUsedLive:0.###})");
                            Debug.WriteLine(
                                $"[OCR] ComicBook full path: {strategyHint} " +
                                $"(regions={regions.Count} fog={fogUsedLive:0.###})");
                            detail.AppendLine(
                                $"(lowConf={detection.LowConfidence} frag={fragmented} " +
                                $"scrap={scrapDetect} solid={solidIslands} regions={regions.Count})");

                            if (usePoi)
                            {
                                var (poiParts, poiTag, poiDucked) =
                                    await RunComicPoiGuideAsync(
                                        ocrImage, regions, pipeW, pipeH,
                                        detail, pipeTimer, token,
                                        speakNow: true, alreadyDucked: ducked)
                                    .ConfigureAwait(false);
                                spokenParts = poiParts;
                                chosen = poiParts;
                                chosenTag = poiTag;
                                ducked = poiDucked;

                                // POI always speaks inside RunComicPoiGuideAsync when
                                // speakNow:true. Must publish + skip the second TTS below.
                                if (spokenParts.Count > 0 &&
                                    chosenTag.StartsWith("comic-poi", StringComparison.Ordinal))
                                {
                                    detail.AppendLine(
                                        $"speak-plan units={spokenParts.Count} tag={chosenTag} " +
                                        "(already spoke in RunComicPoiGuideAsync)");
                                    string poiJoined = string.Join(
                                        Environment.NewLine + Environment.NewLine,
                                        spokenParts);
                                    WriteLastOcrDebug(poiJoined, detail);
                                }
                            }
                            else if (usePerIsland)
                            {
                                detail.AppendLine(
                                    $"strategy=per-island " +
                                    $"(ComicBook; regions={regions.Count})");
                                Debug.WriteLine(
                                    $"[OCR] ComicBook per-island regions " +
                                    $"(count={regions.Count})");

                                var (seqParts, seqTag, seqDucked) =
                                    await RunSequentialRegionsSpeakAsync(
                                        ocrImage, regions, detail, pipeTimer, token,
                                        speakNow: true, alreadyDucked: ducked);
                                spokenParts = seqParts;
                                chosen = seqParts;
                                chosenTag = seqTag;
                                ducked = seqDucked;

                                detail.AppendLine(
                                    $"speak-plan units={spokenParts.Count} tag={chosenTag}");
                                string seqJoined = spokenParts.Count > 0
                                    ? string.Join(
                                        Environment.NewLine + Environment.NewLine,
                                        spokenParts)
                                    : "(unreadable)";
                                WriteLastOcrDebug(seqJoined, detail);
                            }
                            else
                            {
                                var (fbChosen, fbTag) = await RunFullAndCropsBestOfAsync(
                                    ocrImage, regions, scrapDetect, solidIslands,
                                    detail, pipeTimer, token);
                                chosen = fbChosen;
                                chosenTag = fbTag;
                            }
                        }

                    // Crop-stack path: full global speak plan.
                    // Sequential and ALL comic-poi* paths already OCR+TTS'd when
                    // speakNow was true — do not speak again (was double-reading
                    // 1-island POI: tag "comic-poi" missed the old check).
                    bool alreadySpoke =
                        spokenParts.Count > 0 &&
                        (chosenTag.StartsWith("sequential-regions", StringComparison.Ordinal) ||
                         chosenTag.StartsWith("comic-poi", StringComparison.Ordinal));

                    if (!alreadySpoke)
                    {
                        // Fallback ladder when primary path has nothing to speak:
                        // 1) ComicBook was ON → one more full-frame with the same OCR prompt
                        //    (no AppSettings flip; same pre-fog tone image)
                        // 2) Still empty → WinOCR text (last resort; re-detect on fog if needed)
                        if (chosen.Count == 0 || chosen.All(SpeechCleaner.IsUnusableOcrText))
                        {
                            if (userComicBook)
                            {
                                detail.AppendLine(
                                    "comic-unreadable → full-frame retry " +
                                    $"(same OCR prompt; no settings flip)");
                                Debug.WriteLine(
                                    "[OCR] ComicBook unreadable → full-frame retry");

                                sw.Restart();
                                string? offClean = await RunFullFrameKoboldOnBitmapAsync(
                                    ocrImage,
                                    detail,
                                    token,
                                    savePrep: ActiveAnyDebugArtifacts);
                                pipeTimer.Mark("full-frame-ocr (retry)", sw);

                                if (!SpeechCleaner.IsUnusableOcrText(offClean))
                                {
                                    chosen = new List<string> { offClean! };
                                    chosenTag = "full-frame-retry";
                                    detail.AppendLine(
                                        $"winner=full-frame-retry words={ComicRegionGeometry.CountWords(offClean!)}");
                                }
                                else
                                {
                                    detail.AppendLine(
                                        "simple-prompt full-frame also empty/unusable");
                                }
                            }

                            if (chosen.Count == 0 || chosen.All(SpeechCleaner.IsUnusableOcrText))
                            {
                                sw.Restart();
                                var winParts = await TryWinOcrSpeakFallbackAsync(
                                    detectImage, winOcrRegions, detail, token);
                                pipeTimer.Mark("winocr-speak-fallback", sw);
                                if (winParts.Count > 0)
                                {
                                    chosen = winParts;
                                    chosenTag = "winocr-fallback";
                                    detail.AppendLine(
                                        $"winner=winocr-fallback parts={winParts.Count} " +
                                        $"words={winParts.Sum(ComicRegionGeometry.CountWords)}");
                                }
                            }
                        }

                        // Speak once: split on typed pause marks + pause between units.
                        var speakPieces = SpeechCleaner.ExpandToSpeakPieces(chosen);

                        // Pre-TTS: drop units already said (short crop echo after mega crop).
                        if (speakPieces.Count >= 2)
                        {
                            int beforeDedup = speakPieces.Count;
                            speakPieces = SpeechCleaner.DedupeSpeakPiecesForTts(speakPieces, detail);
                            if (speakPieces.Count != beforeDedup)
                            {
                                detail.AppendLine(
                                    $"speak-dedupe {beforeDedup} → {speakPieces.Count}");
                            }
                        }

                        // Glue mid-sentence fragments so pauses fall on real boundaries.
                        if (speakPieces.Count >= 2)
                        {
                            int beforeCoal = speakPieces.Count;
                            speakPieces = SpeechCleaner.CoalesceFragmentSpeakPieces(speakPieces, detail);
                            if (speakPieces.Count != beforeCoal)
                            {
                                detail.AppendLine(
                                    $"speak-coalesce {beforeCoal} → {speakPieces.Count}");
                            }
                        }

                        // Write plan before TTS so cancel mid-speak still leaves a log
                        detail.AppendLine(
                            $"speak-plan units={speakPieces.Count} tag={chosenTag}");
                        for (int pi = 0; pi < speakPieces.Count; pi++)
                        {
                            var sp = speakPieces[pi];
                            string pauseNote = sp.PauseAfterMs > 0
                                ? $" then-pause={sp.PauseAfterMs}ms"
                                : "";
                            detail.AppendLine(
                                $"  plan[{pi + 1}/{speakPieces.Count}]: {sp.Text}{pauseNote}");
                        }
                        string planJoined = string.Join(
                            Environment.NewLine + Environment.NewLine,
                            speakPieces.Select(p => p.Text));
                        WriteLastOcrDebug(
                            string.IsNullOrWhiteSpace(planJoined) ? "(unreadable)" : planJoined,
                            detail);

                        if (speakPieces.Count > 0)
                        {
                            if (!ducked)
                            {
                                DuckOtherAudio();
                                ducked = true;
                            }

                            for (int pi = 0; pi < speakPieces.Count; pi++)
                            {
                                string unit = speakPieces[pi].Text;
                                spokenParts.Add(unit);
                                detail.AppendLine(
                                    $"speak[{chosenTag} {pi + 1}/{speakPieces.Count}]: {unit}");
                                sw.Restart();
                                await SpeakWithSystemAsync(unit, token);
                                pipeTimer.Mark($"tts {chosenTag}[{pi + 1}]", sw);

                                int pauseMs = speakPieces[pi].PauseAfterMs;
                                if (pauseMs > 0)
                                {
                                    detail.AppendLine($"unit-pause {pauseMs} ms");
                                    sw.Restart();
                                    await Task.Delay(pauseMs, token);
                                    pipeTimer.Mark($"unit-pause[{pi + 1}→{pi + 2}]", sw);
                                }
                            }
                        }
                    }

                    totalSw.Stop();
                    pipeTimer.Mark("TOTAL wall-clock", totalSw);
                    detail.AppendLine();
                    detail.AppendLine("--- timings (ms) ---");
                    detail.Append(pipeTimer.FormatReport());

                    string finalJoined = string.Join(
                        Environment.NewLine + Environment.NewLine, spokenParts);
                    WriteLastOcrDebug(
                        string.IsNullOrWhiteSpace(finalJoined) ? "(unreadable)" : finalJoined,
                        detail);

                    if (spokenParts.Count == 0)
                    {
                        Debug.WriteLine("[OCR] nothing usable ? unreadable");
                        if (!ducked)
                        {
                            DuckOtherAudio();
                            ducked = true;
                        }
                        if (_lastText != "unreadable")
                        {
                            _lastText = "unreadable";
                            await SpeakWithSystemAsync("unreadable", token);
                        }
                    }
                    else
                    {
                        _lastText = finalJoined;
                    }
                }
                finally
                {
                    if (ducked)
                        RestoreAudio();
                }
                }
                finally
                {
                    fogOwned?.Dispose();
                    // prepStages owns letterbox/upscale/gray/tone (aliases of *Owned).
                    prepStages?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] Capture error: {ex}");
            }
        }

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= max ? s : s[..max] + "\u2026";
        }

        /// <summary>
        /// Snapshot the previous debug_images root into debug_images/archive/…
        /// then clear root files so only the new run lives at the top level.
        /// Archive is never deleted here (only pruned by age count).
        /// </summary>
        private static void ClearStaleDebugArtifacts()
        {
#if !DEBUG
            return;
#else
            try
            {
                if (!Directory.Exists(DebugFolder))
                    return;

                ArchivePreviousDebugRun();

                foreach (string path in Directory.EnumerateFiles(DebugFolder))
                {
                    string name = Path.GetFileName(path);
                    // Leave nothing stale in the root except we rewrite what we need.
                    // (archive/ is a subdirectory and is not touched here.)
                    if (name.StartsWith("region_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("wide_half_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("stack_crop_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("upscale_bench_", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "upscale_benchmark.txt", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_full_prep.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_llm_send.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_llm_island_stack.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_kobold_send.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_poi_vl_input.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_stacked_column.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_winocr_detect.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_regions.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_detect_fog.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_poi_guide.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_ocr_prep.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_letterbox.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_upscale.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_gray.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_capture.png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "last_ocr.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(path); } catch { /* ignore locked */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] clear debug artifacts: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// Copy current root debug files into archive/yyyyMMdd_HHmmss_&lt;speed&gt;/
        /// before they are overwritten. Keeps the last <see cref="MaxDebugArchiveRuns"/>.
        /// </summary>
        private static void ArchivePreviousDebugRun()
        {
#if !DEBUG
            return;
#else
            try
            {
                if (!Directory.Exists(DebugFolder))
                    return;

                string lastOcr = Path.Combine(DebugFolder, "last_ocr.txt");
                // Only archive when a previous run left a log (skip first-ever capture)
                if (!File.Exists(lastOcr))
                    return;

                var rootFiles = Directory.EnumerateFiles(DebugFolder).ToList();
                if (rootFiles.Count == 0)
                    return;

                string speedTag = InferSpeedTagFromLastOcr(lastOcr);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string dest = Path.Combine(DebugArchiveFolder, $"{stamp}_{speedTag}");
                Directory.CreateDirectory(dest);

                foreach (string path in rootFiles)
                {
                    string name = Path.GetFileName(path);
                    try
                    {
                        File.Copy(path, Path.Combine(dest, name), overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OCR] archive copy {name}: {ex.Message}");
                    }
                }

                Debug.WriteLine($"[OCR] archived previous debug run → {dest}");
                PruneDebugArchives();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] archive previous run: {ex.Message}");
            }
#endif
        }

        private static string InferSpeedTagFromLastOcr(string lastOcrPath)
        {
            try
            {
                string head = File.ReadAllText(lastOcrPath);
                if (head.Contains("ComicBook OFF", StringComparison.OrdinalIgnoreCase) ||
                    head.Contains("comicbook=off", StringComparison.OrdinalIgnoreCase) ||
                    head.Contains("profile=off", StringComparison.OrdinalIgnoreCase))
                    return "off";
                if (head.Contains("ComicBook ON", StringComparison.OrdinalIgnoreCase) ||
                    head.Contains("comicbook=on", StringComparison.OrdinalIgnoreCase) ||
                    head.Contains("profile=full", StringComparison.OrdinalIgnoreCase))
                    return "full";
            }
            catch { /* fall through */ }
            return "run";
        }

        private static void PruneDebugArchives()
        {
#if !DEBUG
            return;
#else
            try
            {
                if (!Directory.Exists(DebugArchiveFolder))
                    return;

                var dirs = Directory.GetDirectories(DebugArchiveFolder)
                    .Select(d => new DirectoryInfo(d))
                    .OrderByDescending(d => d.CreationTimeUtc)
                    .ToList();

                for (int i = MaxDebugArchiveRuns; i < dirs.Count; i++)
                {
                    try { dirs[i].Delete(recursive: true); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OCR] prune archive {dirs[i].Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] prune archives: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// Header block for last_ocr.txt: build identity + pipe knobs in force.
        /// </summary>
        private static string FormatRunHeader(bool comicBookOn, bool detectUsesFog)
        {
            var sb = new StringBuilder();
            sb.AppendLine(GetBuildStamp());
            sb.AppendLine(
                $"settings: ComicBook={(comicBookOn ? "on" : "off")}");

            if (!comicBookOn)
            {
                sb.AppendLine(
                    "profile=off (Default: same Image prep as ComicBook → single OCR; " +
                    "no fog/balloon crops)");
                sb.AppendLine("pipe detail:");
                sb.AppendLine($"  upscale long-edge={PipelineUpscaleLongSideComic}");
                sb.AppendLine(
                    $"  tone={(EnableImagePrep ? "denoise+levels+sharpen (shared prep)" : "off (prep disabled)")}");
                if (SpeakRunSettings.GetImageLlmSendDownscale())
                {
                    sb.AppendLine(
                        $"  Local-LLM send long-edge≤{SpeakRunSettings.GetImageLlmSendMaxLongEdge()}");
                }
                else
                {
                    sb.AppendLine("  Local-LLM send downscale=off");
                }
                sb.AppendLine("  winocr detect=off");
                sb.AppendLine("  OCR decode=single T=0 + recovery ladder");
                sb.AppendLine();
                return sb.ToString();
            }

            bool poi = SpeakRunSettings.GetComicPoiMarkers();
            bool autoStack = SpeakRunSettings.GetComicPoiAutoStack();
            sb.AppendLine(
                poi
                    ? "profile=full+poi (ComicBook: detect → POI guide; " +
                      (autoStack
                          ? "island-canvas=per-island orange VL one-at-a-time; " +
                            "multi fail → per-island on tone"
                          : "island-canvas off; multi → per-island; " +
                            "1 island → full-page guide") +
                      " — live=Balloons)"
                    : "profile=full (ComicBook ON)");
            sb.AppendLine("pipe detail:");
            sb.AppendLine($"  upscale long-edge={ActivePipelineUpscaleLongSide}");
            sb.AppendLine("  tone=denoise+levels+sharpen");
            sb.AppendLine(
                $"  winocr detect=pass1+pass2" +
                $", fog={(detectUsesFog ? "on" : "off")}" +
                (detectUsesFog
                    ? $", amount={WinOcrDetectGrayFogAmount:0.###}"
                    : ""));
            if (poi)
            {
                sb.AppendLine(
                    "  poi guide=bright green region boxes (same as Balloons preview)");
                sb.AppendLine(
                    SpeakRunSettings.GetComicPoiFogOutside()
                        ? "  poi outside-fog=thick (hide art/UI outside islands)"
                        : "  poi outside-fog=off");
                if (autoStack)
                {
                    sb.AppendLine(
                        $"  poi island-canvas=on (per-island VL, one at a time) " +
                        $"(margin={SpeakRunSettings.GetComicPoiAutoStackMarginPx()}px " +
                        $"beef+{SpeakRunSettings.GetComicPoiStackBeefExtra():0.###}; " +
                        "preview stays full page; compose long-edge cap 2560)");
                }
                else
                {
                    sb.AppendLine(
                        "  poi island-canvas=off (multi → per-island; 1 → full-page)");
                }
                if (SpeakRunSettings.GetImageLlmSendDownscale())
                {
                    sb.AppendLine(
                        $"  Local-LLM send long-edge≤{SpeakRunSettings.GetImageLlmSendMaxLongEdge()}");
                }
                else
                {
                    sb.AppendLine(
                        "  Local-LLM send downscale=off " +
                        "(canvas compose still caps long-edge at 2560)");
                }
            }
            sb.AppendLine(
                ActiveDecodeConsensus
                    ? "  OCR decode=consensus 2-of-3 (T=0 / T=0.25 / recovery)"
                    : "  OCR decode=single T=0 + recovery ladder");
            sb.AppendLine("  wide-strip rescue=on");
            sb.AppendLine(
                $"  debug images={(!EnableDebugArtifacts ? "off (Release)" : ActiveHeavyDebugImages ? "full stage dump" : "minimal (ocr_prep + last_ocr.txt)")}");
            sb.AppendLine(
                $"  winocr detect png={(ActiveWinOcrDetectDebugPng ? "on" : "off")}");
            sb.AppendLine("  dual-balloon dash promote (TTS pauses)=on");
            if (poi)
            {
                sb.AppendLine(
                    autoStack
                        ? "  speak=poi per-island orange canvas VL (one at a time)"
                        : "  speak=per-island on tone when multi; full-page guide when 1 island");
            }
            else
            {
                sb.AppendLine(
                    "  speak=per-island OCR+TTS when detect finds islands");
            }
            sb.AppendLine();
            return sb.ToString();
        }

        private static string GetBuildStamp()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var name = asm.GetName();
                string ver =
                    asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion
                    ?? name.Version?.ToString()
                    ?? "?";
                // InformationalVersion may include +git metadata — keep it readable
                int plus = ver.IndexOf('+');
                if (plus > 0)
                    ver = ver[..plus];

                // Single-file safe: never use Assembly.Location (empty / IL3000).
                string path = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "SpeakRect.exe");
                string writeTime = File.Exists(path)
                    ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss")
                    : "unknown";

#if DEBUG
                const string config = "Debug";
#else
                const string config = "Release";
#endif
                string tfm = name.Name ?? "SpeakRect";
                return $"build: {tfm} {ver} {config} dll-write={writeTime}";
            }
            catch (Exception ex)
            {
                return $"build: (unavailable: {ex.Message})";
            }
        }

        /// <summary>
        /// Per-step timings for last_ocr.txt diagnosis only.
        /// Use to find waste - not as a reason to drop accuracy/recovery.
        /// </summary>
        private sealed class PipelineTimer
        {
            private readonly List<(string Name, long Ms)> _steps = new();

            public void Mark(string name, Stopwatch sw)
            {
                if (sw.IsRunning)
                    sw.Stop();
                Add(name, sw.ElapsedMilliseconds);
            }

            public void Add(string name, long ms)
            {
                _steps.Add((name, ms));
                Debug.WriteLine($"[OCR time] {name}: {ms} ms");
            }

            /// <summary>Record a pre-summed total without a stopwatch.</summary>
            public void MarkSum(string name, long ms) => Add(name, ms);

            public string FormatReport()
            {
                if (_steps.Count == 0)
                    return "(no timings)\n";

                int nameW = Math.Max(12, _steps.Max(s => s.Name.Length));
                long total = 0;
                var sb = new StringBuilder();
                foreach (var (name, ms) in _steps)
                {
                    // Don't double-count TOTAL or pre-summed rollups in the sum line
                    bool isRollup =
                        name.StartsWith("TOTAL", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("-total", StringComparison.OrdinalIgnoreCase);
                    if (!isRollup)
                        total += ms;
                    sb.AppendLine($"  {name.PadRight(nameW)}  {ms,6} ms");
                }
                sb.AppendLine($"  {"(sum of steps)".PadRight(nameW)}  {total,6} ms");
                return sb.ToString();
            }
        }

        /// <summary>
        /// Local-LLM crop under-read vs OCR detect for the same island (common when one
        /// mega box holds several balloons and the model starts mid-panel).
        /// </summary>
        private static bool KoboldUnderReadsWinOcr(string? kobold, string? winOcr) =>
            ComicRegionGeometry.KoboldUnderReadsWinOcr(kobold, winOcr);

        /// <summary>
        /// Strong multi-balloon path: for each detect island in reading order,
        /// OCR that crop alone → expand / local-dedupe (within the balloon only)
        /// → TTS → bubble pause → next island.
        /// <para>
        /// Cross-balloon word reuse never hits a global speak-dedupe bag, so short
        /// replies after longer balloons that reuse a stem stay spoken.
        /// Faster time-to-first-speech than stack-then-speak-all.
        /// </para>
        /// <para>
        /// Crop results that under-read vs that island's WinOCR text may try a
        /// full-frame rescue when there is only one region.
        /// </para>
        /// Returns spoken unit texts (already spoken when <paramref name="speakNow"/>
        /// is true). When every crop is empty, one full-frame fallback is tried.
        /// </summary>
        private async Task<(List<string> SpokenParts, string Tag, bool DuckUsed)>
            RunSequentialRegionsSpeakAsync(
                Bitmap pipelineImage,
                List<DetectedTextRegion> regions,
                StringBuilder detail,
                PipelineTimer pipeTimer,
                CancellationToken token,
                bool speakNow,
                bool alreadyDucked)
        {
            var spokenParts = new List<string>();
            bool ducked = alreadyDucked;
            const string tag = "sequential-regions";

            if (pipelineImage == null || regions == null || regions.Count == 0)
            {
                detail.AppendLine("sequential-regions: no regions — skip");
                return (spokenParts, tag, ducked);
            }

            detail.AppendLine(
                $"strategy=sequential-regions (OCR+TTS per balloon; " +
                $"regions={regions.Count}; no crop-stack / no global dedupe bag)");

            var sw = Stopwatch.StartNew();
            int regionSpoke = 0;

            for (int i = 0; i < regions.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var region = regions[i];
                detail.AppendLine(
                    $"  seq[{i + 1}/{regions.Count}] x={region.Bounds.X} y={region.Bounds.Y} " +
                    $"w={region.Bounds.Width} h={region.Bounds.Height}");

                sw.Restart();
                var read = await ReadOneRegionAsync(
                    pipelineImage, region, regions, i, detail, token)
                    .ConfigureAwait(false);
                sw.Stop();
                pipeTimer.Add($"seq-ocr[{i + 1}]", sw.ElapsedMilliseconds);
                detail.AppendLine(
                    $"      time seq-ocr[{i + 1}]: {sw.ElapsedMilliseconds} ms");

                string? regionText = read.Text;

                // Coverage rescue: crop Kobold skipped part of what WinOCR saw.
                if (ComicRegionGeometry.KoboldUnderReadsWinOcr(regionText, region.WinOcrText))
                {
                    int kW = ComicRegionGeometry.CountWords(regionText);
                    int wW = ComicRegionGeometry.CountWords(region.WinOcrText);
                    detail.AppendLine(
                        $"      seq[{i + 1}]: crop under-read vs OCR " +
                        $"(local-llm={kW} ocr={wW})");

                    // Single-island under-read: try full-frame (metric-driven, not area).
                    bool tryFull = regions.Count == 1;

                    if (tryFull)
                    {
                        sw.Restart();
                        var fullParts = await RunFullFrameWithWideRescueAsync(
                            pipelineImage, detail, token).ConfigureAwait(false);
                        pipeTimer.Add($"seq-ocr[{i + 1}]-full-rescue", sw.ElapsedMilliseconds);
                        int fW = fullParts.Sum(ComicRegionGeometry.CountWords);
                        if (fW > kW)
                        {
                            detail.AppendLine(
                                $"      seq[{i + 1}]: full-frame rescue wins " +
                                $"(words {kW} → {fW})");
                            // Speak the full-frame plan for this (mega) island.
                            var rescuePieces = SpeechCleaner.ExpandToSpeakPieces(fullParts);
                            if (rescuePieces.Count >= 2)
                                rescuePieces = SpeechCleaner.DedupeSpeakPiecesForTts(rescuePieces, detail);
                            if (rescuePieces.Count >= 2)
                                rescuePieces =
                                    SpeechCleaner.CoalesceFragmentSpeakPieces(rescuePieces, detail);
                            if (rescuePieces.Count > 0)
                            {
                                if (i < regions.Count - 1 && rescuePieces.Count > 0)
                                    rescuePieces[^1] =
                                        rescuePieces[^1].WithPause(BubblePauseMs);

                                if (speakNow)
                                {
                                    if (!ducked)
                                    {
                                        DuckOtherAudio();
                                        ducked = true;
                                    }
                                    for (int pi = 0; pi < rescuePieces.Count; pi++)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        string unit = rescuePieces[pi].Text;
                                        spokenParts.Add(unit);
                                        detail.AppendLine(
                                            $"speak[{tag} r{i + 1}-full {pi + 1}/" +
                                            $"{rescuePieces.Count}]: {unit}");
                                        sw.Restart();
                                        await SpeakWithSystemAsync(unit, token)
                                            .ConfigureAwait(false);
                                        pipeTimer.Mark(
                                            $"tts seq[{i + 1}.full.{pi + 1}]", sw);
                                        int pauseMs = rescuePieces[pi].PauseAfterMs;
                                        if (pauseMs > 0)
                                        {
                                            detail.AppendLine($"unit-pause {pauseMs} ms");
                                            await Task.Delay(pauseMs, token)
                                                .ConfigureAwait(false);
                                        }
                                    }
                                }
                                else
                                {
                                    foreach (var p in rescuePieces)
                                        spokenParts.Add(p.Text);
                                }

                                regionSpoke++;
                                // Full-frame already covers the panel — stop more crops.
                                if (regions.Count == 1)
                                {
                                    detail.AppendLine(
                                        $"winner={tag}+full-rescue " +
                                        $"regions-spoken={regionSpoke}/{regions.Count} " +
                                        $"units={spokenParts.Count}");
                                    return (spokenParts, tag + "+full-rescue", ducked);
                                }
                                continue;
                            }
                        }
                    }

                    // Prefer cleaned OCR detect text for this island when still richer.
                    string winClean = SpeechCleaner.CleanForSpeech(region.WinOcrText ?? "");
                    if (!SpeechCleaner.IsUnusableOcrText(winClean) &&
                        ComicRegionGeometry.CountWords(winClean) > kW + 1)
                    {
                        detail.AppendLine(
                            $"      seq[{i + 1}]: use OCR detect text " +
                            $"(richer than crop Local-LLM)");
                        regionText = winClean;
                    }
                }

                if (SpeechCleaner.IsUnusableOcrText(regionText))
                {
                    detail.AppendLine($"      seq[{i + 1}]: no usable text — skip");
                    continue;
                }

                // Local expand / dedupe / coalesce only — never against other balloons.
                var pieces = SpeechCleaner.ExpandToSpeakPieces(new[] { regionText! });
                if (pieces.Count >= 2)
                {
                    int before = pieces.Count;
                    pieces = SpeechCleaner.DedupeSpeakPiecesForTts(pieces, detail);
                    if (pieces.Count != before)
                    {
                        detail.AppendLine(
                            $"      seq[{i + 1}] local-dedupe {before} → {pieces.Count}");
                    }
                }
                if (pieces.Count >= 2)
                {
                    int before = pieces.Count;
                    pieces = SpeechCleaner.CoalesceFragmentSpeakPieces(pieces, detail);
                    if (pieces.Count != before)
                    {
                        detail.AppendLine(
                            $"      seq[{i + 1}] local-coalesce {before} → {pieces.Count}");
                    }
                }

                // Do not gate short crop tokens against OCR island text — detect often
                // misses short call-outs / names that Local-LLM reads on the crop.
                // Speech noise rules + SpeechCleaner.IsUnusableOcrText handle model junk.

                if (pieces.Count == 0)
                {
                    detail.AppendLine($"      seq[{i + 1}]: empty after expand — skip");
                    continue;
                }

                // Bubble pause after this region's last unit (except last region).
                if (i < regions.Count - 1 && pieces.Count > 0)
                    pieces[^1] = pieces[^1].WithPause(BubblePauseMs);

                detail.AppendLine(
                    $"      seq[{i + 1}] units={pieces.Count}: " +
                    string.Join(" | ", pieces.Select(p => Truncate(p.Text, 40))));

                if (speakNow)
                {
                    if (!ducked)
                    {
                        DuckOtherAudio();
                        ducked = true;
                    }

                    for (int pi = 0; pi < pieces.Count; pi++)
                    {
                        token.ThrowIfCancellationRequested();
                        string unit = pieces[pi].Text;
                        spokenParts.Add(unit);
                        detail.AppendLine(
                            $"speak[{tag} r{i + 1} {pi + 1}/{pieces.Count}]: {unit}");
                        sw.Restart();
                        await SpeakWithSystemAsync(unit, token).ConfigureAwait(false);
                        pipeTimer.Mark($"tts seq[{i + 1}.{pi + 1}]", sw);

                        int pauseMs = pieces[pi].PauseAfterMs;
                        if (pauseMs > 0)
                        {
                            detail.AppendLine($"unit-pause {pauseMs} ms");
                            sw.Restart();
                            await Task.Delay(pauseMs, token).ConfigureAwait(false);
                            pipeTimer.Mark(
                                $"unit-pause seq[{i + 1}.{pi + 1}]", sw);
                        }
                    }
                }
                else
                {
                    foreach (var p in pieces)
                        spokenParts.Add(p.Text);
                }

                regionSpoke++;
            }

            // All crops empty → one full-frame lifeline (still isolated plan).
            if (spokenParts.Count == 0)
            {
                detail.AppendLine(
                    "sequential-regions: all crops empty → full-frame fallback");
                sw.Restart();
                var fullParts = await RunFullFrameWithWideRescueAsync(
                    pipelineImage, detail, token).ConfigureAwait(false);
                pipeTimer.Mark("seq-full-frame-fallback", sw);

                var pieces = SpeechCleaner.ExpandToSpeakPieces(fullParts);
                if (pieces.Count >= 2)
                    pieces = SpeechCleaner.DedupeSpeakPiecesForTts(pieces, detail);
                if (pieces.Count >= 2)
                    pieces = SpeechCleaner.CoalesceFragmentSpeakPieces(pieces, detail);

                if (pieces.Count > 0)
                {
                    detail.AppendLine(
                        $"sequential-regions full-frame units={pieces.Count}");
                    if (speakNow)
                    {
                        if (!ducked)
                        {
                            DuckOtherAudio();
                            ducked = true;
                        }
                        for (int pi = 0; pi < pieces.Count; pi++)
                        {
                            token.ThrowIfCancellationRequested();
                            string unit = pieces[pi].Text;
                            spokenParts.Add(unit);
                            detail.AppendLine(
                                $"speak[{tag}-full {pi + 1}/{pieces.Count}]: {unit}");
                            sw.Restart();
                            await SpeakWithSystemAsync(unit, token)
                                .ConfigureAwait(false);
                            pipeTimer.Mark($"tts seq-full[{pi + 1}]", sw);
                            int pauseMs = pieces[pi].PauseAfterMs;
                            if (pauseMs > 0)
                                await Task.Delay(pauseMs, token).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        foreach (var p in pieces)
                            spokenParts.Add(p.Text);
                    }
                    return (spokenParts, tag + "+full", ducked);
                }
            }

            detail.AppendLine(
                $"winner={tag} regions-spoken={regionSpoke}/{regions.Count} " +
                $"units={spokenParts.Count}");
            return (spokenParts, tag, ducked);
        }

        /// <summary>
        /// ComicBook best-of: full-frame (fallback) + <b>vertical crop-stack</b>
        /// (primary). Balloons are plain snaps of the prepped tone image in reading
        /// order, stacked, and sent as one Kobold image (no second-pass crop
        /// upscale/tone). Per-region crop Kobold remains a last-resort fallback
        /// if the stack fails.
        /// Used when detect finds no islands (or as recovery), not the primary POI path.
        /// </summary>
        private async Task<(List<string> Chosen, string Tag)> RunFullAndCropsBestOfAsync(
            Bitmap pipelineImage,
            List<DetectedTextRegion> regions,
            bool scrapDetect,
            bool solidIslands,
            StringBuilder detail,
            PipelineTimer pipeTimer,
            CancellationToken token)
        {
            // Full-frame first (fallback / compare). Still useful when detect is empty
            // or stack OCR fails.
            var sw = Stopwatch.StartNew();
            var fullParts = await RunFullFrameWithWideRescueAsync(
                pipelineImage, detail, token);
            pipeTimer.Mark("full-frame-ocr", sw);
            int fullWords = fullParts.Sum(ComicRegionGeometry.CountWords);
            detail.AppendLine(
                $"full-frame parts={fullParts.Count} words={fullWords}");

            if (regions.Count >= 2)
            {
                var splitParts = SplitFullFrameByDetectRegions(
                    fullParts, regions, detail);
                if (splitParts != null && splitParts.Count > 0)
                    fullParts = splitParts;
            }

            var fullUsable = fullParts.Where(p => !SpeechCleaner.IsUnusableOcrText(p)).ToList();

            // ComicBook always crop-stacks when detect found islands.
            // Full-frame is fallback only — crops are snaps of the prepped tone.

            // --- Primary: vertical crop-stack in reading order ---
            if (regions.Count > 0)
            {
                detail.AppendLine(
                    $"crop-stack: building strips={regions.Count} " +
                    $"(native prep crops; canvas=shared orange+beef " +
                    $"gap={SpeakRunSettings.GetComicPoiAutoStackGapPx()} " +
                    $"margin={SpeakRunSettings.GetComicPoiAutoStackMarginPx()})");
                sw.Restart();
                using var stackBmp = BuildVerticalCropStack(
                    pipelineImage, regions, detail, ActiveCropPadPx);
                pipeTimer.Mark("crop-stack-compose", sw);

                if (stackBmp != null)
                {
                    CaptureAnalyticsImage(
                        "crop_stack", "Crop stack", stackBmp);
                    sw.Restart();
                    string? stackClean = await RunCropStackKoboldAsync(
                        stackBmp, detail, token);
                    pipeTimer.Mark("crop-stack-ocr", sw);

                    if (!SpeechCleaner.IsUnusableOcrText(stackClean))
                    {
                        var stackUnits = SpeechCleaner.ExpandToSpeakUnits(
                            new List<string> { stackClean! });
                        if (stackUnits.Count == 0)
                            stackUnits.Add(stackClean!.Trim());
                        stackUnits = stackUnits
                            .Where(u => !SpeechCleaner.IsUnusableOcrText(u))
                            .ToList();
                        int stackWords = stackUnits.Sum(ComicRegionGeometry.CountWords);

                        // Trust stack unless full-frame clearly found much more
                        // (stack under-read). Order from geometry; wording from
                        // prep-native balloon crops.
                        bool fullMuchRicher =
                            fullWords >= stackWords + 8 &&
                            fullUsable.Count > 0;
                        if (!fullMuchRicher && stackUnits.Count > 0)
                        {
                            detail.AppendLine(
                                $"winner=crop-stack units={stackUnits.Count} " +
                                $"words={stackWords} " +
                                $"(full had {fullWords} words; stack primary)");
                            return (stackUnits, "crop-stack");
                        }

                        detail.AppendLine(
                            $"crop-stack deferred: words={stackWords} vs full={fullWords} " +
                            (fullMuchRicher ? "(full much richer)" : "(empty units)"));
                    }
                    else
                    {
                        detail.AppendLine("crop-stack: unusable — fall back to per-crop");
                    }
                }
                else
                {
                    detail.AppendLine("crop-stack: compose failed — fall back to per-crop");
                }
            }

            // --- Fallback: classic per-region crops + full-order merge ---
            var cropReads = new List<CropRead>();
            long cropKoboldMs = 0;
            if (regions.Count > 0)
            {
                detail.AppendLine(
                    $"also collecting crops blocks={regions.Count}" +
                    (scrapDetect ? " (scrapy detect - still trying)" : ""));
                for (int i = 0; i < regions.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var region = regions[i];
                    detail.AppendLine(
                        $"  [{i + 1}] x={region.Bounds.X} y={region.Bounds.Y} " +
                        $"w={region.Bounds.Width} h={region.Bounds.Height}");

                    sw.Restart();
                    var read = await ReadOneRegionAsync(
                        pipelineImage, region, regions, i, detail, token);
                    sw.Stop();
                    long cropMs = sw.ElapsedMilliseconds;
                    cropKoboldMs += cropMs;
                    pipeTimer.Add($"crop-ocr[{i + 1}]", cropMs);
                    detail.AppendLine(
                        $"      time crop-kobold[{i + 1}]: {cropMs} ms");

                    if (SpeechCleaner.IsUnusableOcrText(read.Text))
                    {
                        detail.AppendLine($"      ? no usable text");
                        continue;
                    }

                    string speakText = read.Text!;
                    if (cropReads.Count > 0)
                    {
                        string stripped = StripOverlapWithPrevious(
                            speakText, cropReads[^1].Text);
                        if (!string.Equals(stripped, speakText, StringComparison.Ordinal) &&
                            !SpeechCleaner.IsUnusableOcrText(stripped))
                        {
                            detail.AppendLine(
                                $"      dedupe-prev: \"{Truncate(speakText, 50)}\" → \"{Truncate(stripped, 50)}\"");
                            speakText = stripped;
                        }
                        else if (SpeechCleaner.IsUnusableOcrText(stripped))
                        {
                            detail.AppendLine(
                                "      dedupe-prev emptied → skip");
                            continue;
                        }
                    }

                    if (SpeechCleaner.IsUnusableOcrText(speakText))
                    {
                        detail.AppendLine($"      ? no usable text after dedupe");
                        continue;
                    }

                    cropReads.Add(new CropRead(i, speakText));
                    detail.AppendLine($"      ? crop ok: {speakText}");
                }

                pipeTimer.MarkSum("crop-ocr-total", cropKoboldMs);
            }

            var pick = PickBestOfFullVsCrops(
                fullUsable,
                cropReads,
                detail,
                scrapDetect: scrapDetect,
                solidIslands: solidIslands,
                readingBlocks: regions.Count);
            return (pick.Chosen, pick.Tag);
        }

        /// <summary>
        /// Shared Comic Book POI path for live overlay and Balloons Speak.
        /// <list type="bullet">
        /// <item>Compose base is always <b>tone</b> (never detect fog).</item>
        /// <item>Display boxes: if override pad is 0 (Balloons refine boxes),
        /// region bounds are already final; else expand cores with crop pad once.</item>
        /// <item>Full-page green guide always published for analytics/preview map.</item>
        /// <item><see cref="AppSettings.ComicPoiAutoStack"/> on (stock): each island →
        /// its own orange canvas → VL (+ TTS) one at a time (<c>comic-poi-stack</c>).</item>
        /// <item>Stack off/fail + multi-island: per-island VL+TTS on tone.</item>
        /// <item>1 island + stack off/fail: full-page guide VL.</item>
        /// </list>
        /// </summary>
        private async Task<(List<string> Parts, string Tag, bool Ducked)> RunComicPoiGuideAsync(
            Bitmap toneImage,
            List<DetectedTextRegion> regions,
            int pipeW,
            int pipeH,
            StringBuilder detail,
            PipelineTimer pipeTimer,
            CancellationToken token,
            bool speakNow,
            bool alreadyDucked)
        {
            var sw = Stopwatch.StartNew();
            bool poiFogOutside = SpeakRunSettings.GetComicPoiFogOutside();
            bool poiAutoStack = SpeakRunSettings.GetComicPoiAutoStack();
            int poiStackGap = SpeakRunSettings.GetComicPoiAutoStackGapPx();
            int poiStackMargin = SpeakRunSettings.GetComicPoiAutoStackMarginPx();

            // Pad once: live uses cores (_forcedCropPadPx null); Balloons override already final.
            bool displayBoxesFinal = _forcedCropPadPx == 0;
            List<Rectangle> boxes;
            if (displayBoxesFinal)
            {
                boxes = regions.ConvertAll(r => r.Bounds);
                detail.AppendLine(
                    $"poi-boxes: display-final={boxes.Count} (override; pad not re-applied)");
            }
            else
            {
                boxes = ExpandRegionsByCropPad(
                        regions, pipeW, pipeH, ActiveCropPadPx)
                    .ConvertAll(r => r.Bounds);
                detail.AppendLine(
                    $"poi-boxes: cropPad={ActiveCropPadPx}px → {boxes.Count} (expanded once)");
            }

            detail.AppendLine(
                $"strategy=comic-poi (tone base; islands={boxes.Count}; " +
                $"outsideFog={poiFogOutside}; " +
                $"llmStack={poiAutoStack} " +
                $"gap={poiStackGap}px " +
                $"margin={poiStackMargin}px)");

            // Full-page guide on TONE — same DrawRegionGuides as Balloons POI preview.
            // Always published for Analytics; Speak may use island stack instead.
            Bitmap? guideBmp = null;
            try
            {
                sw.Restart();
                bool fogOutside = poiFogOutside;
                guideBmp = ComicPoiGuide.DrawRegionGuides(
                    toneImage, boxes, detail, fogOutside: fogOutside);
                pipeTimer.Mark(
                    fogOutside ? "poi-outside-fog+boxes" : "poi-green-boxes", sw);
                CaptureAnalyticsImage(
                    "poi_guide",
                    fogOutside ? "POI boxes + outside fog (tone)" : "POI green boxes (tone)",
                    guideBmp);
                // isVlInput only if we will NOT replace with stack (stack overwrites send files).
                SavePoiVlDebug(guideBmp, isVlInput: !poiAutoStack && boxes.Count == 1);

                // Local-LLM send: when Stack islands is on, each island gets its own
                // orange canvas (same beef/margin compose as before) and is sent to VL
                // one at a time — not one multi-strip stack image. Preview stays full page.
                if (poiAutoStack && boxes.Count >= 1)
                {
                    int margin = Math.Clamp(poiStackMargin, 0, 64);
                    int sendCap = ActiveLlmSendMaxLongEdge;
                    detail.AppendLine(
                        $"llm-send-stack: per-island canvas ×{boxes.Count} " +
                        $"margin={margin}px (one VL each; not multi-strip)" +
                        (sendCap > 0 ? $" then-long-edge≤{sendCap}" : " (no send downscale)"));

                    var stackParts = new List<string>();
                    var spoken = new List<string>();
                    bool duckedStack = alreadyDucked;
                    int islandsOk = 0;

                    try
                    {
                        for (int i = 0; i < boxes.Count; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            Bitmap? islandCanvas = null;
                            try
                            {
                                sw.Restart();
                                // One island → one orange canvas (green box + margin/beef).
                                islandCanvas = ComicPoiGuide.BuildVerticalStack(
                                    toneImage,
                                    new[] { boxes[i] },
                                    detail,
                                    paintBullseyes: false,
                                    stripGapPx: 0,
                                    marginPx: margin,
                                    // Full page islands: wide-ribbon expand must not
                                    // grow into other balloons (double-speak).
                                    avoidIslands: boxes);
                                pipeTimer.Add(
                                    $"llm-island-canvas[{i + 1}]",
                                    sw.ElapsedMilliseconds);

                                if (islandCanvas == null)
                                {
                                    detail.AppendLine(
                                        $"  poi-island[{i + 1}/{boxes.Count}]: compose failed");
                                    continue;
                                }

                                string slotKey = i == 0
                                    ? "llm_island_stack"
                                    : $"llm_island_{i + 1}";
                                CaptureAnalyticsImage(
                                    slotKey,
                                    $"Local-LLM island canvas {i + 1}/{boxes.Count} " +
                                    $"{islandCanvas.Width}x{islandCanvas.Height} " +
                                    $"(margin={margin}" +
                                    (sendCap > 0 ? $"; then ≤{sendCap}" : "") + ")",
                                    islandCanvas);

                                if (ActiveAnyDebugArtifacts)
                                {
                                    try
                                    {
                                        EnsureDebugFolder();
                                        // Last canvas written wins (same as prior single-stack dump).
                                        islandCanvas.Save(
                                            Path.Combine(
                                                DebugFolder, "last_llm_island_stack.png"),
                                            ImageFormat.Png);
                                    }
                                    catch { /* debug only */ }
                                }

                                detail.AppendLine(
                                    $"  poi-island[{i + 1}/{boxes.Count}]: " +
                                    $"canvas {islandCanvas.Width}x{islandCanvas.Height} " +
                                    $"box @{boxes[i].X},{boxes[i].Y} " +
                                    $"{boxes[i].Width}x{boxes[i].Height}");

                                sw.Restart();
                                string? islandClean = await RunFullFrameKoboldOnBitmapAsync(
                                    islandCanvas,
                                    detail,
                                    token,
                                    savePrep: false)
                                    .ConfigureAwait(false);
                                pipeTimer.Add(
                                    $"full-frame-ocr (llm-island[{i + 1}])",
                                    sw.ElapsedMilliseconds);

                                if (SpeechCleaner.IsUnusableOcrText(islandClean))
                                {
                                    detail.AppendLine(
                                        $"  poi-island[{i + 1}]: empty/unusable VL");
                                    continue;
                                }

                                islandsOk++;
                                stackParts.Add(islandClean!);
                                detail.AppendLine(
                                    $"  poi-island[{i + 1}]: ok words=" +
                                    $"{ComicRegionGeometry.CountWords(islandClean!)}");

                                if (!speakNow)
                                    continue;

                                var speakPieces =
                                    SpeechCleaner.ExpandToSpeakPieces(
                                        new List<string> { islandClean! });
                                if (speakPieces.Count == 0)
                                    continue;

                                // Same pre-TTS filters as full-frame / §9 paths.
                                // VL often emits plain dialogue + the same lines again
                                // inside invented HTML (div/table). Without dedupe,
                                // poi-stack speak-now double-TTS'd balloon 1.
                                speakPieces = ApplySpeakDedupeCoalesce(
                                    speakPieces, detail, $"poi-stack i{i + 1}");
                                if (speakPieces.Count == 0)
                                    continue;

                                // Balloon pause between islands (not after last).
                                if (i < boxes.Count - 1)
                                {
                                    speakPieces[^1] =
                                        speakPieces[^1].WithPause(BubblePauseMs);
                                }

                                if (!duckedStack)
                                {
                                    DuckOtherAudio();
                                    duckedStack = true;
                                }

                                for (int pi = 0; pi < speakPieces.Count; pi++)
                                {
                                    token.ThrowIfCancellationRequested();
                                    string unit = speakPieces[pi].Text;
                                    spoken.Add(unit);
                                    detail.AppendLine(
                                        $"speak[comic-poi-stack i{i + 1} " +
                                        $"{pi + 1}/{speakPieces.Count}]: {unit}");
                                    sw.Restart();
                                    await SpeakWithSystemAsync(unit, token)
                                        .ConfigureAwait(false);
                                    pipeTimer.Mark(
                                        $"tts comic-poi-stack[{i + 1}.{pi + 1}]", sw);
                                    int pauseMs = speakPieces[pi].PauseAfterMs;
                                    if (pauseMs > 0)
                                    {
                                        await Task.Delay(pauseMs, token)
                                            .ConfigureAwait(false);
                                    }
                                }
                            }
                            finally
                            {
                                try { islandCanvas?.Dispose(); } catch { /* ignore */ }
                            }
                        }

                        if (stackParts.Count > 0)
                        {
                            detail.AppendLine(
                                $"winner=comic-poi-stack islands={islandsOk}/{boxes.Count} " +
                                $"words={stackParts.Sum(ComicRegionGeometry.CountWords)} " +
                                $"(per-island canvas VL)");
                            if (speakNow)
                                return (spoken, "comic-poi-stack", duckedStack);
                            return (stackParts, "comic-poi-stack", duckedStack);
                        }

                        detail.AppendLine(
                            "comic-poi-stack empty/unusable (all islands) → fall through");
                    }
                    catch (OperationCanceledException)
                    {
                        // Stop TTS / new speak — never fall through to sequential
                        // or crop-stack (that re-spoke islands already TTS'd).
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Partial success: return what we already OCR'd/spoke.
                        // Falling through would re-read the same islands.
                        if (spoken.Count > 0 || stackParts.Count > 0)
                        {
                            detail.AppendLine(
                                $"llm-send-stack failed after partial " +
                                $"(spoken={spoken.Count} ocr={stackParts.Count}): {ex.Message} " +
                                "— return partial (no re-speak fallthrough)");
                            if (speakNow)
                                return (spoken, "comic-poi-stack-partial", duckedStack);
                            return (stackParts.Count > 0 ? stackParts : spoken,
                                "comic-poi-stack-partial", duckedStack);
                        }
                        detail.AppendLine($"llm-send-stack failed: {ex.Message}");
                    }
                    // Fall through only when every island empty/failed (no partial).
                }

                // Multi-island (island-canvas off or failed empty): per-island on tone.
                if (boxes.Count >= 2)
                {
                    detail.AppendLine(
                        "poi-speak: per-island on tone (island-canvas off/fail)");
                    Debug.WriteLine(
                        $"[OCR] ComicBook POI multi → per-island islands={boxes.Count}");
                    var (seqParts, seqTag, seqDucked) =
                        await RunSequentialRegionsSpeakAsync(
                            toneImage, regions, detail, pipeTimer, token,
                            speakNow: speakNow, alreadyDucked: alreadyDucked)
                        .ConfigureAwait(false);
                    string tag = seqTag.StartsWith("sequential", StringComparison.Ordinal)
                        ? "comic-poi-seq"
                        : $"comic-poi-seq/{seqTag}";
                    detail.AppendLine(
                        $"winner={tag} parts={seqParts.Count} " +
                        $"words={seqParts.Sum(ComicRegionGeometry.CountWords)}");
                    return (seqParts, tag, seqDucked);
                }

                // Single island fallback: VL input = full-page guideBmp.
                sw.Restart();
                string? poiClean = await RunFullFrameKoboldOnBitmapAsync(
                    guideBmp,
                    detail,
                    token,
                    savePrep: false)
                    .ConfigureAwait(false);
                pipeTimer.Mark("full-frame-ocr (poi)", sw);

                var parts = new List<string>();
                if (!SpeechCleaner.IsUnusableOcrText(poiClean))
                {
                    parts.Add(poiClean!);
                    detail.AppendLine(
                        $"winner=comic-poi words={ComicRegionGeometry.CountWords(poiClean!)}");
                }
                else
                {
                    detail.AppendLine("comic-poi full-frame empty/unusable");
                }

                bool ducked = alreadyDucked;
                if (speakNow && parts.Count > 0)
                {
                    var speakPieces = SpeechCleaner.ExpandToSpeakPieces(parts);
                    speakPieces = ApplySpeakDedupeCoalesce(
                        speakPieces, detail, "comic-poi");
                    if (speakPieces.Count > 0)
                    {
                        if (!ducked)
                        {
                            DuckOtherAudio();
                            ducked = true;
                        }
                        var spoken = new List<string>();
                        for (int pi = 0; pi < speakPieces.Count; pi++)
                        {
                            token.ThrowIfCancellationRequested();
                            string unit = speakPieces[pi].Text;
                            spoken.Add(unit);
                            detail.AppendLine(
                                $"speak[comic-poi {pi + 1}/{speakPieces.Count}]: {unit}");
                            sw.Restart();
                            await SpeakWithSystemAsync(unit, token).ConfigureAwait(false);
                            pipeTimer.Mark($"tts comic-poi[{pi + 1}]", sw);
                            int pauseMs = speakPieces[pi].PauseAfterMs;
                            if (pauseMs > 0)
                                await Task.Delay(pauseMs, token).ConfigureAwait(false);
                        }
                        return (spoken, "comic-poi", ducked);
                    }
                }

                return (parts, parts.Count > 0 ? "comic-poi" : "comic-poi-empty", ducked);
            }
            finally
            {
                try { guideBmp?.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Write the POI guide bitmap for debug. Only mark as VL input when that
        /// exact image is what Local-LLM receives (single-island full-page).
        /// </summary>
        private static void SavePoiVlDebug(Bitmap? guide, bool isVlInput)
        {
            if (guide == null || !ActiveAnyDebugArtifacts)
                return;
            try
            {
                EnsureDebugFolder();
                // Guide at prep resolution (for box inspection). Actual Local-LLM
                // payload is written later in ExtractTextWithLocalLlmAsync (post-640).
                guide.Save(Path.Combine(DebugFolder, "last_poi_guide.png"), ImageFormat.Png);
                if (isVlInput)
                {
                    // Placeholder until Local-LLM send overwrites with scaled payload.
                    guide.Save(
                        Path.Combine(DebugFolder, "last_poi_vl_input.png"), ImageFormat.Png);
                }
            }
            catch { /* debug only */ }
        }

        /// <summary>
        /// Crop each reading-order region (pad vs neighbors), then the same
        /// <see cref="ComicPoiGuide.ComposeVerticalStripStack"/> as POI island stack
        /// (orange canvas + Balloons beef). Strip cut is crop-stack specific;
        /// canvas rules are shared.
        /// </summary>
        private static Bitmap? BuildVerticalCropStack(
            Bitmap pipelineImage,
            List<DetectedTextRegion> regions,
            StringBuilder detail,
            int cropPadPx)
        {
            if (pipelineImage == null || regions == null || regions.Count == 0)
                return null;

            var strips = new List<Bitmap>();
            try
            {
                for (int i = 0; i < regions.Count; i++)
                {
                    var neighbors = regions
                        .Where((_, j) => j != i)
                        .Select(r => r.Bounds)
                        .ToList();
                    using var crop = CropRegionClamped(
                        pipelineImage,
                        regions[i].Bounds,
                        cropPadPx,
                        neighbors);
                    if (crop == null || crop.Width < 4 || crop.Height < 4)
                    {
                        detail.AppendLine(
                            $"  crop-stack strip[{i + 1}]: skip empty crop");
                        continue;
                    }

                    // Plain snap of prep — Image pipeline already upscaled + toned.
                    var strip = (Bitmap)crop.Clone();
                    detail.AppendLine(
                        $"  crop-stack strip[{i + 1}]: " +
                        $"{strip.Width}x{strip.Height} (native prep crop)");
                    strips.Add(strip);
                }

                // Same canvas as POI: orange + beef/bottom-share from Balloons.
                // Gap/margin from stack knobs so one UI drives both paths.
                int gap = Math.Clamp(
                    SpeakRunSettings.GetComicPoiAutoStackGapPx(), 0, 64);
                int margin = Math.Clamp(
                    SpeakRunSettings.GetComicPoiAutoStackMarginPx(), 0, 64);
                var canvas = ComicPoiGuide.ComposeVerticalStripStack(
                    strips,
                    detail,
                    stripGapPx: gap,
                    marginPx: margin,
                    paintGreenBoxes: true,
                    logPrefix: "crop-stack");

                if (canvas != null &&
                    (ActiveHeavyDebugImages || ActiveAnyDebugArtifacts))
                {
                    try
                    {
                        EnsureDebugFolder();
                        canvas.Save(
                            Path.Combine(DebugFolder, "last_crop_stack.png"),
                            ImageFormat.Png);
                    }
                    catch { /* debug only */ }
                }

                return canvas;
            }
            finally
            {
                foreach (var s in strips)
                {
                    try { s.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>
        /// One Kobold call on the vertical balloon stack (same OCR prompt as every path).
        /// </summary>
        private async Task<string?> RunCropStackKoboldAsync(
            Bitmap stackBmp,
            StringBuilder detail,
            CancellationToken token)
        {
            string prompt = LocalLlmTaskPrompt;

            detail.AppendLine(
                $"--- crop-stack kobold {stackBmp.Width}x{stackBmp.Height} ---");

            if (!ComicBookOff && ActiveDecodeConsensus)
            {
                var (clean, rawLog) = await RunKoboldConsensusAsync(
                    stackBmp,
                    prompt,
                    FullFrameMaxTokens,
                    detail,
                    "crop-stack",
                    token);
                detail.AppendLine(
                    SpeechCleaner.IsUnusableOcrText(clean)
                        ? "--- crop-stack consensus: unusable ---"
                        : $"--- crop-stack consensus winner ---\n{clean}");
                if (!SpeechCleaner.IsUnusableOcrText(clean))
                    detail.AppendLine($"(raw sample)\n{Truncate(rawLog, 400)}");
                return clean;
            }

            string raw = await ExtractTextWithLocalLlmAsync(
                stackBmp, prompt, FullFrameMaxTokens, KoboldPrimaryTemperature);
            token.ThrowIfCancellationRequested();
            string cleaned = SpeechCleaner.CleanForSpeech(raw);
            detail.AppendLine($"--- crop-stack raw ---\n{raw}");
            if (!SpeechCleaner.IsUnusableOcrText(cleaned))
                return cleaned;

            raw = await ExtractTextWithLocalLlmAsync(
                stackBmp, LocalLlmTaskPrompt, FullFrameMaxTokens, KoboldPrimaryTemperature);
            token.ThrowIfCancellationRequested();
            cleaned = SpeechCleaner.CleanForSpeech(raw);
            detail.AppendLine($"--- crop-stack recovery ---\n{raw}");
            if (!SpeechCleaner.IsUnusableOcrText(cleaned))
                return cleaned;

            raw = await ExtractTextWithLocalLlmAsync(
                stackBmp, prompt, FullFrameMaxTokens, KoboldRecoveryTemperature);
            token.ThrowIfCancellationRequested();
            cleaned = SpeechCleaner.CleanForSpeech(raw);
            detail.AppendLine(
                $"--- crop-stack recovery T={KoboldRecoveryTemperature:F1} ---\n{raw}");
            return SpeechCleaner.IsUnusableOcrText(cleaned) ? null : cleaned;
        }


        /// <summary>
        /// ComicBook OFF (Default mode): same Image prep as ComicBook
        /// (letterbox → upscale → gray → tone), then one full-frame Kobold call.
        /// No fog / WinOCR detect / balloon crops — strategy differs, prep does not.
        /// POI guide is Comic Book only (see main ComicBook path).
        /// </summary>
        private async Task RunComicBookOffPreparedSnapAsync(
            Bitmap rawSnap,
            PipelineTimer pipeTimer,
            Stopwatch totalSw,
            CancellationToken token)
        {
            var detail = new StringBuilder();
            detail.Append(FormatRunHeader(comicBookOn: false, detectUsesFog: false));
            detail.AppendLine(
                "strategy=ComicBook OFF (Default) letterbox+upscale+gray+tone → full-frame OCR");

            var sw = Stopwatch.StartNew();
            ImagePrepStages? prepStages = null;
            Bitmap? letterboxOwned = null;
            Bitmap? upscaleOwned = null;
            Bitmap? grayOwned = null;
            Bitmap? toneOwned = null;
            Bitmap koboldSource = rawSnap;
            try
            {
                // Same Image prep as ComicBook ON / Settings → Image (includes tone).
                sw.Restart();
                prepStages = BuildImagePrepStages(
                    rawSnap, buildTone: true, detail);
                letterboxOwned = prepStages.Letterbox;
                upscaleOwned = prepStages.Upscale;
                grayOwned = prepStages.Gray;
                toneOwned = prepStages.Tone;
                koboldSource = prepStages.LiveOcrInput; // tone (shared with ComicBook)
                pipeTimer.Mark(
                    $"image-prep → {koboldSource.Width}x{koboldSource.Height}", sw);

                detail.AppendLine(
                    $"pipeline=letterbox+upscale" +
                    (grayOwned != null ? "+gray" : "") +
                    "+tone " +
                    $"{koboldSource.Width}x{koboldSource.Height} " +
                    $"(from snap {rawSnap.Width}x{rawSnap.Height}; " +
                    "ComicBook OFF - same prep as ComicBook, no fog/winocr/crops; " +
                    "prep=Image tab shared pipeline)");
                Debug.WriteLine(
                    $"[OCR] ComicBook OFF prep {koboldSource.Width}x{koboldSource.Height} → Kobold");

                // Analytics: snap → letterbox → upscale → gray → tone (Kobold input).
                CaptureAnalyticsImage("capture", "Capture", rawSnap);
                if (letterboxOwned.Width != rawSnap.Width ||
                    letterboxOwned.Height != rawSnap.Height)
                {
                    CaptureAnalyticsImage("letterbox", "Letterbox", letterboxOwned);
                }
                if (upscaleOwned != null)
                    CaptureAnalyticsImage("upscale", "Upscale", upscaleOwned);
                if (grayOwned != null)
                    CaptureAnalyticsImage("gray", "Ink gray", grayOwned);
                CaptureAnalyticsImage("ocr_prep", "OCR prep / tone", koboldSource);

                if (ActiveAnyDebugArtifacts)
                {
                    try
                    {
                        EnsureDebugFolder();
                        ClearStaleDebugArtifacts();
                        rawSnap.Save(
                            Path.Combine(DebugFolder, "last_capture.png"), ImageFormat.Png);
                        koboldSource.Save(
                            Path.Combine(DebugFolder, "last_full_prep.png"), ImageFormat.Png);
                        if (ActiveHeavyDebugImages)
                        {
                            if (letterboxOwned.Width != rawSnap.Width ||
                                letterboxOwned.Height != rawSnap.Height)
                            {
                                letterboxOwned.Save(
                                    Path.Combine(DebugFolder, "last_letterbox.png"),
                                    ImageFormat.Png);
                            }
                            if (upscaleOwned != null)
                            {
                                upscaleOwned.Save(
                                    Path.Combine(DebugFolder, "last_upscale.png"),
                                    ImageFormat.Png);
                            }
                            if (grayOwned != null)
                            {
                                grayOwned.Save(
                                    Path.Combine(DebugFolder, "last_gray.png"),
                                    ImageFormat.Png);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OCR] debug image save failed: {ex.Message}");
                    }
                }
                pipeTimer.Mark("debug-image-save", sw);

                sw.Restart();
                // savePrep:false — gray already captured above; avoid duplicate "Full-frame prep".
                string? fullClean = await RunFullFrameKoboldOnBitmapAsync(
                    koboldSource, detail, token, savePrep: false);
                pipeTimer.Mark("full-frame-ocr", sw);

                // Same pause pipeline as Comic Book: typed marks → ExpandToSpeakPieces
                // (comma/sentence/other/bubble ms from Voice tab) + dedupe/coalesce.
                // Previously Default only SplitSpeakPieces and skipped SSML multi-sentence
                // breaks in SpeakOneUnit — felt like Voice pauses were ignored.
                detail.AppendLine(
                    $"voice-pauses: encode={(UseCustomPauseEncodings ? "on" : "off")} " +
                    $"comma={CommaPauseMs} sentence={SentencePauseMs} " +
                    $"other={OtherPauseMs} bubble={BubblePauseMs}");

                var speakPieces = new List<SpeechCleaner.SpeakPiece>();
                string chosenTag = "full-frame";
                if (!SpeechCleaner.IsUnusableOcrText(fullClean))
                {
                    speakPieces = SpeechCleaner.ExpandToSpeakPieces(new[] { fullClean! });
                    if (speakPieces.Count >= 2)
                    {
                        int beforeDedup = speakPieces.Count;
                        speakPieces = SpeechCleaner.DedupeSpeakPiecesForTts(speakPieces, detail);
                        if (speakPieces.Count != beforeDedup)
                        {
                            detail.AppendLine(
                                $"speak-dedupe {beforeDedup} → {speakPieces.Count}");
                        }
                    }
                    if (speakPieces.Count >= 2)
                    {
                        int beforeCoal = speakPieces.Count;
                        speakPieces = SpeechCleaner.CoalesceFragmentSpeakPieces(
                            speakPieces, detail);
                        if (speakPieces.Count != beforeCoal)
                        {
                            detail.AppendLine(
                                $"speak-coalesce {beforeCoal} → {speakPieces.Count}");
                        }
                    }
                }

                // Short single balloons ("No!", "OK!") often fail full-frame VL on a
                // busy panel. WinOCR usually still sees the word — use it as TTS.
                if (speakPieces.Count == 0)
                {
                    detail.AppendLine(
                        "ComicBook OFF full-frame empty → WinOCR speak fallback");
                    sw.Restart();
                    var winParts = await TryWinOcrSpeakFallbackAsync(
                        koboldSource, existingRegions: null, detail, token);
                    pipeTimer.Mark("winocr-speak-fallback", sw);
                    if (winParts.Count > 0)
                    {
                        speakPieces = SpeechCleaner.ExpandToSpeakPieces(winParts);
                        chosenTag = "winocr-fallback";
                        detail.AppendLine(
                            $"winner=winocr-fallback parts={winParts.Count} " +
                            $"words={winParts.Sum(ComicRegionGeometry.CountWords)} (ComicBook OFF)");
                    }
                }

                var spokenParts = speakPieces.Select(p => p.Text).ToList();
                int words = spokenParts.Sum(ComicRegionGeometry.CountWords);
                detail.AppendLine(
                    spokenParts.Count > 0
                        ? $"winner={chosenTag} words={words} units={spokenParts.Count} (ComicBook OFF prep)"
                        : "winner=none (ComicBook OFF prep empty)");
                detail.AppendLine(
                    $"speak-plan units={speakPieces.Count} tag={chosenTag}");
                for (int pi = 0; pi < speakPieces.Count; pi++)
                {
                    var sp = speakPieces[pi];
                    string pauseNote = sp.PauseAfterMs > 0
                        ? $" then-pause={sp.PauseAfterMs}ms"
                        : "";
                    detail.AppendLine(
                        $"  plan[{pi + 1}/{speakPieces.Count}]: {sp.Text}{pauseNote}");
                }

                string planJoined = spokenParts.Count > 0
                    ? string.Join(
                        Environment.NewLine + Environment.NewLine, spokenParts)
                    : "(unreadable)";
                WriteLastOcrDebug(planJoined, detail);

                bool ducked = false;
                try
                {
                    if (speakPieces.Count > 0)
                    {
                        DuckOtherAudio();
                        ducked = true;
                        for (int pi = 0; pi < speakPieces.Count; pi++)
                        {
                            token.ThrowIfCancellationRequested();
                            detail.AppendLine(
                                $"speak[{chosenTag} {pi + 1}/{speakPieces.Count}]: {speakPieces[pi].Text}");
                            sw.Restart();
                            await SpeakWithSystemAsync(speakPieces[pi].Text, token);
                            pipeTimer.Mark($"tts {chosenTag}[{pi + 1}]", sw);

                            int pauseMs = speakPieces[pi].PauseAfterMs;
                            if (pauseMs > 0)
                            {
                                detail.AppendLine($"unit-pause {pauseMs} ms");
                                sw.Restart();
                                await Task.Delay(pauseMs, token);
                                pipeTimer.Mark($"unit-pause[{pi + 1}→{pi + 2}]", sw);
                            }
                        }
                        _lastText = string.Join(
                            Environment.NewLine + Environment.NewLine, spokenParts);
                    }
                    else
                    {
                        Debug.WriteLine("[OCR] ComicBook OFF prep → unreadable");
                        DuckOtherAudio();
                        ducked = true;
                        if (_lastText != "unreadable")
                        {
                            _lastText = "unreadable";
                            await SpeakWithSystemAsync("unreadable", token);
                        }
                    }
                }
                finally
                {
                    if (ducked)
                        RestoreAudio();
                }

                totalSw.Stop();
                pipeTimer.Mark("TOTAL wall-clock", totalSw);
                detail.AppendLine();
                detail.AppendLine("--- timings (ms) ---");
                detail.Append(pipeTimer.FormatReport());
                WriteLastOcrDebug(
                    spokenParts.Count > 0
                        ? string.Join(
                            Environment.NewLine + Environment.NewLine, spokenParts)
                        : "(unreadable)",
                    detail);
            }
            finally
            {
                // prepStages owns letterbox/upscale/gray/tone (aliases of *Owned).
                prepStages?.Dispose();
            }
        }

        /// <summary>
        /// Full-frame Kobold on letterbox-trimmed content, with wide-strip L/R rescue
        /// when the model only returns the first balloon. Returns speak-order parts.
        /// </summary>
        private async Task<List<string>> RunFullFrameWithWideRescueAsync(
            Bitmap capture,
            StringBuilder detail,
            CancellationToken token)
        {
            using var content = CropToContentOrClone(capture, out var contentRect, detail);
            string? fullClean = await RunFullFrameKoboldOnBitmapAsync(
                content, detail, token, savePrep: true);

            if (SpeechCleaner.IsUnusableOcrText(fullClean))
                return new List<string>();

            // Wide dual-balloon panels: single full-frame often misses the right bubble.
            if (ActiveWideStripRescue &&
                LooksLikeIncompleteWideStrip(content, fullClean!))
            {
                detail.AppendLine(
                    $"wide-strip incomplete? aspect={content.Width / (double)content.Height:F2} " +
                    $"words={ComicRegionGeometry.CountWords(fullClean!)} ? L/R split rescue");

                var halves = await ReadWideStripHalvesAsync(content, detail, token);
                int halfWords = halves.Sum(ComicRegionGeometry.CountWords);
                int fullWords = ComicRegionGeometry.CountWords(fullClean!);

                if (halves.Count >= 2 && halfWords >= fullWords + 2)
                {
                    detail.AppendLine(
                        $"wide-strip rescue win: {halves.Count} parts, {halfWords} words " +
                        $"(full had {fullWords})");
                    return halves;
                }

                if (halves.Count == 1 && halfWords > fullWords)
                {
                    detail.AppendLine("wide-strip rescue: one stronger half");
                    return halves;
                }

                // Prefer a longer full-frame recovery if split didn't help
                string? longer = await TryLongerFullFrameAsync(content, fullClean!, detail, token);
                if (!SpeechCleaner.IsUnusableOcrText(longer) && ComicRegionGeometry.CountWords(longer!) > fullWords)
                {
                    detail.AppendLine($"wide-strip longer full-frame: {longer}");
                    return new List<string> { longer! };
                }

                detail.AppendLine("wide-strip rescue kept original full-frame");
            }

            return new List<string> { fullClean! };
        }

        private async Task<string?> RunFullFrameKoboldOnBitmapAsync(
            Bitmap source,
            StringBuilder detail,
            CancellationToken token,
            bool savePrep,
            string? promptOverride = null)
        {
            using var fullPrep = PrepareForLocalLlmOcr(source);
            if (savePrep)
            {
                // Analytics: only when full-frame scale+sharpen actually changes the
                // image. With EnableFullFrameScaleAndSharpen=false this is a clone of
                // OCR prep already logged (Capture / letterbox / … / tone) — skip.
                if (EnableFullFrameScaleAndSharpen)
                    CaptureAnalyticsImage("full_prep", "Full-frame prep", fullPrep);
                if (ActiveAnyDebugArtifacts)
                {
                    try
                    {
                        EnsureDebugFolder();
                        fullPrep.Save(Path.Combine(DebugFolder, "last_full_prep.png"), ImageFormat.Png);
                    }
                    catch { }
                }
            }

            string primary = promptOverride ?? LocalLlmTaskPrompt;

            // ComicBook ON: diversified 2-of-3 decode.
            // OFF: single primary then recovery ladder.
            if (!ComicBookOff && ActiveDecodeConsensus)
            {
                var (clean, rawLog) = await RunKoboldConsensusAsync(
                    fullPrep,
                    primary,
                    FullFrameMaxTokens,
                    detail,
                    "full-frame",
                    token);
                detail.AppendLine(
                    SpeechCleaner.IsUnusableOcrText(clean)
                        ? "--- full-frame consensus: unusable ---"
                        : $"--- full-frame consensus winner ---\n{clean}");
                if (!SpeechCleaner.IsUnusableOcrText(clean))
                    detail.AppendLine($"(raw sample)\n{Truncate(rawLog, 400)}");
                return clean;
            }

            // ComicBook OFF / consensus disabled: single primary then recovery
            string fullRaw = await ExtractTextWithLocalLlmAsync(
                fullPrep,
                promptOverride: promptOverride,
                maxTokens: FullFrameMaxTokens);
            token.ThrowIfCancellationRequested();
            string fullClean = SpeechCleaner.CleanForSpeech(fullRaw);
            detail.AppendLine($"--- full-frame raw ---\n{fullRaw}");

            if (SpeechCleaner.IsUnusableOcrText(fullClean))
            {
                fullRaw = await ExtractTextWithLocalLlmAsync(
                    fullPrep, LocalLlmTaskPrompt, FullFrameMaxTokens);
                token.ThrowIfCancellationRequested();
                fullClean = SpeechCleaner.CleanForSpeech(fullRaw);
                detail.AppendLine($"--- full-frame recovery OCR: ---\n{fullRaw}");
            }

            if (SpeechCleaner.IsUnusableOcrText(fullClean))
            {
                fullRaw = await ExtractTextWithLocalLlmAsync(
                    fullPrep,
                    promptOverride: promptOverride,
                    maxTokens: FullFrameMaxTokens,
                    temperature: KoboldRecoveryTemperature);
                token.ThrowIfCancellationRequested();
                fullClean = SpeechCleaner.CleanForSpeech(fullRaw);
                detail.AppendLine(
                    $"--- full-frame recovery T={KoboldRecoveryTemperature:F1} ---\n{fullRaw}");
            }

            return SpeechCleaner.IsUnusableOcrText(fullClean) ? null : fullClean;
        }

        /// <summary>
        /// Extra full-frame passes when the first read looks short for a wide strip.
        /// </summary>
        private async Task<string?> TryLongerFullFrameAsync(
            Bitmap source,
            string current,
            StringBuilder detail,
            CancellationToken token)
        {
            using var prep = PrepareForLocalLlmOcr(source);
            string best = current;

            string raw = await ExtractTextWithLocalLlmAsync(
                prep, LocalLlmTaskPrompt, FullFrameMaxTokens, KoboldPrimaryTemperature);
            token.ThrowIfCancellationRequested();
            string clean = SpeechCleaner.CleanForSpeech(raw);
            detail.AppendLine($"--- wide longer OCR: ---\n{raw}");
            if (!SpeechCleaner.IsUnusableOcrText(clean) && ComicRegionGeometry.CountWords(clean) > ComicRegionGeometry.CountWords(best))
                best = clean;

            raw = await ExtractTextWithLocalLlmAsync(
                prep, null, FullFrameMaxTokens, KoboldRecoveryTemperature);
            token.ThrowIfCancellationRequested();
            clean = SpeechCleaner.CleanForSpeech(raw);
            detail.AppendLine($"--- wide longer T={KoboldRecoveryTemperature:F1} ---\n{raw}");
            if (!SpeechCleaner.IsUnusableOcrText(clean) && ComicRegionGeometry.CountWords(clean) > ComicRegionGeometry.CountWords(best))
                best = clean;

            return best;
        }

        /// <summary>
        /// True when content is a wide strip and full-frame text looks like a single
        /// short balloon (likely missed a second bubble on the far side).
        /// </summary>
        private static bool LooksLikeIncompleteWideStrip(Bitmap content, string text)
        {
            if (content.Width < 200 || content.Height < 20)
                return false;

            double aspect = content.Width / (double)Math.Max(1, content.Height);
            if (aspect < WideStripMinAspect)
                return false;

            int words = ComicRegionGeometry.CountWords(text);
            int alnum = SpeechCleaner.CountAlnum(text);
            if (words <= 0)
                return true;

            // Short transcript on a very wide panel
            if (words <= WideStripMaxWordsBeforeSplit)
                return true;

            // Medium length but still sparse for a cinema-wide strip
            if (aspect >= 3.0 && words <= 18 && alnum < 90)
                return true;

            return false;
        }

        /// <summary>
        /// Kobold left half then right half of a wide content strip (small overlap).
        /// Returns usable parts in left?right order, de-duped against each other.
        /// </summary>
        private async Task<List<string>> ReadWideStripHalvesAsync(
            Bitmap content,
            StringBuilder detail,
            CancellationToken token)
        {
            var parts = new List<string>();
            int w = content.Width;
            int h = content.Height;
            int overlap = Math.Max(24, w / 20);
            int mid = w / 2;

            var leftRect = new Rectangle(0, 0, Math.Min(w, mid + overlap), h);
            var rightRect = new Rectangle(
                Math.Max(0, mid - overlap), 0,
                w - Math.Max(0, mid - overlap), h);

            for (int i = 0; i < 2; i++)
            {
                var rect = i == 0 ? leftRect : rightRect;
                string tag = i == 0 ? "L" : "R";
                using var half = CropBitmap(content, rect);
                if (half == null)
                    continue;

                using var prep = PrepareCropForLocalLlmOcr(half);
                CaptureAnalyticsImage(
                    $"wide_half_{tag}",
                    $"Wide half {tag}",
                    prep);
                if (ActiveHeavyDebugImages)
                {
                    try
                    {
                        EnsureDebugFolder();
                        prep.Save(
                            Path.Combine(DebugFolder, $"wide_half_{tag}.png"), ImageFormat.Png);
                    }
                    catch { }
                }

                detail.AppendLine(
                    $"  wide-half {tag}: {half.Width}x{half.Height} ? prep {prep.Width}x{prep.Height}");

                string clean;
                if (ActiveDecodeConsensus)
                {
                    var (cClean, _) = await RunKoboldConsensusAsync(
                        prep,
                        LocalLlmTaskPrompt,
                        CropMaxTokens,
                        detail,
                        $"wide-half-{tag}",
                        token);
                    clean = cClean ?? "";
                }
                else
                {
                    string raw = await ExtractTextWithLocalLlmAsync(
                        prep, LocalLlmTaskPrompt, CropMaxTokens, KoboldPrimaryTemperature);
                    token.ThrowIfCancellationRequested();
                    clean = SpeechCleaner.CleanForSpeech(raw);

                    if (SpeechCleaner.IsUnusableOcrText(clean))
                    {
                        raw = await ExtractTextWithLocalLlmAsync(
                            prep, LocalLlmTaskPrompt, CropMaxTokens, KoboldPrimaryTemperature);
                        token.ThrowIfCancellationRequested();
                        clean = SpeechCleaner.CleanForSpeech(raw);
                    }
                }

                if (SpeechCleaner.IsUnusableOcrText(clean))
                {
                    detail.AppendLine($"  wide-half {tag}: unusable");
                    continue;
                }

                // Drop overlap re-reads of the previous half
                if (parts.Count > 0)
                {
                    string stripped = StripOverlapWithPrevious(clean, parts[^1]);
                    if (SpeechCleaner.IsUnusableOcrText(stripped))
                    {
                        detail.AppendLine($"  wide-half {tag}: dedupe emptied");
                        continue;
                    }
                    // Also skip if this half is almost fully contained in previous
                    if (NormalizeOcrCompare(parts[^1]).Contains(NormalizeOcrCompare(stripped)) &&
                        ComicRegionGeometry.CountWords(stripped) <= ComicRegionGeometry.CountWords(parts[^1]))
                    {
                        detail.AppendLine($"  wide-half {tag}: subset of previous ? skip");
                        continue;
                    }
                    clean = stripped;
                }

                detail.AppendLine($"  wide-half {tag}: {clean}");
                parts.Add(clean);
            }

            return parts;
        }

        /// <summary>
        /// When Kobold full/crop best-of is empty, speak WinOCR text (all modes).
        /// Reuses ComicBook detect regions when available; otherwise runs detect.
        /// </summary>
        private async Task<List<string>> TryWinOcrSpeakFallbackAsync(
            Bitmap pipelineImage,
            List<DetectedTextRegion>? existingRegions,
            StringBuilder detail,
            CancellationToken token)
        {
            var parts = CollectWinOcrSpeakParts(existingRegions);
            if (parts.Count > 0)
            {
                detail.AppendLine(
                    $"winocr-fallback: reuse detect text parts={parts.Count} " +
                    $"words={parts.Sum(ComicRegionGeometry.CountWords)}");
                foreach (string p in parts)
                    detail.AppendLine($"  winocr-speak: {p}");
                return parts;
            }

            detail.AppendLine(
                "winocr-fallback: no usable text on existing regions ? detect pass");
            try
            {
                var detection = await DetectTextRegionsAsync(pipelineImage, token);
                token.ThrowIfCancellationRequested();
                detail.AppendLine(detection.Detail);

                var regions = ImproveDetectedRegions(
                    detection.Regions, pipelineImage.Width, pipelineImage.Height);
                bool fragmented =
                    detection.LooksFragmented ||
                    LooksFragmented(regions, pipelineImage.Width, pipelineImage.Height);
                regions = CoalesceIntoReadingBlocks(
                    regions, pipelineImage.Width, pipelineImage.Height, fragmented);

                // Prefer separate islands so short balloons stay distinct speak parts.
                if (regions.Count >= 2 &&
                    (fragmented || LooksFragmented(
                        regions, pipelineImage.Width, pipelineImage.Height)) &&
                    !HasWellSeparatedSolidIslands(
                        regions, pipelineImage.Width, pipelineImage.Height))
                {
                    var collapsed = TryCollapseCompactCluster(
                        regions, pipelineImage.Width, pipelineImage.Height);
                    if (collapsed.Count < regions.Count)
                        regions = collapsed;
                }

                parts = CollectWinOcrSpeakParts(regions);
                if (parts.Count > 0)
                {
                    detail.AppendLine(
                        $"winocr-fallback: detect parts={parts.Count} " +
                        $"words={parts.Sum(ComicRegionGeometry.CountWords)}");
                    foreach (string p in parts)
                        detail.AppendLine($"  winocr-speak: {p}");
                    return parts;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                detail.AppendLine($"winocr-fallback: detect failed: {ex.Message}");
                Debug.WriteLine($"[OCR] winocr speak fallback failed: {ex.Message}");
            }

            detail.AppendLine("winocr-fallback: no usable WinOCR text");
            return new List<string>();
        }

        /// <summary>
        /// Clean and collect non-junk WinOCR strings from regions (reading order).
        /// </summary>
        private List<string> CollectWinOcrSpeakParts(
            IReadOnlyList<DetectedTextRegion>? regions)
        {
            var parts = new List<string>();
            if (regions == null || regions.Count == 0)
                return parts;

            // Regions are usually already in comic order; re-sort for safety.
            foreach (var r in SortComicReadingOrderRegions(regions.ToList()))
            {
                if (string.IsNullOrWhiteSpace(r.WinOcrText))
                    continue;
                if (IsJunkWinOcrText(r.WinOcrText))
                    continue;

                string clean = SpeechCleaner.CleanForSpeech(r.WinOcrText);
                if (SpeechCleaner.IsUnusableOcrText(clean))
                    continue;

                // Drop exact dups from overlapping detect merges
                if (parts.Count > 0 &&
                    string.Equals(
                        NormalizeOcrCompare(parts[^1]),
                        NormalizeOcrCompare(clean),
                        StringComparison.Ordinal))
                    continue;

                parts.Add(clean);
            }

            return parts;
        }

        /// <summary>
        /// Kobold crop: plain snap of the OCR pipeline image (pre-fog tone) + recovery.
        /// No second-pass crop upscale/tone — Image prep already did that.
        /// Primary speech text is Kobold; WinOCR text is last-resort TTS only.
        /// Region bounds come from WinOCR detect (possibly fogged); geometry matches.
        /// </summary>
        private async Task<RegionReadResult> ReadOneRegionAsync(
            Bitmap capture,
            DetectedTextRegion region,
            List<DetectedTextRegion> allRegions,
            int regionIndex,
            StringBuilder detail,
            CancellationToken token)
        {
            int index = regionIndex + 1;
            Rectangle bounds = region.Bounds;
            bool expanded = false;

            var neighborBoxes = allRegions
                .Where((_, i) => i != regionIndex)
                .Select(r => r.Bounds)
                .ToList();

            // Tiny scrap boxes ? expand before first Kobold call (prefer vertical),
            // then re-clamp so we don't invade neighbors (core island stays intact).
            if (IsTinyRegion(bounds, capture.Width, capture.Height))
            {
                var core = bounds;
                bounds = InflateRegionBoundsAsym(
                    bounds, capture.Width, capture.Height, 0.28, 0.55, 14, 24);
                bounds = ClampExpandedAwayFromNeighbors(
                    bounds, core, neighborBoxes, capture.Width, capture.Height);
                expanded = true;
                detail.AppendLine(
                    $"      pre-expand tiny ? {bounds.Width}x{bounds.Height}");
            }

            async Task<(string? Clean, string Raw)> TryKoboldOnBounds(Rectangle b, string tag)
            {
                using var crop = CropRegionClamped(
                    capture, b, ActiveCropPadPx, neighborBoxes);
                if (crop == null)
                    return (null, "");

                // Passthrough unless legacy EnableCropScaleAndSharpen is re-enabled.
                using var prepared = PrepareCropForLocalLlmOcr(crop);
                if (prepared.Width != crop.Width || prepared.Height != crop.Height)
                {
                    detail.AppendLine(
                        $"      {tag} crop {crop.Width}x{crop.Height} → prep {prepared.Width}x{prepared.Height}");
                }
                else
                {
                    detail.AppendLine(
                        $"      {tag} crop {crop.Width}x{crop.Height} (native prep snap)");
                }
                string cropKey = $"region_{index:D2}{(string.IsNullOrEmpty(tag) ? "" : tag)}";
                string cropTitle = string.IsNullOrEmpty(tag)
                    ? $"Crop {index}"
                    : $"Crop {index} ({tag.TrimStart('_', '-')})";
                CaptureAnalyticsImage(cropKey, cropTitle, prepared);
                if (ActiveHeavyDebugImages)
                {
                    try
                    {
                        EnsureDebugFolder();
                        prepared.Save(
                            Path.Combine(DebugFolder, $"region_{index:D2}{tag}.png"),
                            ImageFormat.Png);
                    }
                    catch { }
                }

                // ComicBook ON: diversified 2-of-3 consensus.
                if (ActiveDecodeConsensus)
                {
                    var (cClean, cRaw) = await RunKoboldConsensusAsync(
                        prepared,
                        LocalLlmTaskPrompt,
                        CropMaxTokens,
                        detail,
                        $"crop{tag}[{index}]",
                        token);
                    return (cClean, cRaw);
                }

                // Fallback ladder if consensus disabled
                string raw = await ExtractTextWithLocalLlmAsync(
                    prepared, LocalLlmTaskPrompt, CropMaxTokens, KoboldPrimaryTemperature);
                token.ThrowIfCancellationRequested();
                string cleaned = SpeechCleaner.CleanForSpeech(raw);
                if (!SpeechCleaner.IsUnusableOcrText(cleaned))
                    return (cleaned, raw);

                raw = await ExtractTextWithLocalLlmAsync(
                    prepared, LocalLlmTaskPrompt, CropMaxTokens, KoboldPrimaryTemperature);
                token.ThrowIfCancellationRequested();
                cleaned = SpeechCleaner.CleanForSpeech(raw);
                if (!SpeechCleaner.IsUnusableOcrText(cleaned))
                {
                    detail.AppendLine($"      {tag} recovery OCR: ok");
                    return (cleaned, raw);
                }

                raw = await ExtractTextWithLocalLlmAsync(
                    prepared, LocalLlmTaskPrompt, CropMaxTokens, KoboldRecoveryTemperature);
                token.ThrowIfCancellationRequested();
                cleaned = SpeechCleaner.CleanForSpeech(raw);
                detail.AppendLine(
                    SpeechCleaner.IsUnusableOcrText(cleaned)
                        ? $"      {tag} recovery T={KoboldRecoveryTemperature:F1} unusable"
                        : $"      {tag} recovery T={KoboldRecoveryTemperature:F1} ok len={cleaned.Length}");
                return (SpeechCleaner.IsUnusableOcrText(cleaned) ? null : cleaned, raw);
            }

            var (clean, rawLast) = await TryKoboldOnBounds(bounds, "");
            if (clean != null)
            {
                detail.AppendLine($"      kobold ok len={clean.Length}");
                return new RegionReadResult
                {
                    Text = clean,
                    KoboldFailed = false,
                    ExpandedRetry = expanded
                };
            }

            detail.AppendLine($"      kobold unusable raw={Truncate(rawLast, 80)}");

            // Expand and retry once (WinOCR often boxes only the last word)
            if (!expanded)
            {
                var core = region.Bounds;
                var bigger = InflateRegionBoundsAsym(
                    region.Bounds, capture.Width, capture.Height,
                    RegionInflateFractionX * 1.8, RegionInflateFractionY * 1.5,
                    18, 28);
                bigger = ClampExpandedAwayFromNeighbors(
                    bigger, core, neighborBoxes, capture.Width, capture.Height);
                if (bigger != bounds)
                {
                    expanded = true;
                    detail.AppendLine(
                        $"      expand-retry ? {bigger.Width}x{bigger.Height}");
                    (clean, rawLast) = await TryKoboldOnBounds(bigger, "_exp");
                    if (clean != null)
                    {
                        detail.AppendLine($"      kobold expand ok len={clean.Length}");
                        return new RegionReadResult
                        {
                            Text = clean,
                            KoboldFailed = false,
                            ExpandedRetry = true
                        };
                    }
                    detail.AppendLine(
                        $"      kobold expand unusable raw={Truncate(rawLast, 80)}");
                }
            }

            detail.AppendLine("      kobold failed (no WinOCR text fallback)");
            return new RegionReadResult
            {
                Text = null,
                KoboldFailed = true,
                ExpandedRetry = expanded
            };
        }

        /// <summary>
        /// Strip leading words of <paramref name="current"/> that restate the
        /// trailing words of <paramref name="previous"/> (neighbor crop bleed).
        /// </summary>
        private static string StripOverlapWithPrevious(string current, string previous)
        {
            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(previous))
                return current ?? "";

            string cur = current.Trim();
            string prev = previous.Trim();
            var prevWords = SpeechCleaner.TokenizeWords(prev);
            var curWords = SpeechCleaner.TokenizeWords(cur);
            if (prevWords.Count < 2 || curWords.Count < 2)
                return cur;

            int maxCheck = Math.Min(Math.Min(prevWords.Count, curWords.Count), 8);
            int best = 0;
            for (int n = maxCheck; n >= 2; n--)
            {
                bool match = true;
                for (int i = 0; i < n; i++)
                {
                    if (!WordsRoughlyEqual(
                            prevWords[prevWords.Count - n + i],
                            curWords[i]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    best = n;
                    break;
                }
            }

            if (best == 0)
                return cur;

            // Walk character stream past the first `best` words
            int idx = 0;
            int wordsSeen = 0;
            while (idx < cur.Length && wordsSeen < best)
            {
                while (idx < cur.Length && !char.IsLetterOrDigit(cur[idx]))
                    idx++;
                while (idx < cur.Length && char.IsLetterOrDigit(cur[idx]))
                    idx++;
                wordsSeen++;
            }
            while (idx < cur.Length &&
                   (char.IsWhiteSpace(cur[idx]) || cur[idx] is '.' or ',' or ';' or ':' or '!' or '?'))
                idx++;

            string rest = cur[idx..].Trim();
            return rest;
        }


        private static bool WordsRoughlyEqual(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            // Single-char OCR confusions (1/i/l, 0/o)
            if (a.Length == 1 && b.Length == 1)
            {
                char ca = char.ToLowerInvariant(a[0]);
                char cb = char.ToLowerInvariant(b[0]);
                if ((ca is '1' or 'i' or 'l') && (cb is '1' or 'i' or 'l'))
                    return true;
                if ((ca is '0' or 'o') && (cb is '0' or 'o'))
                    return true;
            }
            return false;
        }

        private static bool IsTinyRegion(Rectangle b, int capW, int capH)
        {
            int area = b.Width * b.Height;
            if (area < 2800) return true; // ~53x53
            if (area < capW * capH * 0.0005) return true;
            if (b.Width < 40 || b.Height < 28) return true;
            return false;
        }

        /// <summary>Asymmetric inflate - more vertical (stacked lines), less horizontal (side-by-side balloons).</summary>
        private static Rectangle InflateRegionBoundsAsym(
            Rectangle b, int capW, int capH,
            double fracX, double fracY, int minPxX, int minPxY)
        {
            int padX = Math.Max(minPxX, (int)(b.Width * fracX));
            int padY = Math.Max(minPxY, (int)(b.Height * fracY));
            var r = new Rectangle(
                b.X - padX, b.Y - padY,
                b.Width + padX * 2, b.Height + padY * 2);
            r.Intersect(new Rectangle(0, 0, capW, capH));
            return r.Width < 1 || r.Height < 1 ? b : r;
        }

        private static string NormalizeOcrCompare(string s)
            => ComicConsensus.NormalizeOcrCompare(s);

        /// <summary>
        /// Loose OCR string agreement for consensus voting.
        /// </summary>
        private static bool OcrTextsAgree(string a, string b)
            => ComicConsensus.OcrTextsAgree(a, b);

        /// <summary>
        /// ComicBook ON diversified decode: primary T=0, primary T=0.25, optional
        /// recovery third when they disagree or fail. 2-of-3 majority, else best quality.
        /// </summary>
        private async Task<(string? Clean, string RawLog)> RunKoboldConsensusAsync(
            Bitmap prepared,
            string primaryPrompt,
            int maxTokens,
            StringBuilder detail,
            string logTag,
            CancellationToken token)
        {
            var reads = new List<(string Label, string Raw, string Clean)>(3);

            async Task AddPassAsync(string label, string prompt, double temperature)
            {
                string raw = await ExtractTextWithLocalLlmAsync(
                    prepared, prompt, maxTokens, temperature);
                token.ThrowIfCancellationRequested();
                string clean = SpeechCleaner.CleanForSpeech(raw);
                reads.Add((label, raw, clean));
                detail.AppendLine(
                    $"--- {logTag} consensus {label} " +
                    $"(ok={!SpeechCleaner.IsUnusableOcrText(clean)}) ---\n{raw}");
            }

            // A: mode / stable (always)
            await AddPassAsync(
                $"A T={KoboldPrimaryTemperature:F0}",
                primaryPrompt,
                KoboldPrimaryTemperature);

            string cleanA = reads[0].Clean;
            bool aOk = !SpeechCleaner.IsUnusableOcrText(cleanA);

            // Accuracy-first fast path: strong A → skip B/C (B was almost always
            // empty in logs; C mirrored A). Weak/short A keeps full multi-pass
            // so rare B saves (e.g. "and it was") still work.
            if (EnableConsensusStrongAFastPath &&
                aOk &&
                IsStrongConsensusPrimary(cleanA, out string strongWhy))
            {
                detail.AppendLine(
                    $"--- {logTag} consensus skip-B/C strong-A ({strongWhy}) ---");
                detail.AppendLine(
                    $"{logTag} consensus: strong-A accept " +
                    $"words={ComicRegionGeometry.CountWords(cleanA)} q={SpeechCleaner.OcrTextQualityScore(cleanA)}");
                return (cleanA, reads[0].Raw);
            }

            if (EnableConsensusStrongAFastPath && aOk)
            {
                detail.AppendLine(
                    $"--- {logTag} consensus multi-pass (A ok but not strong: " +
                    $"words={ComicRegionGeometry.CountWords(cleanA)} alnum={SpeechCleaner.CountAlnum(cleanA)} " +
                    $"q={SpeechCleaner.OcrTextQualityScore(cleanA)}) ---");
            }

            // B: diversified decode
            await AddPassAsync(
                $"B T={KoboldConsensusTemperature:F2}",
                primaryPrompt,
                KoboldConsensusTemperature);

            string cleanB = reads[1].Clean;
            bool bOk = !SpeechCleaner.IsUnusableOcrText(cleanB);
            bool abAgree = aOk && bOk && OcrTextsAgree(cleanA, cleanB);

            // C: third opinion only when needed (disagreement or either unusable)
            if (!abAgree)
            {
                string cPrompt = (!aOk && !bOk) ? LocalLlmTaskPrompt : primaryPrompt;
                double cTemp = (!aOk && !bOk)
                    ? KoboldPrimaryTemperature
                    : KoboldConsensusTemperature;
                string cLabel = (!aOk && !bOk)
                    ? $"C recovery T={cTemp:F0}"
                    : $"C T={cTemp:F2}";
                // If both failed primary, try recovery @ T0; if they disagreed,
                // third primary at consensus temp. If only one failed, recovery @ T0.
                if (aOk != bOk)
                {
                    cPrompt = LocalLlmTaskPrompt;
                    cTemp = KoboldPrimaryTemperature;
                    cLabel = $"C recovery T={cTemp:F0}";
                }

                await AddPassAsync(cLabel, cPrompt, cTemp);
            }
            else
            {
                detail.AppendLine(
                    $"--- {logTag} consensus skip-C (A/B agree) ---");
            }

            // Still nothing usable? Last-ditch recovery at higher temp (old ladder).
            if (reads.All(r => SpeechCleaner.IsUnusableOcrText(r.Clean)))
            {
                await AddPassAsync(
                    $"D recovery T={KoboldRecoveryTemperature:F1}",
                    LocalLlmTaskPrompt,
                    KoboldRecoveryTemperature);
            }

            string? winner = PickConsensusWinner(reads, detail, logTag);
            string rawLog = "";
            if (winner != null)
            {
                foreach (var r in reads)
                {
                    if (string.Equals(r.Clean, winner, StringComparison.Ordinal))
                    {
                        rawLog = r.Raw;
                        break;
                    }
                }
                if (rawLog.Length == 0)
                {
                    foreach (var r in reads)
                    {
                        if (!SpeechCleaner.IsUnusableOcrText(r.Clean) && OcrTextsAgree(r.Clean, winner))
                        {
                            rawLog = r.Raw;
                            break;
                        }
                    }
                }
            }
            if (rawLog.Length == 0 && reads.Count > 0)
                rawLog = reads[^1].Raw;

            return (winner, rawLog);
        }

        /// <summary>
        /// True when T=0 primary is solid enough to skip extra consensus passes.
        /// </summary>
        private static bool IsStrongConsensusPrimary(string clean, out string reason)
            => ComicConsensus.IsStrongConsensusPrimary(clean, out reason);

        /// <summary>
        /// Majority (2+) agreement group wins; else best single usable by quality.
        /// </summary>
        private static string? PickConsensusWinner(
            List<(string Label, string Raw, string Clean)> reads,
            StringBuilder detail,
            string logTag)
            => ComicConsensus.PickConsensusWinner(reads, detail, logTag);

        /// <summary>Fresh screen snap of the current rect / ellipse / lasso region.</summary>
        private Bitmap? SnapCapture()
        {
            if (_lassoPoints != null && _lassoPoints.Count > 2)
                return CreateMaskedBitmapFromLasso(_lassoPoints);
            if (_isEllipse)
                return CreateEllipseMaskedBitmap(_rect);
            return CreateRectBitmap(_rect);
        }

        /// <summary>
        /// Screen snap of a region only — no OCR, Kobold, or TTS.
        /// Used by Settings Image / Balloons "Snap region" preview load.
        /// Caller owns and must dispose the returned bitmap.
        /// Does <b>not</b> construct an <see cref="OcrProcessor"/> (that wired TTS/MediaPlayer
        /// for no reason and leaked COM objects until snap failed).
        /// </summary>
        public static Bitmap? SnapRegionOnly(
            Rectangle rect,
            List<Point>? lassoPoints = null,
            bool isEllipse = false)
        {
            if (lassoPoints != null && lassoPoints.Count > 2)
                return CreateMaskedBitmapFromLasso(lassoPoints);
            if (isEllipse)
                return CreateEllipseMaskedBitmap(rect);
            return CreateRectBitmap(rect);
        }

        // ------------------- WinOCR region detect / cluster / order -------------------

        /// <summary>
        /// Single cheap OCR detect pass after prep — word count only (diagnostic log).
        /// No multi-scale retry, no orphan rescue. Does <b>not</b> gate full detect/crops;
        /// Comic Book always continues to <see cref="BuildComicReadingRegionsAsync"/>.
        /// </summary>
        private async Task<(int Words, string Detail)> QuickWinOcrWordCountAsync(
            Bitmap capture,
            CancellationToken token)
        {
            var log = new StringBuilder();
            log.AppendLine(
                $"quick-winocr wordcount (single pass scale~{WinOcrDetectScale}, no rescue)");

            var engine = GetWinOcrEngine();
            if (engine == null || capture.Width < 2 || capture.Height < 2)
            {
                log.AppendLine("  no engine or empty capture ? words=0");
                return (0, log.ToString());
            }

            try
            {
                // One pass only - reuse existing detect bitmap build for consistency
                var regions = await RunWinOcrPassAsync(
                    engine, capture, WinOcrDetectScale, token, log);
                token.ThrowIfCancellationRequested();

                int words = 0;
                foreach (var r in regions)
                    words += ComicRegionGeometry.CountWords(r.WinOcrText);

                // Also count raw joined text in case clustering dropped scraps
                // (regions already filtered junk; word sum is the gate signal)
                log.AppendLine(
                    $"  regions={regions.Count} words={words}");
                return (words, log.ToString());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.AppendLine($"  quick-winocr failed: {ex.Message} ? words=0");
                Debug.WriteLine($"[WinOCR] quick wordcount failed: {ex.Message}");
                return (0, log.ToString());
            }
        }

        /// <summary>
        /// Shared detect entry for live + Balloons preview/speak.
        /// Detect fog off → WinOCR on tone. On → fixed gray fog amount, then full
        /// reading-region pipeline. (Dynamic fog search was removed.)
        /// </summary>
        private async Task<(
            List<DetectedTextRegion> Regions,
            DetectionResult Detection,
            bool Fragmented,
            Bitmap DetectImage,
            bool OwnsDetectImage,
            float FogAmountUsed)> BuildComicRegionsSharedDetectAsync(
            Bitmap toneImage,
            StringBuilder detail,
            CancellationToken token)
        {
            int pipeW = toneImage.Width;
            int pipeH = toneImage.Height;
            float fixedAmt = Math.Clamp(WinOcrDetectGrayFogAmount, 0f, 1f);

            if (!EnableWinOcrDetectGrayFog)
            {
                detail.AppendLine("detect-fog=off (detect on tone)");
                var (r0, d0, f0) = await BuildComicReadingRegionsAsync(
                    toneImage, pipeW, pipeH, detail, token).ConfigureAwait(false);
                return (r0, d0, f0, toneImage, OwnsDetectImage: false, FogAmountUsed: 0f);
            }

            detail.AppendLine($"detect-fog=fixed amount={fixedAmt:0.###}");
            Bitmap fog = ApplyGrayFog(toneImage, fixedAmt, WinOcrDetectGrayFogLevel);
            try
            {
                var (r1, d1, f1) = await BuildComicReadingRegionsAsync(
                    fog, pipeW, pipeH, detail, token).ConfigureAwait(false);
                return (r1, d1, f1, fog, OwnsDetectImage: true, FogAmountUsed: fixedAmt);
            }
            catch
            {
                try { fog.Dispose(); } catch { /* ignore */ }
                throw;
            }
        }

        /// <summary>
        /// Single ComicBook region pipeline for live speak, Balloons preview, and
        /// Balloons speak-test. Same detect image + settings must yield the same
        /// reading-blocks (preview is useless if it disagrees with live).
        /// Steps: WinOCR detect (fog) → grow → dead-island → coalesce →
        /// compact-collapse → mega-split → merge-overlap → Western sort.
        /// </summary>
        private async Task<(
            List<DetectedTextRegion> Regions,
            DetectionResult Detection,
            bool Fragmented)> BuildComicReadingRegionsAsync(
            Bitmap detectImage,
            int pipeW,
            int pipeH,
            StringBuilder detail,
            CancellationToken token)
        {
            var detection = await DetectTextRegionsAsync(detectImage, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            detail.AppendLine(detection.Detail);
            detail.AppendLine(
                $"lowConfidence={detection.LowConfidence} " +
                $"fragmented={detection.LooksFragmented} regions={detection.Regions.Count}");

            var regions = ImproveDetectedRegions(detection.Regions, pipeW, pipeH);
            detail.AppendLine($"afterImprove regions={regions.Count}");

            int beforeDead = regions.Count;
            regions = FilterDeadDetectRegions(regions, detectImage, detail);
            if (regions.Count != beforeDead)
            {
                detail.AppendLine(
                    $"dead-island filter: {beforeDead} → {regions.Count}");
            }

            bool fragmented =
                detection.LooksFragmented ||
                LooksFragmented(regions, pipeW, pipeH);

            int beforeCoalesce = regions.Count;
            regions = CoalesceIntoReadingBlocks(
                regions, pipeW, pipeH, fragmented);
            if (regions.Count != beforeCoalesce)
            {
                detail.AppendLine(
                    $"coalesce {beforeCoalesce} → {regions.Count} (frag={fragmented})");
            }

            // Compact collapse: was live-only historically — that made preview lie.
            if (regions.Count >= 2 &&
                (fragmented || LooksFragmented(regions, pipeW, pipeH)) &&
                !HasWellSeparatedSolidIslands(regions, pipeW, pipeH))
            {
                var collapsed = TryCollapseCompactCluster(regions, pipeW, pipeH);
                if (collapsed.Count < regions.Count)
                {
                    detail.AppendLine(
                        $"collapse-cluster {regions.Count} → {collapsed.Count}");
                    regions = collapsed;
                }
            }

            regions = ApplyMergeOverlappingIslandsIfEnabled(
                regions, pipeW, pipeH, detail);

            // Late scrap after merge can reappear — filter again.
            int beforeDead2 = regions.Count;
            regions = FilterDeadDetectRegions(regions, detectImage, detail);
            if (regions.Count != beforeDead2)
            {
                detail.AppendLine(
                    $"dead-island filter (post-merge): {beforeDead2} → {regions.Count}");
            }

            regions = SortComicReadingOrderRegions(regions);

            detail.AppendLine($"reading-blocks={regions.Count}");
            return (regions, detection, fragmented);
        }

        /// <summary>
        /// Locate text islands for crops. <b>Full strength: do not miss balloons.</b>
        /// Two full-frame OCR detect passes (scale 1.0 + 1.5), pick best, then
        /// bright-blob orphan fill so plates OCR skipped still get a Local-LLM crop.
        /// Always used on the Comic path when there is no region override — word-count
        /// is diagnostic only and does <b>not</b> gate this method.
        /// </summary>
        private async Task<DetectionResult> DetectTextRegionsAsync(Bitmap capture, CancellationToken token)
        {
            var empty = new DetectionResult
            {
                Regions = new List<DetectedTextRegion>(),
                LowConfidence = true,
                LooksFragmented = true,
                Detail = "detect: no engine or empty capture"
            };

            var engine = GetWinOcrEngine();
            if (engine == null || capture.Width < 2 || capture.Height < 2)
                return empty;

            try
            {
                var log = new StringBuilder();
                log.AppendLine(
                    $"detect plan: 2 full-frame passes " +
                    $"(scale {WinOcrDetectScale} + {WinOcrDetectScaleRetry}); " +
                    "no orphan recover");

                // -- Pass 1 --------------------------------------------------
                var first = await RunWinOcrPassAsync(
                    engine, capture, WinOcrDetectScale, token, log);
                token.ThrowIfCancellationRequested();
                bool low1 = IsLowConfidenceDetection(capture, first);
                bool sparse1 = IsSparseWideDetection(capture, first);
                log.AppendLine(
                    $"pass1 scale~{WinOcrDetectScale}: regions={first.Count} " +
                    $"lowConf={low1} sparseWide={sparse1} score={ScoreDetection(first)}");

                List<DetectedTextRegion> best = first;
                Bitmap? pass2Owned = null;
                try
                {
                    // -- Pass 2 --------------------------------------------------
                    // Prefer letterbox content for pass2 when black bars waste scale.
                    Bitmap pass2Src = capture;
                    int offX = 0, offY = 0;

                    if (TryFindContentBounds(capture, out var contentRect) &&
                        ContentBoundsIsMeaningful(capture, contentRect))
                    {
                        pass2Owned = CropBitmap(capture, contentRect);
                        if (pass2Owned != null)
                        {
                            pass2Src = pass2Owned;
                            offX = contentRect.X;
                            offY = contentRect.Y;
                            log.AppendLine(
                                $"pass2 content-crop {contentRect.Width}x{contentRect.Height} " +
                                $"@({offX},{offY})");
                        }
                    }

                    var secondRaw = await RunWinOcrPassAsync(
                        engine, pass2Src, WinOcrDetectScaleRetry, token, log);
                    token.ThrowIfCancellationRequested();

                    var second = (offX != 0 || offY != 0)
                        ? OffsetRegions(secondRaw, offX, offY, capture.Width, capture.Height)
                        : secondRaw;

                    int score1 = ScoreDetection(first);
                    int score2 = ScoreDetection(second);
                    log.AppendLine(
                        $"pass2 scale~{WinOcrDetectScaleRetry}: regions={second.Count} " +
                        $"score={score2}");

                    // Prefer higher score; on tie prefer more islands
                    string pick;
                    if (score2 > score1 ||
                        (score2 == score1 && second.Count > first.Count))
                    {
                        best = second;
                        pick = "pass2";
                    }
                    else
                    {
                        best = first;
                        pick = "pass1";
                    }
                    log.AppendLine(
                        $"pass pick: {pick} (score {Math.Max(score1, score2)}, " +
                        $"regions={best.Count})");

                    // Zero-region rescue only (orphan recover feature removed).
                    if (best.Count == 0)
                    {
                        log.AppendLine("zero-region rescue…");
                        var rescued = await ZeroRegionRescueAsync(
                            engine, capture, token, log);
                        token.ThrowIfCancellationRequested();
                        if (rescued.Count > 0)
                        {
                            log.AppendLine($"zero-region rescue found {rescued.Count} boxes");
                            best = rescued;
                        }
                        else
                        {
                            log.AppendLine("zero-region rescue empty");
                        }
                    }

                    if (best.Count > MaxTextRegions)
                    {
                        log.AppendLine($"cap {best.Count} ? {MaxTextRegions}");
                        best = best.Take(MaxTextRegions).ToList();
                    }

                    bool finalLow = IsLowConfidenceDetection(capture, best) ||
                                    IsSparseWideDetection(capture, best);
                    bool frag = LooksFragmented(best, capture.Width, capture.Height);
                    log.AppendLine(
                        $"final regions={best.Count} lowConfidence={finalLow} fragmented={frag}");
                    Debug.WriteLine(
                        $"[WinOCR] {log.ToString().Replace("\r\n", " | ").Replace('\n', ' ')}");

                    return new DetectionResult
                    {
                        Regions = best,
                        LowConfidence = finalLow,
                        LooksFragmented = frag,
                        Detail = log.ToString().TrimEnd()
                    };
                }
                finally
                {
                    pass2Owned?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WinOCR] detect failed: {ex.Message}");
                return new DetectionResult
                {
                    Regions = new List<DetectedTextRegion>(),
                    LowConfidence = true,
                    LooksFragmented = true,
                    Detail = $"detect failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Crop black/dark and white/near-white letterbox bars on all four sides.
        /// Hard then soft combined pass (a rim is a bar if predominantly dark
        /// <b>or</b> predominantly light). Returns a new bitmap (caller disposes).
        /// If no meaningful bars, returns a full clone.
        /// <paramref name="contentRect"/> is the kept region in <paramref name="source"/> coords.
        /// </summary>
        private static Bitmap CropToContentOrClone(
            Bitmap source,
            out Rectangle contentRect,
            StringBuilder? detail = null)
        {
            contentRect = new Rectangle(0, 0, source.Width, source.Height);

            if (!EnableLetterbox)
            {
                detail?.AppendLine(
                    $"letterbox off — full frame {source.Width}x{source.Height}");
                return (Bitmap)source.Clone();
            }

            if (!TryCropLetterboxPasses(source, out var kept, out var cropped))
                return (Bitmap)source.Clone();

            contentRect = kept;
            detail?.AppendLine(
                $"content-crop {kept.Width}x{kept.Height} " +
                $"@({kept.X},{kept.Y}) from {source.Width}x{source.Height}");
            return cropped;
        }

        /// <summary>
        /// Hard then soft combined dark+light content-bounds crop. On success
        /// <paramref name="cropped"/> is a new bitmap (caller disposes) and
        /// <paramref name="contentInSource"/> is in <paramref name="source"/> coords.
        /// </summary>
        private static bool TryCropLetterboxPasses(
            Bitmap source,
            out Rectangle contentInSource,
            out Bitmap cropped)
        {
            contentInSource = new Rectangle(0, 0, source.Width, source.Height);
            cropped = null!;

            if (!TryFindContentBounds(
                    source, out var hard,
                    LetterboxBlackThreshold, LetterboxWhiteThreshold,
                    LetterboxMinContentFraction) ||
                !ContentBoundsIsMeaningful(source, hard))
            {
                return false;
            }

            Bitmap? working = CropBitmap(source, hard);
            if (working == null)
                return false;

            Rectangle kept = hard;

            if (TryFindContentBounds(
                    working, out var softLocal,
                    LetterboxSoftBlackThreshold, LetterboxSoftWhiteThreshold,
                    LetterboxSoftMinContentFraction) &&
                ContentBoundsIsMeaningful(working, softLocal))
            {
                var softCrop = CropBitmap(working, softLocal);
                if (softCrop != null)
                {
                    kept = new Rectangle(
                        hard.X + softLocal.X,
                        hard.Y + softLocal.Y,
                        softLocal.Width,
                        softLocal.Height);
                    working.Dispose();
                    working = softCrop;
                }
            }

            contentInSource = kept;
            cropped = working;
            return true;
        }

        private static bool ContentBoundsIsMeaningful(Bitmap source, Rectangle content)
        {
            if (content.Width < 8 || content.Height < 8)
                return false;
            if (content.X <= 0 && content.Y <= 0 &&
                content.Width >= source.Width && content.Height >= source.Height)
                return false;

            long full = (long)source.Width * source.Height;
            long c = (long)content.Width * content.Height;
            int topBar = content.Y;
            int botBar = source.Height - content.Bottom;
            int leftBar = content.X;
            int rightBar = source.Width - content.Right;
            int maxBar = Math.Max(Math.Max(topBar, botBar), Math.Max(leftBar, rightBar));
            // Accept thin bars (a few px) - old min of ~12x48 left side pillars untrimmed
            int minBar = Math.Max(3, Math.Min(source.Width, source.Height) / 250);

            if (maxBar >= minBar)
                return true;
            // Even a small area save is worth it (was 8% - missed skinny pillars)
            if (c <= full * 0.985)
                return true;

            return false;
        }

        /// <summary>
        /// When WinOCR finds no boxes: content-crop + high-scale re-detect, then
        /// bright-blob proposals (speech balloons / white UI panels) so we still
        /// send tight crops to Kobold instead of the whole panel.
        /// </summary>
        private static async Task<List<DetectedTextRegion>> ZeroRegionRescueAsync(
            OcrEngine engine,
            Bitmap capture,
            CancellationToken token,
            StringBuilder log)
        {
            var best = new List<DetectedTextRegion>();
            Rectangle contentRect = new Rectangle(0, 0, capture.Width, capture.Height);
            Bitmap? contentOwned = null;
            Bitmap work = capture;

            try
            {
                if (TryFindContentBounds(capture, out var found) &&
                    (found.Width < capture.Width || found.Height < capture.Height))
                {
                    contentRect = found;
                    contentOwned = CropBitmap(capture, contentRect);
                    if (contentOwned != null)
                    {
                        work = contentOwned;
                        log.AppendLine(
                            $"  rescue content-crop {work.Width}x{work.Height} " +
                            $"@({contentRect.X},{contentRect.Y})");
                    }
                }

                // Harder WinOCR on content only (higher scale, contrast on/off)
                foreach (var (scale, contrast, label) in new (double, bool, string)[]
                         {
                             (2.5, true, "content 2.5+c"),
                             (3.0, true, "content 3.0+c"),
                             (2.5, false, "content 2.5"),
                             (3.5, true, "content 3.5+c"),
                         })
                {
                    token.ThrowIfCancellationRequested();
                    var pass = await RunWinOcrPassAsync(engine, work, scale, token, log);
                    // RunWinOcrPassAsync maps to work's coords; offset if cropped
                    var mapped = contentOwned != null
                        ? OffsetRegions(pass, contentRect.X, contentRect.Y,
                            capture.Width, capture.Height)
                        : pass;
                    log.AppendLine($"  rescue [{label}]: regions={mapped.Count}");
                    if (ScoreDetection(mapped) > ScoreDetection(best))
                        best = mapped;
                    if (best.Count >= 2)
                        break;
                }

                if (best.Count > 0)
                    return SortComicReadingOrderRegions(best);

                // Bright-blob proposals: white speech balloons / dialog panels
                var blobs = ProposeBrightBlobRegions(work, maxRegions: MaxTextRegions);
                log.AppendLine($"  rescue bright-blobs: {blobs.Count}");
                if (blobs.Count == 0)
                    return best;

                var blobMapped = contentOwned != null
                    ? OffsetRegions(blobs, contentRect.X, contentRect.Y,
                        capture.Width, capture.Height)
                    : blobs;

                // Optional: re-run WinOCR only inside each blob (tight detect)
                var refined = new List<DetectedTextRegion>();
                foreach (var blob in blobMapped)
                {
                    token.ThrowIfCancellationRequested();
                    var cropRect = Rectangle.Inflate(blob.Bounds, 6, 6);
                    cropRect.Intersect(new Rectangle(0, 0, capture.Width, capture.Height));
                    using var crop = CropBitmap(capture, cropRect);
                    if (crop == null)
                    {
                        refined.Add(blob);
                        continue;
                    }

                    var inner = await RunWinOcrPassAsync(engine, crop, 2.5, token, log);
                    if (inner.Count > 0)
                    {
                        foreach (var r in inner)
                        {
                            var b = r.Bounds;
                            b.Offset(cropRect.X, cropRect.Y);
                            b.Intersect(new Rectangle(0, 0, capture.Width, capture.Height));
                            if (b.Width >= BalloonOcrDetect.MinClusterSize && b.Height >= BalloonOcrDetect.MinClusterSize)
                            {
                                refined.Add(new DetectedTextRegion
                                {
                                    Bounds = b,
                                    WinOcrText = r.WinOcrText
                                });
                            }
                        }
                    }
                    else
                    {
                        // Keep geometric proposal even if WinOCR still blind
                        refined.Add(blob);
                    }
                }

                if (refined.Count > 0)
                {
                    // Coalesce scraps inside each balloon, keep separate islands
                    refined = CoalesceIntoReadingBlocks(
                        refined, capture.Width, capture.Height, aggressive: true);
                    log.AppendLine($"  rescue refined boxes: {refined.Count}");
                    return SortComicReadingOrderRegions(refined);
                }

                return SortComicReadingOrderRegions(blobMapped);
            }
            finally
            {
                contentOwned?.Dispose();
            }
        }

        /// <summary>
        /// When WinOCR returned some islands but bright balloons exist with no
        /// matching box, re-detect inside each orphan (or keep geometry for Kobold
        /// only when WinOCR budget is exhausted without a try).
        /// Fixes partial misses like a left balloon skipped on a wide panel.
        /// Rejects pale-hair / sky / face false blobs: ink check first; if a
        /// tight WinOCR pass runs and finds no letters, do <b>not</b> keep empty
        /// geometry (faces look balloon-sized but OCR-empty).
        /// </summary>
        // Orphan recover removed from Balloons — method kept only if any debug path
        // still references the name; always returns existing islands.
        private static async Task<List<DetectedTextRegion>> FillOrphanBalloonBlobsAsync(
            OcrEngine engine,
            Bitmap capture,
            List<DetectedTextRegion> existing,
            CancellationToken token,
            StringBuilder log)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            _ = (engine, capture, token, log);
            return existing;
        }

#if false // orphan recover removed
        private static async Task<List<DetectedTextRegion>> FillOrphanBalloonBlobsAsync_Removed(
            OcrEngine engine,
            Bitmap capture,
            List<DetectedTextRegion> existing,
            CancellationToken token,
            StringBuilder log)
        {
            var blobs = ProposeBrightBlobRegions(capture, maxRegions: MaxTextRegions);
            if (blobs.Count == 0)
            {
                log.AppendLine("  partial-miss: no bright blobs");
                return existing;
            }

            var orphans = blobs
                .Where(b => !AnyRegionOverlapsBlob(existing, b.Bounds))
                .ToList();

            log.AppendLine(
                $"  partial-miss: blobs={blobs.Count} orphans={orphans.Count} " +
                $"(ocrIslands={existing.Count})");

            if (orphans.Count == 0)
                return existing;

            // Largest balloons first - only the top N get a WinOCR re-detect
            orphans = orphans
                .OrderByDescending(o => o.Bounds.Width * (long)o.Bounds.Height)
                .ToList();

            var merged = existing.ToList();
            int added = 0;
            int rejected = 0;
            int ocrBudget = ActiveMaxOrphanWinOcrPasses;

            foreach (var orphan in orphans)
            {
                token.ThrowIfCancellationRequested();
                if (merged.Count >= MaxTextRegions)
                    break;

                // Ink check before expensive crop OCR - drops white hair / flat sky
                if (!LooksLikeSpeechBalloonFill(capture, orphan.Bounds))
                {
                    rejected++;
                    log.AppendLine(
                        $"  orphan reject-ink @{orphan.Bounds.X},{orphan.Bounds.Y} " +
                        $"{orphan.Bounds.Width}x{orphan.Bounds.Height}");
                    continue;
                }

                long frameA = (long)capture.Width * capture.Height;
                long area = (long)orphan.Bounds.Width * orphan.Bounds.Height;
                bool balloonSized =
                    orphan.Bounds.Width >= 80 &&
                    orphan.Bounds.Height >= 60 &&
                    area >= 8000 &&
                    area <= frameA * 0.08 &&
                    orphan.Bounds.Width <= capture.Width * 0.42 &&
                    orphan.Bounds.Height <= capture.Height * 0.42;

                var cropRect = Rectangle.Inflate(orphan.Bounds, 8, 8);
                cropRect.Intersect(new Rectangle(0, 0, capture.Width, capture.Height));
                if (cropRect.Width < BalloonOcrDetect.MinClusterSize || cropRect.Height < BalloonOcrDetect.MinClusterSize)
                    continue;

                // Budgeted tight WinOCR (largest orphans only)
                if (ocrBudget > 0)
                {
                    using var crop = CropBitmap(capture, cropRect);
                    if (crop == null)
                        continue;

                    ocrBudget--;
                    var inner = await RunWinOcrPassAsync(
                        engine, crop, OrphanWinOcrScale, token, log);
                    if (inner.Count > 0)
                    {
                        Rectangle? union = null;
                        var texts = new List<string>();
                        foreach (var r in inner)
                        {
                            var b = r.Bounds;
                            b.Offset(cropRect.X, cropRect.Y);
                            b.Intersect(new Rectangle(0, 0, capture.Width, capture.Height));
                            if (b.Width < BalloonOcrDetect.MinClusterSize || b.Height < BalloonOcrDetect.MinClusterSize)
                                continue;
                            union = union == null ? b : Rectangle.Union(union.Value, b);
                            if (!string.IsNullOrWhiteSpace(r.WinOcrText))
                                texts.Add(r.WinOcrText.Trim());
                        }

                        if (union != null && !AnyRegionOverlapsBlob(merged, union.Value))
                        {
                            string joined = Regex.Replace(
                                string.Join(" ", texts), @"\s+", " ").Trim();
                            int oAlnum = SpeechCleaner.CountAlnum(joined);
                            int oWords = ComicRegionGeometry.CountWords(joined);
                            // Weak single-token OCR (e.g. "dog" on art) is not a balloon
                            if (oAlnum < MinIslandAlnumChars || oWords < 2)
                            {
                                rejected++;
                                log.AppendLine(
                                    $"  orphan reject-weak-ocr @{union.Value.X},{union.Value.Y} " +
                                    $"{union.Value.Width}x{union.Value.Height} " +
                                    $"alnum={oAlnum} words={oWords} \"{Truncate(joined, 24)}\"");
                                continue;
                            }
                            merged.Add(new DetectedTextRegion
                            {
                                Bounds = union.Value,
                                WinOcrText = joined
                            });
                            added++;
                            log.AppendLine(
                                $"  orphan ocr @{union.Value.X},{union.Value.Y} " +
                                $"{union.Value.Width}x{union.Value.Height} " +
                                $"alnum={oAlnum}");
                            continue;
                        }
                    }

                    // OCR was attempted and found nothing usable - drop the blob.
                    // Faces / sky / hair often pass size + ink but have no letters;
                    // keeping empty geometry only wastes crop-Kobold time.
                    rejected++;
                    log.AppendLine(
                        $"  orphan reject-empty-ocr @{orphan.Bounds.X},{orphan.Bounds.Y} " +
                        $"{orphan.Bounds.Width}x{orphan.Bounds.Height}");
                    continue;
                }

                // Geometry-only only when WinOCR budget is exhausted (never tried).
                // Accuracy-first safety net for remaining balloon-sized plates.
                if (balloonSized)
                {
                    if (AnyRegionOverlapsBlob(merged, orphan.Bounds))
                    {
                        rejected++;
                        continue;
                    }
                    log.AppendLine(
                        $"  orphan keep-geometry @{orphan.Bounds.X},{orphan.Bounds.Y} " +
                        $"{orphan.Bounds.Width}x{orphan.Bounds.Height} (ocr-budget)");
                    merged.Add(new DetectedTextRegion
                    {
                        Bounds = orphan.Bounds,
                        WinOcrText = ""
                    });
                    added++;
                }
                else
                {
                    rejected++;
                    log.AppendLine(
                        $"  orphan reject-no-ocr @{orphan.Bounds.X},{orphan.Bounds.Y} " +
                        $"{orphan.Bounds.Width}x{orphan.Bounds.Height} (size)");
                }
            }

            if (rejected > 0)
                log.AppendLine($"  partial-miss rejected {rejected} non-balloon blob(s)");

            if (added == 0)
                return existing;

            log.AppendLine($"  partial-miss added {added} island(s)");
            return SortComicReadingOrderRegions(merged);
        }
#endif

        /// <summary>
        /// True when an existing OCR island substantially overlaps a bright-blob
        /// proposal (blob is already "claimed").
        /// </summary>
        private static bool AnyRegionOverlapsBlob(
            List<DetectedTextRegion> regions, Rectangle blob)
        {
            if (regions.Count == 0)
                return false;

            double blobArea = Math.Max(1.0, blob.Width * (double)blob.Height);
            foreach (var r in regions)
            {
                var inter = Rectangle.Intersect(r.Bounds, blob);
                if (inter.Width <= 0 || inter.Height <= 0)
                    continue;

                double interA = inter.Width * (double)inter.Height;
                double regA = Math.Max(1.0, r.Bounds.Width * (double)r.Bounds.Height);

                // Blob mostly covered by region, or region mostly inside blob
                if (interA / blobArea >= 0.22 || interA / regA >= 0.45)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Find bright connected components (speech balloons, dialog panels) by
        /// luminance only - used by orphan balloon fill / zero-region rescue.
        /// Filtered by fill + dark-ink density.
        /// </summary>
        private static List<DetectedTextRegion> ProposeBrightBlobRegions(
            Bitmap source, int maxRegions)
        {
            if (source.Width < 16 || source.Height < 16)
                return new List<DetectedTextRegion>();

            const int maxSide = 480;
            double ds = 1.0;
            int dw = source.Width;
            int dh = source.Height;
            if (Math.Max(dw, dh) > maxSide)
            {
                ds = (double)maxSide / Math.Max(dw, dh);
                dw = Math.Max(1, (int)Math.Round(source.Width * ds));
                dh = Math.Max(1, (int)Math.Round(source.Height * ds));
            }

            Bitmap? smallOwned = null;
            Bitmap work = source;
            try
            {
                if (ds < 1.0)
                {
                    smallOwned = ScaleBitmapNearestNeighbor(source, dw, dh);
                    work = smallOwned;
                }

                Bitmap src32 = Ensure32bppArgb(work);
                bool dispose32 = !ReferenceEquals(src32, work);
                try
                {
                    int w = src32.Width;
                    int h = src32.Height;
                    long frameArea = (long)w * h;
                    const int plateLum = 155;

                    var mask = new bool[w * h];
                    var data = src32.LockBits(
                        new Rectangle(0, 0, w, h),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format32bppArgb);
                    try
                    {
                        unsafe
                        {
                            byte* p0 = (byte*)data.Scan0;
                            int stride = data.Stride;
                            for (int y = 0; y < h; y++)
                            {
                                byte* row = p0 + y * stride;
                                for (int x = 0; x < w; x++)
                                {
                                    byte* px = row + x * 4;
                                    int yLum = (px[2] * 30 + px[1] * 59 + px[0] * 11) / 100;
                                    if (yLum >= plateLum)
                                        mask[y * w + x] = true;
                                }
                            }
                        }
                    }
                    finally
                    {
                        src32.UnlockBits(data);
                    }

                    var labels = new int[w * h];
                    var comps = new List<(int MinX, int MinY, int MaxX, int MaxY, int Count)>();
                    int nextLabel = 1;
                    var stack = new Stack<int>();

                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int i = y * w + x;
                            if (!mask[i] || labels[i] != 0)
                                continue;

                            int minX = x, maxX = x, minY = y, maxY = y, count = 0;
                            labels[i] = nextLabel;
                            stack.Push(i);

                            while (stack.Count > 0)
                            {
                                int cur = stack.Pop();
                                count++;
                                int cx = cur % w;
                                int cy = cur / w;
                                if (cx < minX) minX = cx;
                                if (cx > maxX) maxX = cx;
                                if (cy < minY) minY = cy;
                                if (cy > maxY) maxY = cy;

                                void Try(int nx, int ny)
                                {
                                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) return;
                                    int ni = ny * w + nx;
                                    if (!mask[ni] || labels[ni] != 0) return;
                                    labels[ni] = nextLabel;
                                    stack.Push(ni);
                                }
                                Try(cx - 1, cy);
                                Try(cx + 1, cy);
                                Try(cx, cy - 1);
                                Try(cx, cy + 1);
                            }

                            comps.Add((minX, minY, maxX, maxY, count));
                            nextLabel++;
                        }
                    }

                    double invDs = ds < 1.0 ? 1.0 / ds : 1.0;
                    long minArea = Math.Max(80, frameArea / 400);
                    long maxArea = frameArea / 3;
                    int minW = Math.Max(12, w / 40);
                    int minH = Math.Max(10, h / 50);

                    var boxes = new List<DetectedTextRegion>();
                    foreach (var c in comps)
                    {
                        if (c.Count < minArea || c.Count > maxArea)
                            continue;
                        int bw = c.MaxX - c.MinX + 1;
                        int bh = c.MaxY - c.MinY + 1;
                        if (bw < minW || bh < minH)
                            continue;
                        if (bw > bh * 12 || bh > bw * 8)
                            continue;
                        double fill = (double)c.Count / (bw * bh);
                        if (fill < 0.22)
                            continue;

                        int x0 = (int)Math.Floor(c.MinX * invDs);
                        int y0 = (int)Math.Floor(c.MinY * invDs);
                        int x1 = (int)Math.Ceiling((c.MaxX + 1) * invDs);
                        int y1 = (int)Math.Ceiling((c.MaxY + 1) * invDs);
                        int pad = Math.Max(6, (int)(8 * invDs));
                        var box = Rectangle.FromLTRB(
                            Math.Max(0, x0 - pad),
                            Math.Max(0, y0 - pad),
                            Math.Min(source.Width, x1 + pad),
                            Math.Min(source.Height, y1 + pad));
                        if (box.Width < BalloonOcrDetect.MinClusterSize || box.Height < BalloonOcrDetect.MinClusterSize)
                            continue;

                        if (!LooksLikeSpeechBalloonFill(source, box))
                            continue;

                        boxes.Add(new DetectedTextRegion
                        {
                            Bounds = box,
                            WinOcrText = ""
                        });
                    }

                    boxes = MergeOverlappingBoxes(boxes, source.Width, source.Height, 0.35);
                    if (boxes.Count > maxRegions)
                    {
                        boxes = boxes
                            .OrderByDescending(b => b.Bounds.Width * b.Bounds.Height)
                            .Take(maxRegions)
                            .ToList();
                    }

                    return SortComicReadingOrderRegions(boxes);
                }
                finally
                {
                    if (dispose32)
                        src32.Dispose();
                }
            }
            finally
            {
                smallOwned?.Dispose();
            }
        }

        /// <summary>
        /// Speech balloon / dialog plate: light fill (white or mid-gray after
        /// desat) + dark ink. Rejects flat sky and hair without lettering.
        /// </summary>
        private static bool LooksLikeSpeechBalloonFill(Bitmap source, Rectangle bounds)
        {
            var s = SampleBrightBlobStats(source, bounds);
            if (s.Sampled < 48)
                return false;

            // White plate OR light-gray plate (pink/cream balloons after gray)
            double lightFrac = s.BrightFrac + s.PlateFrac;
            if (lightFrac < 0.38)
                return false;
            if (s.BrightFrac < 0.12 && s.PlateFrac < 0.28)
                return false;

            // Lettering: enough dark ink, but not a solid black mass
            if (s.DarkInkFrac < 0.014 || s.DarkInkFrac > 0.55)
                return false;

            // Faces / hair / busy art: mid-tones dominate without a real plate
            if (s.MidFrac > 0.55 && lightFrac < 0.42)
                return false;

            return true;
        }

        private readonly struct BrightBlobStats
        {
            public int Sampled { get; init; }
            /// <summary>Near-white fill (classic balloons).</summary>
            public double BrightFrac { get; init; }
            /// <summary>Light-gray fill (desaturated pink/cream balloons).</summary>
            public double PlateFrac { get; init; }
            public double DarkInkFrac { get; init; }
            public double MidFrac { get; init; }
        }

        /// <summary>
        /// Sparse sample of fill / ink (luminance) inside a candidate balloon box.
        /// </summary>
        private static BrightBlobStats SampleBrightBlobStats(Bitmap source, Rectangle bounds)
        {
            bounds.Intersect(new Rectangle(0, 0, source.Width, source.Height));
            if (bounds.Width < 4 || bounds.Height < 4)
                return default;

            Bitmap src32 = Ensure32bppArgb(source);
            bool dispose32 = !ReferenceEquals(src32, source);
            try
            {
                // Shrink slightly to ignore outline stroke / neighbor bleed
                int insetX = Math.Max(1, bounds.Width / 18);
                int insetY = Math.Max(1, bounds.Height / 18);
                int x0 = bounds.X + insetX;
                int y0 = bounds.Y + insetY;
                int x1 = bounds.Right - insetX;
                int y1 = bounds.Bottom - insetY;
                if (x1 - x0 < 3 || y1 - y0 < 3)
                {
                    x0 = bounds.X;
                    y0 = bounds.Y;
                    x1 = bounds.Right;
                    y1 = bounds.Bottom;
                }

                int stepX = Math.Max(1, (x1 - x0) / 28);
                int stepY = Math.Max(1, (y1 - y0) / 28);

                var data = src32.LockBits(
                    new Rectangle(0, 0, src32.Width, src32.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* p0 = (byte*)data.Scan0;
                        int stride = data.Stride;
                        int sampled = 0, bright = 0, plate = 0, dark = 0, mid = 0;

                        for (int y = y0; y < y1; y += stepY)
                        {
                            byte* row = p0 + y * stride;
                            for (int x = x0; x < x1; x += stepX)
                            {
                                byte* p = row + x * 4;
                                int yLum = (p[2] * 30 + p[1] * 59 + p[0] * 11) / 100;
                                sampled++;

                                if (yLum >= 200)
                                    bright++;
                                else if (yLum >= 150)
                                    plate++; // light gray plate
                                else if (yLum <= 95)
                                    dark++;
                                else
                                    mid++;
                            }
                        }

                        if (sampled == 0)
                            return default;

                        return new BrightBlobStats
                        {
                            Sampled = sampled,
                            BrightFrac = bright / (double)sampled,
                            PlateFrac = plate / (double)sampled,
                            DarkInkFrac = dark / (double)sampled,
                            MidFrac = mid / (double)sampled
                        };
                    }
                }
                finally
                {
                    src32.UnlockBits(data);
                }
            }
            finally
            {
                if (dispose32)
                    src32.Dispose();
            }
        }

        private static List<DetectedTextRegion> MergeOverlappingBoxes(
            List<DetectedTextRegion> regions,
            int capW,
            int capH,
            double iouOrContain)
        {
            if (regions.Count <= 1)
                return regions;

            var list = regions.ToList();
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < list.Count && !changed; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var a = list[i].Bounds;
                        var b = list[j].Bounds;
                        var inter = Rectangle.Intersect(a, b);
                        if (inter.Width <= 0 || inter.Height <= 0)
                            continue;
                        double ia = inter.Width * (double)inter.Height;
                        double aa = a.Width * (double)a.Height;
                        double ba = b.Width * (double)b.Height;
                        if (ia >= Math.Min(aa, ba) * iouOrContain)
                        {
                            var u = Rectangle.Union(a, b);
                            u.Intersect(new Rectangle(0, 0, capW, capH));
                            list[i] = new DetectedTextRegion
                            {
                                Bounds = u,
                                WinOcrText = (list[i].WinOcrText + " " + list[j].WinOcrText).Trim()
                            };
                            list.RemoveAt(j);
                            changed = true;
                            break;
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Bounding box of real content for letterbox/pillarbox trim (hard thresholds).
        /// </summary>
        private static bool TryFindContentBounds(Bitmap bmp, out Rectangle content)
        {
            content = new Rectangle(0, 0, bmp.Width, bmp.Height);
            if (!TryFindContentBounds(
                    bmp, out var found,
                    LetterboxBlackThreshold, LetterboxWhiteThreshold,
                    LetterboxMinContentFraction) ||
                !ContentBoundsIsMeaningful(bmp, found))
            {
                return false;
            }

            content = found;
            return true;
        }

        /// <summary>
        /// Bounding box of real content for letterbox/pillarbox trim.
        /// Scans inward from all four edges. A row/col is a <b>bar</b> when it is
        /// predominantly dark (max ≤ dark thr) <b>or</b> predominantly light
        /// (min ≥ light thr). Content is everything that is neither — so black
        /// outer letterbox, white page margins, and sandwich layouts all trim.
        /// Mid-gray is not treated as a bar (avoids over-cropping art/panels).
        /// Black ink on white still counts as content (column has both dark-content
        /// bright paper and light-content ink).
        /// <para>
        /// Edges are refined iteratively with the scan restricted to the current
        /// opposite-axis band. A full-width row scan would treat
        /// <c>[black pillar][white top margin][black pillar]</c> as content (both
        /// signals present), so vertical white/black bars never trimmed while
        /// horizontal pillars still would. Band-restricted passes fix T/B and L/R.
        /// </para>
        /// </summary>
        private static bool TryFindContentBounds(
            Bitmap bmp,
            out Rectangle content,
            int darkThreshold,
            int lightThreshold,
            double minContentFraction)
        {
            content = new Rectangle(0, 0, bmp.Width, bmp.Height);
            if (bmp.Width < 8 || bmp.Height < 8)
                return false;

            Bitmap src32 = Ensure32bppArgb(bmp);
            bool disposeSrc = !ReferenceEquals(src32, bmp);
            try
            {
                var rect = new Rectangle(0, 0, src32.Width, src32.Height);
                var data = src32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* p0 = (byte*)data.Scan0;
                        int stride = data.Stride;
                        int w = src32.Width;
                        int h = src32.Height;
                        // Dark-content pixel: bright enough to not be a dark bar
                        // Light-content pixel: dark/colored enough to not be a white bar
                        int darkThr = Math.Clamp(darkThreshold, 1, 250);
                        int lightThr = Math.Clamp(lightThreshold, 1, 250);
                        double frac = Math.Clamp(minContentFraction, 0.001, 0.25);

                        // Bar if missing dark-content (mostly black) OR missing
                        // light-content (mostly white). Real art/text has both:
                        // bright paper/highlights and darker ink/color. Mid-gray
                        // satisfies both and is kept (not treated as a bar).
                        // Span is the opposite-axis band currently under test.
                        bool RowIsContent(int y, int x0, int x1)
                        {
                            if (x1 < x0) return false;
                            int need = Math.Max(3, (int)((x1 - x0 + 1) * frac));
                            byte* row = p0 + y * stride;
                            int darkN = 0, lightN = 0;
                            for (int x = x0; x <= x1; x++)
                            {
                                byte* p = row + x * 4;
                                byte b = p[0], g = p[1], r = p[2];
                                int mx = b > g ? b : g;
                                if (r > mx) mx = r;
                                int mn = b < g ? b : g;
                                if (r < mn) mn = r;
                                if (mx > darkThr) darkN++;
                                if (mn < lightThr) lightN++;
                                if (darkN >= need && lightN >= need)
                                    return true;
                            }
                            return darkN >= need && lightN >= need;
                        }

                        bool ColIsContent(int x, int y0, int y1)
                        {
                            if (y1 < y0) return false;
                            int need = Math.Max(3, (int)((y1 - y0 + 1) * frac));
                            int darkN = 0, lightN = 0;
                            for (int y = y0; y <= y1; y++)
                            {
                                byte* p = p0 + y * stride + x * 4;
                                byte b = p[0], g = p[1], r = p[2];
                                int mx = b > g ? b : g;
                                if (r > mx) mx = r;
                                int mn = b < g ? b : g;
                                if (r < mn) mn = r;
                                if (mx > darkThr) darkN++;
                                if (mn < lightThr) lightN++;
                                if (darkN >= need && lightN >= need)
                                    return true;
                            }
                            return darkN >= need && lightN >= need;
                        }

                        int left = 0, right = w - 1, top = 0, bottom = h - 1;

                        // Alternate axis order so either L/R-first or T/B-first
                        // sandwich layouts converge (black corners no longer block
                        // the other axis's white/black bars).
                        for (int pass = 0; pass < 4; pass++)
                        {
                            int prevL = left, prevR = right, prevT = top, prevB = bottom;
                            bool lrFirst = (pass % 2) == 1;

                            void TrimVertical()
                            {
                                while (top <= bottom && !RowIsContent(top, left, right))
                                    top++;
                                while (bottom > top && !RowIsContent(bottom, left, right))
                                    bottom--;
                            }

                            void TrimHorizontal()
                            {
                                while (left <= right && !ColIsContent(left, top, bottom))
                                    left++;
                                while (right > left && !ColIsContent(right, top, bottom))
                                    right--;
                            }

                            if (lrFirst)
                            {
                                TrimHorizontal();
                                TrimVertical();
                            }
                            else
                            {
                                TrimVertical();
                                TrimHorizontal();
                            }

                            if (top >= bottom || left >= right)
                                return false;

                            if (left == prevL && right == prevR &&
                                top == prevT && bottom == prevB)
                                break;
                        }

                        if (top >= bottom || left >= right)
                            return false;

                        int pad = LetterboxContentPad;
                        left = Math.Max(0, left - pad);
                        top = Math.Max(0, top - pad);
                        right = Math.Min(w - 1, right + pad);
                        bottom = Math.Min(h - 1, bottom + pad);

                        content = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
                        return content.Width >= 8 && content.Height >= 8;
                    }
                }
                finally
                {
                    src32.UnlockBits(data);
                }
            }
            finally
            {
                if (disposeSrc)
                    src32.Dispose();
            }
        }

        private static Bitmap? CropBitmap(Bitmap source, Rectangle bounds)
        {
            var r = bounds;
            r.Intersect(new Rectangle(0, 0, source.Width, source.Height));
            if (r.Width < 1 || r.Height < 1)
                return null;

            var crop = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(crop))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(
                    source,
                    new Rectangle(0, 0, r.Width, r.Height),
                    r,
                    GraphicsUnit.Pixel);
            }
            return crop;
        }

        private static List<DetectedTextRegion> OffsetRegions(
            List<DetectedTextRegion> regions,
            int dx,
            int dy,
            int capW,
            int capH)
        {
            var list = new List<DetectedTextRegion>(regions.Count);
            foreach (var r in regions)
            {
                var b = r.Bounds;
                b.Offset(dx, dy);
                b.Intersect(new Rectangle(0, 0, capW, capH));
                if (b.Width < 1 || b.Height < 1)
                    continue;
                list.Add(new DetectedTextRegion
                {
                    Bounds = b,
                    WinOcrText = r.WinOcrText
                });
            }
            return list;
        }

        /// <summary>
        /// Rank detection sets for coverage. Balloon-sized empty plates get a real
        /// bonus (Kobold can still read them). Huge art blobs still penalized.
        /// Scoring exists to <b>maximize found text</b>, not to minimize region count.
        /// </summary>
        private static int ScoreDetection(List<DetectedTextRegion> regions)
        {
            if (regions.Count == 0) return 0;
            int textScore = 0;
            int withText = 0;
            int emptyGood = 0;
            int emptyBad = 0;
            foreach (var r in regions)
            {
                int a = SpeechCleaner.CountAlnum(r.WinOcrText);
                textScore += a;
                if (a >= BalloonOcrDetect.MinWinOcrAlnumChars)
                    withText++;
                else
                {
                    int area = r.Bounds.Width * r.Bounds.Height;
                    if (area >= 8000 && area <= 120_000 &&
                        r.Bounds.Width >= 80 && r.Bounds.Height >= 60 &&
                        r.Bounds.Width <= 700 && r.Bounds.Height <= 700)
                        emptyGood++;
                    else
                        emptyBad++;
                }
            }
            // Accuracy: empty balloon plates help coverage more than they hurt
            return withText * 1000 + textScore + emptyGood * 400 - emptyBad * 300;
        }

        /// <summary>
        /// Accept a detection set if it has better text coverage OR more balloon
        /// islands to feed Kobold. Used for partial-miss fill.
        /// <b>Accuracy over speed:</b> more recoverable balloons beats a "cleaner"
        /// smaller set that omits a speech bubble.
        /// </summary>
        private static bool PreferDetectionForCoverage(
            List<DetectedTextRegion> candidate,
            List<DetectedTextRegion> current)
        {
            if (candidate == null || candidate.Count == 0)
                return false;
            if (current == null || current.Count == 0)
                return true;

            int cText = candidate.Count(r => SpeechCleaner.CountAlnum(r.WinOcrText) >= BalloonOcrDetect.MinWinOcrAlnumChars);
            int oText = current.Count(r => SpeechCleaner.CountAlnum(r.WinOcrText) >= BalloonOcrDetect.MinWinOcrAlnumChars);
            int cAlnum = candidate.Sum(r => SpeechCleaner.CountAlnum(r.WinOcrText));
            int oAlnum = current.Sum(r => SpeechCleaner.CountAlnum(r.WinOcrText));

            if (cText > oText) return true;
            if (cAlnum > oAlnum + 4) return true;
            if (cText >= oText && candidate.Count > current.Count)
                return true; // same text islands + extra balloon plates for crops
            if (ScoreDetection(candidate) > ScoreDetection(current))
                return true;

            return false;
        }

        private static bool IsLowConfidenceDetection(Bitmap capture, List<DetectedTextRegion> regions)
        {
            if (regions.Count == 0)
                return true;

            int area = capture.Width * capture.Height;
            bool large =
                capture.Width >= LowConfidenceMinCaptureWidth ||
                area >= LowConfidenceMinCaptureArea;

            if (large && regions.Count <= LowConfidenceMaxRegions)
                return true;

            // Very little total text on a big panel
            int alnum = regions.Sum(r => SpeechCleaner.CountAlnum(r.WinOcrText));
            if (large && alnum < 8)
                return true;

            return false;
        }

        /// <summary>
        /// Large comic panel with few islands - often a whole balloon was missed
        /// even though classic low-conf (=1 region) did not fire. Wide strips are
        /// the common case; tall multi-balloon panels also get a second pass.
        /// </summary>
        private static bool IsSparseWideDetection(Bitmap capture, List<DetectedTextRegion> regions)
        {
            if (regions.Count == 0)
                return true;
            if (regions.Count > WidePanelSparseMaxRegions)
                return false;

            int area = capture.Width * capture.Height;
            bool large =
                capture.Width >= LowConfidenceMinCaptureWidth ||
                area >= LowConfidenceMinCaptureArea;
            if (!large)
                return false;

            double aspect = capture.Width / (double)Math.Max(1, capture.Height);
            // Wide dual-bubble strips
            if (aspect >= WidePanelMinAspect)
                return true;
            // Tall / square pages with only 1x2 islands on a big capture still
            // often hide a third balloon ? harder redetect + orphan fill.
            if (regions.Count <= 1 && area >= LowConfidenceMinCaptureArea)
                return true;
            return false;
        }

        /// <summary>
        /// One detect pass at <paramref name="requestedScale"/> (capped by max side).
        /// Returns comic-ordered regions in <b>original capture</b> coordinates.
        /// Delegates to <see cref="BalloonOcrDetect.RunPassAsync"/>.
        /// </summary>
        private static async Task<List<DetectedTextRegion>> RunWinOcrPassAsync(
            OcrEngine engine,
            Bitmap capture,
            double requestedScale,
            CancellationToken token,
            StringBuilder log)
        {
            Action<Bitmap>? debug = null;
            if (ActiveWinOcrDetectDebugPng)
            {
                debug = bmp =>
                {
                    try
                    {
                        EnsureDebugFolder();
                        bmp.Save(
                            Path.Combine(DebugFolder, "last_winocr_detect.png"),
                            ImageFormat.Png);
                    }
                    catch { /* debug only */ }
                };
            }

            return await BalloonOcrDetect.RunPassAsync(
                engine,
                capture,
                requestedScale,
                BuildWinOcrDetectBitmapPair,
                token,
                log,
                debug).ConfigureAwait(false);
        }

        /// <summary>Adapter: <see cref="BuildWinOcrDetectBitmap"/> → tuple for detect pass.</summary>
        private static (Bitmap DetectBmp, double UsedScale) BuildWinOcrDetectBitmapPair(
            Bitmap source, double requestedScale)
        {
            var bmp = BuildWinOcrDetectBitmap(source, requestedScale, out double used);
            return (bmp, used);
        }

        /// <summary>
        /// Scale so the longest side equals <paramref name="targetLongSide"/>,
        /// preserving aspect ratio. Upscales smaller images and downscales larger
        /// ones (mode-specific long-edge targets).
        /// </summary>
        private static Bitmap ScaleMaintainAspectToLongSide(Bitmap source, int targetLongSide)
        {
            // Master prep off → identity (raw geometry after optional letterbox skip).
            if (!EnableImagePrep)
                return (Bitmap)source.Clone();

            targetLongSide = Math.Max(1, targetLongSide);
            int srcLong = Math.Max(source.Width, source.Height);
            if (srcLong == targetLongSide)
                return (Bitmap)source.Clone();

            double scale = (double)targetLongSide / srcLong;
            int tw = Math.Max(1, (int)Math.Round(source.Width * scale));
            int th = Math.Max(1, (int)Math.Round(source.Height * scale));

            // Upscale: progressive Lanczos for big jumps; downscale: bicubic is fine
            if (scale >= 1.6)
                return ScaleBitmapLanczosProgressive(source, tw, th);
            return ScaleBitmapBicubic(source, tw, th);
        }

        /// <summary>
        /// Pipeline tone after gray: edge-preserving denoise → percentile auto-levels
        /// → unsharp (last). ComicBook ON only. Always returns a new bitmap.
        /// <paramref name="skipDenoise"/> remains for callers/tests; production always
        /// passes false (full denoise).
        /// </summary>
        private static Bitmap ApplyPipelineTonePrep(Bitmap source, bool skipDenoise = false)
        {
            // Master prep off → no denoise / levels / sharpen.
            if (!EnableImagePrep)
                return (Bitmap)source.Clone();

            Bitmap working = (Bitmap)source.Clone();
            try
            {
                if (!skipDenoise && DenoiseSpatialRadius > 0)
                {
                    var den = EdgePreservingDenoise(
                        working, DenoiseSpatialRadius, DenoiseRangeSigma);
                    if (!ReferenceEquals(den, working))
                    {
                        working.Dispose();
                        working = den;
                    }
                }

                if (EnableAutoLevels)
                {
                    var levels = ApplyPercentileAutoLevels(
                        working, AutoLevelsLowPercentile, AutoLevelsHighPercentile);
                    if (!ReferenceEquals(levels, working))
                    {
                        working.Dispose();
                        working = levels;
                    }
                }

                int passes = PipelineSharpenPasses;
                float amount = PipelineSharpenAmount;
                if (amount > 0.001f && passes > 0)
                {
                    for (int p = 0; p < passes; p++)
                    {
                        var sharp = LightUnsharp(working, amount);
                        if (!ReferenceEquals(sharp, working))
                        {
                            working.Dispose();
                            working = sharp;
                        }
                    }
                }

                return working;
            }
            catch
            {
                working.Dispose();
                throw;
            }
        }

        /// <summary>
        /// WinOCR detect bitmap from the already-prepped pipeline image.
        /// Tone/upscale/gray are done upstream; this only applies optional extra
        /// scale for hard retries (aspect preserved, <b>no max-side cap</b>).
        /// <paramref name="usedScale"/> maps detect coords ? pipeline coords.
        /// </summary>
        private static Bitmap BuildWinOcrDetectBitmap(
            Bitmap source,
            double requestedScale,
            out double usedScale)
        {
            requestedScale = Math.Max(1.0, requestedScale);
            if (requestedScale <= 1.001)
            {
                usedScale = 1.0;
                return (Bitmap)source.Clone();
            }

            int tw = Math.Max(1, (int)Math.Round(source.Width * requestedScale));
            int th = Math.Max(1, (int)Math.Round(source.Height * requestedScale));
            usedScale = (double)tw / Math.Max(1, source.Width);

            if (requestedScale >= 1.6)
                return ScaleBitmapLanczosProgressive(source, tw, th);
            return ScaleBitmapBicubic(source, tw, th);
        }

        /// <summary>
        /// Ink-preserving grayscale for comics: blend min(R,G,B) with Rec.601.
        /// Yellow/red/blue SFX and captions stay dark (min channel), while natural
        /// black lettering tracks luminance. Returns 32bpp ARGB (R=G=B=ink).
        /// </summary>
        private static Bitmap ConvertToInkGrayscale(Bitmap source)
        {
            float wMin = Math.Clamp(InkGrayMinWeight, 0f, 1f);
            float wLum = 1f - wMin;

            Bitmap src32 = Ensure32bppArgb(source);
            bool disposeSrc = !ReferenceEquals(src32, source);
            try
            {
                var result = new Bitmap(src32.Width, src32.Height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, src32.Width, src32.Height);
                var srcData = src32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* s0 = (byte*)srcData.Scan0;
                        byte* d0 = (byte*)dstData.Scan0;
                        int sStride = srcData.Stride;
                        int dStride = dstData.Stride;
                        int w = src32.Width;
                        int h = src32.Height;

                        for (int y = 0; y < h; y++)
                        {
                            byte* s = s0 + y * sStride;
                            byte* d = d0 + y * dStride;
                            for (int x = 0; x < w; x++)
                            {
                                int i = x * 4;
                                int b = s[i], g = s[i + 1], r = s[i + 2];
                                int minC = b < g ? (b < r ? b : r) : (g < r ? g : r);
                                // Rec.601 luminance
                                int yLum = (r * 30 + g * 59 + b * 11) / 100;
                                byte ink = ClampByte(minC * wMin + yLum * wLum);
                                d[i] = ink;
                                d[i + 1] = ink;
                                d[i + 2] = ink;
                                d[i + 3] = s[i + 3];
                            }
                        }
                    }
                }
                finally
                {
                    src32.UnlockBits(srcData);
                    result.UnlockBits(dstData);
                }

                return result;
            }
            finally
            {
                if (disposeSrc)
                    src32.Dispose();
            }
        }

        /// <summary>
        /// True when a sample of pixels is nearly R=G=B (pipeline already gray).
        /// </summary>
        private static bool IsEffectivelyGrayscale(Bitmap source)
        {
            Bitmap src32 = Ensure32bppArgb(source);
            bool disposeSrc = !ReferenceEquals(src32, source);
            try
            {
                var rect = new Rectangle(0, 0, src32.Width, src32.Height);
                var data = src32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* s0 = (byte*)data.Scan0;
                        int stride = data.Stride;
                        int w = src32.Width;
                        int h = src32.Height;
                        int stepX = Math.Max(1, w / 32);
                        int stepY = Math.Max(1, h / 32);
                        int checkedPx = 0;
                        int colorish = 0;
                        for (int y = 0; y < h; y += stepY)
                        {
                            byte* row = s0 + y * stride;
                            for (int x = 0; x < w; x += stepX)
                            {
                                int i = x * 4;
                                int b = row[i], g = row[i + 1], r = row[i + 2];
                                int maxC = Math.Max(b, Math.Max(g, r));
                                int minC = Math.Min(b, Math.Min(g, r));
                                if (maxC - minC > 8)
                                    colorish++;
                                checkedPx++;
                            }
                        }
                        // Mostly gray if fewer than ~2% of samples look colored.
                        return checkedPx == 0 || colorish * 50 < checkedPx;
                    }
                }
                finally
                {
                    src32.UnlockBits(data);
                }
            }
            finally
            {
                if (disposeSrc)
                    src32.Dispose();
            }
        }

        /// <summary>
        /// Fast bilateral-style denoise. Grayscale images keep the cheap R=G=B path.
        /// Color images denoise luminance and re-scale RGB so prep can stay in color
        /// when Image grayscale is off.
        /// </summary>
        private static Bitmap EdgePreservingDenoise(
            Bitmap source, int spatialRadius, float rangeSigma)
        {
            // Match Settings → Image denoise Radius track (0–4) and Range σ (1–80).
            // Older clamp (0–3 / 4–80) made radius=4 and sigma below 4 look identical.
            spatialRadius = Math.Clamp(spatialRadius, 0, 4);
            if (spatialRadius == 0 || source.Width < 3 || source.Height < 3)
                return (Bitmap)source.Clone();

            rangeSigma = Math.Clamp(rangeSigma, 1f, 80f);
            float twoSigmaSq = 2f * rangeSigma * rangeSigma;
            bool grayIn = IsEffectivelyGrayscale(source);

            Bitmap src32 = Ensure32bppArgb(source);
            bool disposeSrc = !ReferenceEquals(src32, source);
            try
            {
                var result = new Bitmap(src32.Width, src32.Height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, src32.Width, src32.Height);
                var srcData = src32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* s0 = (byte*)srcData.Scan0;
                        byte* d0 = (byte*)dstData.Scan0;
                        int sStride = srcData.Stride;
                        int dStride = dstData.Stride;
                        int w = src32.Width;
                        int h = src32.Height;
                        int r = spatialRadius;

                        for (int y = 0; y < h; y++)
                        {
                            byte* dRow = d0 + y * dStride;
                            for (int x = 0; x < w; x++)
                            {
                                byte* sp = s0 + y * sStride + x * 4;
                                int b0 = sp[0], g0 = sp[1], r0 = sp[2];
                                // Rec.601 luminance (or B when already gray)
                                int center = grayIn
                                    ? b0
                                    : (r0 * 30 + g0 * 59 + b0 * 11) / 100;
                                float sum = 0f;
                                float wsum = 0f;

                                for (int dy = -r; dy <= r; dy++)
                                {
                                    int yy = y + dy;
                                    if ((uint)yy >= (uint)h) continue;
                                    byte* row = s0 + yy * sStride;
                                    for (int dx = -r; dx <= r; dx++)
                                    {
                                        int xx = x + dx;
                                        if ((uint)xx >= (uint)w) continue;
                                        byte* p = row + xx * 4;
                                        int v = grayIn
                                            ? p[0]
                                            : (p[2] * 30 + p[1] * 59 + p[0] * 11) / 100;
                                        float diff = v - center;
                                        float weight = MathF.Exp(-(diff * diff) / twoSigmaSq);
                                        // Mild spatial falloff (Manhattan)
                                        int manh = (dy < 0 ? -dy : dy) + (dx < 0 ? -dx : dx);
                                        if (manh > 0)
                                            weight *= 1f / (1f + 0.35f * manh);
                                        sum += v * weight;
                                        wsum += weight;
                                    }
                                }

                                float denY = wsum > 1e-6f ? sum / wsum : center;
                                int di = x * 4;
                                if (grayIn)
                                {
                                    byte outV = ClampByte(denY);
                                    dRow[di] = outV;
                                    dRow[di + 1] = outV;
                                    dRow[di + 2] = outV;
                                }
                                else
                                {
                                    // Scale RGB by luminance change to preserve chroma.
                                    float scale = center > 1e-3f ? denY / center : 1f;
                                    dRow[di] = ClampByte(b0 * scale);
                                    dRow[di + 1] = ClampByte(g0 * scale);
                                    dRow[di + 2] = ClampByte(r0 * scale);
                                }
                                dRow[di + 3] = sp[3];
                            }
                        }
                    }
                }
                finally
                {
                    src32.UnlockBits(srcData);
                    result.UnlockBits(dstData);
                }

                return result;
            }
            finally
            {
                if (disposeSrc)
                    src32.Dispose();
            }
        }

        /// <summary>
        /// Percentile auto-levels. Stretches [lo%, hi%] toward 0..255.
        /// Grayscale: single-channel stretch (R=G=B). Color: histogram on luminance,
        /// same stretch applied to R,G,B so chroma is preserved when ink-gray is off.
        /// </summary>
        private static Bitmap ApplyPercentileAutoLevels(
            Bitmap source, double lowPercentile, double highPercentile)
        {
            lowPercentile = Math.Clamp(lowPercentile, 0.0, 20.0);
            highPercentile = Math.Clamp(highPercentile, 80.0, 100.0);
            if (highPercentile <= lowPercentile + 0.5)
                return (Bitmap)source.Clone();

            bool grayIn = IsEffectivelyGrayscale(source);

            Bitmap src32 = Ensure32bppArgb(source);
            bool disposeSrc = !ReferenceEquals(src32, source);
            try
            {
                var rect = new Rectangle(0, 0, src32.Width, src32.Height);
                int[] hist = new int[256];
                long total = 0;

                var histData = src32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* s0 = (byte*)histData.Scan0;
                        int stride = histData.Stride;
                        int w = src32.Width;
                        int h = src32.Height;
                        for (int y = 0; y < h; y++)
                        {
                            byte* row = s0 + y * stride;
                            for (int x = 0; x < w; x++)
                            {
                                int i = x * 4;
                                int v = grayIn
                                    ? row[i]
                                    : (row[i + 2] * 30 + row[i + 1] * 59 + row[i] * 11) / 100;
                                hist[v]++;
                                total++;
                            }
                        }
                    }
                }
                finally
                {
                    src32.UnlockBits(histData);
                }

                if (total < 16)
                    return (Bitmap)source.Clone();

                long loTarget = Math.Max(1, (long)Math.Ceiling(total * (lowPercentile / 100.0)));
                long hiTarget = Math.Min(total, Math.Max(loTarget + 1,
                    (long)Math.Floor(total * (highPercentile / 100.0))));

                int lo = 0, hi = 255;
                long cum = 0;
                for (int i = 0; i < 256; i++)
                {
                    cum += hist[i];
                    if (cum >= loTarget)
                    {
                        lo = i;
                        break;
                    }
                }
                cum = 0;
                for (int i = 0; i < 256; i++)
                {
                    cum += hist[i];
                    if (cum >= hiTarget)
                    {
                        hi = i;
                        break;
                    }
                }

                int range = hi - lo;
                if (range < 8)
                    return (Bitmap)source.Clone();

                // scaleFactor maps [lo,hi] toward [0,255]; soft caps avoid posterizing
                double scaleFactor = 255.0 / range;
                if (scaleFactor > 2.8)
                    scaleFactor = 2.8;
                if (range >= 200)
                    scaleFactor = Math.Min(scaleFactor, 1.15);
                else if (range < AutoLevelsMinRange)
                    scaleFactor = Math.Min(scaleFactor, 1.8);

                var result = new Bitmap(src32.Width, src32.Height, PixelFormat.Format32bppArgb);
                var srcData = src32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* s0 = (byte*)srcData.Scan0;
                        byte* d0 = (byte*)dstData.Scan0;
                        int sStride = srcData.Stride;
                        int dStride = dstData.Stride;
                        int w = src32.Width;
                        int h = src32.Height;

                        for (int y = 0; y < h; y++)
                        {
                            byte* s = s0 + y * sStride;
                            byte* d = d0 + y * dStride;
                            for (int x = 0; x < w; x++)
                            {
                                int i = x * 4;
                                if (grayIn)
                                {
                                    byte o = ClampByte((s[i] - lo) * scaleFactor);
                                    d[i] = o;
                                    d[i + 1] = o;
                                    d[i + 2] = o;
                                }
                                else
                                {
                                    // Same lo/hi stretch on each channel — preserves hue.
                                    d[i] = ClampByte((s[i] - lo) * scaleFactor);
                                    d[i + 1] = ClampByte((s[i + 1] - lo) * scaleFactor);
                                    d[i + 2] = ClampByte((s[i + 2] - lo) * scaleFactor);
                                }
                                d[i + 3] = s[i + 3];
                            }
                        }
                    }
                }
                finally
                {
                    src32.UnlockBits(srcData);
                    result.UnlockBits(dstData);
                }

                return result;
            }
            finally
            {
                if (disposeSrc)
                    src32.Dispose();
            }
        }

        /// <summary>
        /// Blend image toward solid gray: out = src*(1-amount) + gray*amount.
        /// Softens background art while leaving high-contrast ink relatively visible.
        /// Used for WinOCR balloon detect only - not for Kobold full-frame/crops.
        /// </summary>
        private static Bitmap ApplyGrayFog(Bitmap source, float amount, byte grayLevel)
        {
            amount = Math.Clamp(amount, 0f, 1f);
            if (amount <= 0.001f)
                return (Bitmap)source.Clone();

            Bitmap src32 = Ensure32bppArgb(source);
            bool disposeSrc = !ReferenceEquals(src32, source);
            try
            {
                var result = new Bitmap(src32.Width, src32.Height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, src32.Width, src32.Height);
                var srcData = src32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                float keep = 1f - amount;
                float g = grayLevel;
                try
                {
                    unsafe
                    {
                        byte* s0 = (byte*)srcData.Scan0;
                        byte* d0 = (byte*)dstData.Scan0;
                        int sStride = srcData.Stride;
                        int dStride = dstData.Stride;
                        int w = src32.Width;
                        int h = src32.Height;

                        for (int y = 0; y < h; y++)
                        {
                            byte* s = s0 + y * sStride;
                            byte* d = d0 + y * dStride;
                            for (int x = 0; x < w; x++)
                            {
                                int i = x * 4;
                                d[i] = ClampByte(s[i] * keep + g * amount);       // B
                                d[i + 1] = ClampByte(s[i + 1] * keep + g * amount); // G
                                d[i + 2] = ClampByte(s[i + 2] * keep + g * amount); // R
                                d[i + 3] = s[i + 3];
                            }
                        }
                    }
                }
                finally
                {
                    src32.UnlockBits(srcData);
                    result.UnlockBits(dstData);
                }

                return result;
            }
            finally
            {
                if (disposeSrc)
                    src32.Dispose();
            }
        }

        private static Rectangle MapRectToCapture(
            Rectangle detectRect, double scale, int capW, int capH)
            => BalloonOcrDetect.MapRectToCapture(detectRect, scale, capW, capH);

        private static bool IsJunkWinOcrText(string? text)
            => BalloonOcrDetect.IsJunkWinOcrText(text);

        /// <summary>
        /// Cluster nearby WinOCR lines into one island (multi-line balloon).
        /// </summary>
        private static List<DetectedTextRegion> ClusterTextBoxesWithText(
            List<(Rectangle Box, string Text)> lines,
            int captureW,
            int captureH)
            => BalloonOcrDetect.ClusterTextBoxesWithText(lines, captureW, captureH);

        /// <summary>
        /// Grow each island for crop padding (background-agnostic). Coalescing of
        /// line scraps is separate. After inflate (Grow X/Y):
        /// <list type="bullet">
        /// <item><see cref="EnableMergeOverlappingIslands"/> on (default): union any
        /// islands whose boxes would overlap after Grow and Crop pad into one island
        /// large enough to cover all text.</item>
        /// <item>Off: nudge grow-overlaps apart (carve larger island; never below OCR core).</item>
        /// </list>
        /// <para>
        /// Uses live <see cref="RegionInflateFractionX"/> / Y from Settings → Balloons
        /// (Grow X / Grow Y). Zero grow means zero inflate.
        /// Merge also honors <see cref="TextRegionPadding"/> when testing overlap.
        /// </para>
        /// </summary>
        /// <param name="growOnlyNoMergeNoNudge">
        /// Inflate only — do not merge or nudge (legacy trial path).
        /// </param>
        private static List<DetectedTextRegion> ImproveDetectedRegions(
            List<DetectedTextRegion> regions,
            int capW,
            int capH,
            bool growOnlyNoMergeNoNudge = false)
        {
            if (regions.Count == 0)
                return regions;

            double fracX = RegionInflateFractionX;
            double fracY = RegionInflateFractionY;

            var cores = new List<Rectangle>(regions.Count);
            var inflated = new List<DetectedTextRegion>(regions.Count);
            foreach (var r in regions)
            {
                var core = r.Bounds;
                core.Intersect(new Rectangle(0, 0, capW, capH));
                if (core.Width < 1 || core.Height < 1)
                    continue;

                // No fixed 16px floor — that made Grow 0..~0.16 look identical on small boxes.
                // Tiny islands with non-zero grow still get a couple of pixels so lettering
                // is not crop-starved.
                int minX = 0;
                int minY = 0;
                int maxSide = Math.Max(core.Width, core.Height);
                if (maxSide > 0 && maxSide <= RegionInflateSmallMaxSide)
                {
                    if (fracX > 0.001)
                        minX = 2;
                    if (fracY > 0.001)
                        minY = 2;
                }

                var bounds = InflateRegionBoundsAsym(
                    core, capW, capH, fracX, fracY, minX, minY);
                bounds.Intersect(new Rectangle(0, 0, capW, capH));
                if (bounds.Width < 1 || bounds.Height < 1)
                    continue;

                cores.Add(core);
                inflated.Add(new DetectedTextRegion
                {
                    Bounds = bounds,
                    WinOcrText = r.WinOcrText
                });
            }

            if (growOnlyNoMergeNoNudge)
            {
                // Dyn-fog trial: leave grown boxes as-is (overlaps ok for area score).
                return SortComicReadingOrderRegions(inflated);
            }

            if (EnableMergeOverlappingIslands)
                inflated = MergeOverlappingIslands(inflated, capW, capH);
            else
                inflated = NudgeApartOverlappingRegions(inflated, cores, capW, capH);
            return SortComicReadingOrderRegions(inflated);
        }

        /// <summary>
        /// When <see cref="EnableMergeOverlappingIslands"/> is on, union any islands
        /// whose effective boxes (Grow bounds + Crop pad) intersect (transitive).
        /// Logs only when the count changes.
        /// </summary>
        private static List<DetectedTextRegion> ApplyMergeOverlappingIslandsIfEnabled(
            List<DetectedTextRegion> regions,
            int capW,
            int capH,
            StringBuilder? detail)
        {
            if (!EnableMergeOverlappingIslands || regions.Count <= 1)
                return regions;

            int before = regions.Count;
            var merged = MergeOverlappingIslands(regions, capW, capH);
            if (merged.Count != before && detail != null)
                detail.AppendLine($"merge-overlap {before} → {merged.Count}");
            return merged;
        }

        /// <summary>
        /// Effective box used only for merge-overlap tests: grow-inflated
        /// <paramref name="bounds"/> plus unclamped Crop pad (same pad that green
        /// dashed outlines / OCR crops use). Neighbor clamping is intentionally
        /// skipped here — if pads would meet, the islands should merge instead.
        /// </summary>
        private static Rectangle ExpandBoundsForMergeOverlapTest(
            Rectangle bounds, int capW, int capH, int cropPadPx)
            => ComicRegionGeometry.ExpandBoundsForMergeOverlapTest(bounds, capW, capH, cropPadPx);

        /// <summary>
        /// Union any pair of islands whose effective boxes overlap (grow bounds +
        /// Crop pad; positive area intersection). Transitive merge of stored bounds.
        /// </summary>
        private static List<DetectedTextRegion> MergeOverlappingIslands(
            List<DetectedTextRegion> regions,
            int capW,
            int capH,
            int? cropPadOverride = null)
            => ComicRegionGeometry.MergeOverlappingIslands(
                regions, capW, capH, cropPadOverride ?? TextRegionPadding);

        /// <summary>
        /// Expand reading islands by fixed <see cref="TextRegionPadding"/> (Crop pad)
        /// for green-box preview so the overlay matches the actual OCR crop rect.
        /// Uses the same neighbor clamping as <see cref="CropRegionClamped"/> so stacked
        /// balloons do not draw overlapping pads that OCR would never use.
        /// </summary>
        private static List<DetectedTextRegion> ExpandRegionsByCropPad(
            List<DetectedTextRegion> regions,
            int capW,
            int capH,
            int padPx)
        {
            if (regions.Count == 0 || padPx <= 0)
                return regions;

            var neighbors = new List<Rectangle>(regions.Count);
            foreach (var r in regions)
            {
                if (r.Bounds.Width > 0 && r.Bounds.Height > 0)
                    neighbors.Add(r.Bounds);
            }

            var result = new List<DetectedTextRegion>(regions.Count);
            foreach (var r in regions)
            {
                var b = ComputeClampedCropRect(
                    r.Bounds, padPx, capW, capH, neighbors);
                if (b.Width < 1 || b.Height < 1)
                    continue;
                result.Add(new DetectedTextRegion
                {
                    Bounds = b,
                    WinOcrText = r.WinOcrText
                });
            }
            return result.Count > 0 ? result : regions;
        }

        // Mega-island split removed from Balloons — no-op.
        private static Task<List<DetectedTextRegion>> SplitMegaReadingIslandsAsync(
            Bitmap pipelineImage,
            List<DetectedTextRegion> regions,
            StringBuilder detail,
            CancellationToken token)
        {
            _ = (pipelineImage, detail, token);
            return Task.FromResult(regions);
        }

#if false // mega-split removed
        private static bool IsMegaReadingIsland(Rectangle b, int capW, int capH)
        {
            _ = (b, capW, capH);
            return false;
        }

        private static async Task<List<DetectedTextRegion>> SplitMegaReadingIslandsAsync_Removed(
            Bitmap pipelineImage,
            List<DetectedTextRegion> regions,
            StringBuilder detail,
            CancellationToken token)
        {
            if (regions.Count == 0 || pipelineImage == null)
                return regions;

            if (true)
            {
                detail.AppendLine("mega-split: removed");
                return regions;
            }

            int capW = pipelineImage.Width;
            int capH = pipelineImage.Height;
            if (!regions.Any(r => IsMegaReadingIsland(r.Bounds, capW, capH)))
                return regions;

            var engine = GetWinOcrEngine();
            if (engine == null)
            {
                detail.AppendLine("mega-split: no WinOCR engine - skip");
                return regions;
            }

            var result = new List<DetectedTextRegion>();
            int splitCount = 0;

            foreach (var region in regions)
            {
                token.ThrowIfCancellationRequested();

                if (!IsMegaReadingIsland(region.Bounds, capW, capH))
                {
                    result.Add(region);
                    continue;
                }

                var cropRect = Rectangle.Inflate(region.Bounds, MegaIslandSplitPad, MegaIslandSplitPad);
                cropRect.Intersect(new Rectangle(0, 0, capW, capH));
                if (cropRect.Width < 40 || cropRect.Height < 40)
                {
                    result.Add(region);
                    continue;
                }

                using var crop = CropBitmap(pipelineImage, cropRect);
                if (crop == null)
                {
                    result.Add(region);
                    continue;
                }

                var splitLog = new StringBuilder();
                List<DetectedTextRegion> inner;
                try
                {
                    inner = await RunWinOcrPassAsync(
                        engine, crop, MegaIslandSplitScale, token, splitLog);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    detail.AppendLine(
                        $"mega-split fail @{region.Bounds.X},{region.Bounds.Y} " +
                        $"{region.Bounds.Width}x{region.Bounds.Height}: {ex.Message}");
                    result.Add(region);
                    continue;
                }

                if (inner.Count == 0)
                {
                    detail.AppendLine(
                        $"mega-split keep (empty OCR) " +
                        $"@{region.Bounds.X},{region.Bounds.Y} " +
                        $"{region.Bounds.Width}x{region.Bounds.Height}");
                    result.Add(region);
                    continue;
                }

                // Map crop-local boxes ? pipeline coords
                var mapped = new List<DetectedTextRegion>(inner.Count);
                foreach (var r in inner)
                {
                    var b = r.Bounds;
                    b.Offset(cropRect.X, cropRect.Y);
                    b.Intersect(new Rectangle(0, 0, capW, capH));
                    if (b.Width < BalloonOcrDetect.MinClusterSize || b.Height < BalloonOcrDetect.MinClusterSize)
                        continue;
                    mapped.Add(new DetectedTextRegion
                    {
                        Bounds = b,
                        WinOcrText = r.WinOcrText
                    });
                }

                if (mapped.Count == 0)
                {
                    result.Add(region);
                    continue;
                }

                // Mild pad + scrap-only coalesce so line scraps rejoin, balloons stay split
                mapped = ImproveDetectedRegions(mapped, capW, capH);
                mapped = CoalesceIntoReadingBlocks(mapped, capW, capH, aggressive: false);
                mapped = FilterDeadDetectRegions(mapped, pipelineImage, detail);

                // Only replace mega if we truly got multiple islands
                if (mapped.Count < 2)
                {
                    detail.AppendLine(
                        $"mega-split keep (pieces={mapped.Count}) " +
                        $"@{region.Bounds.X},{region.Bounds.Y} " +
                        $"{region.Bounds.Width}x{region.Bounds.Height}");
                    result.Add(region);
                    continue;
                }

                // Safety: pieces must not explode past budget
                if (result.Count + mapped.Count > MaxTextRegions)
                {
                    int room = Math.Max(1, MaxTextRegions - result.Count);
                    mapped = mapped.Take(room).ToList();
                }

                detail.AppendLine(
                    $"mega-split @{region.Bounds.X},{region.Bounds.Y} " +
                    $"{region.Bounds.Width}x{region.Bounds.Height} ? {mapped.Count} islands");
                foreach (var p in mapped)
                {
                    detail.AppendLine(
                        $"    piece @{p.Bounds.X},{p.Bounds.Y} " +
                        $"{p.Bounds.Width}x{p.Bounds.Height} " +
                        $"\"{Truncate(p.WinOcrText, 36)}\"");
                }

                result.AddRange(mapped);
                splitCount++;
            }

            if (splitCount == 0)
                return regions;

            return SortComicReadingOrderRegions(result);
        }
#endif

        /// <summary>
        /// Merge WinOCR <b>line scraps of the same balloon</b> only.
        /// Comic / non-aggressive: scrap-only - never glue two real text blocks for
        /// "order"; reading order is <see cref="SortComicReadingOrderRegions"/> after.
        /// Aggressive: looser gaps for game UI / fragmented dialogs.
        /// </summary>
        private static List<DetectedTextRegion> CoalesceIntoReadingBlocks(
            List<DetectedTextRegion> regions,
            int capW,
            int capH,
            bool aggressive)
        {
            if (regions.Count <= 1)
                return regions;

            int medianH = regions
                .Select(r => r.Bounds.Height)
                .OrderBy(h => h)
                .ElementAt(regions.Count / 2);
            // Aggressive: game dialogs / fragmented UI - pull line pieces together.
            // Non-aggressive: very tight gaps; merge only same-balloon scraps.
            double gx = aggressive ? 2.4 : 0.45;
            double gy = aggressive ? 1.35 : 0.40;
            int gapX = Math.Max(aggressive ? 28 : 4, (int)(medianH * gx));
            int gapY = Math.Max(aggressive ? 16 : 4, (int)(medianH * gy));

            int n = regions.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a != b) parent[b] = a;
            }

            long frameArea = (long)capW * capH;
            // Non-aggressive: refuse multi-balloon globs.
            double maxUnionFrac = aggressive ? 0.22 : 0.10;
            long maxUnionArea = (long)(frameArea * maxUnionFrac);

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    // Distinct balloons / call-outs ? never merge (order is sort, not union).
                    if (AreSeparateBalloonIslands(regions[i], regions[j], capW, capH, medianH))
                        continue;

                    // Comic path: only glue true line scraps of one balloon.
                    if (!aggressive &&
                        !LooksLikeSameBalloonLineScraps(
                            regions[i], regions[j], capW, capH, medianH))
                        continue;

                    // Empty geometry orphans must not absorb OCR islands into one crop
                    bool aEmpty = SpeechCleaner.CountAlnum(regions[i].WinOcrText) < BalloonOcrDetect.MinWinOcrAlnumChars;
                    bool bEmpty = SpeechCleaner.CountAlnum(regions[j].WinOcrText) < BalloonOcrDetect.MinWinOcrAlnumChars;
                    if (aEmpty != bEmpty)
                    {
                        // Only merge empty+text if empty is small and mostly inside text box
                        var empty = aEmpty ? regions[i].Bounds : regions[j].Bounds;
                        var texted = aEmpty ? regions[j].Bounds : regions[i].Bounds;
                        var inter = Rectangle.Intersect(empty, texted);
                        double ea = Math.Max(1.0, empty.Width * (double)empty.Height);
                        if (inter.Width <= 0 || inter.Height <= 0 ||
                            (inter.Width * (double)inter.Height) / ea < 0.55)
                            continue;
                    }

                    if (!BoxesNear(regions[i].Bounds, regions[j].Bounds, gapX, gapY))
                        continue;

                    var union = Rectangle.Union(regions[i].Bounds, regions[j].Bounds);
                    if ((long)union.Width * union.Height > maxUnionArea)
                        continue; // would create a half-page crop

                    Union(i, j);
                }
            }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!groups.TryGetValue(r, out var list))
                {
                    list = new List<int>();
                    groups[r] = list;
                }
                list.Add(i);
            }

            var blocks = new List<DetectedTextRegion>();
            foreach (var idxs in groups.Values)
            {
                var ordered = idxs
                    .OrderBy(i => regions[i].Bounds.Top + regions[i].Bounds.Height / 2.0)
                    .ThenBy(i => regions[i].Bounds.Left)
                    .ToList();

                Rectangle bounds = regions[ordered[0]].Bounds;
                var texts = new List<string>();
                foreach (int i in ordered)
                {
                    bounds = Rectangle.Union(bounds, regions[i].Bounds);
                    if (!string.IsNullOrWhiteSpace(regions[i].WinOcrText))
                        texts.Add(regions[i].WinOcrText.Trim());
                }

                bounds.Intersect(new Rectangle(0, 0, capW, capH));
                if (bounds.Width < 1 || bounds.Height < 1)
                    continue;

                blocks.Add(new DetectedTextRegion
                {
                    Bounds = bounds,
                    WinOcrText = string.Join(" ", texts)
                });
            }

            // Order is always sort - never implied by merge geometry.
            return SortComicReadingOrderRegions(blocks);
        }

        /// <summary>
        /// True when two boxes look like WinOCR line pieces of the <b>same</b> balloon
        /// (nested scrap, or stacked lines with shared X column). False for two real
        /// dialogue blocks that merely sit near each other.
        /// </summary>
        private static bool LooksLikeSameBalloonLineScraps(
            DetectedTextRegion a,
            DetectedTextRegion b,
            int capW,
            int capH,
            int medianH)
        {
            var ra = a.Bounds;
            var rb = b.Bounds;
            int aa = ra.Width * ra.Height;
            int ba = rb.Width * rb.Height;

            var inter = Rectangle.Intersect(ra, rb);
            if (inter.Width > 0 && inter.Height > 0)
            {
                double ia = inter.Width * (double)inter.Height;
                // Nested / almost contained ? same island
                if (ia >= Math.Min(aa, ba) * 0.55)
                    return true;
            }

            int wa = ComicRegionGeometry.CountWords(a.WinOcrText);
            int wb = ComicRegionGeometry.CountWords(b.WinOcrText);
            int alnumA = SpeechCleaner.CountAlnum(a.WinOcrText);
            int alnumB = SpeechCleaner.CountAlnum(b.WinOcrText);
            bool contentA = wa >= 3 || alnumA >= 12;
            bool contentB = wb >= 3 || alnumB >= 12;
            bool scrapA = !contentA || (wa <= 2 && aa < 8000);
            bool scrapB = !contentB || (wb <= 2 && ba < 8000);

            // Two real multi-word blocks ? never scrap-merge (even if close).
            if (contentA && contentB && !scrapA && !scrapB)
                return false;

            // Need at least one scrap-like side (line fragment), or both short.
            if (!scrapA && !scrapB)
                return false;

            double minW = Math.Max(1.0, Math.Min(ra.Width, rb.Width));
            double minH = Math.Max(1.0, Math.Min(ra.Height, rb.Height));
            double xOverlap = Math.Max(0, Math.Min(ra.Right, rb.Right) - Math.Max(ra.Left, rb.Left));
            double xOverlapRatio = xOverlap / minW;
            int gap = BoxGap(ra, rb);

            // Stacked lines of one balloon: shared column + small vertical gap
            if (xOverlapRatio >= 0.40 && gap <= Math.Max(12, medianH * 0.75))
                return true;

            // Tiny fragment glued to a larger parent it mostly overlaps in X
            if (xOverlapRatio >= 0.55 && gap <= Math.Max(18, medianH) &&
                (aa < 3500 || ba < 3500))
                return true;

            // Both microscopic scraps very near each other
            if (aa < 2000 && ba < 2000 && gap <= Math.Max(10, medianH / 3) &&
                xOverlapRatio >= 0.25)
                return true;

            // Solid island check: two solids with any center offset are not line scraps
            if (IsSolidReadingIsland(a, capW, capH) && IsSolidReadingIsland(b, capW, capH))
            {
                double acx = ra.Left + ra.Width / 2.0;
                double acy = ra.Top + ra.Height / 2.0;
                double bcx = rb.Left + rb.Width / 2.0;
                double bcy = rb.Top + rb.Height / 2.0;
                if (Math.Abs(acx - bcx) >= minW * 0.25 || Math.Abs(acy - bcy) >= minH * 0.25)
                    return false;
            }

            return false;
        }

        /// <summary>
        /// When fragments form one compact text block (dialog box, paragraph) and
        /// there are no well-separated solid islands, use a single union crop.
        /// </summary>
        private static List<DetectedTextRegion> TryCollapseCompactCluster(
            List<DetectedTextRegion> regions,
            int capW,
            int capH)
        {
            if (regions.Count <= 1)
                return regions;

            Rectangle union = regions[0].Bounds;
            long sumArea = 0;
            foreach (var r in regions)
            {
                union = Rectangle.Union(union, r.Bounds);
                sumArea += (long)r.Bounds.Width * r.Bounds.Height;
            }

            union.Intersect(new Rectangle(0, 0, capW, capH));
            if (union.Width < 8 || union.Height < 8)
                return regions;

            long unionArea = (long)union.Width * union.Height;
            long frameArea = (long)capW * capH;
            if (unionArea < 1 || frameArea < 1)
                return regions;

            double coverage = (double)sumArea / unionArea;
            double frameFrac = (double)unionArea / frameArea;

            // One compact block of text (UI dialog, caption) - not the whole panel art
            if (coverage >= 0.22 && frameFrac <= 0.70 && frameFrac >= 0.02)
            {
                string text = string.Join(" ",
                    regions.Select(r => r.WinOcrText).Where(t => !string.IsNullOrWhiteSpace(t)));
                return new List<DetectedTextRegion>
                {
                    new DetectedTextRegion { Bounds = union, WinOcrText = text }
                };
            }

            return regions;
        }

        /// <summary>
        /// True when there are 2+ large islands with a clear gap (typical multi-balloon
        /// comic panel) - do not collapse those into one crop.
        /// </summary>
        private static bool HasWellSeparatedSolidIslands(
            List<DetectedTextRegion> regions, int capW, int capH)
        {
            if (regions.Count < 2)
                return false;

            int medianH = regions
                .Select(r => r.Bounds.Height)
                .OrderBy(h => h)
                .ElementAt(regions.Count / 2);
            var solid = regions
                .Where(r => IsSolidReadingIsland(r, capW, capH))
                .ToList();
            if (solid.Count < 2)
                return false;

            for (int i = 0; i < solid.Count; i++)
            {
                for (int j = i + 1; j < solid.Count; j++)
                {
                    if (AreSeparateSolidBalloons(solid[i], solid[j], capW, capH, medianH))
                        return true;
                }
            }
            return false;
        }

        private static bool IsSolidReadingIsland(DetectedTextRegion r, int capW, int capH)
        {
            if (IsTinyRegion(r.Bounds, capW, capH))
                return false;
            int area = r.Bounds.Width * r.Bounds.Height;
            // Multi-line speech balloons are typically this large after inflate
            if (area >= 4500 && r.Bounds.Height >= 48)
                return true;
            // Detect-only text length as a size hint (not for speech)
            if (SpeechCleaner.CountAlnum(r.WinOcrText) >= 16 && area >= 2400)
                return true;
            if (ComicRegionGeometry.CountWords(r.WinOcrText) >= 6 && area >= 2000)
                return true;
            // Short call-outs ("SPARTAN!") after inflate
            if (SpeechCleaner.CountAlnum(r.WinOcrText) >= 8 && area >= 1800 && r.Bounds.Height >= 36)
                return true;
            return false;
        }

        /// <summary>
        /// Two islands that should stay separate crops (stacked/side-by-side balloons).
        /// True ? do not coalesce. Nested line scraps inside one balloon return false.
        /// Content-rich islands with distinct centers stay split even when inflate
        /// made their boxes touch (dense comic panels).
        /// </summary>
        private static bool AreSeparateBalloonIslands(
            DetectedTextRegion a, DetectedTextRegion b, int capW, int capH, int medianH)
        {
            var ra = a.Bounds;
            var rb = b.Bounds;
            int aa = ra.Width * ra.Height;
            int ba = rb.Width * rb.Height;

            // Both microscopic ? allow merge (word scraps)
            if (aa < 900 && ba < 900)
                return false;

            var inter = Rectangle.Intersect(ra, rb);
            if (inter.Width > 0 && inter.Height > 0)
            {
                double ia = inter.Width * (double)inter.Height;
                // One box almost contains the other ? same island / nested scrap
                if (ia >= Math.Min(aa, ba) * 0.65)
                    return false;
            }

            double acx = ra.Left + ra.Width / 2.0;
            double acy = ra.Top + ra.Height / 2.0;
            double bcx = rb.Left + rb.Width / 2.0;
            double bcy = rb.Top + rb.Height / 2.0;
            double dx = Math.Abs(acx - bcx);
            double dy = Math.Abs(acy - bcy);
            double minH = Math.Max(1.0, Math.Min(ra.Height, rb.Height));
            double minW = Math.Max(1.0, Math.Min(ra.Width, rb.Width));
            double maxH = Math.Max(ra.Height, rb.Height);
            double maxW = Math.Max(ra.Width, rb.Width);

            double xOverlap = Math.Max(0, Math.Min(ra.Right, rb.Right) - Math.Max(ra.Left, rb.Left));
            double xOverlapRatio = xOverlap / minW;
            int gap = BoxGap(ra, rb);

            int wa = ComicRegionGeometry.CountWords(a.WinOcrText);
            int wb = ComicRegionGeometry.CountWords(b.WinOcrText);
            int alnumA = SpeechCleaner.CountAlnum(a.WinOcrText);
            int alnumB = SpeechCleaner.CountAlnum(b.WinOcrText);
            bool contentA = wa >= 3 || alnumA >= 12;
            bool contentB = wb >= 3 || alnumB >= 12;
            bool scrapA = wa <= 2 && alnumA < 12 && aa < 6000;
            bool scrapB = wb <= 2 && alnumB < 12 && ba < 6000;

            // Content-rich islands with clearly offset centers = distinct balloons.
            // Inflate often makes them touch (gap=0); still refuse merge.
            if (contentA && contentB &&
                (dy >= minH * 0.35 || dx >= minW * 0.35))
                return true;

            // Multi-line scraps inside ONE balloon: heavy X-overlap + small gap,
            // and at least one side is a scrap (not two full dialogue islands).
            if (xOverlapRatio >= 0.45 &&
                gap <= Math.Max(14, medianH * 0.85) &&
                (scrapA || scrapB || !contentA || !contentB))
                return false;

            // Stacked distinct balloons: vertical separation + real gap
            if (dy >= minH * 0.28 && dy >= maxH * 0.18 &&
                gap >= Math.Max(10, medianH / 5))
                return true;

            // Side-by-side balloons
            if (dx >= minW * 0.32 && dx >= maxW * 0.18 &&
                gap >= Math.Max(8, medianH / 6))
                return true;

            // Clear geometric gap between boxes
            if (gap >= Math.Max(12, medianH / 4))
                return true;

            // Both look like real islands and centers are not nested ? keep split
            // (gap may be 0 after inflate; centers still diverge)
            if (IsSolidReadingIsland(a, capW, capH) && IsSolidReadingIsland(b, capW, capH))
            {
                if (dy >= minH * 0.22 || dx >= minW * 0.28)
                {
                    if (gap >= Math.Max(6, medianH / 8) || contentA || contentB)
                        return true;
                }
            }

            return false;
        }

        /// <summary>Legacy name used by well-separated checks.</summary>
        private static bool AreSeparateSolidBalloons(
            DetectedTextRegion a, DetectedTextRegion b, int capW, int capH, int medianH)
            => AreSeparateBalloonIslands(a, b, capW, capH, medianH);

        /// <summary>Axis-aligned gap between two rects (0 if they touch/overlap).</summary>
        private static int BoxGap(Rectangle a, Rectangle b)
        {
            int dx = 0;
            if (a.Right < b.Left) dx = b.Left - a.Right;
            else if (b.Right < a.Left) dx = a.Left - b.Right;

            int dy = 0;
            if (a.Bottom < b.Top) dy = b.Top - a.Bottom;
            else if (b.Bottom < a.Top) dy = a.Top - b.Bottom;

            if (dx == 0 && dy == 0)
                return 0;
            if (dx == 0) return dy;
            if (dy == 0) return dx;
            // Diagonal separation: use max as "clear gap" measure
            return Math.Max(dx, dy);
        }

        /// <summary>
        /// When two inflated islands overlap, shrink them so each crop owns distinct
        /// pixels. Prefer carving the <b>larger</b> island. Never shrink past each
        /// region's WinOCR <paramref name="cores"/> (keeps "ARE THE" on the left).
        /// </summary>
        private static List<DetectedTextRegion> NudgeApartOverlappingRegions(
            List<DetectedTextRegion> regions,
            List<Rectangle> cores,
            int capW,
            int capH)
        {
            if (regions.Count <= 1)
                return regions;

            var boxes = regions.Select(r => r.Bounds).ToArray();
            if (cores.Count != boxes.Length)
            {
                // Fallback: treat current box as core
                cores = boxes.ToList();
            }

            Rectangle Floor(int idx, Rectangle proposed)
            {
                var c = cores[idx];
                int l = Math.Min(proposed.Left, c.Left);
                int t = Math.Min(proposed.Top, c.Top);
                int r = Math.Max(proposed.Right, c.Right);
                int b = Math.Max(proposed.Bottom, c.Bottom);
                var u = Rectangle.FromLTRB(l, t, r, b);
                u.Intersect(new Rectangle(0, 0, capW, capH));
                return u.Width >= 1 && u.Height >= 1 ? u : proposed;
            }

            // Pairwise; a few passes so multi-way overlaps settle
            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                for (int i = 0; i < boxes.Length; i++)
                {
                    for (int j = i + 1; j < boxes.Length; j++)
                    {
                        var a = boxes[i];
                        var b = boxes[j];
                        var inter = Rectangle.Intersect(a, b);
                        if (inter.Width <= 0 || inter.Height <= 0)
                            continue;

                        double acx = a.Left + a.Width / 2.0;
                        double acy = a.Top + a.Height / 2.0;
                        double bcx = b.Left + b.Width / 2.0;
                        double bcy = b.Top + b.Height / 2.0;
                        double dx = Math.Abs(acx - bcx);
                        double dy = Math.Abs(acy - bcy);

                        long aArea = (long)a.Width * a.Height;
                        long bArea = (long)b.Width * b.Height;
                        bool preferShrinkA = aArea >= bArea * 3 / 2;
                        bool preferShrinkB = bArea >= aArea * 3 / 2;

                        if (dy >= dx)
                        {
                            if (acy <= bcy)
                            {
                                if (preferShrinkA && !preferShrinkB)
                                {
                                    int newABottom = Math.Min(a.Bottom, Math.Max(b.Top, cores[i].Bottom));
                                    if (newABottom - a.Top >= 8)
                                    {
                                        boxes[i] = Floor(i, Rectangle.FromLTRB(a.Left, a.Top, a.Right, newABottom));
                                        changed = true;
                                        continue;
                                    }
                                }
                                if (preferShrinkB && !preferShrinkA)
                                {
                                    int newBTop = Math.Max(b.Top, Math.Min(a.Bottom, cores[j].Top));
                                    if (b.Bottom - newBTop >= 8)
                                    {
                                        boxes[j] = Floor(j, Rectangle.FromLTRB(b.Left, newBTop, b.Right, b.Bottom));
                                        changed = true;
                                        continue;
                                    }
                                }
                                int midY = inter.Top + inter.Height / 2;
                                boxes[i] = Floor(i, Rectangle.FromLTRB(a.Left, a.Top, a.Right, Math.Min(a.Bottom, midY)));
                                boxes[j] = Floor(j, Rectangle.FromLTRB(b.Left, Math.Max(b.Top, midY), b.Right, b.Bottom));
                                changed = true;
                            }
                            else
                            {
                                if (preferShrinkB && !preferShrinkA)
                                {
                                    int newBBottom = Math.Min(b.Bottom, Math.Max(a.Top, cores[j].Bottom));
                                    if (newBBottom - b.Top >= 8)
                                    {
                                        boxes[j] = Floor(j, Rectangle.FromLTRB(b.Left, b.Top, b.Right, newBBottom));
                                        changed = true;
                                        continue;
                                    }
                                }
                                if (preferShrinkA && !preferShrinkB)
                                {
                                    int newATop = Math.Max(a.Top, Math.Min(b.Bottom, cores[i].Top));
                                    if (a.Bottom - newATop >= 8)
                                    {
                                        boxes[i] = Floor(i, Rectangle.FromLTRB(a.Left, newATop, a.Right, a.Bottom));
                                        changed = true;
                                        continue;
                                    }
                                }
                                int midY = inter.Top + inter.Height / 2;
                                boxes[j] = Floor(j, Rectangle.FromLTRB(b.Left, b.Top, b.Right, Math.Min(b.Bottom, midY)));
                                boxes[i] = Floor(i, Rectangle.FromLTRB(a.Left, Math.Max(a.Top, midY), a.Right, a.Bottom));
                                changed = true;
                            }
                        }
                        else
                        {
                            // Side-by-side - protect left edge of right balloon (ARE THE-)
                            if (acx <= bcx)
                            {
                                if (preferShrinkA && !preferShrinkB)
                                {
                                    int newARight = Math.Min(a.Right, Math.Max(b.Left, cores[i].Right));
                                    if (newARight - a.Left >= 8)
                                    {
                                        boxes[i] = Floor(i, Rectangle.FromLTRB(a.Left, a.Top, newARight, a.Bottom));
                                        changed = true;
                                        continue;
                                    }
                                }
                                if (preferShrinkB && !preferShrinkA)
                                {
                                    int newBLeft = Math.Max(b.Left, Math.Min(a.Right, cores[j].Left));
                                    if (b.Right - newBLeft >= 8)
                                    {
                                        boxes[j] = Floor(j, Rectangle.FromLTRB(newBLeft, b.Top, b.Right, b.Bottom));
                                        changed = true;
                                        continue;
                                    }
                                }
                                // Default: shrink the LEFT (usually larger) island only
                                int midX = inter.Left + inter.Width / 2;
                                int aRight = Math.Min(a.Right, Math.Max(midX, cores[i].Right));
                                boxes[i] = Floor(i, Rectangle.FromLTRB(a.Left, a.Top, aRight, a.Bottom));
                                // Right island keeps at least its OCR core left
                                boxes[j] = Floor(j, b);
                                changed = true;
                            }
                            else
                            {
                                if (preferShrinkB && !preferShrinkA)
                                {
                                    int newBRight = Math.Min(b.Right, Math.Max(a.Left, cores[j].Right));
                                    if (newBRight - b.Left >= 8)
                                    {
                                        boxes[j] = Floor(j, Rectangle.FromLTRB(b.Left, b.Top, newBRight, b.Bottom));
                                        changed = true;
                                        continue;
                                    }
                                }
                                if (preferShrinkA && !preferShrinkB)
                                {
                                    int newALeft = Math.Max(a.Left, Math.Min(b.Right, cores[i].Left));
                                    if (a.Right - newALeft >= 8)
                                    {
                                        boxes[i] = Floor(i, Rectangle.FromLTRB(newALeft, a.Top, a.Right, a.Bottom));
                                        changed = true;
                                        continue;
                                    }
                                }
                                int midX = inter.Left + inter.Width / 2;
                                int bRight = Math.Min(b.Right, Math.Max(midX, cores[j].Right));
                                boxes[j] = Floor(j, Rectangle.FromLTRB(b.Left, b.Top, bRight, b.Bottom));
                                boxes[i] = Floor(i, a);
                                changed = true;
                            }
                        }
                    }
                }
                if (!changed)
                    break;
            }

            var result = new List<DetectedTextRegion>(regions.Count);
            for (int i = 0; i < regions.Count; i++)
            {
                var b = Floor(i, boxes[i]);
                b.Intersect(new Rectangle(0, 0, capW, capH));
                if (b.Width < 1 || b.Height < 1)
                    continue;
                result.Add(new DetectedTextRegion
                {
                    Bounds = b,
                    WinOcrText = regions[i].WinOcrText
                });
            }
            return result;
        }

        /// <summary>
        /// True when WinOCR boxes look like word scraps (not full speech balloons).
        /// Triggers full-frame-first so we do not speak "no. issue." for a full balloon.
        /// </summary>
        private static bool LooksFragmented(
            List<DetectedTextRegion> regions, int capW, int capH)
        {
            if (regions.Count == 0)
                return true;

            double avgArea = regions.Average(r => (double)r.Bounds.Width * r.Bounds.Height);
            double imgArea = (double)capW * capH;

            // Tiny average box
            if (avgArea < 3500)
                return true;
            if (avgArea < imgArea * 0.001)
                return true;

            // Many short WinOCR snippets = word hits, not bubbles
            int shortSnippets = regions.Count(r => SpeechCleaner.CountAlnum(r.WinOcrText) <= 8);
            if (regions.Count >= 2 && shortSnippets >= 2 &&
                (double)shortSnippets / regions.Count >= 0.5)
                return true;

            // Any ultra-tiny region among larger ones
            int tiny = regions.Count(r => IsTinyRegion(r.Bounds, capW, capH));
            if (tiny >= 1 && regions.Count >= 2 && tiny >= regions.Count - 1)
                return true;

            return false;
        }

        /// <summary>
        /// WinOCR produced line scraps / partial words rather than full balloons.
        /// Sequential crops will clip and misread - prefer full-frame VL instead.
        /// </summary>
        private static bool LooksLikeScrapDetect(
            List<DetectedTextRegion> regions,
            int capW,
            int capH,
            bool fragmented)
        {
            if (regions.Count == 0)
                return true;
            if (fragmented)
                return true;

            int totalAlnum = regions.Sum(r => SpeechCleaner.CountAlnum(r.WinOcrText));
            int withText = regions.Count(r => SpeechCleaner.CountAlnum(r.WinOcrText) >= BalloonOcrDetect.MinWinOcrAlnumChars);
            double avgAlnum = withText > 0
                ? regions.Where(r => SpeechCleaner.CountAlnum(r.WinOcrText) >= BalloonOcrDetect.MinWinOcrAlnumChars)
                    .Average(r => SpeechCleaner.CountAlnum(r.WinOcrText))
                : 0;

            // Very little detect text for the number of boxes (Warblade-style scraps)
            if (regions.Count >= 3 && totalAlnum < regions.Count * 10)
                return true;
            if (regions.Count >= 2 && avgAlnum > 0 && avgAlnum < 14)
                return true;

            // Mostly short snippets
            int shortSnips = regions.Count(r =>
            {
                int a = SpeechCleaner.CountAlnum(r.WinOcrText);
                return a > 0 && a <= 10;
            });
            if (regions.Count >= 3 && shortSnips >= regions.Count - 1)
                return true;

            // Average box too small for a multi-line balloon
            double avgArea = regions.Average(r => (double)r.Bounds.Width * r.Bounds.Height);
            double avgH = regions.Average(r => (double)r.Bounds.Height);
            if (regions.Count >= 3 && avgH < 100 && avgArea < 25000)
                return true;

            // Not solid islands and many boxes ? scrap cluster
            if (regions.Count >= 4 &&
                !HasWellSeparatedSolidIslands(regions, capW, capH) &&
                totalAlnum < 80)
                return true;

            return false;
        }

        private static bool BoxesNear(Rectangle a, Rectangle b, int gapX, int gapY)
            => BalloonOcrDetect.BoxesNear(a, b, gapX, gapY);

        /// <summary>
        /// Western comic order (geometry only): rows top→bottom, left→right in each row.
        /// </summary>
        private static List<DetectedTextRegion> SortComicReadingOrderRegions(
            List<DetectedTextRegion> regions)
            => ComicRegionGeometry.SortComicReadingOrderRegions(regions);










        /// <summary>Usable crop text with reading-order region index.</summary>
        private readonly struct CropRead
        {
            public int RegionIndex { get; }
            public string Text { get; }
            public CropRead(int regionIndex, string text)
            {
                RegionIndex = regionIndex;
                Text = text;
            }

            public ComicBestOfFusion.CropRead ToFusion() => new(RegionIndex, Text);
        }

        /// <summary>
        /// Choose full-frame vs crops for ComicBook best-of (delegates to
        /// <see cref="ComicBestOfFusion"/>).
        /// </summary>
        private static (List<string> Chosen, string Tag) PickBestOfFullVsCrops(
            List<string> fullParts,
            List<CropRead> cropReads,
            StringBuilder detail,
            bool scrapDetect = false,
            bool solidIslands = false,
            int readingBlocks = 0)
        {
            var fusionReads = cropReads.Select(c => c.ToFusion()).ToList();
            return ComicBestOfFusion.PickBestOfFullVsCrops(
                fullParts, fusionReads, detail, scrapDetect, solidIslands, readingBlocks);
        }



        // -----------------------------------------------------------------------
        // Geometry-guided full-frame balloon split
        // -----------------------------------------------------------------------

        /// <summary>
        /// Geometry-guided full-frame balloon split (delegates to
        /// <see cref="ComicBestOfFusion"/>).
        /// </summary>
        private static List<string>? SplitFullFrameByDetectRegions(
            List<string> fullParts,
            List<DetectedTextRegion> regions,
            StringBuilder detail)
            => ComicBestOfFusion.SplitFullFrameByDetectRegions(fullParts, regions, detail);



















        /// <summary>
        /// Drop WinOCR scrap islands before crop-Kobold: empty non-balloons, digit
        /// noise, vowelless scraps, and single-token logos on non-balloon art.
        /// Keeps real short dialogue words (WATCHDOG, ANGRY, names) when they sit
        /// on a light speech plate. Keeps empty balloon-sized geometry
        /// (ocr-budget orphans) for vision recovery.
        /// </summary>
        private static List<DetectedTextRegion> FilterDeadDetectRegions(
            List<DetectedTextRegion> regions,
            Bitmap capture,
            StringBuilder detail)
        {
            if (regions.Count == 0)
                return regions;

            long frameA = Math.Max(1L, (long)capture.Width * capture.Height);
            var kept = new List<DetectedTextRegion>(regions.Count);

            foreach (var r in regions)
            {
                int alnum = SpeechCleaner.CountAlnum(r.WinOcrText);
                int words = ComicRegionGeometry.CountWords(r.WinOcrText);
                long area = (long)r.Bounds.Width * r.Bounds.Height;
                bool emptyText = string.IsNullOrWhiteSpace(r.WinOcrText);

                if (emptyText)
                {
                    // Geometry-only: keep only balloon-sized plates
                    bool balloonish =
                        r.Bounds.Width >= 80 &&
                        r.Bounds.Height >= 60 &&
                        area >= 8000 &&
                        area <= frameA * 0.10;
                    if (!balloonish)
                    {
                        detail.AppendLine(
                            $"  dead-island drop empty-small " +
                            $"@{r.Bounds.X},{r.Bounds.Y} {r.Bounds.Width}x{r.Bounds.Height}");
                        continue;
                    }
                    kept.Add(r);
                    continue;
                }

                // Real short dialogue token? (WATCHDOG, ANGRY, SHEPHERD-) - keep
                // only when other checks pass (esp. balloon-fill for single tokens).
                bool realWordIsland = LooksLikeRealDialogueToken(r.WinOcrText);

                // Junk OCR text - but not real dialogue words.
                if (!realWordIsland && IsJunkWinOcrText(r.WinOcrText))
                {
                    detail.AppendLine(
                        $"  dead-island drop weak-ocr alnum={alnum} words={words} " +
                        $"\"{Truncate(r.WinOcrText, 24)}\" " +
                        $"@{r.Bounds.X},{r.Bounds.Y}");
                    continue;
                }

                // Single token on a large box: drop only scrap (digits, no vowels, tiny)
                // Keep letter-words with a vowel even if alone (balloon fragments).
                if (words <= 1 && area >= 8000 && !realWordIsland)
                {
                    detail.AppendLine(
                        $"  dead-island drop single-token-scrap alnum={alnum} " +
                        $"\"{Truncate(r.WinOcrText, 24)}\" " +
                        $"@{r.Bounds.X},{r.Bounds.Y} {r.Bounds.Width}x{r.Bounds.Height}");
                    continue;
                }

                // Large box with almost no letter structure + fails balloon fill
                // (skip this check for real short dialogue tokens)
                if (!realWordIsland &&
                    words <= 2 && alnum < 8 && area >= 12000 &&
                    !LooksLikeSpeechBalloonFill(capture, r.Bounds))
                {
                    detail.AppendLine(
                        $"  dead-island drop non-balloon alnum={alnum} " +
                        $"\"{Truncate(r.WinOcrText, 24)}\" " +
                        $"@{r.Bounds.X},{r.Bounds.Y}");
                    continue;
                }

                kept.Add(r);
            }

            return kept;
        }

        /// <summary>
        /// True for a short alnum token that looks like real English dialogue.
        /// Delegates to <see cref="ComicBestOfFusion.LooksLikeRealDialogueToken"/>.
        /// </summary>
        private static bool LooksLikeRealDialogueToken(string? text)
            => ComicBestOfFusion.LooksLikeRealDialogueToken(text);

        /// <summary>
        /// Encode a live pipeline bitmap for Analytics at the size that stage used.
        /// Prep stages stay pipe-native (no accidental re-sample). The intentional
        /// Local-LLM send cap (640 long-edge) is a real stage — capture it under
        /// <c>llm_send</c>, not by downscaling earlier slots.
        /// Gallery UI thumbs are display-only; stored PNGs stay stage-native.
        /// Optionally replaces an existing entry with the same key.
        /// </summary>
        private void CaptureAnalyticsImage(string key, string title, Bitmap? source)
        {
            if (source == null || _runImages == null)
                return;
            if (string.IsNullOrWhiteSpace(key))
                return;

            try
            {
                // Replace same key so re-tries (region_01 recovery prep) keep one slot
                int existing = _runImages.FindIndex(
                    i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
                if (existing < 0 && _runImages.Count >= AnalyticsMaxImages)
                    return;

                int srcW = source.Width;
                int srcH = source.Height;
                if (srcW < 1 || srcH < 1)
                    return;

                using var ms = new MemoryStream();
                // Encode the exact pipeline bitmap — no long-edge re-sample.
                if (source.PixelFormat == PixelFormat.Format32bppArgb)
                {
                    source.Save(ms, ImageFormat.Png);
                }
                else
                {
                    using var converted = new Bitmap(
                        srcW, srcH, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(converted))
                    {
                        g.DrawImage(source, 0, 0, srcW, srcH);
                    }
                    converted.Save(ms, ImageFormat.Png);
                }

                var entry = new OcrResultImage
                {
                    Key = key,
                    Title = string.IsNullOrWhiteSpace(title) ? key : title,
                    Width = srcW,
                    Height = srcH,
                    SourceWidth = srcW,
                    SourceHeight = srcH,
                    PngBytes = ms.ToArray(),
                };
                if (existing >= 0)
                    _runImages[existing] = entry;
                else
                    _runImages.Add(entry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OCR] analytics image {key}: {ex.Message}");
            }
        }

        private void ClearRunFogAnalytics()
        {
            _runFogAmountUsed = 0f;
        }

        private void SetRunFogAnalytics(float fogUsed)
        {
            _runFogAmountUsed = fogUsed;
        }

        /// <summary>
        /// Publish analytics snapshot (always) and write last_ocr.txt (Debug builds only).
        /// Called for the speak plan (pre-TTS) and again after timings so Analytics stays current.
        /// </summary>
        private void WriteLastOcrDebug(
            string body,
            StringBuilder detail,
            bool notifyNewCapture = true)
        {
            string spoken = body ?? "";
            bool unreadable =
                string.IsNullOrWhiteSpace(spoken) ||
                spoken.Equals("(unreadable)", StringComparison.OrdinalIgnoreCase) ||
                spoken.Equals("unreadable", StringComparison.OrdinalIgnoreCase);

            string shape =
                _isEllipse ? "Oval" :
                (_lassoPoints != null && _lassoPoints.Count > 2) ? "Lasso" :
                "Rectangle";

            IReadOnlyList<OcrResultImage> images =
                _runImages != null && _runImages.Count > 0
                    ? _runImages.ToList()
                    : Array.Empty<OcrResultImage>();

            var snapshot = new OcrLastResult
            {
                CompletedLocal = DateTime.Now,
                CaptureBounds = _rect,
                Shape = shape,
                SpokenText = spoken,
                Detail = detail?.ToString() ?? "",
                Unreadable = unreadable,
                Images = images,
                FogAmountUsed = _runFogAmountUsed,
            };
            lock (LastResultLock)
                LastResult = snapshot;

            // Live capture only — Balloons Speak must not wipe refine session.
            if (notifyNewCapture)
                ComicRegionOverrideSession.NotifyNewCapture();

#if DEBUG
            try
            {
                EnsureDebugFolder();
                File.WriteAllText(
                    Path.Combine(DebugFolder, "last_ocr.txt"),
                    $"{spoken}\n\n--- detail ---\n{snapshot.Detail}");
            }
            catch { /* ignore locked / missing folder */ }
#endif
        }












        /// <summary>
        /// Crop with padding, but do not expand into neighboring island boxes
        /// (prevents stacked balloons from sharing crop pixels).
        /// </summary>
        private static Bitmap? CropRegionClamped(
            Bitmap source,
            Rectangle bounds,
            int padding,
            IReadOnlyList<Rectangle>? neighbors)
        {
            var r = ComputeClampedCropRect(
                bounds, padding, source.Width, source.Height, neighbors);
            if (r.Width < 1 || r.Height < 1)
                return null;

            var crop = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(crop))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(
                    source,
                    new Rectangle(0, 0, r.Width, r.Height),
                    r,
                    GraphicsUnit.Pixel);
            }
            return crop;
        }

        /// <summary>
        /// Pad <paramref name="bounds"/> but stop at neighbor edges / midlines so
        /// crops of stacked or side-by-side balloons do not include each other.
        /// Stacked balloons only clamp Y (not X); side-by-side only clamp X.
        /// <b>Never shrinks inside the core detect box</b> - only the padding ring
        /// is clamped (fixes left-clipped "SPARTAN!" style crops).
        /// </summary>
        private static Rectangle ComputeClampedCropRect(
            Rectangle bounds,
            int padding,
            int capW,
            int capH,
            IReadOnlyList<Rectangle>? neighbors)
        {
            // Core island - inviolable (except image edges)
            int coreL = bounds.Left;
            int coreT = bounds.Top;
            int coreR = bounds.Right;
            int coreB = bounds.Bottom;

            int left = coreL - padding;
            int top = coreT - padding;
            int right = coreR + padding;
            int bottom = coreB + padding;

            if (neighbors != null && neighbors.Count > 0 && padding > 0)
            {
                double acx = bounds.Left + bounds.Width / 2.0;
                double acy = bounds.Top + bounds.Height / 2.0;

                foreach (var o in neighbors)
                {
                    if (o.Width < 1 || o.Height < 1)
                        continue;

                    double ocx = o.Left + o.Width / 2.0;
                    double ocy = o.Top + o.Height / 2.0;
                    double dx = Math.Abs(acx - ocx);
                    double dy = Math.Abs(acy - ocy);

                    // Dominant relationship - stacked bubbles often share X range;
                    // do NOT cut left/right just because centers differ slightly.
                    bool primarilyStacked = dy >= dx * 0.75;
                    bool primarilySideBySide = dx > dy * 0.75 && !primarilyStacked;

                    // Horizontal proximity: would padded rects meet in X?
                    bool xNear = coreL - padding < o.Right &&
                                 coreR + padding > o.Left;
                    // Vertical proximity
                    bool yNear = coreT - padding < o.Bottom &&
                                 coreB + padding > o.Top;

                    // Stacked (or clearly above/below): only clamp vertical *padding*
                    if (xNear && (primarilyStacked || (!primarilySideBySide && dy > 0)))
                    {
                        if (ocy < acy)
                        {
                            // Neighbor above: do not pad into their box, but keep coreT
                            if (o.Bottom <= coreT)
                                top = Math.Max(top, o.Bottom);
                            else
                                top = Math.Max(top, Math.Min(coreT, (o.Bottom + coreT) / 2));
                        }
                        else if (ocy > acy)
                        {
                            if (o.Top >= coreB)
                                bottom = Math.Min(bottom, o.Top);
                            else
                                bottom = Math.Min(bottom, Math.Max(coreB, (coreB + o.Top) / 2));
                        }
                    }

                    // Side-by-side only: clamp horizontal *padding*
                    if (yNear && primarilySideBySide)
                    {
                        if (ocx < acx)
                        {
                            if (o.Right <= coreL)
                                left = Math.Max(left, o.Right);
                            else
                                left = Math.Max(left, Math.Min(coreL, (o.Right + coreL) / 2));
                        }
                        else if (ocx > acx)
                        {
                            if (o.Left >= coreR)
                                right = Math.Min(right, o.Left);
                            else
                                right = Math.Min(right, Math.Max(coreR, (coreR + o.Left) / 2));
                        }
                    }
                }
            }

            // Hard floor: crop must always include the full detect island
            left = Math.Min(left, coreL);
            top = Math.Min(top, coreT);
            right = Math.Max(right, coreR);
            bottom = Math.Max(bottom, coreB);

            left = Math.Clamp(left, 0, Math.Max(0, capW - 1));
            top = Math.Clamp(top, 0, Math.Max(0, capH - 1));
            right = Math.Clamp(right, left + 1, capW);
            bottom = Math.Clamp(bottom, top + 1, capH);

            // Re-assert core after image clamp when possible
            if (coreL >= 0 && coreL < capW) left = Math.Min(left, coreL);
            if (coreT >= 0 && coreT < capH) top = Math.Min(top, coreT);
            if (coreR > left && coreR <= capW) right = Math.Max(right, coreR);
            if (coreB > top && coreB <= capH) bottom = Math.Max(bottom, coreB);

            right = Math.Clamp(right, left + 1, capW);
            bottom = Math.Clamp(bottom, top + 1, capH);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        /// <summary>
        /// After expand-retry / tiny pre-expand: trim only the *expanded* margin so
        /// we do not swallow neighbors, but never smaller than <paramref name="core"/>.
        /// </summary>
        private static Rectangle ClampExpandedAwayFromNeighbors(
            Rectangle expanded,
            Rectangle core,
            IReadOnlyList<Rectangle> neighbors,
            int capW,
            int capH)
        {
            // Start from expanded, always cover core
            int left = Math.Min(expanded.Left, core.Left);
            int top = Math.Min(expanded.Top, core.Top);
            int right = Math.Max(expanded.Right, core.Right);
            int bottom = Math.Max(expanded.Bottom, core.Bottom);

            if (neighbors != null)
            {
                foreach (var o in neighbors)
                {
                    if (o.Width < 1 || o.Height < 1)
                        continue;

                    var cur = Rectangle.FromLTRB(left, top, right, bottom);
                    var inter = Rectangle.Intersect(cur, o);
                    if (inter.Width <= 0 || inter.Height <= 0)
                        continue;

                    double acx = (left + right) / 2.0;
                    double acy = (top + bottom) / 2.0;
                    double ocx = o.Left + o.Width / 2.0;
                    double ocy = o.Top + o.Height / 2.0;

                    if (Math.Abs(acy - ocy) >= Math.Abs(acx - ocx))
                    {
                        // Vertical separation preferred
                        if (ocy < acy)
                        {
                            // Neighbor above: pull our top down, not past core.Top
                            int limit = Math.Min(core.Top, o.Bottom);
                            top = Math.Max(top, limit);
                        }
                        else
                        {
                            int limit = Math.Max(core.Bottom, o.Top);
                            bottom = Math.Min(bottom, limit);
                        }
                    }
                    else
                    {
                        if (ocx < acx)
                        {
                            int limit = Math.Min(core.Left, o.Right);
                            left = Math.Max(left, limit);
                        }
                        else
                        {
                            int limit = Math.Max(core.Right, o.Left);
                            right = Math.Min(right, limit);
                        }
                    }
                }
            }

            // Core floor
            left = Math.Min(left, core.Left);
            top = Math.Min(top, core.Top);
            right = Math.Max(right, core.Right);
            bottom = Math.Max(bottom, core.Bottom);

            left = Math.Clamp(left, 0, Math.Max(0, capW - 1));
            top = Math.Clamp(top, 0, Math.Max(0, capH - 1));
            right = Math.Clamp(right, left + 1, capW);
            bottom = Math.Clamp(bottom, top + 1, capH);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        /// <summary>
        /// Clone <paramref name="capture"/> and draw numbered green boxes for each region
        /// (same look as <c>last_regions.png</c>). Caller disposes the returned bitmap.
        /// </summary>
        private static Bitmap BuildRegionsOverlayBitmap(
            Bitmap capture,
            List<DetectedTextRegion> regions)
        {
            var overlay = (Bitmap)capture.Clone();
            using (var g = Graphics.FromImage(overlay))
            using (var pen = new Pen(Color.LimeGreen, 2))
            using (var font = new Font("Segoe UI", 12, FontStyle.Bold))
            using (var bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
            using (var fg = new SolidBrush(Color.Lime))
            {
                for (int i = 0; i < regions.Count; i++)
                {
                    var r = regions[i].Bounds;
                    g.DrawRectangle(pen, r);
                    string label = (i + 1).ToString();
                    var size = g.MeasureString(label, font);
                    var tag = new RectangleF(
                        r.X,
                        Math.Max(0, r.Y - size.Height - 2),
                        size.Width + 4,
                        size.Height + 2);
                    g.FillRectangle(bg, tag);
                    g.DrawString(label, font, fg, tag.X + 2, tag.Y);
                }
            }
            return overlay;
        }

        /// <summary>
        /// Numbered green boxes for Analytics / last_regions.png.
        /// Boxes match Balloons preview: post-grow cores expanded by live Crop pad
        /// (neighbor-clamped). Base should be detect view (fog when on).
        /// </summary>
        private void SaveRegionDebugOverlay(Bitmap capture, List<DetectedTextRegion> regions)
        {
            try
            {
                if (capture == null || regions == null)
                    return;

                // Always use settings pad (not ActiveCropPadPx override) so Analytics shows
                // the same solid crop boxes as Balloons even when Speak overrides pad to 0.
                int pad = Math.Max(0, SpeakRunSettings.GetComicRegionPadding());
                var boxes = ExpandRegionsByCropPad(
                    regions, capture.Width, capture.Height, pad);

                using var overlay = BuildRegionsOverlayBitmap(capture, boxes);
                // WinOCR detect view only — not Local-LLM VL input (see poi_guide / llm_island_*).
                CaptureAnalyticsImage(
                    "regions",
                    "WinOCR detect boxes (fog when on; not VL input)",
                    overlay);
                if (ActiveHeavyDebugImages)
                {
                    EnsureDebugFolder();
                    overlay.Save(Path.Combine(DebugFolder, "last_regions.png"), ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WinOCR] overlay save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// GDI Bitmap → WinRT SoftwareBitmap (BGRA8) for Windows.Media.Ocr.
        /// </summary>
        private static Task<SoftwareBitmap?> BitmapToSoftwareBitmapAsync(Bitmap bitmap)
            => BalloonOcrDetect.ToSoftwareBitmapAsync(bitmap);

        /// <summary>
        /// Full-frame prep for Kobold just before the API call.
        /// Default: native clone (caller already letterbox/upscale/gray or full tone).
        /// Optional scale+sharpen when <see cref="EnableFullFrameScaleAndSharpen"/>.
        /// </summary>
        private static Bitmap PrepareForLocalLlmOcr(Bitmap source)
        {
            if (!EnableFullFrameScaleAndSharpen)
                return (Bitmap)source.Clone();

            return ScaleToFitAndSharpen(
                source,
                OcrTargetWidth,
                OcrTargetHeight,
                LightSharpenAmount,
                SharpenPasses,
                upscaleOnly: false,
                maxUpscale: 0);
        }

        /// <summary>
        /// Region crop for Kobold: default is a plain clone of the cut from the
        /// fully prepped tone image (no second upscale/tone — Image prep already
        /// did letterbox/upscale/gray/tone). Optional legacy scale+unsharp when
        /// <see cref="EnableCropScaleAndSharpen"/> is re-enabled.
        /// </summary>
        private static Bitmap PrepareCropForLocalLlmOcr(Bitmap source)
        {
            if (!EnableCropScaleAndSharpen)
                return (Bitmap)source.Clone();

            return ScaleToFitAndSharpen(
                source,
                CropTargetWidth,
                CropTargetHeight,
                CropSharpenAmount,
                CropSharpenPasses,
                upscaleOnly: true,
                maxUpscale: MaxCropUpscale);
        }

        /// <summary>
        /// Fit inside target box (aspect preserved), then N unsharp passes.
        /// When <paramref name="upscaleOnly"/> is true, never shrink - only enlarge
        /// (or clone + sharpen) so large crops stay cheap for the VL model.
        /// <paramref name="maxUpscale"/> &gt; 0 caps enlargement (avoids mushy 5x+ bicubic).
        /// Crop upscales use Lanczos-3 (sharper lettering); full-frame uses GDI bicubic.
        /// </summary>
        private static Bitmap ScaleToFitAndSharpen(
            Bitmap source,
            int targetW,
            int targetH,
            float sharpenAmount,
            int sharpenPasses,
            bool upscaleOnly,
            double maxUpscale)
        {
            int w = source.Width;
            int h = source.Height;
            if (w < 1 || h < 1)
                return (Bitmap)source.Clone();

            double scale = Math.Min((double)targetW / w, (double)targetH / h);
            if (upscaleOnly && scale < 1.0)
                scale = 1.0;
            if (upscaleOnly && maxUpscale > 0 && scale > maxUpscale)
                scale = maxUpscale;

            int tw = Math.Max(1, (int)Math.Round(w * scale));
            int th = Math.Max(1, (int)Math.Round(h * scale));
            if (!upscaleOnly)
            {
                tw = Math.Min(tw, targetW);
                th = Math.Min(th, targetH);
            }

            Bitmap working;
            bool skipSharpen = false;
            if (tw == w && th == h)
            {
                working = (Bitmap)source.Clone();
            }
            else if (upscaleOnly)
            {
                double up = Math.Max((double)tw / w, (double)th / h);
                // Pixel-art / game UI fonts: nearest-neighbor keeps glyphs blocky
                // (Lanczos blurs FF-style text into mush).
                if (up >= 2.2 && LooksLikePixelOrUiText(source))
                {
                    working = ScaleBitmapNearestNeighbor(source, tw, th);
                    skipSharpen = true;
                }
                else
                {
                    // Smooth print / comic lettering: Lanczos-3 progressive
                    working = ScaleBitmapLanczosProgressive(source, tw, th);
                }
            }
            else
            {
                working = ScaleBitmapBicubic(source, tw, th);
            }

            int passes = skipSharpen ? 0 : Math.Max(0, sharpenPasses);
            for (int pass = 0; pass < passes; pass++)
            {
                var sharp = LightUnsharp(working, sharpenAmount);
                working.Dispose();
                working = sharp;
            }

            return working;
        }

        /// <summary>
        /// Heuristic: small high-contrast crops (game dialogs, pixel fonts, UI labels)
        /// prefer nearest-neighbor upscale over Lanczos.
        /// </summary>
        private static bool LooksLikePixelOrUiText(Bitmap source)
        {
            int area = source.Width * source.Height;
            // Large photo-like crops ? smooth scale
            if (area > 120_000)
                return false;
            // Tiny/medium crops at high upscale are usually UI / pixel / hard subtitles
            if (area <= 50_000)
                return true;

            // Sample for limited palette / hard edges
            try
            {
                Bitmap src32 = Ensure32bppArgb(source);
                bool dispose = !ReferenceEquals(src32, source);
                try
                {
                    var data = src32.LockBits(
                        new Rectangle(0, 0, src32.Width, src32.Height),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format32bppArgb);
                    try
                    {
                        unsafe
                        {
                            byte* p0 = (byte*)data.Scan0;
                            int stride = data.Stride;
                            int w = src32.Width;
                            int h = src32.Height;
                            int step = Math.Max(1, Math.Min(w, h) / 32);
                            var colors = new HashSet<int>();
                            int samples = 0;
                            for (int y = 0; y < h; y += step)
                            {
                                byte* row = p0 + y * stride;
                                for (int x = 0; x < w; x += step)
                                {
                                    byte* p = row + x * 4;
                                    // quantize to 4 bits/channel
                                    int key = (p[2] >> 4) << 8 | (p[1] >> 4) << 4 | (p[0] >> 4);
                                    colors.Add(key);
                                    samples++;
                                    if (colors.Count > 48)
                                        return false;
                                }
                            }
                            // Few distinct colors ? UI / pixel art
                            return samples > 0 && colors.Count <= 40;
                        }
                    }
                    finally
                    {
                        src32.UnlockBits(data);
                    }
                }
                finally
                {
                    if (dispose) src32.Dispose();
                }
            }
            catch
            {
                return area <= 60_000;
            }
        }

        /// <summary>GDI nearest-neighbor - best for pixel fonts / hard UI glyphs.</summary>
        private static Bitmap ScaleBitmapNearestNeighbor(Bitmap source, int w, int h)
        {
            var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;
                g.DrawImage(source, new Rectangle(0, 0, w, h));
            }
            return scaled;
        }

        /// <summary>GDI+ high-quality bicubic - fine for full-frame / downscale.</summary>
        private static Bitmap ScaleBitmapBicubic(Bitmap source, int w, int h)
        {
            var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, w, h));
            }
            return scaled;
        }

        /// <summary>
        /// Upscale with progressive =2- Lanczos-3 steps, then a final Lanczos to exact size.
        /// Multi-step keeps edges sharper than one big bicubic jump (comic lettering).
        /// Crops are small, so the extra CPU is cheap vs Kobold latency.
        /// </summary>
        private static Bitmap ScaleBitmapLanczosProgressive(Bitmap source, int destW, int destH)
        {
            if (destW < 1 || destH < 1)
                return (Bitmap)source.Clone();
            if (source.Width == destW && source.Height == destH)
                return (Bitmap)source.Clone();

            Bitmap current = Ensure32bppArgb(source);
            bool disposeCurrent = !ReferenceEquals(current, source);

            try
            {
                // Grow by at most 2- per step until within 2- of target
                while (current.Width * 2 < destW || current.Height * 2 < destH)
                {
                    int nw = Math.Min(destW, Math.Max(current.Width + 1, current.Width * 2));
                    int nh = Math.Min(destH, Math.Max(current.Height + 1, current.Height * 2));
                    // Keep aspect roughly: if one dim already at target, only grow the other
                    if (current.Width >= destW) nw = destW;
                    if (current.Height >= destH) nh = destH;

                    var next = ScaleBitmapLanczos3(current, nw, nh);
                    if (disposeCurrent)
                        current.Dispose();
                    current = next;
                    disposeCurrent = true;
                }

                if (current.Width != destW || current.Height != destH)
                {
                    var final = ScaleBitmapLanczos3(current, destW, destH);
                    if (disposeCurrent)
                        current.Dispose();
                    current = final;
                    disposeCurrent = true;
                }

                if (!disposeCurrent)
                    return (Bitmap)current.Clone();

                // Transfer ownership to caller
                var result = current;
                current = null!;
                disposeCurrent = false;
                return result;
            }
            finally
            {
                if (disposeCurrent)
                    current?.Dispose();
            }
        }

        private static Bitmap Ensure32bppArgb(Bitmap source)
        {
            if (source.PixelFormat == PixelFormat.Format32bppArgb)
                return source;

            var converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(converted))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }
            return converted;
        }

        /// <summary>
        /// Separable Lanczos-3 resize (a=3). Better edge retention than GDI bicubic on text.
        /// </summary>
        private static Bitmap ScaleBitmapLanczos3(Bitmap source, int destW, int destH)
        {
            Bitmap src32 = Ensure32bppArgb(source);
            bool disposeSrc32 = !ReferenceEquals(src32, source);

            try
            {
                int sw = src32.Width;
                int sh = src32.Height;
                if (sw == destW && sh == destH)
                    return (Bitmap)src32.Clone();

                // Horizontal pass ? temp (destW - sh), then vertical ? dest
                Bitmap? temp = null;
                try
                {
                    temp = new Bitmap(destW, sh, PixelFormat.Format32bppArgb);
                    ResampleSeparableLanczos3(src32, temp, horizontal: true);

                    var dest = new Bitmap(destW, destH, PixelFormat.Format32bppArgb);
                    ResampleSeparableLanczos3(temp, dest, horizontal: false);
                    return dest;
                }
                finally
                {
                    temp?.Dispose();
                }
            }
            finally
            {
                if (disposeSrc32)
                    src32.Dispose();
            }
        }

        private const int LanczosA = 3;

        private static void ResampleSeparableLanczos3(Bitmap src, Bitmap dst, bool horizontal)
        {
            int sw = src.Width;
            int sh = src.Height;
            int dw = dst.Width;
            int dh = dst.Height;

            var srcRect = new Rectangle(0, 0, sw, sh);
            var dstRect = new Rectangle(0, 0, dw, dh);
            var srcData = src.LockBits(srcRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dstData = dst.LockBits(dstRect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    byte* s0 = (byte*)srcData.Scan0;
                    byte* d0 = (byte*)dstData.Scan0;
                    int sStride = srcData.Stride;
                    int dStride = dstData.Stride;

                    if (horizontal)
                    {
                        // dst is destW - srcH
                        double scale = (double)sw / dw;
                        for (int y = 0; y < sh; y++)
                        {
                            byte* sRow = s0 + y * sStride;
                            byte* dRow = d0 + y * dStride;
                            for (int x = 0; x < dw; x++)
                            {
                                double srcX = (x + 0.5) * scale - 0.5;
                                SampleLanczos1D(sRow, sw, srcX, out byte b, out byte g, out byte r, out byte a);
                                int di = x * 4;
                                dRow[di] = b;
                                dRow[di + 1] = g;
                                dRow[di + 2] = r;
                                dRow[di + 3] = a;
                            }
                        }
                    }
                    else
                    {
                        // dst is destW - destH; src is destW - srcH
                        double scale = (double)sh / dh;
                        for (int y = 0; y < dh; y++)
                        {
                            double srcY = (y + 0.5) * scale - 0.5;
                            byte* dRow = d0 + y * dStride;
                            for (int x = 0; x < dw; x++)
                            {
                                SampleLanczos1DColumn(s0, sStride, sh, x, srcY, out byte b, out byte g, out byte r, out byte a);
                                int di = x * 4;
                                dRow[di] = b;
                                dRow[di + 1] = g;
                                dRow[di + 2] = r;
                                dRow[di + 3] = a;
                            }
                        }
                    }
                }
            }
            finally
            {
                src.UnlockBits(srcData);
                dst.UnlockBits(dstData);
            }
        }

        private static unsafe void SampleLanczos1D(
            byte* row, int width, double srcX,
            out byte b, out byte g, out byte r, out byte a)
        {
            int x0 = (int)Math.Floor(srcX) - LanczosA + 1;
            int x1 = (int)Math.Floor(srcX) + LanczosA;
            double sumB = 0, sumG = 0, sumR = 0, sumA = 0, sumW = 0;

            for (int xi = x0; xi <= x1; xi++)
            {
                int xc = xi < 0 ? 0 : (xi >= width ? width - 1 : xi);
                double w = LanczosKernel(srcX - xi);
                if (w == 0) continue;
                byte* p = row + xc * 4;
                sumB += p[0] * w;
                sumG += p[1] * w;
                sumR += p[2] * w;
                sumA += p[3] * w;
                sumW += w;
            }

            if (sumW <= 1e-8)
            {
                int xc = Math.Clamp((int)Math.Round(srcX), 0, width - 1);
                byte* p = row + xc * 4;
                b = p[0]; g = p[1]; r = p[2]; a = p[3];
                return;
            }

            b = ClampByte(sumB / sumW);
            g = ClampByte(sumG / sumW);
            r = ClampByte(sumR / sumW);
            a = ClampByte(sumA / sumW);
        }

        private static unsafe void SampleLanczos1DColumn(
            byte* basePtr, int stride, int height, int x, double srcY,
            out byte b, out byte g, out byte r, out byte a)
        {
            int y0 = (int)Math.Floor(srcY) - LanczosA + 1;
            int y1 = (int)Math.Floor(srcY) + LanczosA;
            double sumB = 0, sumG = 0, sumR = 0, sumA = 0, sumW = 0;

            for (int yi = y0; yi <= y1; yi++)
            {
                int yc = yi < 0 ? 0 : (yi >= height ? height - 1 : yi);
                double w = LanczosKernel(srcY - yi);
                if (w == 0) continue;
                byte* p = basePtr + yc * stride + x * 4;
                sumB += p[0] * w;
                sumG += p[1] * w;
                sumR += p[2] * w;
                sumA += p[3] * w;
                sumW += w;
            }

            if (sumW <= 1e-8)
            {
                int yc = Math.Clamp((int)Math.Round(srcY), 0, height - 1);
                byte* p = basePtr + yc * stride + x * 4;
                b = p[0]; g = p[1]; r = p[2]; a = p[3];
                return;
            }

            b = ClampByte(sumB / sumW);
            g = ClampByte(sumG / sumW);
            r = ClampByte(sumR / sumW);
            a = ClampByte(sumA / sumW);
        }

        private static double LanczosKernel(double x)
        {
            x = Math.Abs(x);
            if (x < 1e-8) return 1.0;
            if (x >= LanczosA) return 0.0;
            double pix = Math.PI * x;
            // sinc(x) * sinc(x/a)
            return (Math.Sin(pix) / pix) * (Math.Sin(pix / LanczosA) / (pix / LanczosA));
        }

        private static byte ClampByte(double v) =>
            (byte)(v < 0 ? 0 : (v > 255 ? 255 : (int)Math.Round(v)));

        /// <summary>
        /// Mild unsharp: result = src + amount * (src - 3x3 box blur). Cheap; call twice for 2-pass.
        /// </summary>
        private static Bitmap LightUnsharp(Bitmap source, float amount)
        {
            if (source.Width < 3 || source.Height < 3)
                return (Bitmap)source.Clone();

            // Settings → Image sharpen amount is 0.00–2.00. Honor zero as off;
            // do not force a 0.1 floor (that made tiny sliders look like medium sharpen).
            if (amount <= 0.001f)
                return (Bitmap)source.Clone();
            amount = Math.Clamp(amount, 0.01f, 2.0f);
            var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, source.Width, source.Height);

            var srcData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    byte* s0 = (byte*)srcData.Scan0;
                    byte* d0 = (byte*)dstData.Scan0;
                    int sStride = srcData.Stride;
                    int dStride = dstData.Stride;
                    int w = source.Width;
                    int h = source.Height;

                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int sumB = 0, sumG = 0, sumR = 0, count = 0;
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                int yy = Math.Clamp(y + dy, 0, h - 1);
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int xx = Math.Clamp(x + dx, 0, w - 1);
                                    byte* p = s0 + yy * sStride + xx * 4;
                                    sumB += p[0];
                                    sumG += p[1];
                                    sumR += p[2];
                                    count++;
                                }
                            }

                            byte* s = s0 + y * sStride + x * 4;
                            byte* d = d0 + y * dStride + x * 4;
                            int bBlur = sumB / count;
                            int gBlur = sumG / count;
                            int rBlur = sumR / count;

                            d[0] = (byte)Math.Clamp((int)Math.Round(s[0] + amount * (s[0] - bBlur)), 0, 255);
                            d[1] = (byte)Math.Clamp((int)Math.Round(s[1] + amount * (s[1] - gBlur)), 0, 255);
                            d[2] = (byte)Math.Clamp((int)Math.Round(s[2] + amount * (s[2] - rBlur)), 0, 255);
                            d[3] = s[3];
                        }
                    }
                }
            }
            finally
            {
                source.UnlockBits(srcData);
                result.UnlockBits(dstData);
            }

            return result;
        }

        private async Task<string> ExtractTextWithLocalLlmAsync(
            Bitmap bmp,
            string? promptOverride = null,
            int maxTokens = CropMaxTokens,
            double temperature = KoboldPrimaryTemperature)
        {
            Bitmap? scaledOwned = null;
            try
            {
                // Final stage: optional long-edge cap from Image tab (default 640).
                // Prep / detect may stay at 900; only the Local-LLM payload is capped.
                Bitmap send = bmp;
                int maxEdge = ActiveLlmSendMaxLongEdge;
                bool didScale = maxEdge > 0 &&
                    Math.Max(bmp.Width, bmp.Height) > maxEdge;
                if (didScale)
                {
                    scaledOwned = ScaleDownToMaxLongEdge(bmp, maxEdge);
                    send = scaledOwned;
                    Debug.WriteLine(
                        $"[LocalLlm] send scale {bmp.Width}x{bmp.Height} → " +
                        $"{send.Width}x{send.Height} (max long-edge {maxEdge})");
                }

                // Analytics + debug: exact pixels Local-LLM receives (not pre-scale tone).
                string scaleNote = didScale
                    ? $" (from {bmp.Width}x{bmp.Height})"
                    : (maxEdge > 0 ? "" : " (downscale off)");
                try
                {
                    CaptureAnalyticsImage(
                        "llm_send",
                        $"Local-LLM send {send.Width}x{send.Height}{scaleNote}",
                        send);
                }
                catch { /* ignore */ }

                if (ActiveAnyDebugArtifacts)
                {
                    try
                    {
                        EnsureDebugFolder();
                        // Exact API payload (post optional cap). Overwrites POI pre-scale aliases.
                        send.Save(
                            Path.Combine(DebugFolder, "last_llm_send.png"),
                            ImageFormat.Png);
                        send.Save(
                            Path.Combine(DebugFolder, "last_full_prep.png"),
                            ImageFormat.Png);
                        send.Save(
                            Path.Combine(DebugFolder, "last_poi_vl_input.png"),
                            ImageFormat.Png);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[LocalLlm] send debug save: {ex.Message}");
                    }
                }

                // Crops / small images: PNG (no DCT mush on lettering).
                // Full-frame / large: JPEG q95 (bandwidth without much quality loss).
                bool usePng = (long)send.Width * send.Height <= KoboldPngMaxPixels;
                string mime;
                byte[] imageBytes;
                using (var clone = (Bitmap)send.Clone())
                using (var ms = new MemoryStream())
                {
                    if (usePng)
                    {
                        clone.Save(ms, ImageFormat.Png);
                        mime = "image/png";
                    }
                    else
                    {
                        SaveJpeg(clone, ms, KoboldFullFrameJpegQuality);
                        mime = "image/jpeg";
                    }
                    imageBytes = ms.ToArray();
                }
                var base64 = Convert.ToBase64String(imageBytes);

                string prompt = promptOverride ?? LocalLlmTaskPrompt;
                string dataUrl = $"data:{mime};base64,{base64}";

                // Build content with JsonNode only — never anonymous types.
                // Prefer JsonObject over anonymous types for stable vision JSON
                // (empty Kobold → WinOCR failsafe → fast/bad speech).
                return await LocalLlmClient.ChatAsync(
                    LocalLlmClient.BuildUserContent(dataUrl, prompt),
                    maxTokens,
                    temperature).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalLlm] failed: {ex.Message}");
                return "";
            }
            finally
            {
                try { scaledOwned?.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Downscale so the long edge is at most <paramref name="maxLongEdge"/>
        /// (aspect preserved). No-op clone if already within limit. Caller owns result.
        /// </summary>
        private static Bitmap ScaleDownToMaxLongEdge(Bitmap source, int maxLongEdge)
        {
            int w = source.Width;
            int h = source.Height;
            if (w < 1 || h < 1)
                return (Bitmap)source.Clone();

            int longEdge = Math.Max(w, h);
            if (longEdge <= maxLongEdge)
                return (Bitmap)source.Clone();

            double scale = (double)maxLongEdge / longEdge;
            int tw = Math.Max(1, (int)Math.Round(w * scale));
            int th = Math.Max(1, (int)Math.Round(h * scale));
            // Guard float rounding above the cap.
            if (Math.Max(tw, th) > maxLongEdge)
            {
                if (tw >= th)
                {
                    tw = maxLongEdge;
                    th = Math.Max(1, (int)Math.Round(h * ((double)maxLongEdge / w)));
                }
                else
                {
                    th = maxLongEdge;
                    tw = Math.Max(1, (int)Math.Round(w * ((double)maxLongEdge / h)));
                }
            }

            var result = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, tw, th));
            }
            return result;
        }

        /// <summary>Smoke: long-edge downscale for Local-LLM send (does not upscale).</summary>
        public static Size SmokeKoboldSendScaleSize(int width, int height, int maxLongEdge = 640)
        {
            if (width < 1 || height < 1)
                return Size.Empty;
            int longEdge = Math.Max(width, height);
            if (longEdge <= maxLongEdge)
                return new Size(width, height);
            double scale = (double)maxLongEdge / longEdge;
            return new Size(
                Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
        }

        private static void SaveJpeg(Bitmap bmp, Stream stream, long quality)
        {
            var encoder = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

            if (encoder == null)
            {
                bmp.Save(stream, ImageFormat.Png);
                return;
            }

            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            bmp.Save(stream, encoder, ep);
        }

        /// <summary>
        /// Smoke / post-obfuscation check: payload must contain required wire keys.
        /// </summary>
        public static bool SmokeVerifyKoboldJsonShape(out string sampleJson) =>
            LocalLlmClient.SmokeVerifyJsonShape(out sampleJson);

        /// <summary>
        /// Smoke helper: run the full pre-TTS speech cleaner
        /// (keep contractions, expand abbrevs/titles, punctuation → blank-line breaks).
        /// </summary>
        public static string SmokeCleanForSpeech(string input, bool comicBook = true)
        {
            // Thread-local override only — never mutates SpeakRunSettings.GetComicBook()
            // (live speak may be reading mode flags concurrently).
            return SpeechCleaner.CleanForSpeech(input, comicBook);
        }

        /// <summary>Smoke helper: count speak units (same as TTS splitter).</summary>
        public static int SmokeSpeakUnitCount(string cleaned) =>
            SpeechCleaner.SplitSpeakBlocks(cleaned ?? "").Count;

        /// <summary>Smoke helper: speak unit texts (same as TTS splitter).</summary>
        public static List<string> SmokeSpeakUnits(string cleaned) =>
            SpeechCleaner.SplitSpeakBlocks(cleaned ?? "");

        /// <summary>
        /// Smoke helper: full pre-TTS expand path (CleanForSpeech already applied
        /// or raw OCR → clean → ExpandToSpeakPieces). Mirrors production
        /// <see cref="ExpandToSpeakPieces"/> filtering of short units.
        /// </summary>
        public static List<string> SmokeExpandSpeakUnits(IEnumerable<string> parts) =>
            SpeechCleaner.ExpandToSpeakPieces(parts).Select(p => p.Text).ToList();

        /// <summary>
        /// Smoke helper: whether cleaned OCR would be kept (not treated as empty /
        /// refusal). Used to guard short dialogue like "no" after "No!" cleaning.
        /// </summary>
        public static bool SmokeIsUsableOcrText(string? text) =>
            !SpeechCleaner.IsUnusableOcrText(text);

        /// <summary>
        /// Smoke helper: crop Kobold under-read vs WinOCR word count.
        /// </summary>
        public static bool SmokeKoboldUnderReadsWinOcr(
            string? kobold, string? winOcr) =>
            ComicRegionGeometry.KoboldUnderReadsWinOcr(kobold, winOcr);

        /// <summary>
        /// Smoke helper: Western comic reading order on geometry only
        /// (L→R within row, top→bottom bands). Returns ordered copies of
        /// <paramref name="boxes"/>.
        /// </summary>
        public static List<Rectangle> SmokeSortComicReadingOrder(
            IEnumerable<Rectangle> boxes)
        {
            var regions = (boxes ?? Array.Empty<Rectangle>())
                .Select(b => new DetectedTextRegion { Bounds = b, WinOcrText = "" })
                .ToList();
            return SortComicReadingOrderRegions(regions)
                .Select(r => r.Bounds)
                .ToList();
        }

        /// <summary>
        /// Smoke helper: union any islands whose effective boxes overlap (grow
        /// bounds + optional Crop pad). Mirrors Settings → Balloons
        /// "Merge overlapping islands". When <paramref name="cropPadPx"/> is null,
        /// uses live <see cref="AppSettings.ComicRegionPadding"/>.
        /// </summary>
        public static List<Rectangle> SmokeMergeOverlappingIslands(
            IEnumerable<Rectangle> boxes,
            int capW = 2000,
            int capH = 2000,
            int? cropPadPx = null)
        {
            var regions = (boxes ?? Array.Empty<Rectangle>())
                .Select(b => new DetectedTextRegion { Bounds = b, WinOcrText = "" })
                .ToList();
            return MergeOverlappingIslands(regions, capW, capH, cropPadPx)
                .Select(r => r.Bounds)
                .ToList();
        }

        /// <summary>
        /// Expand post-grow cores by Crop pad (neighbor-clamped) — same rect Speak
        /// crops. Balloons seeds solid green boxes with this so the preview is the snap.
        /// </summary>
        public static List<Rectangle> ExpandRegionsWithCropPad(
            IEnumerable<Rectangle> cores,
            int capW,
            int capH,
            int? padPx = null)
        {
            var regions = (cores ?? Array.Empty<Rectangle>())
                .Select(b => new DetectedTextRegion { Bounds = b, WinOcrText = "" })
                .ToList();
            int pad = Math.Max(0, padPx ?? SpeakRunSettings.GetComicRegionPadding());
            return ExpandRegionsByCropPad(regions, capW, capH, pad)
                .Select(r => r.Bounds)
                .ToList();
        }

        /// <summary>
        /// Same crop rectangle Local-LLM uses for one island: core + Crop pad,
        /// neighbor-clamped (does not invade other cores).
        /// </summary>
        public static Rectangle ComputeSpeakCropRect(
            Rectangle core,
            int padPx,
            int capW,
            int capH,
            IReadOnlyList<Rectangle>? neighborCores)
            => ComputeClampedCropRect(core, padPx, capW, capH, neighborCores);

        /// <summary>
        /// Smoke helper: bitmap live OCR would read (no fog) — same prep for
        /// Default and ComicBook (tone end). Matches Image preview full-pipe.
        /// </summary>
        public static Bitmap SmokeBuildLiveOcrInput(Bitmap rawSnap)
        {
            if (rawSnap == null)
                throw new ArgumentNullException(nameof(rawSnap));
            AppSettings.Current.NormalizeImagePrepSettings();
            using var stages = BuildImagePrepStages(
                rawSnap, buildTone: true, detail: null);
            return new Bitmap(stages.LiveOcrInput);
        }

        /// <summary>
        /// Smoke helper: true when two bitmaps match size and all ARGB pixels.
        /// </summary>
        public static bool SmokeBitmapsPixelEqual(
            Bitmap a, Bitmap b, out string detail)
        {
            detail = "";
            if (a == null || b == null)
            {
                detail = "null bitmap";
                return false;
            }
            if (a.Width != b.Width || a.Height != b.Height)
            {
                detail = $"{a.Width}x{a.Height} vs {b.Width}x{b.Height}";
                return false;
            }

            Bitmap a32 = Ensure32bppArgb(a);
            Bitmap b32 = Ensure32bppArgb(b);
            bool disposeA = !ReferenceEquals(a32, a);
            bool disposeB = !ReferenceEquals(b32, b);
            try
            {
                var ra = new Rectangle(0, 0, a32.Width, a32.Height);
                var da = a32.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var db = b32.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* pa = (byte*)da.Scan0;
                        byte* pb = (byte*)db.Scan0;
                        int strideA = da.Stride;
                        int strideB = db.Stride;
                        int w = a32.Width;
                        int h = a32.Height;
                        for (int y = 0; y < h; y++)
                        {
                            byte* rowA = pa + y * strideA;
                            byte* rowB = pb + y * strideB;
                            for (int x = 0; x < w; x++)
                            {
                                int i = x * 4;
                                if (rowA[i] != rowB[i] ||
                                    rowA[i + 1] != rowB[i + 1] ||
                                    rowA[i + 2] != rowB[i + 2] ||
                                    rowA[i + 3] != rowB[i + 3])
                                {
                                    detail =
                                        $"pixel mismatch @({x},{y}) " +
                                        $"A={rowA[i + 2]},{rowA[i + 1]},{rowA[i]},{rowA[i + 3]} " +
                                        $"B={rowB[i + 2]},{rowB[i + 1]},{rowB[i]},{rowB[i + 3]}";
                                    return false;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    a32.UnlockBits(da);
                    b32.UnlockBits(db);
                }
                return true;
            }
            finally
            {
                if (disposeA) try { a32.Dispose(); } catch { /* ignore */ }
                if (disposeB) try { b32.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Smoke helper: dead-island filter used after WinOCR detect (drops logos
        /// on non-balloon art, junk OCR, empty small boxes).
        /// </summary>
        public static List<(Rectangle Bounds, string Text)> SmokeFilterDeadDetectRegions(
            Bitmap capture,
            IEnumerable<(Rectangle Bounds, string Text)> regions)
        {
            if (capture == null)
                throw new ArgumentNullException(nameof(capture));
            var list = (regions ?? Array.Empty<(Rectangle, string)>())
                .Select(r => new DetectedTextRegion
                {
                    Bounds = r.Bounds,
                    WinOcrText = r.Text ?? ""
                })
                .ToList();
            var detail = new StringBuilder();
            return FilterDeadDetectRegions(list, capture, detail)
                .Select(r => (r.Bounds, r.WinOcrText ?? ""))
                .ToList();
        }

        /// <summary>
        /// Smoke helper: light speech-plate heuristic used by dead-island / orphan
        /// filters.
        /// </summary>
        public static bool SmokeLooksLikeSpeechBalloonFill(
            Bitmap capture, Rectangle bounds) =>
            LooksLikeSpeechBalloonFill(capture, bounds);

        /// <summary>
        /// Smoke helper: live snap publishes full-res last capture for Balloons.
        /// </summary>
        public static void SmokePublishLastCapture(Bitmap rawSnap) =>
            DevCaptureCache.PublishLastCapture(rawSnap);

        /// <summary>
        /// Smoke helper: load full-res last capture (never Analytics long-edge thumb).
        /// Caller owns the bitmap.
        /// </summary>
        public static Bitmap? SmokeTryLoadLastOcrCapture() =>
            DevCaptureCache.TryLoadLastOcrCapture();

        /// <summary>
        /// Smoke helper: clear shared still cache (isolate last-capture tests).
        /// </summary>
        public static void SmokeClearDevCaptureCache() =>
            DevCaptureCache.Clear();

        /// <summary>
        /// Pre-TTS: drop echo units (VL plain + HTML re-copy) then glue fragments.
        /// Shared by full-frame-style paths and POI speak-now so no path skips it.
        /// </summary>
        private static List<SpeechCleaner.SpeakPiece> ApplySpeakDedupeCoalesce(
            List<SpeechCleaner.SpeakPiece> speakPieces,
            StringBuilder detail,
            string tag)
        {
            if (speakPieces == null || speakPieces.Count < 2)
                return speakPieces ?? new List<SpeechCleaner.SpeakPiece>();

            int beforeDedup = speakPieces.Count;
            speakPieces = SpeechCleaner.DedupeSpeakPiecesForTts(speakPieces, detail);
            if (speakPieces.Count != beforeDedup)
            {
                detail.AppendLine(
                    $"  speak-dedupe [{tag}] {beforeDedup} → {speakPieces.Count}");
            }

            if (speakPieces.Count < 2)
                return speakPieces;

            int beforeCoal = speakPieces.Count;
            speakPieces = SpeechCleaner.CoalesceFragmentSpeakPieces(speakPieces, detail);
            if (speakPieces.Count != beforeCoal)
            {
                detail.AppendLine(
                    $"  speak-coalesce [{tag}] {beforeCoal} → {speakPieces.Count}");
            }

            return speakPieces;
        }

        /// <summary>
        /// Smoke helper: pause-after ms list for each unit except the last
        /// (length = unitCount - 1). Empty when fewer than 2 units.
        /// </summary>
        public static List<int> SmokePauseAfterMsList(string cleaned)
        {
            var pieces = SpeechCleaner.SplitSpeakPieces(cleaned ?? "");
            if (pieces.Count < 2)
                return new List<int>();
            return pieces.Take(pieces.Count - 1).Select(p => p.PauseAfterMs).ToList();
        }

        /// <summary>
        /// Smoke helper: run fragment coalesce (must not re-glue finished short
        /// sentences like "tell you?" that keep their terminal punct).
        /// </summary>
        public static List<string> SmokeCoalesceSpeakUnits(IEnumerable<string> units) =>
            SpeechCleaner.CoalesceFragmentSpeakUnits(
                units?.ToList() ?? new List<string>(),
                new StringBuilder());

        /// <summary>
        /// Smoke helper: pre-TTS unit dedupe (must keep short balloons that only
        /// reuse a word from a longer earlier unit, e.g. "Really?" after
        /// "it's really good to see you").
        /// </summary>
        public static List<string> SmokeDedupeSpeakUnits(IEnumerable<string> units) =>
            SpeechCleaner.DedupeSpeakUnitsForTts(
                units?.ToList() ?? new List<string>(),
                new StringBuilder());

        // StripSpottingGeometry / StripMarkdown: moved to SpeechTextRulesCatalog
        // (Settings → Speech → Text rules, Noise stage).

        /// <summary>Clamp a screen rect to the virtual desktop; empty if no overlap.</summary>
        private static Rectangle ClampToVirtualScreen(Rectangle r)
        {
            if (r.Width < 1 || r.Height < 1)
                return Rectangle.Empty;
            var vs = SystemInformation.VirtualScreen;
            var hit = Rectangle.Intersect(r, vs);
            if (hit.Width < 1 || hit.Height < 1)
                return Rectangle.Empty;
            return hit;
        }

        private static Bitmap CreateRectBitmap(Rectangle r)
        {
            r = ClampToVirtualScreen(r);
            if (r.Width < 1 || r.Height < 1)
                return new Bitmap(1, 1, PixelFormat.Format32bppArgb);

            var b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            try
            {
                using var g = Graphics.FromImage(b);
                g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Capture] CopyFromScreen failed: {ex.Message}");
                using var g = Graphics.FromImage(b);
                g.Clear(Color.White);
            }
            return b;
        }

        private static Bitmap CreateMaskedBitmapFromLasso(List<Point> points)
        {
            if (points == null || points.Count < 3)
                return new Bitmap(1, 1, PixelFormat.Format32bppArgb);

            int minX = points.Min(p => p.X);
            int minY = points.Min(p => p.Y);
            int maxX = points.Max(p => p.X);
            int maxY = points.Max(p => p.Y);
            // Inclusive pixel span so the outer edge of the lasso is not clipped
            int w = Math.Max(1, maxX - minX + 1);
            int h = Math.Max(1, maxY - minY + 1);

            var captureRect = ClampToVirtualScreen(new Rectangle(minX, minY, w, h));
            if (captureRect.IsEmpty)
                return new Bitmap(1, 1, PixelFormat.Format32bppArgb);

            // Re-base points if clamp moved the origin
            int originX = captureRect.X;
            int originY = captureRect.Y;
            w = captureRect.Width;
            h = captureRect.Height;

            using var full = CreateRectBitmap(captureRect);

            // White matte outside the lasso - black made the VL model often return empty
            var masked = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(masked))
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using var path = new GraphicsPath();
                var relPts = points
                    .Select(p => new Point(p.X - originX, p.Y - originY))
                    .Where(p => p.X >= -2 && p.Y >= -2 && p.X <= w + 2 && p.Y <= h + 2)
                    .ToArray();

                if (relPts.Length >= 3)
                {
                    path.AddPolygon(relPts);
                    // SetClip + DrawImage is more stable than TextureBrush across GDI+ builds
                    g.SetClip(path);
                    g.DrawImage(full, Point.Empty);
                    g.ResetClip();
                }
            }

            return masked;
        }

        private static Bitmap CreateEllipseMaskedBitmap(Rectangle bounds)
        {
            bounds = ClampToVirtualScreen(bounds);
            if (bounds.Width < 5 || bounds.Height < 5)
                return CreateRectBitmap(bounds);

            using var full = CreateRectBitmap(bounds);

            // White matte outside the oval
            var masked = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(masked))
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using var path = new GraphicsPath();
                // Full bounds — matches rect capture and painted overlay ellipse
                // (Width-1/Height-1 clipped dialogue on the oval rim).
                path.AddEllipse(0, 0, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
                g.SetClip(path);
                g.DrawImage(full, Point.Empty);
                g.ResetClip();
            }

            return masked;
        }

        private async Task SpeakWithSystemAsync(string text, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                // Live Start() hosts only: preempted by newer Start/Stop — do not TTS.
                // Balloons SpeakComicFromBitmap leaves _speakGeneration at 0 (skip).
                if (_speakGeneration != 0 && !IsLiveSpeakCurrent)
                    throw new OperationCanceledException();
                token.ThrowIfCancellationRequested();

                // Defense in depth: multi-unit cleaned strings get typed pauses.
                var pieces = SpeechCleaner.SplitSpeakPieces(text);
                if (pieces.Count > 1)
                {
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        string u = pieces[i].Text;
                        if (u.Length == 0) continue;
                        await SpeakOneUnitAsync(u, token).ConfigureAwait(false);
                        int pause = pieces[i].PauseAfterMs;
                        if (pause > 0)
                            await Task.Delay(pause, token).ConfigureAwait(false);
                    }
                    return;
                }

                text = Regex.Replace(
                    pieces.Count == 1 ? pieces[0].Text : text, @"\s+", " ").Trim();
                if (string.IsNullOrWhiteSpace(text)) return;

                await SpeakOneUnitAsync(text, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemTTS] Error: {ex}");
            }
        }

        private async Task SpeakOneUnitAsync(string text, CancellationToken token)
        {
            // Prefer frozen speak-run voice knobs; normalize live only when no snap.
            if (SpeakRunSettings.Active == null)
                AppSettings.Current.NormalizeVoiceSettings();
            if (SpeakRunSettings.GetIsSapiTtsEngine())
            {
                await SpeakWithSapiAsync(text, token).ConfigureAwait(false);
                return;
            }

            await SpeakWithWinRtAsync(text, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Forced TTS language for OCR dialogue / announcements. Comic text is
        /// scrubbed to Latin; engines should pronounce as English regardless of
        /// OS UI culture. Voice selection also prefers an English pack when possible.
        /// </summary>
        public const string TtsForcedLanguage = "en-US";

        /// <summary>UWP / OneCore path (Windows.Media.SpeechSynthesis).</summary>
        private async Task SpeakWithWinRtAsync(string text, CancellationToken token)
        {
            // Re-apply each speak so live VOICE panel changes take effect mid-session.
            ApplyVoiceSettings(_synth);

            // Always SSML with xml:lang=en-US so OneCore uses English pronunciation
            // rules even when the system default culture is not English.
            // Multi-sentence units (Default + Comic): short SSML breaks when custom
            // pause encoding did not already split the unit. Balloon/comma pauses
            // stay on Task.Delay (Voice tab ms).
            int breakMs = SentenceBreakMs > 0 && LooksMultiSentence(text)
                ? SentenceBreakMs
                : 0;
            string ssml = BuildSpeakSsml(text, breakMs, TtsForcedLanguage);
            WinSpeech.SpeechSynthesisStream stream =
                await _synth.SynthesizeSsmlToStreamAsync(ssml).AsTask(token);

            using (stream)
            {
                if (token.IsCancellationRequested) return;

                var tcs = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                void OnEnded(MediaPlayer sender, object args) => tcs.TrySetResult(null);
                void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs e) =>
                    tcs.TrySetException(new Exception(e.Error.ToString()));

                _player.MediaEnded += OnEnded;
                _player.MediaFailed += OnFailed;

                try
                {
                    _player.Source = MediaSource.CreateFromStream(
                        stream, stream.ContentType);
                    _player.Play();

                    using (token.Register(() =>
                    {
                        try { _player.Pause(); _player.Source = null; } catch { }
                        tcs.TrySetCanceled();
                    }))
                    {
                        await tcs.Task;
                    }
                }
                finally
                {
                    _player.MediaEnded -= OnEnded;
                    _player.MediaFailed -= OnFailed;
                }
            }
        }

        /// <summary>SAPI 5 path (System.Speech) — includes adapter-registered natural voices.</summary>
        private async Task SpeakWithSapiAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            SapiSpeech.SpeechSynthesizer synth;
            lock (_sapiLock)
            {
                _sapiSynth ??= new SapiSpeech.SpeechSynthesizer();
                synth = _sapiSynth;
            }

            ApplySapiVoiceSettings(synth);

            var tcs = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnCompleted(object? sender, SapiSpeech.SpeakCompletedEventArgs e)
            {
                if (e.Cancelled)
                    tcs.TrySetCanceled();
                else if (e.Error != null)
                    tcs.TrySetException(e.Error);
                else
                    tcs.TrySetResult(null);
            }

            synth.SpeakCompleted += OnCompleted;
            try
            {
                using (token.Register(() =>
                {
                    try { synth.SpeakAsyncCancelAll(); } catch { /* ignore */ }
                    tcs.TrySetCanceled();
                }))
                {
                    // Always SSML with forced en-US so SAPI does not follow OS UI culture.
                    // Same multi-sentence SSML break as WinRT (Default + Comic).
                    int breakMs =
                        SentenceBreakMs > 0 && LooksMultiSentence(text)
                            ? SentenceBreakMs
                            : 0;
                    string ssml = BuildSapiSpeakSsml(
                        text,
                        breakMs,
                        TtsForcedLanguage,
                        SpeakRunSettings.GetVoicePitch());
                    synth.SpeakSsmlAsync(ssml);

                    await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                synth.SpeakCompleted -= OnCompleted;
            }
        }

        /// <summary>Apply rate/volume/voice to a SAPI synthesizer (speak snap or live settings).</summary>
        public static void ApplySapiVoiceSettings(SapiSpeech.SpeechSynthesizer synth)
        {
            if (synth == null) return;

            if (SpeakRunSettings.Active == null)
                AppSettings.Current.NormalizeVoiceSettings();
            string sapiName = SpeakRunSettings.GetSapiVoiceName();
            double rate = SpeakRunSettings.GetVoiceSpeakingRate();
            double volume = SpeakRunSettings.GetVoiceVolume();

            try
            {
                // Explicit name → that voice (any culture — user picked it).
                // Blank → leave engine default (do not substitute first English).
                if (!string.IsNullOrWhiteSpace(sapiName))
                {
                    var named = synth.GetInstalledVoices()
                        .FirstOrDefault(v => v.Enabled &&
                            string.Equals(
                                v.VoiceInfo.Name, sapiName,
                                StringComparison.OrdinalIgnoreCase));
                    if (named != null)
                        synth.SelectVoice(named.VoiceInfo.Name);
                    else
                        Debug.WriteLine(
                            $"[SapiTTS] voice missing: {sapiName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SapiTTS] voice select: {ex.Message}");
            }

            try
            {
                synth.Rate = MapSpeakingRateToSapi(rate);
                synth.Volume = Math.Clamp((int)Math.Round(volume * 100.0), 0, 100);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SapiTTS] options: {ex.Message}");
            }
        }

        /// <summary>Map UWP-style rate (0.5–6) to SAPI Rate (-10..10).</summary>
        private static int MapSpeakingRateToSapi(double rate)
        {
            rate = Math.Clamp(rate, 0.5, 6.0);
            // log2 so 1.0 → 0, 0.5 → -5, 2.0 → 5, ~4+ → near max
            double log2 = Math.Log(rate) / Math.Log(2.0);
            return Math.Clamp((int)Math.Round(log2 * 5.0), -10, 10);
        }

        /// <summary>
        /// SAPI SSML with optional sentence breaks and pitch (prosody).
        /// Pitch 1.0 = default; 0.0–2.0 maps to about -50%…+50%.
        /// </summary>
        private static string BuildSapiSpeakSsml(
            string text, int breakMs, string culture, double pitchMultiplier)
        {
            var parts = new List<string>();
            if (breakMs > 0 && LooksMultiSentence(text))
            {
                foreach (string unit in ComicBestOfFusion.SplitIntoSpeakUnits(text))
                {
                    string t = unit.Trim();
                    if (t.Length > 0)
                        parts.Add(t);
                }
            }
            if (parts.Count == 0)
                parts.Add(text.Trim());

            // Always force English — ignore OS / voice culture for pronunciation rules.
            string lang = TtsForcedLanguage;
            double pitchPct = Math.Clamp((pitchMultiplier - 1.0) * 50.0, -50.0, 50.0);
            string pitchAttr = pitchPct >= 0
                ? $"+{pitchPct:0.#}%"
                : $"{pitchPct:0.#}%";

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"")
                .Append(System.Security.SecurityElement.Escape(lang))
                .Append("\">");
            sb.Append("<prosody pitch=\"").Append(pitchAttr).Append("\">");
            for (int i = 0; i < parts.Count; i++)
            {
                sb.Append(System.Security.SecurityElement.Escape(parts[i]));
                if (i < parts.Count - 1 && breakMs > 0)
                    sb.Append($"<break time=\"{breakMs}ms\"/>");
                else if (i < parts.Count - 1)
                    sb.Append(' ');
            }
            sb.Append("</prosody></speak>");
            return sb.ToString();
        }

        private static bool LooksMultiSentence(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            // At least two sentence-ish chunks
            int ends = Regex.Matches(text, @"[.!?]+\s+\S").Count;
            return ends >= 1 && ComicRegionGeometry.CountWords(text) >= 12;
        }

        /// <summary>
        /// Build SSML with a short break after each sentence for easier tracking.
        /// Escapes XML special chars in spoken text. Always uses
        /// <see cref="TtsForcedLanguage"/> (English) for pronunciation.
        /// </summary>
        private static string BuildSpeakSsml(string text, int breakMs, string? voiceLang = null)
        {
            breakMs = Math.Clamp(breakMs, 0, 2000);
            // Split on sentence end but keep punctuation with the clause
            var parts = Regex.Split(text.Trim(), @"(?<=[.!?])\s+")
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            if (parts.Count == 0)
                parts.Add(text.Trim());

            // Ignore caller/voice culture — OCR speak path is English-only.
            string lang = TtsForcedLanguage;
            _ = voiceLang;
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<speak version=\"1.0\" xml:lang=\"")
                .Append(System.Security.SecurityElement.Escape(lang))
                .Append("\">");
            for (int i = 0; i < parts.Count; i++)
            {
                sb.Append(System.Security.SecurityElement.Escape(parts[i]));
                if (i < parts.Count - 1 && breakMs > 0)
                    sb.Append($"<break time=\"{breakMs}ms\"/>");
                else if (i < parts.Count - 1)
                    sb.Append(' ');
            }
            sb.Append("</speak>");
            return sb.ToString();
        }

        /// <summary>True when a BCP-47 / culture tag is English (en, en-US, en-GB, …).</summary>
        private static bool IsEnglishCultureName(string? lang) =>
            !string.IsNullOrWhiteSpace(lang) &&
            (lang.Equals("en", StringComparison.OrdinalIgnoreCase) ||
             lang.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
             lang.StartsWith("en_", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Resolve UWP/OneCore voice: explicit <paramref name="preferredId"/> if
        /// installed; otherwise true OS default (<see cref="WinSpeech.SpeechSynthesizer.DefaultVoice"/>).
        /// Blank VoiceId must not substitute "first English in AllVoices" — that
        /// diverged from DefaultVoice and made "(System default)" preview/status lie.
        /// </summary>
        private static WinSpeech.VoiceInformation ResolveWinRtVoice(string? preferredId)
        {
            if (!string.IsNullOrWhiteSpace(preferredId))
            {
                var named = WinSpeech.SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
                    string.Equals(v.Id, preferredId, StringComparison.OrdinalIgnoreCase));
                if (named != null)
                    return named;
            }

            return WinSpeech.SpeechSynthesizer.DefaultVoice;
        }

        /// <summary>
        /// Apply voice Id + SpeechSynthesizerOptions (rate, pitch, volume, silence).
        /// Uses <see cref="SpeakRunSettings"/> when a speak run is active so mid-run
        /// Settings changes do not alter TTS; otherwise live AppSettings (Voice preview).
        /// </summary>
        public static void ApplyVoiceSettings(WinSpeech.SpeechSynthesizer synth)
        {
            if (synth == null) return;

            if (SpeakRunSettings.Active == null)
                AppSettings.Current.NormalizeVoiceSettings();
            string voiceId = SpeakRunSettings.GetVoiceId();
            double rate = SpeakRunSettings.GetVoiceSpeakingRate();
            double pitch = SpeakRunSettings.GetVoicePitch();
            double volume = SpeakRunSettings.GetVoiceVolume();
            string appended = SpeakRunSettings.GetVoiceAppendedSilence();
            string punct = SpeakRunSettings.GetVoicePunctuationSilence();

            try
            {
                synth.Voice = ResolveWinRtVoice(voiceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemTTS] voice select: {ex.Message}");
                try
                {
                    synth.Voice = WinSpeech.SpeechSynthesizer.DefaultVoice;
                }
                catch { /* ignore */ }
            }

            try
            {
                var opts = synth.Options;
                opts.SpeakingRate = rate;
                opts.AudioPitch = pitch;
                opts.AudioVolume = volume;

                opts.AppendedSilence =
                    appended.Equals("Min", StringComparison.OrdinalIgnoreCase)
                        ? WinSpeech.SpeechAppendedSilence.Min
                        : WinSpeech.SpeechAppendedSilence.Default;

                opts.PunctuationSilence =
                    punct.Equals("Min", StringComparison.OrdinalIgnoreCase)
                        ? WinSpeech.SpeechPunctuationSilence.Min
                        : WinSpeech.SpeechPunctuationSilence.Default;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemTTS] options: {ex.Message}");
            }
        }

        /// <summary>
        /// Installed UWP / OneCore TTS voices for the VOICE panel (Id + friendly label).
        /// </summary>
        public static IReadOnlyList<(string Id, string Label)> ListInstalledVoices()
        {
            try
            {
                return WinSpeech.SpeechSynthesizer.AllVoices
                    .Select(v =>
                    {
                        string gender = v.Gender.ToString();
                        string label = $"{v.DisplayName}  ·  {v.Language}  ·  {gender}";
                        return (v.Id, label);
                    })
                    .OrderBy(x => x.label, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemTTS] AllVoices: {ex.Message}");
                return Array.Empty<(string, string)>();
            }
        }

        /// <summary>
        /// Installed SAPI 5 voices (Name + friendly label). Includes adapter-registered engines.
        /// </summary>
        public static IReadOnlyList<(string Name, string Label)> ListInstalledSapiVoices()
        {
            try
            {
                using var synth = new SapiSpeech.SpeechSynthesizer();
                return synth.GetInstalledVoices()
                    .Where(v => v.Enabled)
                    .Select(v =>
                    {
                        var info = v.VoiceInfo;
                        string culture = info.Culture?.Name ?? "?";
                        string label =
                            $"{info.Name}  ·  {culture}  ·  {info.Gender}";
                        return (info.Name, label);
                    })
                    .OrderBy(x => x.label, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SapiTTS] GetInstalledVoices: {ex.Message}");
                return Array.Empty<(string, string)>();
            }
        }

        /// <summary>
        /// Display label for the voice that speak/preview will actually use
        /// (same resolution as <see cref="ApplyVoiceSettings"/> /
        /// <see cref="ApplySapiVoiceSettings"/>).
        /// </summary>
        public static string DescribeCurrentVoice()
        {
            try
            {
                var s = AppSettings.Current;
                s.NormalizeVoiceSettings();

                if (s.IsSapiTtsEngine)
                {
                    if (!string.IsNullOrWhiteSpace(s.SapiVoiceName))
                    {
                        var match = ListInstalledSapiVoices()
                            .FirstOrDefault(v =>
                                string.Equals(v.Name, s.SapiVoiceName, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(match.Name))
                            return $"SAPI · {match.Name}";
                        return $"SAPI · {s.SapiVoiceName} (missing?)";
                    }

                    try
                    {
                        using var synth = new SapiSpeech.SpeechSynthesizer();
                        // Fresh synthesizer = engine default (blank SapiVoiceName path).
                        return $"SAPI · system default · {synth.Voice.Name}";
                    }
                    catch
                    {
                        return "SAPI · system default";
                    }
                }

                if (!string.IsNullOrWhiteSpace(s.VoiceId))
                {
                    var match = WinSpeech.SpeechSynthesizer.AllVoices
                        .FirstOrDefault(v =>
                            string.Equals(v.Id, s.VoiceId, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        return $"{match.DisplayName} ({match.Language})";
                    return $"{s.VoiceId} (missing?)";
                }

                // Blank VoiceId → same as ApplyVoiceSettings: OS DefaultVoice.
                var voice = WinSpeech.SpeechSynthesizer.DefaultVoice;
                return $"System default · {voice.DisplayName} ({voice.Language})";
            }
            catch
            {
                return "System default";
            }
        }

        // ------------------------------------------------------------------
        // Short UI announcements (mode toggles while overlay is hidden)
        // ------------------------------------------------------------------

        private static readonly object AnnouncementLock = new();
        private static CancellationTokenSource? _announceCts;

        /// <summary>
        /// Cancel any short UI announcement TTS in progress (mode toggles, etc.).
        /// Safe to call when nothing is speaking.
        /// </summary>
        public static void CancelAnnouncement()
        {
            CancellationTokenSource? cts;
            lock (AnnouncementLock)
            {
                cts = _announceCts;
                _announceCts = null;
            }
            try { cts?.Cancel(); } catch { /* ignore */ }
            try { cts?.Dispose(); } catch { /* ignore */ }
        }

        /// <summary>
        /// Fire-and-forget short TTS for UI state (e.g. mode switch from tray).
        /// Cancels any previous announcement so rapid toggles only speak the latest.
        /// Does not duck system audio or touch the OCR speak pipeline.
        /// </summary>
        public static void SpeakAnnouncement(string text)
        {
            text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            CancellationTokenSource cts;
            lock (AnnouncementLock)
            {
                try { _announceCts?.Cancel(); } catch { /* ignore */ }
                try { _announceCts?.Dispose(); } catch { /* ignore */ }
                _announceCts = new CancellationTokenSource();
                cts = _announceCts;
            }

            CancellationToken token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    AppSettings.Current.NormalizeVoiceSettings();
                    if (AppSettings.Current.IsSapiTtsEngine)
                    {
                        await SpeakAnnouncementSapiAsync(text, token).ConfigureAwait(false);
                        return;
                    }

                    await SpeakAnnouncementWinRtAsync(text, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // superseded by a newer announcement
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AnnounceTTS] {ex.Message}");
                }
            });
        }

        private static async Task SpeakAnnouncementWinRtAsync(string text, CancellationToken token)
        {
            MediaPlayer? player = null;
            try
            {
                using var synth = new WinSpeech.SpeechSynthesizer();
                ApplyVoiceSettings(synth);
                string ssml = BuildSpeakSsml(text, breakMs: 0, TtsForcedLanguage);
                using var stream =
                    await synth.SynthesizeSsmlToStreamAsync(ssml).AsTask(token);
                if (token.IsCancellationRequested)
                    return;

                player = new MediaPlayer();
                var tcs = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                void OnEnded(MediaPlayer sender, object args) =>
                    tcs.TrySetResult(null);
                void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs e) =>
                    tcs.TrySetResult(null);

                player.MediaEnded += OnEnded;
                player.MediaFailed += OnFailed;
                player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
                player.Play();

                using (token.Register(() =>
                {
                    try { player.Pause(); player.Source = null; } catch { /* ignore */ }
                    tcs.TrySetCanceled();
                }))
                {
                    await tcs.Task.ConfigureAwait(false);
                }

                player.MediaEnded -= OnEnded;
                player.MediaFailed -= OnFailed;
            }
            finally
            {
                try
                {
                    if (player != null)
                    {
                        player.Pause();
                        player.Source = null;
                        player.Dispose();
                    }
                }
                catch { /* ignore */ }
            }
        }

        private static async Task SpeakAnnouncementSapiAsync(string text, CancellationToken token)
        {
            using var synth = new SapiSpeech.SpeechSynthesizer();
            ApplySapiVoiceSettings(synth);

            var tcs = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnCompleted(object? sender, SapiSpeech.SpeakCompletedEventArgs e)
            {
                if (e.Cancelled)
                    tcs.TrySetCanceled();
                else if (e.Error != null)
                    tcs.TrySetException(e.Error);
                else
                    tcs.TrySetResult(null);
            }

            synth.SpeakCompleted += OnCompleted;
            try
            {
                using (token.Register(() =>
                {
                    try { synth.SpeakAsyncCancelAll(); } catch { /* ignore */ }
                    tcs.TrySetCanceled();
                }))
                {
                    string ssml = BuildSapiSpeakSsml(
                        text, breakMs: 0, TtsForcedLanguage, SpeakRunSettings.GetVoicePitch());
                    synth.SpeakSsmlAsync(ssml);
                    await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                synth.SpeakCompleted -= OnCompleted;
            }
        }

        /// <summary>
        /// One short phrase for a mode toggle — only what the user needs to hear.
        /// </summary>
        public static string DescribeModeChange(bool wasComic, bool nowComic)
        {
            if (wasComic && !nowComic)
                return "Default mode on";
            if (!wasComic && nowComic)
                return "Comic book on";
            if (!nowComic)
                return "Default mode on";
            return "Comic book on";
        }
    }
}
