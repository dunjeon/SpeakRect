using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class ComicSettingsClampTests
{
    [Fact]
    public void Fog_amount_clamps()
    {
        var s = AppSettings.Current;
        float prev = s.ComicDetectFogAmount;
        try
        {
            s.ComicDetectFogAmount = 9f;
            s.NormalizeComicRegionSettings();
            Assert.InRange(s.ComicDetectFogAmount, 0f, 1f);
            s.ComicDetectFogAmount = -1f;
            s.NormalizeComicRegionSettings();
            Assert.InRange(s.ComicDetectFogAmount, 0f, 1f);
        }
        finally
        {
            s.ComicDetectFogAmount = prev;
            s.NormalizeComicRegionSettings();
        }
    }

    [Fact]
    public void Sequential_regions_defaults_on()
    {
        AppSettings.Current.ResetComicRegionSettingsToDefaults();
        Assert.True(AppSettings.Current.ComicSequentialRegions);
    }

    [Fact]
    public void Merge_overlapping_defaults_on()
    {
        AppSettings.Current.ResetComicRegionSettingsToDefaults();
        Assert.True(AppSettings.Current.ComicMergeOverlappingIslands);
    }

    [Fact]
    public void Dynamic_fog_defaults_on()
    {
        AppSettings.Current.ResetComicRegionSettingsToDefaults();
        Assert.True(AppSettings.Current.ComicDynamicFog);
        Assert.True(AppSettings.Current.ComicDetectFog);
        Assert.InRange(AppSettings.Current.ComicDetectFogAmount, 0f, 1f);
    }

    [Fact]
    public void Dynamic_fog_pick_keeps_peak_when_area_shrinks()
    {
        // start=0.35 → areas climb then fall; best is index 3 (largest).
        long[] scores = { 1000, 1200, 1500, 1800, 1700, 900 };
        int best = OcrProcessor.SmokeSelectDynamicFogBestIndex(scores, shrinkVsPeak: 0.97);
        Assert.Equal(3, best);
    }

    [Fact]
    public void Dynamic_fog_pick_ignores_tiny_wobble_then_stops()
    {
        // Peak at 0; small noise; then clear shrink.
        long[] scores = { 10000, 9900, 9950, 8000 };
        int best = OcrProcessor.SmokeSelectDynamicFogBestIndex(scores, shrinkVsPeak: 0.97);
        Assert.Equal(0, best);
    }

    [Fact]
    public void Default_mode_is_product_default_poi_off()
    {
        AppSettings.Current.ResetComicRegionSettingsToDefaults();
        Assert.False(AppSettings.Current.ComicBook);
        Assert.False(AppSettings.Current.ComicPoiMarkers);
        // Sub-options ready for when user enables POI under Comic Book.
        Assert.True(AppSettings.Current.ComicPoiFogOutside);
        Assert.True(AppSettings.Current.ComicPoiAutoStack);
    }

    [Fact]
    public void Image_upscale_long_edge_defaults_900()
    {
        AppSettings.Current.ResetImagePrepSettingsToDefaults();
        Assert.Equal(900, AppSettings.DefaultImageUpscaleLongSide);
        Assert.Equal(900, AppSettings.Current.ImageUpscaleLongSide);
    }

    [Fact]
    public void Voice_pause_defaults_match_stock_ini()
    {
        // look/SpeakRect.ini [VOICE] stock values.
        Assert.Equal(102, AppSettings.DefaultCommaPauseMs);
        Assert.Equal(502, AppSettings.DefaultSentencePauseMs);
        Assert.Equal(52, AppSettings.DefaultOtherPauseMs);
        Assert.Equal(752, AppSettings.DefaultBubblePauseMs);
    }

    [Fact]
    public void Local_llm_send_scales_down_to_640_long_edge_only()
    {
        // Prep may be 900; Local-LLM send caps at 640. Already-small images unchanged.
        Assert.Equal(new System.Drawing.Size(400, 300),
            OcrProcessor.SmokeKoboldSendScaleSize(400, 300, 640));
        Assert.Equal(new System.Drawing.Size(640, 480),
            OcrProcessor.SmokeKoboldSendScaleSize(640, 480, 640));
        var down = OcrProcessor.SmokeKoboldSendScaleSize(900, 600, 640);
        Assert.Equal(640, Math.Max(down.Width, down.Height));
        Assert.True(down.Width <= 640 && down.Height <= 640);
        // Aspect ~ 3:2
        Assert.InRange(down.Width / (double)down.Height, 1.4, 1.6);
    }

    [Fact]
    public void Default_mode_clean_for_speech_encodes_voice_pauses()
    {
        var s = AppSettings.Current;
        bool prevComic = s.ComicBook;
        bool prevEncode = s.VoiceUseCustomPauseEncodings;
        int prevComma = s.VoiceCommaPauseMs;
        int prevSent = s.VoiceSentencePauseMs;
        try
        {
            s.ComicBook = false; // Default mode
            s.VoiceUseCustomPauseEncodings = true;
            s.VoiceCommaPauseMs = 102;
            s.VoiceSentencePauseMs = 502;

            string cleaned = OcrProcessor.SmokeCleanForSpeech(
                "Hello, world. Next!", comicBook: false);
            var pauses = OcrProcessor.SmokePauseAfterMsList(cleaned);
            // Expect comma then sentence pauses (last unit pause forced 0).
            Assert.True(pauses.Count >= 2, $"units/pauses={pauses.Count} text={cleaned}");
            Assert.Contains(102, pauses);
            Assert.Contains(502, pauses);
        }
        finally
        {
            s.ComicBook = prevComic;
            s.VoiceUseCustomPauseEncodings = prevEncode;
            s.VoiceCommaPauseMs = prevComma;
            s.VoiceSentencePauseMs = prevSent;
        }
    }

    [Fact]
    public void Leaving_comic_book_suspends_poi_and_restore_brings_it_back()
    {
        var s = AppSettings.Current;
        try
        {
            s.ResetComicRegionSettingsToDefaults();
            s.ComicBook = true;
            s.ComicPoiMarkers = true;
            s.ComicPoiFogOutside = true;
            s.ComicPoiAutoStack = true;
            // Clear any prior session stash by re-entering comic with POI already on.
            s.NormalizeModeFlags();

            // Leave Comic Book (same as DEFAULT mode / toggle COMIC BOOK off).
            s.ComicBook = false;
            s.NormalizeModeFlags();
            Assert.False(s.ComicBook);
            Assert.False(s.ComicPoiMarkers);

            // Re-enter Comic Book — POI (and sub-options) restored.
            s.ComicBook = true;
            s.NormalizeModeFlags();
            Assert.True(s.ComicBook);
            Assert.True(s.ComicPoiMarkers);
            Assert.True(s.ComicPoiFogOutside);
            Assert.True(s.ComicPoiAutoStack);
        }
        finally
        {
            s.ResetComicRegionSettingsToDefaults();
        }
    }

    [Fact]
    public void Leaving_comic_book_does_not_force_comic_back_on_when_poi_was_on()
    {
        var s = AppSettings.Current;
        try
        {
            s.ResetComicRegionSettingsToDefaults();
            s.ComicBook = true;
            s.ComicPoiMarkers = true;
            s.NormalizeModeFlags();

            s.ComicBook = false;
            s.NormalizeModeFlags();
            Assert.False(s.ComicBook, "Default mode must stick even if POI was on");
            Assert.False(s.ComicPoiMarkers);
        }
        finally
        {
            s.ResetComicRegionSettingsToDefaults();
        }
    }

    [Fact]
    public void Entering_comic_book_via_mode_flag_applies_comic_starting_defaults()
    {
        // Product default = Default mode. First Comic Book MODE enter (no stash)
        // applies Comic Book's own starting point (POI on, etc.).
        // Note: SetFlag saves ini — restore via full region reset so other tests
        // are not order-dependent on leftover ComicBook/POI.
        var s = AppSettings.Current;
        try
        {
            s.ResetComicRegionSettingsToDefaults();
            Assert.False(s.ComicBook);
            Assert.False(s.ComicPoiMarkers);

            // Same as sidebar / hotkey COMIC BOOK on (skips disk if we can avoid
            // pollution — still Save inside SetFlag).
            s.SetFlag(AppSettings.FlagIndexComicBook, true);
            Assert.True(s.ComicBook);
            Assert.True(s.ComicPoiMarkers);
            Assert.True(s.ComicPoiFogOutside);
            Assert.True(s.ComicPoiAutoStack);
            Assert.Equal(ComicPoiGuide.DefaultAutoStackGapPx, s.ComicPoiAutoStackGapPx);
            Assert.Equal(ComicPoiGuide.LlmSendStackMarginPx, s.ComicPoiAutoStackMarginPx);
            Assert.True(s.ImageLlmSendDownscale);
            Assert.Equal(AppSettings.DefaultImageLlmSendMaxLongEdge, s.ImageLlmSendMaxLongEdge);
            Assert.True(s.ComicDynamicFog);
            Assert.True(s.ComicDetectFog);
            Assert.True(s.ComicMergeOverlappingIslands);
            Assert.True(s.ComicSequentialRegions);
            Assert.True(s.ComicSplitLargeRegions);
        }
        finally
        {
            s.ResetComicRegionSettingsToDefaults();
            s.ResetImagePrepSettingsToDefaults();
        }
    }

    [Fact]
    public void Profile_style_comic_on_poi_off_not_overwritten_by_normalize()
    {
        // Ini/profile load sets ComicBook=true, POI=false — NormalizeModeFlags
        // must not force Comic starting defaults (only SetFlag enter does that).
        var s = AppSettings.Current;
        try
        {
            s.ResetComicRegionSettingsToDefaults();
            s.ComicBook = true;
            s.ComicPoiMarkers = false;
            s.NormalizeModeFlags();
            Assert.True(s.ComicBook);
            Assert.False(s.ComicPoiMarkers);
        }
        finally
        {
            s.ResetComicRegionSettingsToDefaults();
        }
    }
}

