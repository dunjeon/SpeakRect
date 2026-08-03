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
    public void Stack_compose_max_long_edge_is_2560()
    {
        Assert.Equal(2560, ComicPoiGuide.StackComposeMaxLongEdge);
    }

    [Fact]
    public void Image_llm_send_downscale_defaults_off()
    {
        AppSettings.Current.ResetImagePrepSettingsToDefaults();
        Assert.False(AppSettings.Current.ImageLlmSendDownscale);
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
    public void Dynamic_fog_stop_only_on_clear_shrink_not_plateau()
    {
        // Flat scores: keep climbing (no plateau early-out) so late peaks (~0.51) are seen.
        var scores = new List<long> { 29374, 29374, 29374, 29374, 29374 };
        Assert.False(OcrProcessor.SmokeDynamicFogShouldStopClimb(scores, 0.97));

        // Clear shrink vs peak → stop.
        long[] shrink = { 10000, 12000, 11000, 8000 };
        Assert.True(OcrProcessor.SmokeDynamicFogShouldStopClimb(shrink, 0.97));
    }

    [Fact]
    public void Dynamic_fog_flat_then_late_peak_not_stopped_early()
    {
        // Missed-balloon case: flat low fog, then area rises mid-range.
        var scores = new List<long> { 29374, 29374, 29374 };
        Assert.False(OcrProcessor.SmokeDynamicFogShouldStopClimb(scores, 0.97));
        scores.Add(40000); // new peak
        Assert.False(OcrProcessor.SmokeDynamicFogShouldStopClimb(scores, 0.97));
        scores.Add(35000); // still above 0.97×peak? 35000 > 40000*0.97=38800? no
        // 35000 < 38800 → shrink stop
        Assert.True(OcrProcessor.SmokeDynamicFogShouldStopClimb(scores, 0.97));
    }

    [Fact]
    public void Dynamic_fog_global_pick_includes_baseline_zero()
    {
        // 0 baseline beats climb peak → choose none.
        var scores = new Dictionary<float, long>
        {
            [0f] = 20000,
            [0.25f] = 18000,
            [0.30f] = 17000,
        };
        Assert.Equal(0f, OcrProcessor.SmokeSelectDynamicFogBestAmount(scores));

        // Fog peak larger → choose fog (climb still worth it).
        scores[0.30f] = 25000;
        Assert.Equal(0.30f, OcrProcessor.SmokeSelectDynamicFogBestAmount(scores));

        // Exact tie with fog → lower amount wins (prefer none).
        scores = new Dictionary<float, long>
        {
            [0f] = 15000,
            [0.25f] = 15000,
        };
        Assert.Equal(0f, OcrProcessor.SmokeSelectDynamicFogBestAmount(scores));
    }

    [Fact]
    public void Dyn_fog_island_crop_empty_nukes()
    {
        // Ghost island: crop re-OCR empty / junk → drop.
        Assert.True(OcrProcessor.SmokeDynFogIslandCropIsEmpty(null));
        Assert.True(OcrProcessor.SmokeDynFogIslandCropIsEmpty(""));
        Assert.True(OcrProcessor.SmokeDynFogIslandCropIsEmpty("   "));
        Assert.True(OcrProcessor.SmokeDynFogIslandCropIsEmpty("!"));
        Assert.True(OcrProcessor.SmokeDynFogIslandCropIsEmpty("a")); // below min alnum floor
        // Real balloon text from crop → keep.
        Assert.False(OcrProcessor.SmokeDynFogIslandCropIsEmpty("Hello"));
        Assert.False(OcrProcessor.SmokeDynFogIslandCropIsEmpty("OK"));
        Assert.False(OcrProcessor.SmokeDynFogIslandCropIsEmpty("No!"));
    }

    [Fact]
    public void Dyn_fog_keep_prior_text_when_crop_empty()
    {
        // Cuff-page miss: full-frame saw "...USE..." but crop re-OCR returned empty.
        // Must keep — not a fog ghost.
        Assert.True(OcrProcessor.SmokeDynFogKeepOnEmptyCrop("...USE.„"));
        Assert.True(OcrProcessor.SmokeDynFogKeepOnEmptyCrop("USE"));
        Assert.True(OcrProcessor.SmokeDynFogKeepOnEmptyCrop("…USE… ME…"));
        Assert.True(OcrProcessor.SmokeDynFogKeepOnEmptyCrop("No!"));
        Assert.True(OcrProcessor.SmokeDynFogKeepOnEmptyCrop("we don't have a weapon"));

        // True ghosts / junk prior → still nuke on empty crop.
        Assert.False(OcrProcessor.SmokeDynFogKeepOnEmptyCrop(null));
        Assert.False(OcrProcessor.SmokeDynFogKeepOnEmptyCrop(""));
        Assert.False(OcrProcessor.SmokeDynFogKeepOnEmptyCrop("   "));
        Assert.False(OcrProcessor.SmokeDynFogKeepOnEmptyCrop("!"));
        Assert.False(OcrProcessor.SmokeDynFogKeepOnEmptyCrop("a"));
        Assert.False(OcrProcessor.SmokeDynFogKeepOnEmptyCrop("123"));
    }

    [Fact]
    public void Dynamic_fog_global_pick_still_prefers_zero_on_tie()
    {
        // Baseline 0 remains a strong play: equal area → none wins.
        // (Island-verify skip/prior-keep is what saves tiny balloons at 0,
        //  not forcing climb-floor over an equal baseline.)
        var scores = new Dictionary<float, long>
        {
            [0f] = 42224,
            [0.25f] = 42224,
            [0.30f] = 42224,
        };
        Assert.Equal(0f, OcrProcessor.SmokeSelectDynamicFogBestAmount(scores));
    }

    [Fact]
    public void Dynamic_fog_linear_grind_defaults()
    {
        // Full 0.01 grind stock range 0.00…1.00 (user can shrink with floor/ceiling).
        Assert.Equal(0.01f, OcrProcessor.DynamicFogSearchStep);
        Assert.Equal(0f, OcrProcessor.DynamicFogSearchFloor);
        Assert.Equal(1f, OcrProcessor.DynamicFogSearchMax);
        Assert.Equal(0f, AppSettings.DefaultComicDynamicFogMin);
        Assert.Equal(1f, AppSettings.DefaultComicDynamicFogMax);

        AppSettings.Current.ResetComicRegionSettingsToDefaults();
        Assert.Equal(0f, AppSettings.Current.ComicDynamicFogMin);
        Assert.Equal(1f, AppSettings.Current.ComicDynamicFogMax);
    }

    [Fact]
    public void Dynamic_fog_min_max_normalize_swaps_when_inverted()
    {
        var s = AppSettings.Current;
        try
        {
            s.ComicDynamicFogMin = 0.80f;
            s.ComicDynamicFogMax = 0.20f;
            s.NormalizeComicRegionSettings();
            Assert.Equal(0.20f, s.ComicDynamicFogMin);
            Assert.Equal(0.80f, s.ComicDynamicFogMax);
        }
        finally
        {
            s.ResetComicRegionSettingsToDefaults();
        }
    }

    [Fact]
    public void Dynamic_fog_global_pick_prefers_mid_range_peak()
    {
        // Same island counts: largest area wins (0.55).
        var scores = new Dictionary<float, long>
        {
            [0f] = 40000,
            [0.25f] = 40000,
            [0.30f] = 39800,
            [0.50f] = 41000,
            [0.55f] = 45000,
            [0.60f] = 42000,
            [1.00f] = 10000,
        };
        Assert.Equal(0.55f, OcrProcessor.SmokeSelectDynamicFogBestAmount(scores));
    }

    [Fact]
    public void Dynamic_fog_pick_more_islands_beats_larger_area()
    {
        // Peak rule: MOST boxes first, then largest. 3 smaller balloons beat 1 mega blob.
        var scores = new Dictionary<float, long>
        {
            [0f] = 50000,
            [0.55f] = 30000,
        };
        var islands = new Dictionary<float, int>
        {
            [0f] = 1,
            [0.55f] = 3,
        };
        Assert.Equal(
            0.55f,
            OcrProcessor.SmokeSelectDynamicFogBestAmount(scores, islands));
    }

    [Fact]
    public void Dynamic_fog_pick_same_islands_prefers_larger_area()
    {
        var scores = new Dictionary<float, long>
        {
            [0.30f] = 20000,
            [0.55f] = 35000,
            [0.70f] = 25000,
        };
        var islands = new Dictionary<float, int>
        {
            [0.30f] = 2,
            [0.55f] = 2,
            [0.70f] = 2,
        };
        Assert.Equal(
            0.55f,
            OcrProcessor.SmokeSelectDynamicFogBestAmount(scores, islands));
    }

    [Fact]
    public void Dynamic_fog_coverage_is_better_matches_climb_rule()
    {
        // More islands always better.
        Assert.True(OcrProcessor.SmokeDynFogCoverageIsBetter(3, 100, 2, 99999));
        // Fewer islands never better even if huge area.
        Assert.False(OcrProcessor.SmokeDynFogCoverageIsBetter(1, 99999, 2, 100));
        // Same islands: larger area better.
        Assert.True(OcrProcessor.SmokeDynFogCoverageIsBetter(2, 500, 2, 400));
        Assert.False(OcrProcessor.SmokeDynFogCoverageIsBetter(2, 400, 2, 500));
        // Equal: not better (keep lower fog / earlier peak).
        Assert.False(OcrProcessor.SmokeDynFogCoverageIsBetter(2, 500, 2, 500));
    }

    [Fact]
    public void Real_dialogue_token_accepts_short_callouts()
    {
        Assert.True(ComicBestOfFusion.LooksLikeRealDialogueToken("SORRY"));
        Assert.True(ComicBestOfFusion.LooksLikeRealDialogueToken("NO"));
        Assert.False(ComicBestOfFusion.LooksLikeRealDialogueToken(""));
        Assert.False(ComicBestOfFusion.LooksLikeRealDialogueToken("123"));
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
    public void Comic_book_mode_reset_defaults_poi_on()
    {
        // Balloons "Reset defaults" while in Comic Book — stock Comic Book
        // (fresh comic path), not product Default mode.
        var s = AppSettings.Current;
        try
        {
            s.ResetComicRegionSettingsToDefaults(asComicBookMode: true);
            Assert.True(s.ComicBook);
            Assert.True(s.ComicPoiMarkers);
            Assert.True(s.ComicPoiFogOutside);
            Assert.True(s.ComicPoiAutoStack);
            Assert.True(s.ComicDynamicFog);
            Assert.True(s.ComicDetectFog);
            Assert.True(s.ComicSequentialRegions);
            Assert.False(s.ImageLlmSendDownscale);
            Assert.Equal(ComicPoiGuide.DefaultStackBeefExtra, s.ComicPoiStackBeefExtra);
            Assert.Equal(ComicPoiGuide.DefaultStackBottomPadShare, s.ComicPoiStackBottomPadShare);
        }
        finally
        {
            s.ResetComicRegionSettingsToDefaults();
            s.ResetImagePrepSettingsToDefaults();
        }
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
            Assert.False(s.ImageLlmSendDownscale);
            Assert.Equal(AppSettings.DefaultImageLlmSendMaxLongEdge, s.ImageLlmSendMaxLongEdge);
            Assert.Equal(ComicPoiGuide.DefaultStackBeefExtra, s.ComicPoiStackBeefExtra);
            Assert.Equal(ComicPoiGuide.DefaultStackBottomPadShare, s.ComicPoiStackBottomPadShare);
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

