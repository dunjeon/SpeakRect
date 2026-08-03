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
    public void Expand_regions_with_crop_pad_grows_solid_box()
    {
        var core = new Rectangle(100, 100, 40, 40);
        var expanded = OcrProcessor.ExpandRegionsWithCropPad(
            new[] { core }, capW: 400, capH: 400, padPx: 16);
        Assert.Single(expanded);
        Assert.Equal(new Rectangle(84, 84, 72, 72), expanded[0]);
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
