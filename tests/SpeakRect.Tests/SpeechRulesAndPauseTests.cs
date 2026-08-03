using System.Linq;
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
    public void Speech_rule_word_vs_phrase_catalog_nonempty()
    {
        Assert.NotEmpty(SpeechTextRulesCatalog.CreateDefaults());
    }

    [Fact]
    public void Strip_symbols_keeps_hyphen_compounds()
    {
        // deco-strip-symbols + NormalizeSpeechPunctuation keep mid-word hyphens;
        // angle-bracket comic lettering keeps the words (HTML strip is off).
        string c = OcrProcessor.SmokeCleanForSpeech(
            "Hello <X-Men> [ wow ] { ok } ( yes ) * # ^ & more",
            comicBook: true);
        Assert.Contains("x-men", c, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wow", c, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", c, StringComparison.Ordinal);
        Assert.DoesNotContain(">", c, StringComparison.Ordinal);
        Assert.DoesNotContain("[", c, StringComparison.Ordinal);
        Assert.DoesNotContain("]", c, StringComparison.Ordinal);
        Assert.DoesNotContain("{", c, StringComparison.Ordinal);
        Assert.DoesNotContain("}", c, StringComparison.Ordinal);
        Assert.DoesNotContain("(", c, StringComparison.Ordinal);
        Assert.DoesNotContain(")", c, StringComparison.Ordinal);
        Assert.DoesNotContain("*", c, StringComparison.Ordinal);
        Assert.DoesNotContain("#", c, StringComparison.Ordinal);
        Assert.DoesNotContain("^", c, StringComparison.Ordinal);
        Assert.DoesNotContain("&", c, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_includes_deco_strip_symbols()
    {
        var rule = SpeechTextRulesCatalog.CreateDefaults()
            .FirstOrDefault(r => r.Id == "deco-strip-symbols");
        Assert.NotNull(rule);
        Assert.Equal(SpeechTextRuleStage.Decorators, rule!.Stage);
        Assert.Contains("\\-", rule.Pattern, StringComparison.Ordinal);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void Angle_bracket_dialogue_not_eaten_as_html()
    {
        // Comic radio/phone lettering: <WHERE ARE YOU, COUSIN?>
        string c = OcrProcessor.SmokeCleanForSpeech(
            "<WHERE ARE YOU, COUSIN?> JUST CALL ME BACK",
            comicBook: true);
        Assert.Contains("where", c, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cousin", c, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("just", c, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", c, StringComparison.Ordinal);
        Assert.DoesNotContain(">", c, StringComparison.Ordinal);
    }

    [Fact]
    public void Retired_html_noise_rules_not_in_defaults()
    {
        var ids = SpeechTextRulesCatalog.CreateDefaults().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("noise-md-html", ids);
        Assert.DoesNotContain("noise-entity-lt", ids);
        var merged = SpeechTextRulesCatalog.MergeWithDefaults(new[]
        {
            new SpeechTextRule
            {
                Id = "noise-md-html",
                Name = "HTML tags",
                Stage = SpeechTextRuleStage.Noise,
                Pattern = @"</?[a-zA-Z][^>]*>",
                Replace = " ",
                Enabled = true,
                IsBuiltIn = true,
            },
        });
        Assert.DoesNotContain(merged, r => r.Id.Equals("noise-md-html", StringComparison.OrdinalIgnoreCase));
    }
}
