using System.Collections.Generic;
using System.Drawing;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class ComicPoiGuideTests
{
    [Fact]
    public void PlaceMarkerCenters_prefers_left_of_island()
    {
        var regions = new List<Rectangle>
        {
            new(100, 80, 60, 40),
        };
        int r = 10;
        int foot = ComicPoiGuide.MarkerFootprintRadius(r);
        var centers = ComicPoiGuide.PlaceMarkerCenters(regions, 400, 300, r);
        Assert.Single(centers);
        // Left of the box and fully outside the text island.
        Assert.True(centers[0].X + foot < regions[0].X);
        Assert.False(ComicPoiGuide.MarkerHitsAnyText(centers[0], foot, regions));
    }

    [Fact]
    public void PlaceMarkerCenters_never_covers_any_text_island()
    {
        var regions = new List<Rectangle>
        {
            new(40, 50, 50, 40),
            new(140, 50, 50, 40),
        };
        int r = 8;
        int foot = ComicPoiGuide.MarkerFootprintRadius(r);
        var centers = ComicPoiGuide.PlaceMarkerCenters(regions, 400, 200, r);
        Assert.Equal(2, centers.Count);

        foreach (var c in centers)
        {
            Assert.False(
                ComicPoiGuide.MarkerHitsAnyText(c, foot, regions),
                $"bullseye @({c.X},{c.Y}) covers text");
        }
    }

    [Fact]
    public void PlaceMarkerCenters_corner_balloon_does_not_cover_text()
    {
        // Same failure mode as last debug run: island flush to left/bottom edge.
        // Old code clamped the bullseye onto the text ("THANK" covered).
        var regions = new List<Rectangle>
        {
            new(0, 500, 120, 80),
        };
        int r = 18;
        int foot = ComicPoiGuide.MarkerFootprintRadius(r);
        var centers = ComicPoiGuide.PlaceMarkerCenters(regions, 463, 640, r);
        foreach (var c in centers)
        {
            Assert.False(
                ComicPoiGuide.MarkerHitsAnyText(c, foot, regions),
                $"corner bullseye @({c.X},{c.Y}) covers text");
        }
        // Prefer top/right when left is flush with the canvas; never land on text.
        // Omitting the marker entirely is also valid.
    }

    [Fact]
    public void MarkerRadius_clamps_to_sane_range()
    {
        var tiny = new List<Rectangle> { new(0, 0, 10, 6) };
        int rTiny = ComicPoiGuide.MarkerRadiusFor(tiny, 640);
        Assert.InRange(rTiny, 6, 18);

        var huge = new List<Rectangle> { new(0, 0, 400, 300) };
        int rHuge = ComicPoiGuide.MarkerRadiusFor(huge, 640);
        Assert.InRange(rHuge, 6, 18);
    }

    [Fact]
    public void Poi_markers_default_off()
    {
        AppSettings.Current.ResetComicRegionSettingsToDefaults();
        Assert.False(AppSettings.Current.ComicPoiMarkers);
        Assert.False(AppSettings.Current.ComicBook);
        Assert.True(AppSettings.Current.ComicPoiFogOutside);
        Assert.True(AppSettings.Current.ComicPoiAutoStack);
    }

    [Fact]
    public void Default_prompt_names_green_rectangles()
    {
        Assert.Contains("green", ComicPoiGuide.DefaultPrompt,
            System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rectangle", ComicPoiGuide.DefaultPrompt,
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlaceMarkerCenters_prefers_upper_left_over_right_or_below()
    {
        // Plenty of room on all sides — should pick NW / left / top, not right/below.
        var regions = new List<Rectangle> { new(100, 100, 50, 40) };
        int r = 8;
        var centers = ComicPoiGuide.PlaceMarkerCenters(regions, 400, 400, r);
        Assert.Single(centers);
        var c = centers[0];
        var box = regions[0];
        Assert.True(c.X < box.X || c.Y < box.Y,
            "expected upper-left / left / top placement");
        Assert.False(c.X > box.Right && c.Y >= box.Y && c.Y <= box.Bottom,
            "should not prefer pure-right when NW/left/top are free");
    }

    [Fact]
    public void BoxSeparation_returns_vertical_gap()
    {
        var a = new Rectangle(10, 10, 40, 20);
        var b = new Rectangle(10, 50, 40, 20); // 20px gap below a
        Assert.Equal(20, ComicPoiGuide.BoxSeparationPx(a, b));
        Assert.Equal(0, ComicPoiGuide.BoxSeparationPx(a, a));
    }

    [Fact]
    public void ShouldAutoStack_when_gap_exceeds_threshold()
    {
        var boxes = new List<Rectangle>
        {
            new(10, 10, 50, 30),
            new(10, 100, 50, 30), // gap = 60
        };
        Assert.True(ComicPoiGuide.ShouldAutoStack(boxes, 8, out int maxGap));
        Assert.Equal(60, maxGap);
        Assert.False(ComicPoiGuide.ShouldAutoStack(boxes, 100, out _));
    }

    [Fact]
    public void ShouldAutoStack_gap_zero_always_when_two_plus()
    {
        var boxes = new List<Rectangle>
        {
            new(10, 10, 40, 20),
            new(10, 32, 40, 20), // 2px gap
        };
        Assert.True(ComicPoiGuide.ShouldAutoStack(boxes, 0, out _));
        Assert.False(ComicPoiGuide.ShouldAutoStack(
            new List<Rectangle> { boxes[0] }, 0, out _));
    }

    [Fact]
    public void Auto_stack_defaults()
    {
        AppSettings.Current.ResetComicRegionSettingsToDefaults();
        Assert.True(AppSettings.Current.ComicPoiAutoStack);
        Assert.Equal(ComicPoiGuide.DefaultAutoStackGapPx,
            AppSettings.Current.ComicPoiAutoStackGapPx);
    }
}
