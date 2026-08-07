using System.Drawing;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class ComicRegionOverrideSessionTests : IDisposable
{
    public ComicRegionOverrideSessionTests()
    {
        ComicRegionOverrideSession.Clear();
    }

    public void Dispose()
    {
        ComicRegionOverrideSession.Clear();
    }

    [Fact]
    public void TryGetForPipeline_exact_size_returns_boxes()
    {
        var boxes = new[]
        {
            new Rectangle(10, 20, 100, 40),
            new Rectangle(50, 200, 80, 30),
        };
        ComicRegionOverrideSession.Set("page|800x600", boxes, 800, 600, basePipeline: null);

        Assert.True(ComicRegionOverrideSession.TryGetForPipeline(800, 600, out var got));
        Assert.Equal(2, got.Count);
        Assert.Equal(boxes[0], got[0]);
        Assert.Equal(boxes[1], got[1]);
    }

    [Fact]
    public void TryGetForPipeline_mismatched_aspect_rejects()
    {
        ComicRegionOverrideSession.Set(
            "page|800x600",
            new[] { new Rectangle(10, 20, 100, 40) },
            800, 600, basePipeline: null);

        Assert.False(ComicRegionOverrideSession.TryGetForPipeline(800, 400, out _));
    }

    [Fact]
    public void TryGetForPipeline_uniform_scale_maps_boxes()
    {
        ComicRegionOverrideSession.Set(
            "page|400x300",
            new[] { new Rectangle(40, 30, 100, 50) },
            400, 300, basePipeline: null);

        // 2× scale, same aspect
        Assert.True(ComicRegionOverrideSession.TryGetForPipeline(800, 600, out var got));
        Assert.Single(got);
        Assert.Equal(new Rectangle(80, 60, 200, 100), got[0]);
    }

    [Fact]
    public void TryGetForPipeline_inactive_returns_false()
    {
        Assert.False(ComicRegionOverrideSession.TryGetForPipeline(800, 600, out _));
    }
}
