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

    [Fact]
    public void Name_pack_discover_skips_readme_docs()
    {
        Assert.True(SpeechNamePacks.IsDocumentationFile("README.txt"));
        Assert.True(SpeechNamePacks.IsDocumentationFile(@"C:\packs\readme.TXT"));
        Assert.True(SpeechNamePacks.IsDocumentationFile("LICENSE.txt"));
        Assert.False(SpeechNamePacks.IsDocumentationFile("x-men.txt"));
        Assert.False(SpeechNamePacks.IsDocumentationFile("my-game.txt"));

        // Live folder next to the test host may include README.txt + x-men.txt.
        if (!Directory.Exists(SpeechNamePacks.PacksDir))
            return;
        var discovered = SpeechNamePacks.Discover();
        Assert.DoesNotContain(discovered, p =>
            Path.GetFileNameWithoutExtension(p.FilePath)
                .Equals("README", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(discovered, p =>
            p.DisplayName.Equals("README", StringComparison.OrdinalIgnoreCase) ||
            p.DisplayName.Equals("Readme", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void XMen_name_pack_is_on_by_default_sorted_and_merges()
    {
        // Pack ships as NamePacks\x-men.txt next to the app (not embedded in the DLL).
        Assert.True(
            Directory.Exists(SpeechNamePacks.PacksDir) ||
            File.Exists(Path.Combine(AppSettings.AppDir, "NamePacks", "x-men.txt")),
            $"Expected NamePacks under AppDir: {AppSettings.AppDir}");

        var discovered = SpeechNamePacks.Discover();
        Assert.Contains(discovered, p =>
            p.Id.Equals("x-men", StringComparison.OrdinalIgnoreCase));
        // Docs are not packs; Discover is list-only (never auto-applies rules).
        Assert.DoesNotContain(discovered, p =>
            Path.GetFileNameWithoutExtension(p.FilePath)
                .Equals("README", StringComparison.OrdinalIgnoreCase));
        var xMenInfo = discovered.First(p =>
            p.Id.Equals("x-men", StringComparison.OrdinalIgnoreCase));
        Assert.True(xMenInfo.RuleCount > 100, $"Expected rule count on PackInfo, got {xMenInfo.RuleCount}");

        var pack = SpeechNamePacks.Create("x-men");
        Assert.NotEmpty(pack);
        Assert.True(pack.Count > 100);
        Assert.Equal(pack.Count, xMenInfo.RuleCount);
        Assert.All(pack, r => Assert.True(r.Enabled));
        // A–Z by Find for easy browsing.
        for (int i = 1; i < pack.Count; i++)
        {
            Assert.True(
                string.Compare(pack[i - 1].Match, pack[i].Match, StringComparison.OrdinalIgnoreCase) <= 0,
                $"Expected A–Z: '{pack[i - 1].Match}' before '{pack[i].Match}'");
        }
        Assert.Contains(pack, r =>
            r.Match.Equals("X-Men", StringComparison.OrdinalIgnoreCase) &&
            r.Replace.Contains("Ex", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pack, r =>
            r.Match.Equals("Cyclops", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pack, r =>
            r.Match.Equals("Magneto", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pack, r =>
            r.Match.Equals("Jean Grey", StringComparison.OrdinalIgnoreCase));

        var list = new System.Collections.Generic.List<SpeechRule>
        {
            new() { Match = "X-Men", Replace = "custom", Kind = SpeechMatchKind.Word, Enabled = true },
        };
        int added = SpeechNamePacks.MergeInto(list, "x-men", out int skipped);
        Assert.True(added > 100);
        Assert.True(skipped >= 1); // existing X-Men Find (and any pack self-collisions)
        Assert.Contains(list, r =>
            r.Match.Equals("X-Men", StringComparison.OrdinalIgnoreCase) &&
            r.Replace.Equals("custom", StringComparison.Ordinal) &&
            r.Enabled);
        Assert.All(list, r => Assert.True(r.Enabled));
        for (int i = 1; i < list.Count; i++)
        {
            Assert.True(
                string.Compare(list[i - 1].Match, list[i].Match, StringComparison.OrdinalIgnoreCase) <= 0,
                $"Merged list A–Z: '{list[i - 1].Match}' before '{list[i].Match}'");
        }
    }

    [Fact]
    public void Name_rules_longer_find_wins_despite_az_list_order()
    {
        // List is A–Z so "X-Men" appears before "X-Treme X-Men"; engine still
        // prefers the longer Find.
        var rules = new[]
        {
            new SpeechRule
            {
                Match = "X-Men",
                Replace = "Ex-Men",
                Kind = SpeechMatchKind.Word,
                Enabled = true,
            },
            new SpeechRule
            {
                Match = "X-Treme X-Men",
                Replace = "Ex-Treem Ex-Men",
                Kind = SpeechMatchKind.Word,
                Enabled = true,
            },
        };
        Assert.Equal(
            "the Ex-Treem Ex-Men",
            SpeechRulesEngine.Apply("the x-treme x-men", rules));
        Assert.Equal(
            "the Ex-Men",
            SpeechRulesEngine.Apply("the x-men", rules));
    }

    [Fact]
    public void Name_rule_matches_any_case()
    {
        // Engine always IgnoreCase — pack Find "X-Men" hits x-men / X-MEN / X-Men.
        var rules = new[]
        {
            new SpeechRule
            {
                Match = "X-Men",
                Replace = "Ex-Men",
                Kind = SpeechMatchKind.Word,
                Enabled = true,
            },
        };
        Assert.Equal(
            "the Ex-Men win",
            SpeechRulesEngine.Apply("the x-men win", rules));
        Assert.Equal(
            "the Ex-Men win",
            SpeechRulesEngine.Apply("the X-MEN win", rules));
        Assert.Equal(
            "the Ex-Men win",
            SpeechRulesEngine.Apply("the X-Men win", rules));
    }

    [Fact]
    public void Custom_name_pack_file_parses_pipe_and_header()
    {
        string dir = Path.Combine(Path.GetTempPath(), "SpeakRectPackTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "my-pack.txt");
        try
        {
            File.WriteAllText(path,
                "Id=demo\nName=Demo Pack\nDescription=Test pack.\n\n" +
                "Foo | Bar\n" +
                "Baz\tQux\tPhrase\n" +
                "; comment\n" +
                "Hello = World\n",
                System.Text.Encoding.UTF8);

            var rules = SpeechNamePacks.LoadFile(path);
            Assert.Equal(3, rules.Count);
            Assert.All(rules, r => Assert.True(r.Enabled));
            // A–Z: Baz, Foo, Hello
            Assert.Equal("Baz", rules[0].Match);
            Assert.Equal("Foo", rules[1].Match);
            Assert.Equal("Hello", rules[2].Match);
            Assert.Contains(rules, r =>
                r.Match.Equals("Foo", StringComparison.Ordinal) &&
                r.Replace.Equals("Bar", StringComparison.Ordinal) &&
                r.Kind == SpeechMatchKind.Word);
            Assert.Contains(rules, r =>
                r.Match.Equals("Baz", StringComparison.Ordinal) &&
                r.Replace.Equals("Qux", StringComparison.Ordinal) &&
                r.Kind == SpeechMatchKind.Phrase);
            Assert.Contains(rules, r =>
                r.Match.Equals("Hello", StringComparison.Ordinal) &&
                r.Replace.Equals("World", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }
}
