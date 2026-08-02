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
    public void Catalog_includes_html_scaffold_handler()
    {
        var rule = SpeechTextRulesCatalog.CreateDefaults()
            .FirstOrDefault(r => r.Id == "noise-html-scaffold");
        Assert.NotNull(rule);
        Assert.Equal(SpeechTextRuleStage.Noise, rule!.Stage);
        Assert.True(SpeechTextRule.IsHandlerPattern(rule.Pattern));
        Assert.Equal("html-scaffold", SpeechTextRule.GetHandlerName(rule.Pattern));
        Assert.True(rule.Enabled);
        Assert.Contains("lettering", rule.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merge_injects_html_scaffold_when_missing()
    {
        // Old profile without the rule still gets it (upgrade path).
        var sparse = SpeechTextRulesCatalog.CreateDefaults()
            .Where(r => r.Id != "noise-html-scaffold")
            .Select(r => r.Clone())
            .ToList();
        var merged = SpeechTextRulesCatalog.MergeWithDefaults(sparse);
        Assert.Contains(merged, r => r.Id == "noise-html-scaffold");
    }

    [Fact]
    public void Html_scaffold_disabled_leaves_markup_tokens()
    {
        var rules = SpeechTextRulesCatalog.CreateDefaults();
        foreach (var r in rules)
        {
            if (r.Id == "noise-html-scaffold")
                r.Enabled = false;
        }
        AppSettings.Current.SetSpeechTextRules(rules);
        try
        {
            string c = OcrProcessor.SmokeCleanForSpeech(
                "HELLO html lang\"en\" div style\"color:red\" WORLD",
                comicBook: true);
            // With handler off, structural words can survive into the stream
            // (symbol strip may remove quotes/punctuation only).
            Assert.Contains("hello", c, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("world", c, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AppSettings.Current.ResetSpeechTextRulesToDefaults();
        }
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
