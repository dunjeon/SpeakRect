using SpeakRect;
using Xunit;

namespace SpeakRect.Tests;

public class SpeechRulesAndPauseTests
{
    [Fact]
    public void Abbreviation_mrs()
    {
        string c = OcrProcessor.SmokeCleanForSpeech("Meet Mrs. Jones.", comicBook: true);
        Assert.Contains("missus", c, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pause_units_on_sentence()
    {
        string c = OcrProcessor.SmokeCleanForSpeech("hello. world", comicBook: true);
        Assert.Equal(2, OcrProcessor.SmokeSpeakUnitCount(c));
    }

    [Fact]
    public void Pause_list_nonempty_with_custom_encodings()
    {
        AppSettings.Current.VoiceUseCustomPauseEncodings = true;
        AppSettings.Current.NormalizeVoiceSettings();
        string c = OcrProcessor.SmokeCleanForSpeech("Hello, world!", comicBook: true);
        var pauses = OcrProcessor.SmokePauseAfterMsList(c);
        Assert.NotEmpty(pauses);
    }

    [Fact]
    public void Uchar_noise_unusable_or_stripped()
    {
        string c = OcrProcessor.SmokeCleanForSpeech("uchar", comicBook: true);
        Assert.True(OcrProcessor.SmokeIsUsableOcrText(c) == false ||
                    !c.Contains("uchar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Speech_rule_word_vs_phrase_catalog_nonempty()
    {
        Assert.NotEmpty(SpeechTextRulesCatalog.CreateDefaults());
    }
}
