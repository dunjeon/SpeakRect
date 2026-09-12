using System.Collections.Generic;
using System.Text;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class ComicBestOfFusionTests
{
    [Fact]
    public void One_word_full_frame_is_weak_so_crops_win()
    {
        var detail = new StringBuilder();
        var full = new List<string> { "Hi" };
        var crops = new List<ComicBestOfFusion.CropRead>
        {
            new(0, "hello there friend how are you today"),
        };

        var (chosen, tag) = ComicBestOfFusion.PickBestOfFullVsCrops(full, crops, detail);

        Assert.Equal("crops", tag);
        Assert.Single(chosen);
        Assert.Contains("hello there", chosen[0], System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_word_full_frame_counts_as_usable()
    {
        var detail = new StringBuilder();
        var full = new List<string> { "Hello there" };
        var crops = new List<ComicBestOfFusion.CropRead>();

        var (_, tag) = ComicBestOfFusion.PickBestOfFullVsCrops(full, crops, detail);

        Assert.Equal("full-frame", tag);
    }
}
