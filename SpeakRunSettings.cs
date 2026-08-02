using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SpeakRect
{
    /// <summary>
    /// Frozen copy of OCR / prep / voice / speech knobs for one speak or Balloons
    /// still-image run. Pushed on an <see cref="AsyncLocal{T}"/> so async OCR work
    /// does not pick up mid-run Settings toggles (MODE, pad, pauses, prompts).
    /// When no snap is active, <see cref="Active"/> is null and callers fall back
    /// to live <see cref="AppSettings.Current"/>.
    /// </summary>
    public sealed class SpeakRunSettings
    {
        private static readonly AsyncLocal<SpeakRunSettings?> ActiveLocal = new();

        /// <summary>Snap for the current async speak flow, or null outside a run.</summary>
        public static SpeakRunSettings? Active => ActiveLocal.Value;

        // ---- Mode / strategy ----
        public bool ComicBook { get; init; }
        public bool ComicPoiMarkers { get; init; }
        public bool ComicPoiFogOutside { get; init; }
        public bool ComicPoiAutoStack { get; init; }
        public int ComicPoiAutoStackGapPx { get; init; }
        public int ComicPoiAutoStackMarginPx { get; init; }
        public double ComicPoiStackBeefExtra { get; init; }
        public double ComicPoiStackBottomPadShare { get; init; }
        public bool ComicSequentialRegions { get; init; }
        public bool ComicDetectFog { get; init; }
        public float ComicDetectFogAmount { get; init; }
        public bool ComicDynamicFog { get; init; }
        public double ComicClusterGapX { get; init; }
        public double ComicClusterGapY { get; init; }
        public double ComicInflateFracX { get; init; }
        public double ComicInflateFracY { get; init; }
        public int ComicRegionPadding { get; init; }
        public int ComicDenseIslandCount { get; init; }
        public bool ComicSplitLargeRegions { get; init; }
        public bool ComicMergeOverlappingIslands { get; init; }
        public int ComicOrphanRecoverPasses { get; init; }
        public int ComicMinIslandAlnum { get; init; }

        // ---- Image prep ----
        public bool ImagePrepEnabled { get; init; }
        public bool ImageLetterbox { get; init; }
        public int ImageLetterboxPad { get; init; }
        public int ImageLetterboxBlack { get; init; }
        public int ImageLetterboxWhite { get; init; }
        public int ImageUpscaleLongSide { get; init; }
        public bool ImageGrayscale { get; init; }
        public float ImageInkGrayWeight { get; init; }
        public bool ImageAutoLevels { get; init; }
        public double ImageAutoLevelsLow { get; init; }
        public double ImageAutoLevelsHigh { get; init; }
        public int ImageAutoLevelsMinRange { get; init; }
        public int ImageDenoiseRadius { get; init; }
        public float ImageDenoiseSigma { get; init; }
        public float ImageSharpenAmount { get; init; }
        public int ImageSharpenPasses { get; init; }
        public bool ImageLlmSendDownscale { get; init; }
        public int ImageLlmSendMaxLongEdge { get; init; }

        // ---- Prompts (resolved at capture) ----
        public string FullOrSimplePrompt { get; init; } = "";
        public string CropPrompt { get; init; } = "";
        public string SimplePrompt { get; init; } = "";
        public string RecoveryPrompt { get; init; } = "";
        public string PoiPrompt { get; init; } = "";
        public IReadOnlyList<string> KnownPrompts { get; init; } = Array.Empty<string>();

        // ---- Voice / pauses ----
        public string TtsEngine { get; init; } = "Windows";
        public string VoiceId { get; init; } = "";
        public string SapiVoiceName { get; init; } = "";
        public double VoiceSpeakingRate { get; init; } = 1.0;
        public double VoicePitch { get; init; } = 1.0;
        public double VoiceVolume { get; init; } = 1.0;
        public string VoiceAppendedSilence { get; init; } = "";
        public string VoicePunctuationSilence { get; init; } = "";
        public bool VoiceUseCustomPauseEncodings { get; init; }
        public int VoiceCommaPauseMs { get; init; }
        public int VoiceSentencePauseMs { get; init; }
        public int VoiceOtherPauseMs { get; init; }
        public int VoiceBubblePauseMs { get; init; }
        public bool IsSapiTtsEngine { get; init; }

        // ---- Speech clean ----
        public bool SpeechTitleCaseAllCaps { get; init; }
        public bool SpeechForceLowercase { get; init; }
        public IReadOnlyList<SpeechRule> SpeechRules { get; init; } = Array.Empty<SpeechRule>();
        public IReadOnlyList<SpeechTextRule> SpeechTextRules { get; init; } =
            Array.Empty<SpeechTextRule>();

        /// <summary>
        /// Snapshot live <see cref="AppSettings.Current"/> (after normalize).
        /// Call at the start of a speak path only.
        /// </summary>
        public static SpeakRunSettings CaptureFromApp()
        {
            var s = AppSettings.Current;
            s.NormalizeComicRegionSettings();
            s.NormalizeImagePrepSettings();
            s.NormalizeVoiceSettings();

            return new SpeakRunSettings
            {
                ComicBook = s.ComicBook,
                ComicPoiMarkers = s.ComicPoiMarkers,
                ComicPoiFogOutside = s.ComicPoiFogOutside,
                ComicPoiAutoStack = s.ComicPoiAutoStack,
                ComicPoiAutoStackGapPx = s.ComicPoiAutoStackGapPx,
                ComicPoiAutoStackMarginPx = s.ComicPoiAutoStackMarginPx,
                ComicPoiStackBeefExtra = s.ComicPoiStackBeefExtra,
                ComicPoiStackBottomPadShare = s.ComicPoiStackBottomPadShare,
                ComicSequentialRegions = s.ComicSequentialRegions,
                ComicDetectFog = s.ComicDetectFog,
                ComicDetectFogAmount = s.ComicDetectFogAmount,
                ComicDynamicFog = s.ComicDynamicFog,
                ComicClusterGapX = s.ComicClusterGapX,
                ComicClusterGapY = s.ComicClusterGapY,
                ComicInflateFracX = s.ComicInflateFracX,
                ComicInflateFracY = s.ComicInflateFracY,
                ComicRegionPadding = s.ComicRegionPadding,
                ComicDenseIslandCount = s.ComicDenseIslandCount,
                ComicSplitLargeRegions = s.ComicSplitLargeRegions,
                ComicMergeOverlappingIslands = s.ComicMergeOverlappingIslands,
                ComicOrphanRecoverPasses = s.ComicOrphanRecoverPasses,
                ComicMinIslandAlnum = s.ComicMinIslandAlnum,

                ImagePrepEnabled = s.ImagePrepEnabled,
                ImageLetterbox = s.ImageLetterbox,
                ImageLetterboxPad = s.ImageLetterboxPad,
                ImageLetterboxBlack = s.ImageLetterboxBlack,
                ImageLetterboxWhite = s.ImageLetterboxWhite,
                ImageUpscaleLongSide = s.ImageUpscaleLongSide,
                ImageGrayscale = s.ImageGrayscale,
                ImageInkGrayWeight = s.ImageInkGrayWeight,
                ImageAutoLevels = s.ImageAutoLevels,
                ImageAutoLevelsLow = s.ImageAutoLevelsLow,
                ImageAutoLevelsHigh = s.ImageAutoLevelsHigh,
                ImageAutoLevelsMinRange = s.ImageAutoLevelsMinRange,
                ImageDenoiseRadius = s.ImageDenoiseRadius,
                ImageDenoiseSigma = s.ImageDenoiseSigma,
                ImageSharpenAmount = s.ImageSharpenAmount,
                ImageSharpenPasses = s.ImageSharpenPasses,
                ImageLlmSendDownscale = s.ImageLlmSendDownscale,
                ImageLlmSendMaxLongEdge = s.ImageLlmSendMaxLongEdge,

                FullOrSimplePrompt = s.ActiveFullOrSimplePrompt,
                CropPrompt = s.ResolveCropPrompt(),
                SimplePrompt = s.ResolveSimplePrompt(),
                RecoveryPrompt = s.ResolveRecoveryPrompt(),
                PoiPrompt = s.ResolvePoiPrompt(),
                KnownPrompts = s.AllKnownPrompts().ToList(),

                TtsEngine = s.TtsEngine,
                VoiceId = s.VoiceId,
                SapiVoiceName = s.SapiVoiceName,
                VoiceSpeakingRate = s.VoiceSpeakingRate,
                VoicePitch = s.VoicePitch,
                VoiceVolume = s.VoiceVolume,
                VoiceAppendedSilence = s.VoiceAppendedSilence,
                VoicePunctuationSilence = s.VoicePunctuationSilence,
                VoiceUseCustomPauseEncodings = s.VoiceUseCustomPauseEncodings,
                VoiceCommaPauseMs = s.VoiceCommaPauseMs,
                VoiceSentencePauseMs = s.VoiceSentencePauseMs,
                VoiceOtherPauseMs = s.VoiceOtherPauseMs,
                VoiceBubblePauseMs = s.VoiceBubblePauseMs,
                IsSapiTtsEngine = s.IsSapiTtsEngine,

                SpeechTitleCaseAllCaps = s.SpeechTitleCaseAllCaps,
                SpeechForceLowercase = s.SpeechForceLowercase,
                SpeechRules = s.SpeechRules.ToList(),
                SpeechTextRules = s.SpeechTextRules.ToList(),
            };
        }

        /// <summary>
        /// Install <paramref name="snap"/> for the current async flow until dispose.
        /// Nested pushes restore the previous value.
        /// </summary>
        public static IDisposable Push(SpeakRunSettings snap)
        {
            if (snap == null)
                throw new ArgumentNullException(nameof(snap));
            var prev = ActiveLocal.Value;
            ActiveLocal.Value = snap;
            return new PopScope(prev);
        }

        private sealed class PopScope : IDisposable
        {
            private readonly SpeakRunSettings? _prev;
            private bool _done;

            public PopScope(SpeakRunSettings? prev) => _prev = prev;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                ActiveLocal.Value = _prev;
            }
        }

        // ---- Resolved accessors (snap when active, else live AppSettings) ----

        public static bool GetComicBook() =>
            Active?.ComicBook ?? AppSettings.Current.ComicBook;

        public static bool GetComicPoiMarkers() =>
            Active?.ComicPoiMarkers ?? AppSettings.Current.ComicPoiMarkers;

        public static bool GetComicPoiFogOutside() =>
            Active?.ComicPoiFogOutside ?? AppSettings.Current.ComicPoiFogOutside;

        public static bool GetComicPoiAutoStack() =>
            Active?.ComicPoiAutoStack ?? AppSettings.Current.ComicPoiAutoStack;

        public static int GetComicPoiAutoStackGapPx() =>
            Active?.ComicPoiAutoStackGapPx ?? AppSettings.Current.ComicPoiAutoStackGapPx;

        public static int GetComicPoiAutoStackMarginPx() =>
            Active?.ComicPoiAutoStackMarginPx ?? AppSettings.Current.ComicPoiAutoStackMarginPx;

        public static double GetComicPoiStackBeefExtra() =>
            Active?.ComicPoiStackBeefExtra ?? AppSettings.Current.ComicPoiStackBeefExtra;

        public static double GetComicPoiStackBottomPadShare() =>
            Active?.ComicPoiStackBottomPadShare ??
            AppSettings.Current.ComicPoiStackBottomPadShare;

        public static bool GetComicSequentialRegions() =>
            Active?.ComicSequentialRegions ?? AppSettings.Current.ComicSequentialRegions;

        public static bool GetComicDetectFog() =>
            Active?.ComicDetectFog ?? AppSettings.Current.ComicDetectFog;

        public static float GetComicDetectFogAmount() =>
            Active?.ComicDetectFogAmount ?? AppSettings.Current.ComicDetectFogAmount;

        public static bool GetComicDynamicFog() =>
            Active?.ComicDynamicFog ?? AppSettings.Current.ComicDynamicFog;

        public static double GetComicClusterGapX() =>
            Active?.ComicClusterGapX ?? AppSettings.Current.ComicClusterGapX;

        public static double GetComicClusterGapY() =>
            Active?.ComicClusterGapY ?? AppSettings.Current.ComicClusterGapY;

        public static double GetComicInflateFracX() =>
            Active?.ComicInflateFracX ?? AppSettings.Current.ComicInflateFracX;

        public static double GetComicInflateFracY() =>
            Active?.ComicInflateFracY ?? AppSettings.Current.ComicInflateFracY;

        public static int GetComicRegionPadding() =>
            Active?.ComicRegionPadding ?? AppSettings.Current.ComicRegionPadding;

        public static int GetComicDenseIslandCount() =>
            Active?.ComicDenseIslandCount ?? AppSettings.Current.ComicDenseIslandCount;

        public static bool GetComicSplitLargeRegions() =>
            Active?.ComicSplitLargeRegions ?? AppSettings.Current.ComicSplitLargeRegions;

        public static bool GetComicMergeOverlappingIslands() =>
            Active?.ComicMergeOverlappingIslands ??
            AppSettings.Current.ComicMergeOverlappingIslands;

        public static int GetComicOrphanRecoverPasses() =>
            Active?.ComicOrphanRecoverPasses ?? AppSettings.Current.ComicOrphanRecoverPasses;

        public static int GetComicMinIslandAlnum() =>
            Active?.ComicMinIslandAlnum ?? AppSettings.Current.ComicMinIslandAlnum;

        public static bool GetImagePrepEnabled() =>
            Active?.ImagePrepEnabled ?? AppSettings.Current.ImagePrepEnabled;

        public static bool GetImageLetterbox() =>
            Active?.ImageLetterbox ?? AppSettings.Current.ImageLetterbox;

        public static int GetImageLetterboxPad() =>
            Active?.ImageLetterboxPad ?? AppSettings.Current.ImageLetterboxPad;

        public static int GetImageLetterboxBlack() =>
            Active?.ImageLetterboxBlack ?? AppSettings.Current.ImageLetterboxBlack;

        public static int GetImageLetterboxWhite() =>
            Active?.ImageLetterboxWhite ?? AppSettings.Current.ImageLetterboxWhite;

        public static int GetImageUpscaleLongSide() =>
            Active?.ImageUpscaleLongSide ?? AppSettings.Current.ImageUpscaleLongSide;

        public static bool GetImageGrayscale() =>
            Active?.ImageGrayscale ?? AppSettings.Current.ImageGrayscale;

        public static float GetImageInkGrayWeight() =>
            Active?.ImageInkGrayWeight ?? AppSettings.Current.ImageInkGrayWeight;

        public static bool GetImageAutoLevels() =>
            Active?.ImageAutoLevels ?? AppSettings.Current.ImageAutoLevels;

        public static double GetImageAutoLevelsLow() =>
            Active?.ImageAutoLevelsLow ?? AppSettings.Current.ImageAutoLevelsLow;

        public static double GetImageAutoLevelsHigh() =>
            Active?.ImageAutoLevelsHigh ?? AppSettings.Current.ImageAutoLevelsHigh;

        public static int GetImageAutoLevelsMinRange() =>
            Active?.ImageAutoLevelsMinRange ?? AppSettings.Current.ImageAutoLevelsMinRange;

        public static int GetImageDenoiseRadius() =>
            Active?.ImageDenoiseRadius ?? AppSettings.Current.ImageDenoiseRadius;

        public static float GetImageDenoiseSigma() =>
            Active?.ImageDenoiseSigma ?? AppSettings.Current.ImageDenoiseSigma;

        public static float GetImageSharpenAmount() =>
            Active?.ImageSharpenAmount ?? AppSettings.Current.ImageSharpenAmount;

        public static int GetImageSharpenPasses() =>
            Active?.ImageSharpenPasses ?? AppSettings.Current.ImageSharpenPasses;

        public static bool GetImageLlmSendDownscale() =>
            Active?.ImageLlmSendDownscale ?? AppSettings.Current.ImageLlmSendDownscale;

        public static int GetImageLlmSendMaxLongEdge() =>
            Active?.ImageLlmSendMaxLongEdge ?? AppSettings.Current.ImageLlmSendMaxLongEdge;

        public static string GetFullOrSimplePrompt() =>
            Active?.FullOrSimplePrompt ?? AppSettings.Current.ActiveFullOrSimplePrompt;

        public static string GetCropPrompt() =>
            Active?.CropPrompt ?? AppSettings.Current.ResolveCropPrompt();

        public static string GetSimplePrompt() =>
            Active?.SimplePrompt ?? AppSettings.Current.ResolveSimplePrompt();

        public static string GetRecoveryPrompt() =>
            Active?.RecoveryPrompt ?? AppSettings.Current.ResolveRecoveryPrompt();

        public static string GetPoiPrompt() =>
            Active?.PoiPrompt ?? AppSettings.Current.ResolvePoiPrompt();

        public static IEnumerable<string> GetKnownPrompts() =>
            Active?.KnownPrompts ?? AppSettings.Current.AllKnownPrompts();

        public static bool GetVoiceUseCustomPauseEncodings() =>
            Active?.VoiceUseCustomPauseEncodings ??
            AppSettings.Current.VoiceUseCustomPauseEncodings;

        public static int GetVoiceCommaPauseMs() =>
            Active?.VoiceCommaPauseMs ?? AppSettings.Current.VoiceCommaPauseMs;

        public static int GetVoiceSentencePauseMs() =>
            Active?.VoiceSentencePauseMs ?? AppSettings.Current.VoiceSentencePauseMs;

        public static int GetVoiceOtherPauseMs() =>
            Active?.VoiceOtherPauseMs ?? AppSettings.Current.VoiceOtherPauseMs;

        public static int GetVoiceBubblePauseMs() =>
            Active?.VoiceBubblePauseMs ?? AppSettings.Current.VoiceBubblePauseMs;

        public static bool GetIsSapiTtsEngine() =>
            Active?.IsSapiTtsEngine ?? AppSettings.Current.IsSapiTtsEngine;

        public static string GetVoiceId() =>
            Active?.VoiceId ?? AppSettings.Current.VoiceId;

        public static string GetSapiVoiceName() =>
            Active?.SapiVoiceName ?? AppSettings.Current.SapiVoiceName;

        public static double GetVoiceSpeakingRate() =>
            Active?.VoiceSpeakingRate ?? AppSettings.Current.VoiceSpeakingRate;

        public static double GetVoicePitch() =>
            Active?.VoicePitch ?? AppSettings.Current.VoicePitch;

        public static double GetVoiceVolume() =>
            Active?.VoiceVolume ?? AppSettings.Current.VoiceVolume;

        public static string GetVoiceAppendedSilence() =>
            Active?.VoiceAppendedSilence ?? AppSettings.Current.VoiceAppendedSilence;

        public static string GetVoicePunctuationSilence() =>
            Active?.VoicePunctuationSilence ?? AppSettings.Current.VoicePunctuationSilence;

        public static bool GetSpeechTitleCaseAllCaps() =>
            Active?.SpeechTitleCaseAllCaps ?? AppSettings.Current.SpeechTitleCaseAllCaps;

        public static bool GetSpeechForceLowercase() =>
            Active?.SpeechForceLowercase ?? AppSettings.Current.SpeechForceLowercase;

        public static IReadOnlyList<SpeechRule> GetSpeechRules() =>
            Active?.SpeechRules ?? AppSettings.Current.SpeechRules;

        public static IReadOnlyList<SpeechTextRule> GetSpeechTextRules() =>
            Active?.SpeechTextRules ?? AppSettings.Current.SpeechTextRules;
    }
}
