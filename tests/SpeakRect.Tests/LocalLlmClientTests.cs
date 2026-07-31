using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class LocalLlmClientTests
{
    [Fact]
    public void Vision_json_payload_has_wire_keys()
    {
        Assert.True(LocalLlmClient.SmokeVerifyJsonShape(out string json));
        Assert.Contains("\"model\"", json, StringComparison.Ordinal);
        Assert.Contains("\"messages\"", json, StringComparison.Ordinal);
        Assert.Contains("\"image_url\"", json, StringComparison.Ordinal);
        Assert.Contains("\"content\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserContent_includes_image_and_prompt()
    {
        var arr = LocalLlmClient.BuildUserContent("data:image/png;base64,AA==", "read me");
        string s = arr.ToJsonString();
        Assert.Contains("image_url", s, StringComparison.Ordinal);
        Assert.Contains("read me", s, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeModelText_keeps_latin_dialogue_and_basic_punct()
    {
        string raw = "daemonites?! will someone please tell me what's going on?";
        Assert.Equal(
            "daemonites?! will someone please tell me what's going on?",
            LocalLlmClient.SanitizeModelText(raw));
    }

    [Fact]
    public void SanitizeModelText_maps_typography_and_drops_non_latin()
    {
        // Curly quotes / em dash / ellipsis → ASCII; CJK + private-use dropped.
        // Latin "junk" after PUA stays (allow-list is script-based, not dictionary).
        string raw = "\u201CHello\u201D\u2014world\u2026 \u4e16\u754c \uE000\uE001";
        string got = LocalLlmClient.SanitizeModelText(raw);
        Assert.Equal("\"Hello\"-world...", got);
    }

    [Fact]
    public void SanitizeModelText_keeps_latin1_letters()
    {
        Assert.Equal("café naïve", LocalLlmClient.SanitizeModelText("café naïve"));
    }

    [Fact]
    public void SanitizeModelText_empty_and_null()
    {
        Assert.Equal("", LocalLlmClient.SanitizeModelText(null));
        Assert.Equal("", LocalLlmClient.SanitizeModelText(""));
        Assert.Equal("", LocalLlmClient.SanitizeModelText("   \n\t  "));
        Assert.Equal("", LocalLlmClient.SanitizeModelText("\u4e16\u754c\uE000"));
    }
}
