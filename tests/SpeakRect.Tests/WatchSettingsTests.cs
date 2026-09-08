using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class WatchSettingsTests
{
    [Fact]
    public void Interval_clamps()
    {
        var s = AppSettings.Current;
        int prevMs = s.WatchIntervalMs;
        int prevSlot = s.WatchRegionSlot;
        bool prevOn = s.WatchEnabled;
        try
        {
            s.WatchIntervalMs = 1;
            s.NormalizeWatchSettings();
            Assert.Equal(RegionWatch.MinIntervalMs, s.WatchIntervalMs);

            s.WatchIntervalMs = 999_999;
            s.NormalizeWatchSettings();
            Assert.Equal(RegionWatch.MaxIntervalMs, s.WatchIntervalMs);
        }
        finally
        {
            s.WatchIntervalMs = prevMs;
            s.WatchRegionSlot = prevSlot;
            s.WatchEnabled = prevOn;
            s.NormalizeWatchSettings();
        }
    }

    [Fact]
    public void Slot_clamps_to_0_7()
    {
        var s = AppSettings.Current;
        int prev = s.WatchRegionSlot;
        try
        {
            s.WatchRegionSlot = -3;
            s.NormalizeWatchSettings();
            Assert.Equal(0, s.WatchRegionSlot);

            s.WatchRegionSlot = 99;
            s.NormalizeWatchSettings();
            Assert.Equal(7, s.WatchRegionSlot);
        }
        finally
        {
            s.WatchRegionSlot = prev;
            s.NormalizeWatchSettings();
        }
    }

    [Fact]
    public void Normalize_text_collapses_whitespace()
    {
        Assert.Equal("hello world", RegionWatch.NormalizeText("  hello   world \n"));
        Assert.Equal("", RegionWatch.NormalizeText("   "));
        Assert.Equal("", RegionWatch.NormalizeText(null));
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("(unreadable)")]
    [InlineData("UNREADABLE")]
    public void Unreadable_is_detected(string raw)
    {
        Assert.True(RegionWatch.IsUnreadable(raw));
        Assert.False(RegionWatch.TryNormalizeSpeakable(raw, out _));
    }

    [Fact]
    public void Speakable_text_normalizes()
    {
        Assert.True(RegionWatch.TryNormalizeSpeakable("  Hello\nWorld  ", out string n));
        Assert.Equal("Hello World", n);
    }

    [Fact]
    public void Empty_slot_is_not_a_capture()
    {
        var slot = new RegionSlotData();
        Assert.False(RegionWatch.TryGetCapture(slot, out _, out _, out _));
        Assert.False(RegionWatch.TryGetCapture(null, out _, out _, out _));
    }

    [Fact]
    public void Rect_slot_is_a_capture()
    {
        var slot = new RegionSlotData();
        slot.SetBox("Rect", new Rectangle(10, 20, 80, 40));
        Assert.True(RegionWatch.TryGetCapture(slot, out var bounds, out var lasso, out bool ellipse));
        Assert.Equal(new Rectangle(10, 20, 80, 40), bounds);
        Assert.Null(lasso);
        Assert.False(ellipse);
    }

    [Fact]
    public void CaptureFromApp_comic_book_override_ignores_live_mode()
    {
        var forcedOn = SpeakRunSettings.CaptureFromApp(comicBook: true);
        Assert.True(forcedOn.ComicBook);
        var forcedOff = SpeakRunSettings.CaptureFromApp(comicBook: false);
        Assert.False(forcedOff.ComicBook);
    }

    [Theory]
    [InlineData(null, WatchPipeline.RawSnap)]
    [InlineData("", WatchPipeline.RawSnap)]
    [InlineData("RawSnap", WatchPipeline.RawSnap)]
    [InlineData("raw", WatchPipeline.RawSnap)]
    [InlineData("Image", WatchPipeline.Image)]
    [InlineData("imageprep", WatchPipeline.Image)]
    [InlineData("ImageBalloon", WatchPipeline.ImageBalloon)]
    [InlineData("Image+Balloon", WatchPipeline.ImageBalloon)]
    [InlineData("2", WatchPipeline.ImageBalloon)]
    [InlineData("nope", WatchPipeline.RawSnap)]
    public void Pipeline_parse(string? raw, WatchPipeline expect)
    {
        Assert.Equal(expect, RegionWatch.ParsePipeline(raw));
    }

    [Fact]
    public void Pipeline_normalize_unknown_is_raw()
    {
        Assert.Equal(WatchPipeline.RawSnap, RegionWatch.NormalizePipeline((WatchPipeline)99));
        Assert.Equal("RawSnap", RegionWatch.PipelineToIni(WatchPipeline.RawSnap));
        Assert.Equal("Image", RegionWatch.PipelineToIni(WatchPipeline.Image));
        Assert.Equal("ImageBalloon", RegionWatch.PipelineToIni(WatchPipeline.ImageBalloon));

        var s = AppSettings.Current;
        var prev = s.WatchPipeline;
        try
        {
            s.WatchPipeline = (WatchPipeline)99;
            s.NormalizeWatchSettings();
            Assert.Equal(WatchPipeline.RawSnap, s.WatchPipeline);
        }
        finally
        {
            s.WatchPipeline = prev;
            s.NormalizeWatchSettings();
        }
    }

    [Fact]
    public void CaptureForWatch_raw_disables_image_tab()
    {
        var snap = SpeakRunSettings.CaptureForWatch(WatchPipeline.RawSnap);
        Assert.False(snap.ComicBook);
        Assert.False(snap.ImagePrepEnabled);
        Assert.False(snap.ImageLlmSendDownscale);
    }

    [Fact]
    public void CaptureForWatch_image_is_default_mode_with_live_prep()
    {
        var s = AppSettings.Current;
        bool prevPrep = s.ImagePrepEnabled;
        bool prevDown = s.ImageLlmSendDownscale;
        try
        {
            s.ImagePrepEnabled = true;
            s.ImageLlmSendDownscale = true;
            var snap = SpeakRunSettings.CaptureForWatch(WatchPipeline.Image);
            Assert.False(snap.ComicBook);
            Assert.True(snap.ImagePrepEnabled);
            Assert.True(snap.ImageLlmSendDownscale);
        }
        finally
        {
            s.ImagePrepEnabled = prevPrep;
            s.ImageLlmSendDownscale = prevDown;
        }
    }

    [Fact]
    public void CaptureForWatch_balloon_forces_comic_book()
    {
        var snap = SpeakRunSettings.CaptureForWatch(WatchPipeline.ImageBalloon);
        Assert.True(snap.ComicBook);
    }

    [Theory]
    [InlineData(null, WatchTextSource.LocalLlm)]
    [InlineData("", WatchTextSource.LocalLlm)]
    [InlineData("LocalLlm", WatchTextSource.LocalLlm)]
    [InlineData("LLM", WatchTextSource.LocalLlm)]
    [InlineData("Ocr", WatchTextSource.Ocr)]
    [InlineData("OCR", WatchTextSource.Ocr)]
    [InlineData("WinOCR", WatchTextSource.Ocr)]
    [InlineData("1", WatchTextSource.Ocr)]
    [InlineData("nope", WatchTextSource.LocalLlm)]
    public void TextSource_parse(string? raw, WatchTextSource expect)
    {
        Assert.Equal(expect, RegionWatch.ParseTextSource(raw));
    }

    [Fact]
    public void TextSource_default_is_local_llm()
    {
        Assert.Equal(WatchTextSource.LocalLlm, AppSettings.DefaultWatchTextSource);
        Assert.Equal("LocalLlm", RegionWatch.TextSourceToIni(WatchTextSource.LocalLlm));
        Assert.Equal("Ocr", RegionWatch.TextSourceToIni(WatchTextSource.Ocr));
        Assert.Equal(WatchTextSource.LocalLlm, RegionWatch.NormalizeTextSource((WatchTextSource)99));
    }

    [Fact]
    public void Watch_knobs_round_trip_ini_snapshot()
    {
        var s = AppSettings.Current;
        bool prevOn = s.WatchEnabled;
        int prevSlot = s.WatchRegionSlot;
        int prevMs = s.WatchIntervalMs;
        var prevPipe = s.WatchPipeline;
        var prevSrc = s.WatchTextSource;
        bool prevClear = s.WatchClearLastOnNoText;
        int prevDiff = s.WatchMinDifferencePercent;
        string path = Path.Combine(
            Path.GetTempPath(), "SpeakRect-watch-rt-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            s.WatchEnabled = true;
            s.WatchRegionSlot = 3;
            s.WatchIntervalMs = 3500;
            s.WatchPipeline = WatchPipeline.ImageBalloon;
            s.WatchTextSource = WatchTextSource.Ocr;
            s.WatchClearLastOnNoText = false;
            s.WatchMinDifferencePercent = 40;
            s.NormalizeWatchSettings();
            s.SaveTo(path);

            s.WatchEnabled = false;
            s.WatchRegionSlot = 0;
            s.WatchIntervalMs = 2000;
            s.WatchPipeline = WatchPipeline.RawSnap;
            s.WatchTextSource = WatchTextSource.LocalLlm;
            s.WatchClearLastOnNoText = true;
            s.WatchMinDifferencePercent = 90;

            s.LoadFrom(path, resetFirst: false);
            Assert.True(s.WatchEnabled);
            Assert.Equal(3, s.WatchRegionSlot);
            Assert.Equal(3500, s.WatchIntervalMs);
            Assert.Equal(WatchPipeline.ImageBalloon, s.WatchPipeline);
            Assert.Equal(WatchTextSource.Ocr, s.WatchTextSource);
            Assert.False(s.WatchClearLastOnNoText);
            Assert.Equal(40, s.WatchMinDifferencePercent);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
            s.WatchEnabled = prevOn;
            s.WatchRegionSlot = prevSlot;
            s.WatchIntervalMs = prevMs;
            s.WatchPipeline = prevPipe;
            s.WatchTextSource = prevSrc;
            s.WatchClearLastOnNoText = prevClear;
            s.WatchMinDifferencePercent = prevDiff;
            s.NormalizeWatchSettings();
        }
    }

    [Fact]
    public void WinOcr_junk_filter_is_the_boolean_floor()
    {
        Assert.True(BalloonOcrDetect.IsJunkWinOcrText(null));
        Assert.True(BalloonOcrDetect.IsJunkWinOcrText(""));
        Assert.True(BalloonOcrDetect.IsJunkWinOcrText("…"));
        Assert.False(BalloonOcrDetect.IsJunkWinOcrText("Hello"));
    }

    [Fact]
    public void WinOcr_gate_false_when_empty_or_no_words()
    {
        Assert.False(RegionWatch.WinOcrFoundSpeakableText(null));
        Assert.False(RegionWatch.WinOcrFoundSpeakableText(Array.Empty<DetectedTextRegion>()));
        Assert.False(RegionWatch.WinOcrFoundSpeakableText(new[]
        {
            new DetectedTextRegion { Bounds = new Rectangle(1, 1, 40, 12), WinOcrText = "   " },
        }));
        Assert.False(RegionWatch.WinOcrFoundSpeakableText(new[]
        {
            new DetectedTextRegion { Bounds = new Rectangle(1, 1, 1, 1), WinOcrText = "Hello" },
        }));
    }

    [Fact]
    public void WinOcr_gate_true_when_a_box_has_words()
    {
        Assert.True(RegionWatch.WinOcrFoundSpeakableText(new[]
        {
            new DetectedTextRegion { Bounds = new Rectangle(8, 8, 80, 20), WinOcrText = "Hello" },
        }));
    }

    [Fact]
    public void Spoken_words_match_ignores_case_and_whitespace()
    {
        Assert.True(RegionWatch.SameSpokenWords("Hello  WORLD", "hello world"));
        Assert.False(RegionWatch.SameSpokenWords("Hello world", "Goodbye world"));
        Assert.False(RegionWatch.SameSpokenWords("unreadable", "Hello"));
    }

    [Fact]
    public void Difference_percent_identical_is_zero()
    {
        Assert.Equal(0, RegionWatch.DifferencePercent("Hello world", "hello  world"));
        Assert.False(RegionWatch.DiffersEnough("Hello world", "hello world", 90));
    }

    [Fact]
    public void Difference_percent_empty_previous_is_100()
    {
        Assert.Equal(100, RegionWatch.DifferencePercent("", "Hello"));
        Assert.True(RegionWatch.DiffersEnough("", "Hello", 90));
    }

    [Fact]
    public void Difference_percent_unrelated_strings_is_high()
    {
        double pct = RegionWatch.DifferencePercent("aaaa", "bbbb");
        Assert.True(pct >= 90, $"got {pct}");
        Assert.True(RegionWatch.DiffersEnough("aaaa", "bbbb", 90));
    }

    [Fact]
    public void Difference_threshold_zero_speaks_any_change()
    {
        Assert.True(RegionWatch.DiffersEnough("Hello", "Hello!", 0));
        Assert.False(RegionWatch.DiffersEnough("Hello", "hello", 0));
    }

    [Fact]
    public void ToggleWatch_hotkey_row_is_global_ctrl_shift_w()
    {
        var row = AppSettings.HotkeyMapRows.First(
            r => r.Id.Equals("ToggleWatch", StringComparison.Ordinal));
        Assert.True(row.IsGlobal);
        Assert.Equal("Watch on / off", row.Label);
        Assert.Equal(Keys.W, AppSettings.DefaultToggleWatch.Key);
        Assert.Equal(
            HotkeyChord.MOD_CONTROL | HotkeyChord.MOD_SHIFT,
            AppSettings.DefaultToggleWatch.Modifiers);
        Assert.Equal("Ctrl+Shift+W", AppSettings.DefaultToggleWatch.ToIniString());
    }

    [Fact]
    public void Min_difference_percent_clamps()
    {
        var s = AppSettings.Current;
        int prev = s.WatchMinDifferencePercent;
        try
        {
            s.WatchMinDifferencePercent = -5;
            s.NormalizeWatchSettings();
            Assert.Equal(0, s.WatchMinDifferencePercent);
            s.WatchMinDifferencePercent = 250;
            s.NormalizeWatchSettings();
            Assert.Equal(100, s.WatchMinDifferencePercent);
        }
        finally
        {
            s.WatchMinDifferencePercent = prev;
            s.NormalizeWatchSettings();
        }
    }

    [Fact]
    public void Defaults_are_off_two_seconds_slot_1()
    {
        var s = AppSettings.Current;
        bool prevOn = s.WatchEnabled;
        int prevSlot = s.WatchRegionSlot;
        int prevMs = s.WatchIntervalMs;
        try
        {
            s.WatchEnabled = true;
            s.WatchRegionSlot = 4;
            s.WatchIntervalMs = 5000;
            s.WatchEnabled = false;
            s.WatchRegionSlot = 0;
            s.WatchIntervalMs = AppSettings.DefaultWatchIntervalMs;
            s.NormalizeWatchSettings();
            Assert.False(s.WatchEnabled);
            Assert.Equal(0, s.WatchRegionSlot);
            Assert.Equal(2000, s.WatchIntervalMs);
            Assert.Equal(WatchPipeline.RawSnap, AppSettings.DefaultWatchPipeline);
        }
        finally
        {
            s.WatchEnabled = prevOn;
            s.WatchRegionSlot = prevSlot;
            s.WatchIntervalMs = prevMs;
            s.NormalizeWatchSettings();
        }
    }
}
