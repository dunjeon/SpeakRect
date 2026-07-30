using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class ProfileNameTests
{
    [Fact]
    public void Accepts_simple_name()
    {
        Assert.True(AppSettings.TryNormalizeProfileName("My Game", out string clean, out _));
        Assert.Equal("My Game", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty(string? name)
    {
        Assert.False(AppSettings.TryNormalizeProfileName(name, out _, out string? err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Theory]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("a:b")]
    [InlineData("x*y")]
    public void Rejects_illegal_filename_chars(string name)
    {
        Assert.False(AppSettings.TryNormalizeProfileName(name, out _, out _));
    }

    [Fact]
    public void Rejects_too_long()
    {
        Assert.False(AppSettings.TryNormalizeProfileName(new string('a', 65), out _, out _));
    }

    [Fact]
    public void Rejects_dot_dot()
    {
        Assert.False(AppSettings.TryNormalizeProfileName("..", out _, out _));
    }
}
