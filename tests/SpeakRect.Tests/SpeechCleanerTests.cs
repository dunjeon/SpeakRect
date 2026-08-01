using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class SpeechCleanerTests
{
    [Fact]
    public void Expands_mr_title()
    {
        string cleaned = SpeechCleaner.CleanForSpeech("Hello Mr. Smith.", comicBook: true);
        Assert.Contains("mister", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Keeps_contraction_youre()
    {
        string cleaned = SpeechCleaner.CleanForSpeech("you're fine", comicBook: true);
        Assert.Contains("you're", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("you are", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_is_unusable()
    {
        Assert.True(SpeechCleaner.IsUnusable(""));
        Assert.True(SpeechCleaner.IsUnusable("   "));
    }

    [Fact]
    public void Short_dialogue_usable()
    {
        Assert.False(SpeechCleaner.IsUnusable(
            OcrProcessor.SmokeCleanForSpeech("No!", comicBook: true)));
    }

    [Fact]
    public void StripMarkdownLlmJunk_removes_fence_and_keeps_dialogue()
    {
        string raw =
            "```json\n" +
            "VENNOR...! THAT'S WHERE I WENT TO SCHOOL!\n" +
            "```";
        string s = SpeechCleaner.StripMarkdownLlmJunk(raw);
        Assert.Contains("VENNOR", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("```", s, StringComparison.Ordinal);
        Assert.DoesNotContain("json", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripMarkdownLlmJunk_unclosed_fence_and_preamble()
    {
        string raw =
            "Here is the extracted text:\n" +
            "```\n" +
            "Hello from the balloon\n";
        string s = SpeechCleaner.StripMarkdownLlmJunk(raw);
        Assert.Contains("Hello from the balloon", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("```", s, StringComparison.Ordinal);
        Assert.DoesNotContain("extracted text", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripMarkdownLlmJunk_bold_and_heading()
    {
        string raw = "## Caption\n**REALLY?** I went to school!";
        string s = SpeechCleaner.StripMarkdownLlmJunk(raw);
        Assert.Contains("REALLY", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("school", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("**", s, StringComparison.Ordinal);
        Assert.DoesNotContain("##", s, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanForSpeech_strips_markdown_before_tts()
    {
        string cleaned = SpeechCleaner.CleanForSpeech(
            "```text\nHello world.\n```",
            comicBook: true);
        Assert.Contains("Hello", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("```", cleaned, StringComparison.Ordinal);
    }
}
