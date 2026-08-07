using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class ComicRegionGeometryTests
{
    [Fact]
    public void Under_read_when_crop_much_shorter()
    {
        string win = string.Join(' ', Enumerable.Repeat("word", 54));
        string kobold = string.Join(' ', Enumerable.Repeat("word", 29));
        Assert.True(ComicRegionGeometry.KoboldUnderReadsWinOcr(kobold, win));
    }

    [Fact]
    public void Matching_counts_not_under_read()
    {
        string t = string.Join(' ', Enumerable.Repeat("word", 20));
        Assert.False(ComicRegionGeometry.KoboldUnderReadsWinOcr(t, t));
    }

    [Fact]
    public void CountWords_basic()
    {
        Assert.Equal(0, ComicRegionGeometry.CountWords(""));
        Assert.Equal(3, ComicRegionGeometry.CountWords("hello world 123"));
    }

    [Fact]
    public void Snap_envelope_unions_all_islands_to_one_box()
    {
        var regions = new List<DetectedTextRegion>
        {
            new() { Bounds = new Rectangle(10, 20, 40, 30), WinOcrText = "Hello" },
            new() { Bounds = new Rectangle(100, 80, 50, 40), WinOcrText = "World" },
            new() { Bounds = new Rectangle(30, 120, 20, 15), WinOcrText = "!" },
        };

        var one = ComicRegionGeometry.CollapseToSnapEnvelope(regions, capW: 200, capH: 200);
        Assert.Single(one);
        Assert.Equal(new Rectangle(10, 20, 140, 115), one[0].Bounds);
        Assert.Contains("Hello", one[0].WinOcrText);
        Assert.Contains("World", one[0].WinOcrText);
    }

    [Fact]
    public void Snap_envelope_single_or_empty_unchanged_count()
    {
        var empty = ComicRegionGeometry.CollapseToSnapEnvelope(
            new List<DetectedTextRegion>(), 100, 100);
        Assert.Empty(empty);

        var one = new List<DetectedTextRegion>
        {
            new() { Bounds = new Rectangle(5, 5, 10, 10), WinOcrText = "x" }
        };
        var same = ComicRegionGeometry.CollapseToSnapEnvelope(one, 100, 100);
        Assert.Single(same);
        Assert.Equal(new Rectangle(5, 5, 10, 10), same[0].Bounds);
    }
}
