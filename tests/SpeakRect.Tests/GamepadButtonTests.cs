using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class GamepadButtonTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("LT")]
    [InlineData("DPadUp")]
    [InlineData("LSUp")]
    public void Parse_common_bindings(string raw)
    {
        Assert.True(GamepadButton.TryParse(raw, out var b));
        Assert.False(b.IsEmpty);
        Assert.False(string.IsNullOrEmpty(b.ToIniString()));
    }

    [Fact]
    public void Empty_parse()
    {
        Assert.True(GamepadButton.ParseOrEmpty("").IsEmpty);
        Assert.False(GamepadButton.TryParse("NotAButton", out _));
    }

    [Fact]
    public void DPad_equality_across_kinds()
    {
        var a = GamepadButton.DPadUp;
        var b = GamepadButton.FromDigital(GamepadButton.DPAD_UP);
        Assert.Equal(a, b);
    }
}
