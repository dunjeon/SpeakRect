using System.Drawing;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class GeometryPipelineTests
{
    [Fact]
    public void Sort_side_by_side_left_first()
    {
        var boxes = new[]
        {
            new Rectangle(200, 50, 80, 60),
            new Rectangle(40, 40, 80, 70),
        };
        var ordered = OcrProcessor.SmokeSortComicReadingOrder(boxes);
        Assert.Equal(2, ordered.Count);
        Assert.True(ordered[0].Left < ordered[1].Left);
    }

    [Fact]
    public void Sort_vertical_stack_upper_first()
    {
        var boxes = new[]
        {
            new Rectangle(50, 200, 100, 50),
            new Rectangle(50, 40, 100, 50),
        };
        var ordered = OcrProcessor.SmokeSortComicReadingOrder(boxes);
        Assert.True(ordered[0].Top < ordered[1].Top);
    }

    [Fact]
    public void Sort_elevated_right_still_left_first()
    {
        // Slightly higher right reply must not seed before the left main balloon.
        var left = new Rectangle(100, 80, 200, 180);
        var rightHigher = new Rectangle(360, 30, 150, 100);
        var ordered = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { rightHigher, left });
        Assert.Equal(left, ordered[0]);
        Assert.Equal(rightHigher, ordered[1]);
    }

    [Fact]
    public void Sort_2x2_grid_row_major()
    {
        var tl = new Rectangle(40, 30, 120, 80);
        var tr = new Rectangle(220, 40, 110, 70);
        var bl = new Rectangle(50, 180, 120, 80);
        var br = new Rectangle(230, 190, 110, 70);
        var ordered = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { br, bl, tr, tl });
        Assert.Equal(new[] { tl, tr, bl, br }, ordered);
    }

    [Fact]
    public void Sort_tight_left_column_before_right_balloons()
    {
        // Cyblade-like strip: left caption stack (touching) then top dialogue row.
        // Close vertical proximity must win over false same-row Y-overlap with
        // tall right balloons — read the left column top→bottom first.
        var c1 = new Rectangle(0, 0, 202, 84);
        var c2 = new Rectangle(0, 84, 202, 105);
        var c3 = new Rectangle(0, 189, 332, 97);
        var b1 = new Rectangle(202, 0, 130, 117);
        var b2 = new Rectangle(332, 0, 154, 230);
        var b3 = new Rectangle(486, 0, 313, 96);
        var b4 = new Rectangle(783, 31, 117, 51);
        var b5 = new Rectangle(701, 195, 118, 91);

        var ordered = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { b5, b2, c2, b4, c1, b3, c3, b1 });

        Assert.Equal(8, ordered.Count);
        Assert.Equal(c1, ordered[0]);
        Assert.Equal(c2, ordered[1]);
        Assert.Equal(c3, ordered[2]);
        Assert.Equal(b1, ordered[3]);
        Assert.Equal(b2, ordered[4]);
        Assert.Equal(b3, ordered[5]);
        Assert.Equal(b4, ordered[6]);
        Assert.Equal(b5, ordered[7]);
    }

    [Fact]
    public void Sort_grown_side_balloons_still_left_to_right()
    {
        var leftGrown = new Rectangle(90, 40, 280, 170);
        var rightGrown = new Rectangle(300, 25, 160, 100);
        var ordered = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { rightGrown, leftGrown });
        Assert.Equal(leftGrown, ordered[0]);
        Assert.Equal(rightGrown, ordered[1]);
    }

    [Fact]
    public void Sort_caption_strip_before_lower_callout()
    {
        var strip = new Rectangle(20, 10, 500, 70);
        var callout = new Rectangle(40, 100, 160, 90);
        var ordered = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { callout, strip });
        Assert.Equal(strip, ordered[0]);
        Assert.Equal(callout, ordered[1]);
    }

    [Fact]
    public void Merge_overlapping_unions()
    {
        var boxes = new[]
        {
            new Rectangle(10, 10, 50, 50),
            new Rectangle(40, 40, 50, 50),
        };
        var merged = OcrProcessor.SmokeMergeOverlappingIslands(boxes, 500, 500, cropPadPx: 0);
        Assert.Single(merged);
        Assert.True(merged[0].Width >= 70);
        Assert.True(merged[0].Height >= 70);
    }

    [Fact]
    public void Merge_with_pad_bridges_gap()
    {
        var boxes = new[]
        {
            new Rectangle(10, 10, 40, 40),
            new Rectangle(60, 10, 40, 40), // 10px gap
        };
        var separate = OcrProcessor.SmokeMergeOverlappingIslands(boxes, 500, 500, cropPadPx: 0);
        Assert.Equal(2, separate.Count);

        var bridged = OcrProcessor.SmokeMergeOverlappingIslands(boxes, 500, 500, cropPadPx: 8);
        Assert.Single(bridged);
    }

    [Fact]
    public void Separate_side_by_side_no_positive_overlap()
    {
        // Grown boxes overlap; OCR cores have a gap — §4 should fence at the gap.
        var cores = new[]
        {
            new Rectangle(10, 20, 40, 40),   // 10..50
            new Rectangle(70, 20, 40, 40),   // 70..110, 20px gap
        };
        var grown = new[]
        {
            new Rectangle(0, 10, 80, 60),    // 0..80  overlaps right
            new Rectangle(50, 10, 80, 60),   // 50..130
        };
        var outBoxes = OcrProcessor.SmokeSeparateOverlappingIslands(
            grown, cores, capW: 500, capH: 500);
        Assert.Equal(2, outBoxes.Count);

        var inter = Rectangle.Intersect(outBoxes[0], outBoxes[1]);
        Assert.True(inter.Width <= 0 || inter.Height <= 0,
            $"expected no overlap after separate; a={outBoxes[0]} b={outBoxes[1]} inter={inter}");

        // Each result must still cover its OCR core.
        Assert.True(outBoxes[0].Contains(cores[0]) ||
                    Rectangle.Intersect(outBoxes[0], cores[0]) == cores[0]);
        Assert.True(outBoxes[1].Contains(cores[1]) ||
                    Rectangle.Intersect(outBoxes[1], cores[1]) == cores[1]);
    }

    [Fact]
    public void Separate_vertical_stack_no_positive_overlap()
    {
        var cores = new[]
        {
            new Rectangle(40, 10, 50, 40),   // top 10..50
            new Rectangle(40, 70, 50, 40),   // bot 70..110
        };
        var grown = new[]
        {
            new Rectangle(30, 0, 70, 80),    // 0..80
            new Rectangle(30, 50, 70, 80),   // 50..130
        };
        var outBoxes = OcrProcessor.SmokeSeparateOverlappingIslands(
            grown, cores, capW: 500, capH: 500);
        Assert.Equal(2, outBoxes.Count);
        var inter = Rectangle.Intersect(outBoxes[0], outBoxes[1]);
        Assert.True(inter.Width <= 0 || inter.Height <= 0,
            $"expected no overlap; a={outBoxes[0]} b={outBoxes[1]} inter={inter}");
    }

    [Fact]
    public void Separate_keeps_count_unlike_merge()
    {
        var grown = new[]
        {
            new Rectangle(10, 10, 50, 50),
            new Rectangle(40, 40, 50, 50),
        };
        // Cores still overlap a little — separate keeps 2 islands; merge would union to 1.
        var cores = new[]
        {
            new Rectangle(15, 15, 30, 30),
            new Rectangle(55, 55, 30, 30),
        };
        var sep = OcrProcessor.SmokeSeparateOverlappingIslands(grown, cores, 500, 500);
        Assert.Equal(2, sep.Count);
        var merged = OcrProcessor.SmokeMergeOverlappingIslands(grown, 500, 500, cropPadPx: 0);
        Assert.Single(merged);
    }

    [Fact]
    public void Expand_regions_with_crop_pad_grows_solid_box()
    {
        var core = new Rectangle(100, 100, 40, 40);
        var expanded = OcrProcessor.ExpandRegionsWithCropPad(
            new[] { core }, capW: 400, capH: 400, padPx: 16);
        Assert.Single(expanded);
        Assert.Equal(new Rectangle(84, 84, 72, 72), expanded[0]);
    }

    [Fact]
    public void Crop_pad_spends_budget_up_when_bottom_is_blocked()
    {
        // Core near bottom with a neighbor just below: pad down is tiny, so leftover
        // pad budget must go up (stop short of any neighbor above / frame top).
        var core = new Rectangle(100, 200, 40, 40);   // 200..240
        var lower = new Rectangle(100, 250, 40, 40);  // 10px gap below
        const int pad = 30;
        var crop = OcrProcessor.ComputeSpeakCropRect(
            core, pad, capW: 400, capH: 400, neighborCores: new[] { core, lower });

        Assert.True(crop.Top <= core.Top && crop.Bottom >= core.Bottom);
        Assert.True(crop.Bottom <= lower.Top,
            $"must not invade lower; crop={crop} lower={lower}");
        // Full vertical budget ≈ 2*pad when free; here bottom is blocked so top
        // should take more than plain symmetric pad (core.Top - pad).
        Assert.True(crop.Top < core.Top - pad,
            $"expected extra upward pad; crop.Top={crop.Top} core.Top={core.Top}");
        int topPad = core.Top - crop.Top;
        int botPad = crop.Bottom - core.Bottom;
        Assert.True(topPad + botPad >= pad,
            $"should use available pad budget; topPad={topPad} botPad={botPad}");
    }

    [Fact]
    public void Crop_pad_spends_budget_up_when_low_in_frame()
    {
        var core = new Rectangle(100, 360, 40, 30); // bottom 390 on 400 frame
        const int pad = 40;
        var crop = OcrProcessor.ComputeSpeakCropRect(
            core, pad, capW: 400, capH: 400, neighborCores: null);

        Assert.Equal(400, crop.Bottom); // hits frame bottom
        Assert.True(crop.Top < core.Top - (400 - core.Bottom),
            "leftover pad budget should extend upward past one-sided bottom clip");
        Assert.True(crop.Top <= core.Top - pad || crop.Height >= core.Height + pad,
            $"expected meaningful upward growth; crop={crop}");
    }

    [Fact]
    public void Dead_island_keeps_multiword_on_balloon()
    {
        using var bmp = new Bitmap(200, 200);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 40, 40, 40));
            g.FillRectangle(Brushes.White, 20, 20, 80, 40);
        }

        var islands = new List<(Rectangle Bounds, string Text)>
        {
            (new Rectangle(25, 25, 60, 30), "Hello there"),
        };
        var kept = OcrProcessor.SmokeFilterDeadDetectRegions(bmp, islands);
        Assert.Contains(kept, x => x.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dead_island_drops_empty_tiny_geometry()
    {
        using var bmp = new Bitmap(200, 200);
        using (var g = Graphics.FromImage(bmp))
            g.Clear(Color.FromArgb(255, 40, 40, 40));

        var islands = new List<(Rectangle Bounds, string Text)>
        {
            (new Rectangle(10, 10, 20, 15), ""),
            (new Rectangle(40, 40, 100, 80), "Hello there friend"),
        };
        var kept = OcrProcessor.SmokeFilterDeadDetectRegions(bmp, islands);
        Assert.DoesNotContain(kept, x => string.IsNullOrWhiteSpace(x.Text) && x.Bounds.Width < 80);
        Assert.Contains(kept, x => x.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase));
    }
}
