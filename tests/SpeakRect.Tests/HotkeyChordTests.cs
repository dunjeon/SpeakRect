using System.Windows.Forms;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class HotkeyChordTests
{
    [Fact]
    public void Parse_shift_tab()
    {
        Assert.True(HotkeyChord.TryParse("Shift+Tab", out var c));
        Assert.Equal(Keys.Tab, c.Key);
        Assert.Equal(HotkeyChord.MOD_SHIFT, c.Modifiers);
        Assert.Equal("Shift+Tab", c.ToIniString());
    }

    [Fact]
    public void Unbound_tokens()
    {
        Assert.True(HotkeyChord.IsUnboundToken("None"));
        Assert.True(HotkeyChord.IsUnboundToken("Off"));
        var empty = HotkeyChord.ParseFromIni("None", new HotkeyChord(HotkeyChord.MOD_SHIFT, Keys.F1));
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void Missing_ini_key_uses_fallback()
    {
        var fb = new HotkeyChord(HotkeyChord.MOD_CONTROL, Keys.D);
        var c = HotkeyChord.ParseFromIni(null, fb);
        Assert.Equal(fb, c);
    }

    [Fact]
    public void Invalid_parse_fails()
    {
        Assert.False(HotkeyChord.TryParse("NotARealKey+++", out _));
    }
}
