using System.Drawing;
using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class AppIconTests
{
    [Fact]
    public void Embedded_app_icon_is_in_the_assembly()
    {
        using Stream? s = typeof(UiTheme).Assembly.GetManifestResourceStream("SpeakRect.app.ico");
        Assert.NotNull(s);
        Assert.True(s!.Length > 1000);
        using var icon = new Icon(s);
        Assert.True(icon.Width >= 16);
        Assert.True(icon.Height >= 16);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    public void Icon_size_can_be_pulled_and_drawn(int size)
    {
        using Stream? s = typeof(UiTheme).Assembly.GetManifestResourceStream("SpeakRect.app.ico");
        Assert.NotNull(s);
        using var icon = new Icon(s!, size, size);
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
        using Bitmap bmp = icon.ToBitmap();
        Assert.Equal(size, bmp.Width);
        Assert.Equal(size, bmp.Height);
        // Glyph is not an empty/transparent tile.
        int opaque = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).A > 16)
                    opaque++;
            }
        }
        Assert.True(opaque > size * size / 8, $"only {opaque} opaque pixels at {size}px");
    }
}
