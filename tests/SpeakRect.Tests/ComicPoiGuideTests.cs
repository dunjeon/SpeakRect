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
    public void Default_prompt_is_sole_ocr_instruction()
    {
        Assert.Equal(AppSettings.DefaultOcrPrompt, ComicPoiGuide.DefaultPrompt);
        Assert.Contains("english text", ComicPoiGuide.DefaultPrompt,
            System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("html", ComicPoiGuide.DefaultPrompt,
            System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("markdown", ComicPoiGuide.DefaultPrompt,
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
        // Canvas compose is fixed (not AppSettings): gap 10, margin 12, beef 0, bottom 0.
        Assert.Equal(10, ComicPoiGuide.DefaultAutoStackGapPx);
        Assert.Equal(12, ComicPoiGuide.LlmSendStackMarginPx);
        Assert.Equal(0.0, ComicPoiGuide.DefaultStackBeefExtra);
        Assert.Equal(0.0, ComicPoiGuide.DefaultStackBottomPadShare);
    }

    [Fact]
    public void BuildVerticalStack_uses_orange_canvas()
    {
        using var src = new Bitmap(80, 80);
        using (var g = Graphics.FromImage(src))
        {
            g.Clear(Color.White);
            using var ink = new SolidBrush(Color.Black);
            g.FillRectangle(ink, 8, 8, 28, 16);
            g.FillRectangle(ink, 8, 48, 28, 16);
        }

        var boxes = new List<Rectangle>
        {
            new(5, 5, 34, 22),
            new(5, 45, 34, 22),
        };
        using var stack = ComicPoiGuide.BuildVerticalStack(
            src, boxes, stripGapPx: 8, marginPx: 12);
        Assert.NotNull(stack);
        // Corner is margin fill — orange canvas, not white balloon paper.
        Color corner = stack!.GetPixel(0, 0);
        Assert.Equal(ComicPoiGuide.StackCanvasColor.R, corner.R);
        Assert.Equal(ComicPoiGuide.StackCanvasColor.G, corner.G);
        Assert.Equal(ComicPoiGuide.StackCanvasColor.B, corner.B);
    }

    [Fact]
    public void ComputeBeefyStackCanvas_default_beef_is_tight()
    {
        ComicPoiGuide.ComputeBeefyStackCanvas(
            300, 120,
            out int cw, out int ch,
            out float ox, out float oy,
            ComicPoiGuide.DefaultStackBeefExtra,
            ComicPoiGuide.DefaultStackBottomPadShare);
        Assert.Equal(300, cw);
        Assert.Equal(120, ch);
        Assert.Equal(0f, ox);
        Assert.Equal(0f, oy);
    }

    [Fact]
    public void ComputeBeefyStackCanvas_bottom_heavy_puts_pad_below()
    {
        ComicPoiGuide.ComputeBeefyStackCanvas(
            300, 120,
            out int cw, out int ch,
            out float ox, out float oy,
            beefExtra: 1.0 / 3.0,
            bottomPadShare: 0.85);
        int padY = ch - 120;
        // Most of the vertical pad should sit below content (small offsetY).
        Assert.True(oy < padY * 0.25f);
        Assert.InRange(ox, 49f, 51f); // sides still centered
    }

    [Fact]
    public void Default_stack_canvas_knobs()
    {
        Assert.Equal(0.0, ComicPoiGuide.DefaultStackBeefExtra);
        Assert.Equal(0.0, ComicPoiGuide.DefaultStackBottomPadShare);
        Assert.Equal(10, ComicPoiGuide.DefaultAutoStackGapPx);
        Assert.Equal(12, ComicPoiGuide.LlmSendStackMarginPx);
    }

    [Fact]
    public void BuildVerticalStack_canvas_includes_margin_around_content()
    {
        using var src = new Bitmap(200, 100);
        using (var g = Graphics.FromImage(src))
        {
            g.Clear(Color.White);
            using var ink = new SolidBrush(Color.Black);
            g.FillRectangle(ink, 10, 10, 80, 30);
        }

        var boxes = new List<Rectangle> { new(5, 5, 100, 50) };
        using var stack = ComicPoiGuide.BuildVerticalStack(
            src, boxes, stripGapPx: 8, marginPx: 12);
        Assert.NotNull(stack);
        Assert.True(stack!.Width >= 100 + 24);
        Assert.True(stack.Height >= 50);
    }

    [Fact]
    public void Wide_ribbon_gate_from_archive_geometry()
    {
        var cases = new (int W, int H, int FW, int FH, bool Expand)[]
        {
            (900, 162, 900, 598, true),
            (900, 296, 900, 699, true),
            (892, 242, 900, 600, true),
            (695, 201, 900, 479, true),
            (587, 209, 900, 785, true),
            (448, 116, 900, 710, true),
            (191, 88, 900, 273, false),
            (186, 106, 900, 273, false),
            (184, 127, 900, 867, false),
            (128, 101, 900, 381, false),
            (255, 174, 900, 540, false),
            (440, 240, 900, 765, false),
        };
        foreach (var c in cases)
        {
            bool got = ComicPoiGuide.IsWideThinIslandStrip(
                new Rectangle(0, 0, c.W, c.H), c.FW, c.FH, boxCountOnCanvas: 1);
            Assert.True(got == c.Expand,
                $"tight {c.W}x{c.H} on {c.FW}x{c.FH}: expand want={c.Expand} got={got}");
        }
        Assert.False(ComicPoiGuide.IsWideThinIslandStrip(
            new Rectangle(0, 0, 900, 160), 900, 600, boxCountOnCanvas: 2));
    }

    [Fact]
    public void Wide_ribbon_expands_to_min_height()
    {
        var tight = new Rectangle(0, 0, 900, 162);
        Assert.True(ComicPoiGuide.IsWideThinIslandStrip(tight, 900, 598, 1));
        var grown = ComicPoiGuide.ExpandIslandCropToMinSize(
            tight, 900, 598, 0, ComicPoiGuide.IslandStripMinHeight);
        Assert.Equal(900, grown.Width);
        Assert.Equal(480, grown.Height);
    }

    [Fact]
    public void Wide_ribbon_expand_clamps_before_neighbor_island()
    {
        // Last-run failure: top ribbon 458×146 @0,0 expanded to H=480 and ate
        // lower balloon @305,283 173×140 — double-spoke "no he stays / neither".
        var top = new Rectangle(0, 0, 458, 146);
        var lower = new Rectangle(305, 283, 173, 140);
        var grown = ComicPoiGuide.ExpandIslandCropToMinSize(
            top, frameW: 478, frameH: 900,
            minWidth: 0,
            minHeight: ComicPoiGuide.IslandStripMinHeight,
            avoidIslands: new[] { top, lower });
        Assert.True(grown.Contains(top) || Rectangle.Intersect(grown, top) == top ||
            (grown.Left <= top.Left && grown.Right >= top.Right &&
             grown.Top <= top.Top && grown.Bottom >= top.Bottom));
        // Must not cover the lower island core.
        Assert.True(
            grown.Bottom <= lower.Top,
            $"expanded bottom {grown.Bottom} must be ≤ lower.Top {lower.Top}; grown={grown}");
        Assert.True(grown.Height < ComicPoiGuide.IslandStripMinHeight ||
                    grown.Bottom <= lower.Top);
    }

    [Fact]
    public void Wide_ribbon_expands_up_when_bottom_blocked_by_neighbor()
    {
        // Tight island low in free band: little room below (neighbor), lots above.
        // Old path centered then cut the bottom → short crop; must reclaim height upward.
        var tight = new Rectangle(50, 500, 400, 80);   // 500..580
        var lower = new Rectangle(50, 620, 400, 80);   // gap 40 below
        const int minH = 400;
        var grown = ComicPoiGuide.ExpandIslandCropToMinSize(
            tight, frameW: 500, frameH: 900,
            minWidth: 0,
            minHeight: minH,
            avoidIslands: new[] { tight, lower });

        Assert.True(grown.Top <= tight.Top && grown.Bottom >= tight.Bottom,
            $"must contain tight; grown={grown}");
        Assert.True(grown.Bottom <= lower.Top - ComicPoiGuide.IslandExpandNeighborGapPx + 1 ||
                    grown.Bottom <= lower.Top,
            $"must stop before lower island; grown={grown} lower={lower}");
        Assert.True(grown.Top < tight.Top,
            $"must expand upward into free space; grown={grown}");
        Assert.True(grown.Height >= minH - 1,
            $"should reach min height by growing up; grown H={grown.Height}");
    }

    [Fact]
    public void Wide_ribbon_edge_touch_neighbor_must_not_swallow()
    {
        // debug_images 2026-08-05 Titania page (merge-overlap OFF):
        // island1 824×207 @12,45 touches island2 798×404 @102,252 (seam at y=252).
        // Bug: gap-inflate made "clearly below" fail; RecoverMinSize re-grew to
        // minH=480 @y=0 and VL double-spoke Titania text.
        var top = new Rectangle(12, 45, 824, 207);     // bottom 252
        var lower = new Rectangle(102, 252, 798, 404); // top 252 (edge touch)
        var grown = ComicPoiGuide.ExpandIslandCropToMinSize(
            top, frameW: 900, frameH: 851,
            minWidth: 0,
            minHeight: ComicPoiGuide.IslandStripMinHeight,
            avoidIslands: new[] { top, lower });

        Assert.True(grown.Contains(top) ||
            (grown.Left <= top.Left && grown.Right >= top.Right &&
             grown.Top <= top.Top && grown.Bottom >= top.Bottom),
            $"must contain top; grown={grown}");
        Assert.True(
            grown.Bottom <= lower.Top,
            $"must not enter lower island; grown.Bottom={grown.Bottom} lower.Top={lower.Top}");
        // Prefer stopping short by the gap when room exists.
        Assert.True(
            grown.Bottom <= lower.Top - ComicPoiGuide.IslandExpandNeighborGapPx + 1 ||
            grown.Bottom <= lower.Top,
            $"prefer gap before lower; grown={grown}");
        // Must not claim full minH by eating the next balloon.
        Assert.True(
            grown.Height < ComicPoiGuide.IslandStripMinHeight ||
            grown.Bottom <= lower.Top,
            $"full minH only if still clear of lower; grown H={grown.Height}");
    }

    [Fact]
    public void Wide_ribbon_expands_up_when_low_in_frame()
    {
        // Near frame bottom: no room down — full minH must come from above.
        var tight = new Rectangle(20, 700, 450, 100); // bottom 800 on frame 850
        const int minH = 400;
        var grown = ComicPoiGuide.ExpandIslandCropToMinSize(
            tight, frameW: 500, frameH: 850,
            minWidth: 0,
            minHeight: minH);

        Assert.True(grown.Contains(tight) ||
            (grown.Left <= tight.Left && grown.Right >= tight.Right &&
             grown.Top <= tight.Top && grown.Bottom >= tight.Bottom));
        Assert.Equal(minH, grown.Height);
        Assert.True(grown.Top < tight.Top);
        Assert.True(grown.Bottom <= 850);
    }
}
