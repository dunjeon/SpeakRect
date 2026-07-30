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
    public void Dead_island_drops_art_logo_token()
    {
        // cream-style single token on non-balloon fill (ModeSmoke parity)
        using var bmp = new Bitmap(200, 200);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 40, 40, 40)); // dark art
            // light plate only in a strip (speech balloon-ish)
            g.FillRectangle(Brushes.White, 20, 20, 80, 40);
        }

        var islands = new List<(Rectangle Bounds, string Text)>
        {
            (new Rectangle(25, 25, 60, 30), "Hello there"),
            (new Rectangle(120, 120, 40, 20), "cream"),
        };
        var kept = OcrProcessor.SmokeFilterDeadDetectRegions(bmp, islands);
        Assert.Contains(kept, x => x.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase));
        // cream on dark art should drop when filter applies
        Assert.DoesNotContain(kept, x =>
            x.Text.Equals("cream", StringComparison.OrdinalIgnoreCase));
    }
}
