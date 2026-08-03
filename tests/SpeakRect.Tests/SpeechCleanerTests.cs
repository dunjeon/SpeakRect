using System.Text.RegularExpressions;
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
    public void CleanForSpeech_unwraps_loose_text_field_without_braces()
    {
        // Hattie panel (2026-08-01): VL mixed plain balloon + freestyle
        // "text": "…" — not a JSON object, so brace unwrap missed it and TTS
        // spoke the word "text".
        string raw =
            "...AND SHE WASN'T ANYTHIN' LIKE YOU.\n\n" +
            "\"text\": \"HER NAME WAS HATTIE ST. ANGE, AND SHE WAS A..." +
            "DIFFICULT WOMAN. YOU KNOW THE TYPE: TOO SMART FOR HER OWN GOOD..." +
            "HYPERSENSITIVE... TORPEDOED EVERY RELATIONSHIP SHE EVER HAD...\"";

        string cleaned = SpeechCleaner.CleanForSpeech(raw, comicBook: true);
        Assert.Contains("hattie", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anythin", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("text her name", cleaned, StringComparison.OrdinalIgnoreCase);
        // No spoken JSON key as its own lead-in word.
        Assert.False(
            Regex.IsMatch(cleaned, @"(?i)(?:^|[\x1c-\x1f\s])text\s+her\s+name"),
            $"cleaned={cleaned}");
        // Double quotes are not useful for TTS — strip to spaces / gone.
        Assert.DoesNotContain("\"", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanForSpeech_keeps_dialogue_colon_phrases()
    {
        // Must not treat "TYPE: TOO SMART" as a JSON field (only known keys unwrap).
        string cleaned = SpeechCleaner.CleanForSpeech(
            "YOU KNOW THE TYPE: TOO SMART FOR HER OWN GOOD.",
            comicBook: true);
        Assert.Contains("type", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("too smart", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("own good", cleaned, StringComparison.OrdinalIgnoreCase);
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
