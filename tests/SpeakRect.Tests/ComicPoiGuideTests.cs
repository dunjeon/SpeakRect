using System.Collections.Generic;
using System.Drawing;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class ComicPoiGuideTests
{
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
    public void Auto_stack_defaults()
    {
        AppSettings.Current.ResetComicRegionSettingsToDefaults();
        Assert.True(AppSettings.Current.ComicPoiAutoStack);
        Assert.True(AppSettings.Current.ComicIslandZoom);
        Assert.Equal(
            ComicPoiGuide.IslandZoomMaxFactor,
            AppSettings.Current.ComicIslandZoomFactor);
        Assert.Equal(10, AppSettings.Current.ComicPoiAutoStackGapPx);
        Assert.Equal(12, AppSettings.Current.ComicPoiAutoStackMarginPx);
        Assert.Equal(0.0, AppSettings.Current.ComicPoiStackBeefExtra);
        Assert.Equal(0.0, AppSettings.Current.ComicPoiStackBottomPadShare);
    }

    [Fact]
    public void ApplyIslandZoom_enlarges_small_crop()
    {
        bool prev = AppSettings.Current.ComicIslandZoom;
        AppSettings.Current.ComicIslandZoom = true;
        try
        {
            using var small = new Bitmap(80, 40);
            using (var g = Graphics.FromImage(small))
                g.Clear(Color.White);

            // ApplyIslandZoomIfEnabled disposes input when it returns a new bitmap.
            var owned = (Bitmap)small.Clone();
            var zoomed = ComicPoiGuide.ApplyIslandZoomIfEnabled(owned);
            try
            {
                Assert.True(zoomed.Width > 80 || zoomed.Height > 40);
                int longEdge = Math.Max(zoomed.Width, zoomed.Height);
                Assert.True(longEdge <= ComicPoiGuide.IslandZoomTargetLongEdge + 1);
                // Max factor on 80 long
                Assert.True(longEdge <= (int)Math.Ceiling(80 * ComicPoiGuide.IslandZoomMaxFactor) + 1);
            }
            finally
            {
                if (!ReferenceEquals(zoomed, owned))
                    zoomed.Dispose();
                else
                    owned.Dispose();
            }
        }
        finally
        {
            AppSettings.Current.ComicIslandZoom = prev;
        }
    }

    [Fact]
    public void MapRectBetweenImages_scales_and_clamps()
    {
        // tone 100x100 → letterbox 200x200
        var r = ComicPoiGuide.MapRectBetweenImages(
            new Rectangle(10, 20, 30, 40), 100, 100, 200, 200);
        Assert.Equal(20, r.X);
        Assert.Equal(40, r.Y);
        Assert.Equal(60, r.Width);
        Assert.Equal(80, r.Height);
    }

    [Fact]
    public void HiResIsRicher_requires_meaningful_gain()
    {
        using var pipe = new Bitmap(900, 600);
        using var same = new Bitmap(900, 600);
        using var richer = new Bitmap(1800, 1200);
        using var tinyGain = new Bitmap(920, 620);
        Assert.False(ComicPoiGuide.HiResIsRicher(pipe, same));
        Assert.True(ComicPoiGuide.HiResIsRicher(pipe, richer));
        Assert.False(ComicPoiGuide.HiResIsRicher(pipe, tinyGain));
    }

    [Fact]
    public void BuildVerticalStack_hires_crop_path_uses_prepare()
    {
        bool prevZoom = AppSettings.Current.ComicIslandZoom;
        AppSettings.Current.ComicIslandZoom = true;
        try
        {
            using var tone = new Bitmap(100, 100);
            using (var g = Graphics.FromImage(tone))
                g.Clear(Color.Gray);
            // 2× letterbox — richer native pixels
            using var letterbox = new Bitmap(200, 200);
            using (var g = Graphics.FromImage(letterbox))
            {
                g.Clear(Color.White);
                using var ink = new SolidBrush(Color.Black);
                g.FillRectangle(ink, 20, 20, 60, 40);
            }

            bool prepCalled = false;
            var boxes = new List<Rectangle> { new(10, 10, 30, 20) };
            using var stack = ComicPoiGuide.BuildVerticalStack(
                tone,
                boxes,
                stripGapPx: 0,
                marginPx: 4,
                hiResSource: letterbox,
                prepareHiResCropOwned: crop =>
                {
                    prepCalled = true;
                    // Echo crop size; own and return
                    var copy = (Bitmap)crop.Clone();
                    crop.Dispose();
                    return copy;
                });
            Assert.True(prepCalled);
            Assert.NotNull(stack);
            // Zoom should enlarge beyond the tiny tone 30×20 crop
            Assert.True(stack!.Width > 40 || stack.Height > 30);
        }
        finally
        {
            AppSettings.Current.ComicIslandZoom = prevZoom;
        }
    }

    [Fact]
    public void ApplyIslandZoom_skips_when_disabled()
    {
        bool prev = AppSettings.Current.ComicIslandZoom;
        AppSettings.Current.ComicIslandZoom = false;
        try
        {
            var bmp = new Bitmap(50, 30);
            var same = ComicPoiGuide.ApplyIslandZoomIfEnabled(bmp);
            Assert.Same(bmp, same);
            bmp.Dispose();
        }
        finally
        {
            AppSettings.Current.ComicIslandZoom = prev;
        }
    }

    [Fact]
    public void ApplyIslandZoom_respects_mag_factor_cap()
    {
        bool prevOn = AppSettings.Current.ComicIslandZoom;
        double prevFac = AppSettings.Current.ComicIslandZoomFactor;
        AppSettings.Current.ComicIslandZoom = true;
        AppSettings.Current.ComicIslandZoomFactor = 2.0; // 200%
        try
        {
            using var small = new Bitmap(100, 50);
            using (var g = Graphics.FromImage(small))
                g.Clear(Color.White);

            var owned = (Bitmap)small.Clone();
            var zoomed = ComicPoiGuide.ApplyIslandZoomIfEnabled(owned);
            try
            {
                // 100 long × 2.0 = 200; must not race toward full 1800 target.
                Assert.Equal(200, zoomed.Width);
                Assert.Equal(100, zoomed.Height);
            }
            finally
            {
                if (!ReferenceEquals(zoomed, owned))
                    zoomed.Dispose();
                else
                    owned.Dispose();
            }
        }
        finally
        {
            AppSettings.Current.ComicIslandZoom = prevOn;
            AppSettings.Current.ComicIslandZoomFactor = prevFac;
        }
    }

    [Fact]
    public void BuildVerticalStack_uses_orange_canvas()
    {
        bool prevZoom = AppSettings.Current.ComicIslandZoom;
        AppSettings.Current.ComicIslandZoom = false;
        try
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
        finally
        {
            AppSettings.Current.ComicIslandZoom = prevZoom;
        }
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
        bool prevZoom = AppSettings.Current.ComicIslandZoom;
        AppSettings.Current.ComicIslandZoom = false;
        try
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
        finally
        {
            AppSettings.Current.ComicIslandZoom = prevZoom;
        }
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
        // Top ribbon must not expand into a lower island (that double-spoke both).
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
        // Edge-touching islands (seam at y=252): gap inflate used to hide the
        // neighbor so RecoverMinSize grew through it.
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
