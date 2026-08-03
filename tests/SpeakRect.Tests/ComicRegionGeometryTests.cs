using System.Drawing;
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
}
