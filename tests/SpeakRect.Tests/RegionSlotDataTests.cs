using System.Drawing;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class RegionSlotDataTests
{
    [Fact]
    public void Rect_round_trips()
    {
        var slot = new RegionSlotData();
        slot.SetBox("Rect", new Rectangle(10, 20, 30, 40));
        string ini = slot.ToIniString();
        var back = RegionSlotData.Parse(ini);
        Assert.Equal("Rect", back.Mode);
        Assert.Equal(new Rectangle(10, 20, 30, 40), back.ToRectangle());
        Assert.False(back.IsEmpty);
    }

    [Fact]
    public void Oval_round_trips()
    {
        var slot = new RegionSlotData();
        slot.SetBox("Oval", new Rectangle(1, 2, 3, 4));
        var back = RegionSlotData.Parse(slot.ToIniString());
        Assert.True(back.IsOvalMode);
        Assert.Equal(1, back.X);
    }

    [Fact]
    public void Lasso_uses_pipe_separators()
    {
        var slot = new RegionSlotData();
        slot.SetLasso(new[]
        {
            new Point(10, 10), new Point(200, 15), new Point(180, 120), new Point(10, 10)
        });
        string ini = slot.ToIniString();
        Assert.StartsWith("Lasso:", ini);
        Assert.Contains('|', ini);
        Assert.DoesNotContain(';', ini);

        var back = RegionSlotData.Parse(ini);
        Assert.True(back.IsLassoMode);
        Assert.Equal(4, back.GetLassoPoints().Count);
    }

    [Fact]
    public void Legacy_semicolon_lasso_parses()
    {
        var back = RegionSlotData.Parse("Lasso:1,1;2,3;4,5;1,1");
        Assert.True(back.IsLassoMode);
        Assert.True(back.GetLassoPoints().Count >= 3);
    }
}
