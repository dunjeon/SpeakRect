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
    public void StripHtmlScaffolding_keeps_comic_angle_lettering()
    {
        // Any comic lettering: first token is not an HTML element name.
        foreach (string raw in new[]
        {
            "<WHERE ARE YOU, COUSIN?> JUST CALL ME BACK",
            "<YOU GREW UP IN OUR WORLD>",
            "<OK!>",
            "<*SIGH*>",
        })
        {
            string s = SpeechCleaner.StripHtmlScaffolding(raw);
            Assert.DoesNotContain("<", s, StringComparison.Ordinal);
            Assert.DoesNotContain(">", s, StringComparison.Ordinal);
            // Words from the interior must survive (brackets only are structural).
            Assert.Matches(new Regex(@"[A-Za-z]{2,}"), s);
        }

        string cousin = SpeechCleaner.StripHtmlScaffolding(
            "<WHERE ARE YOU, COUSIN?> JUST CALL ME BACK");
        Assert.Contains("WHERE ARE YOU, COUSIN?", cousin, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JUST CALL ME BACK", cousin, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StripHtmlScaffolding_drops_real_html_tags()
    {
        string s = SpeechCleaner.StripHtmlScaffolding(
            "<div style=\"color:red\">Hello world</div><br/><p>More</p>");
        Assert.Contains("Hello world", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("More", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("div", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("br", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanForSpeech_strips_unbracketed_markup_soup_keeps_dialogue()
    {
        AppSettings.Current.ResetSpeechTextRulesToDefaults();
        // Structural VL failure mode (any page): tags + CSS attrs wrap / follow dialogue.
        // No page-specific phrase filters — only tag/attr/CSS structure.
        string raw =
            "LEAVE THIS PLACE AT ONCE!\n" +
            "html lang\"en\" head meta charset\"UTF-8\"\n" +
            "titleRandom Chrome Title/title /head body\n" +
            "div style\"display: flex; background-color: #abc123; font-size: 18px;\"\n" +
            "LEAVE THIS PLACE AT ONCE!/div\n" +
            "div style\"width: 50%; margin: 10px;\"WE WILL NOT WARN YOU AGAIN./div\n" +
            "/body /html";

        string cleaned = SpeechCleaner.CleanForSpeech(raw, comicBook: true);
        Assert.Contains("leave this place", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not warn you", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("html", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("div", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("charset", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("background", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", cleaned, StringComparison.OrdinalIgnoreCase);
        // title…/title wrapper drops chrome text entirely (markup, not balloon lettering).
        Assert.DoesNotContain("random chrome", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanForSpeech_strips_vl_table_qa_soup()
    {
        // Debug 2026-08-02: model switched from div-card to table Q&A template.
        // noise-html-scaffold must kill table/tr/th/td chrome, keep cell dialogue.
        AppSettings.Current.ResetSpeechTextRulesToDefaults();
        string raw =
            "YOU GREW UP IN OUR WORLD, KENUICHIO HARADA. YOU WERE ONE OF US!\n" +
            "html lang\"en\" head meta charset\"UTF-8\" titleFilm Quotes/title /head body\n" +
            "table border\"1\"\n" +
            "tr th style\"text-align: left;\"Question/th th style\"text-align: center;\"Answer/th /tr\n" +
            "tr td style\"text-align: left;\"YOU GREW UP IN OUR WORLD, KENUICHIO HARADA. YOU WERE ONE OF US!/td\n" +
            "td style\"text-align: center;\"HOW COULD YOU TURN YOUR BACK ON HONOR AND LOYALTY? HOW COULD YOU FORGET OUR VALUES SO COMPLETELY?/td\n" +
            "/tr /table /body /html";

        string cleaned = SpeechCleaner.CleanForSpeech(raw, comicBook: true);
        Assert.Contains("grew up", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kenuichio", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("honor", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loyalty", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("table", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tr", cleaned, StringComparison.OrdinalIgnoreCase);
        // "th" / "td" as bare tags — not mid-word
        Assert.False(Regex.IsMatch(cleaned, @"(?i)(?<!\p{L})(?:th|td|tr|table)(?!\p{L})"),
            $"cleaned={cleaned}");
        Assert.DoesNotContain("question", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answer", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("html", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanForSpeech_strips_vl_html_quote_card_soup()
    {
        AppSettings.Current.ResetSpeechTextRulesToDefaults();
        // Regression: one real capture where VL invented a full HTML quote card.
        // Same structural rules as any other page (not a special-case phrase list).
        // Handler lives in Speech → Text rules (noise-html-scaffold), not hard-coded.
        string raw =
            "YOU GREW UP IN OUR WORLD, KENUICHIO HARADA. YOU WERE ONE OF US!\n" +
            "html lang\"en\"\n" +
            "head\n" +
            "meta charset\"UTF-8\"\n" +
            "titleInspirational Quotes/title\n" +
            "/head\n" +
            "body\n" +
            "div style\"background-color: f2f2f2; font-family: Arial, sans-serif;\"\n" +
            "h1 style\"text-align: center; font-size: 24px;\"Inspirational Quotes/h1\n" +
            "div style\"display: flex; justify-content: space-between; align-items: center;\"\n" +
            "div style\"width: 60; background-color: f2f2f2; font-size: 24px;\"YOU GREW UP IN OUR WORLD, KENUICHIO HARADA. YOU WERE ONE OF US!/div\n" +
            "div style\"width: 60; background-color: f2f2f2; font-size: 24px;\"HOW COULD YOU TURN YOUR BACK ON HONOR AND LOYALTY? HOW COULD YOU FORGET OUR VALUES SO COMPLETELY?/div\n" +
            "/div\n" +
            "/body\n" +
            "/html";

        string cleaned = SpeechCleaner.CleanForSpeech(raw, comicBook: true);
        Assert.Contains("grew up", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kenuichio", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("honor", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loyalty", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("html", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("div", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("background", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("f2f2f2", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("charset", cleaned, StringComparison.OrdinalIgnoreCase);
        // title…/title and h1…/h1 are markup wrappers → chrome text dropped.
        Assert.DoesNotContain("inspirational", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanForSpeech_comic_brackets_and_html_together()
    {
        AppSettings.Current.ResetSpeechTextRulesToDefaults();
        // Comic lettering kept; HTML tags dropped; text between HTML tags still spoken.
        string cleaned = SpeechCleaner.CleanForSpeech(
            "<YOU GREW UP IN OUR WORLD> <div class=\"x\">still spoken</div> HOW COULD YOU?",
            comicBook: true);
        Assert.Contains("grew up", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still spoken", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("how could you", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("div", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanForSpeech_does_not_eat_english_i_or_a_as_html()
    {
        // Bare single-letter HTML tags must never strip English "I" / "a".
        string cleaned = SpeechCleaner.CleanForSpeech(
            "I am a hero. I said a word.",
            comicBook: true);
        Assert.Contains("i am a hero", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("i said a word", cleaned, StringComparison.OrdinalIgnoreCase);
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
