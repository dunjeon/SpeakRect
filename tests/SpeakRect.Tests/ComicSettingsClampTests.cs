using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class ComicSettingsClampTests
{
    [Fact]
    public void Fog_amount_clamps()
    {
        var s = AppSettings.Current;
        float prev = s.ComicDetectFogAmount;
        try
        {
            s.ComicDetectFogAmount = 9f;
            s.NormalizeComicRegionSettings();
            Assert.InRange(s.ComicDetectFogAmount, 0f, 1f);
            s.ComicDetectFogAmount = -1f;
            s.NormalizeComicRegionSettings();
            Assert.InRange(s.ComicDetectFogAmount, 0f, 1f);
        }
        finally
        {
            s.ComicDetectFogAmount = prev;
            s.NormalizeComicRegionSettings();
        }
    }

    [Fact]
    public void Sequential_regions_defaults_on()
    {
        AppSettings.Current.ResetComicRegionSettingsToDefaults();
        Assert.True(AppSettings.Current.ComicSequentialRegions);
    }

    [Fact]
    public void Merge_overlapping_defaults_on()
    {
        AppSettings.Current.ResetComicRegionSettingsToDefaults();
        Assert.True(AppSettings.Current.ComicMergeOverlappingIslands);
    }
}

