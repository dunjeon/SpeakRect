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
}
