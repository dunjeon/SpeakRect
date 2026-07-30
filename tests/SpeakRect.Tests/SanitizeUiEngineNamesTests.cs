using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class SanitizeUiEngineNamesTests
{
    [Theory]
    [InlineData("KoboldCPP timeout", "Local-LLM timeout")]
    [InlineData("Kobold under-read", "Local-LLM under-read")]
    [InlineData("kobold failed", "Local-LLM failed")]
    public void Host_brands_become_LocalLlm(string input, string expected)
    {
        Assert.Equal(expected, UiTheme.SanitizeUiEngineNames(input));
    }

    [Theory]
    [InlineData("WinOCR seeded 3", "OCR seeded 3")]
    [InlineData("winocr detect", "OCR detect")]
    public void Detect_brands_become_OCR(string input, string expected)
    {
        Assert.Equal(expected, UiTheme.SanitizeUiEngineNames(input));
    }

    [Fact]
    public void Never_maps_Kobold_to_OCR_alone()
    {
        string s = UiTheme.SanitizeUiEngineNames("Kobold under-read vs WinOCR");
        Assert.Contains("Local-LLM", s);
        Assert.Contains("OCR", s);
        Assert.DoesNotContain("Kobold", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WinOCR", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OCR engine", s, StringComparison.OrdinalIgnoreCase);
    }
}
