using System.Drawing;
using System.IO;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class OverlaySettingsTests
{
    [Fact]
    public void Overlay_knobs_round_trip_ini()
    {
        var s = AppSettings.Current;
        bool prevPause = s.OverlayPauseUnderlay;
        bool prevSnap = s.OverlayDrawOnScreenshot;
        string path = Path.Combine(
            Path.GetTempPath(), "SpeakRect-overlay-rt-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            s.OverlayPauseUnderlay = true;
            s.OverlayDrawOnScreenshot = true;
            s.SaveTo(path);

            s.OverlayPauseUnderlay = false;
            s.OverlayDrawOnScreenshot = false;
            s.LoadFrom(path, resetFirst: false);
            Assert.True(s.OverlayPauseUnderlay);
            Assert.True(s.OverlayDrawOnScreenshot);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
            s.OverlayPauseUnderlay = prevPause;
            s.OverlayDrawOnScreenshot = prevSnap;
        }
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("explorer.exe")]
    [InlineData("dwm")]
    [InlineData("SpeakRect")]
    [InlineData("koboldcpp")]
    [InlineData("")]
    [InlineData(null)]
    public void Protected_names_are_never_held(string? name)
    {
        Assert.True(OverlayUnderlay.IsProtectedProcessName(name));
    }

    [Fact]
    public void Ordinary_game_name_is_not_protected()
    {
        Assert.False(OverlayUnderlay.IsProtectedProcessName("MyCoolRpg"));
    }

    [Fact]
    public void Self_pid_cannot_be_held()
    {
        Assert.False(OverlayUnderlay.CanHoldPid(Environment.ProcessId));
        Assert.False(OverlayUnderlay.CanHoldPid(0));
        Assert.False(OverlayUnderlay.CanHoldPid(4));
    }

    [Fact]
    public void Snapshot_map_keeps_screen_origin()
    {
        var snap = new Rectangle(0, 0, 1920, 1080);
        Assert.Equal(
            new Rectangle(10, 20, 30, 40),
            OverlayUnderlay.MapScreenRectToSnapshot(new Rectangle(10, 20, 30, 40), snap));
    }

    [Fact]
    public void Snapshot_map_handles_negative_virtual_origin()
    {
        var snap = new Rectangle(-100, -50, 2000, 1200);
        Assert.Equal(
            new Rectangle(100, 50, 10, 10),
            OverlayUnderlay.MapScreenRectToSnapshot(new Rectangle(0, 0, 10, 10), snap));
    }

    [Fact]
    public void DiscardSnapshot_is_safe_when_empty()
    {
        OverlayUnderlay.DiscardSnapshot();
        OverlayUnderlay.EndForOverlay();
        Assert.False(OverlayUnderlay.HasSnapshot);
        Assert.False(OverlayUnderlay.IsHoldingProcess);
    }

    [Fact]
    public void Snapshot_map_empty_when_no_overlap()
    {
        var snap = new Rectangle(0, 0, 100, 100);
        Assert.True(OverlayUnderlay.MapScreenRectToSnapshot(
            new Rectangle(500, 500, 10, 10), snap).IsEmpty);
    }
}
