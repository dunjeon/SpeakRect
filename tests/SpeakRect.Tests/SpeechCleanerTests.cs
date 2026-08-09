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

    /// <summary>
    /// Refusal gate must not treat ordinary "I can't …" dialogue as VL junk.
    /// The old can/can't + see|read|… arm matched "seem" as a "see" prefix and
    /// dropped the second sentence after pause-split (Default + ComicBook).
    /// </summary>
    [Fact]
    public void Cant_seem_dialogue_is_usable_and_expands()
    {
        const string raw =
            "stay up late tonight?\nI can't seem to fall back asleep.";
        string cleaned = OcrProcessor.SmokeCleanForSpeech(raw, comicBook: true);
        Assert.False(SpeechCleaner.IsUnusable(cleaned));
        Assert.False(SpeechCleaner.IsUnusable("i can't seem to fall back asleep."));

        var units = OcrProcessor.SmokeExpandSpeakUnits(new[] { cleaned });
        Assert.True(units.Count >= 2, $"expected ≥2 speak units, got {units.Count}: [{string.Join(" | ", units)}]");
        Assert.Contains(units, u => u.Contains("stay up late tonight", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(units, u => u.Contains("seem", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(units, u => u.Contains("asleep", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sorry_i_cant_dialogue_is_usable()
    {
        // Old "sorry.? I can't/could not…" arm killed common balloon lines.
        Assert.False(SpeechCleaner.IsUnusable("sorry, i can't."));
        Assert.False(SpeechCleaner.IsUnusable("sorry. i can't go with you"));
        Assert.False(SpeechCleaner.IsUnusable(
            OcrProcessor.SmokeCleanForSpeech("Sorry, I can't.", comicBook: true)));
    }

    [Fact]
    public void Short_refusal_tokens_still_unusable()
    {
        Assert.True(SpeechCleaner.IsUnusable("unreadable"));
        Assert.True(SpeechCleaner.IsUnusable("no text found"));
        Assert.True(SpeechCleaner.IsUnusable("there is no text"));
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
        // VL freestyle "text": "…" without outer braces must not speak the key.
        string raw =
            "...AND SHE WASN'T ANYTHIN' LIKE YOU.\n\n" +
            "\"text\": \"HER NAME WAS RIVER ST. CLAIR, AND SHE WAS A..." +
            "DIFFICULT WOMAN. YOU KNOW THE TYPE: TOO SMART FOR HER OWN GOOD..." +
            "HYPERSENSITIVE... TORPEDOED EVERY RELATIONSHIP SHE EVER HAD...\"";

        string cleaned = SpeechCleaner.CleanForSpeech(raw, comicBook: true);
        Assert.Contains("river", cleaned, StringComparison.OrdinalIgnoreCase);
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
