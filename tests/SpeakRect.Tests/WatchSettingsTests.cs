using System.Drawing;
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
