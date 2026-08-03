using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using SpeakRect;

// Top-level smoke: Default (ComicBook OFF) / ComicBook ON.
// last_ocr.txt / debug_images only exist when this is a Debug build of SpeakRect.
// Release/publish never dump OCR debug artifacts.

int failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) Console.WriteLine($"  PASS  {name}");
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}{(string.IsNullOrEmpty(detail) ? "" : " — " + detail)}");
    }
}

static string TruncateForSmoke(string s, int max)
{
    if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
    return s[..max] + "…";
}

Console.OutputEncoding = Encoding.UTF8;

var vs = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
         ?? new Rectangle(0, 0, 1920, 1080);
int insetX = Math.Max(40, vs.Width / 8);
int insetY = Math.Max(40, vs.Height / 12);
var rect = new Rectangle(
    vs.X + insetX,
    vs.Y + insetY,
    Math.Max(200, vs.Width - insetX * 2),
    Math.Max(200, vs.Height - insetY * 2 - 48));

foreach (string a in args)
{
    if (a.StartsWith("--rect=", StringComparison.OrdinalIgnoreCase))
    {
        var parts = a["--rect=".Length..].Split(',');
        if (parts.Length == 4 &&
            int.TryParse(parts[0], out int x) &&
            int.TryParse(parts[1], out int y) &&
            int.TryParse(parts[2], out int w) &&
            int.TryParse(parts[3], out int h))
        {
            rect = new Rectangle(x, y, w, h);
        }
    }
}

Console.WriteLine("=== ModeSmoke (OCR modes) ===");
Console.WriteLine($"capture rect: {rect.X},{rect.Y} {rect.Width}x{rect.Height}");
Console.WriteLine($"base dir: {AppContext.BaseDirectory}");
string debugDir = Path.Combine(AppContext.BaseDirectory, "debug_images");
Console.WriteLine($"debug_images: {debugDir}");

// Post-obfuscation guard: vision JSON wire keys must still serialize correctly.
if (!OcrProcessor.SmokeVerifyKoboldJsonShape(out string sampleJson))
{
    Check("Local-LLM JSON shape (obfuscation-safe)", false,
        sampleJson.Length > 200 ? sampleJson[..200] + "…" : sampleJson);
    Console.WriteLine("Aborting — payload would not talk to Local-LLM correctly.");
    return 4;
}
Check("Local-LLM JSON shape (obfuscation-safe)", true);
Console.WriteLine($"  sample payload head: {sampleJson.AsSpan(0, Math.Min(120, sampleJson.Length))}");

// UI branding sanitizer (two-axis): Local-LLM host brands vs OCR detect — never Kobold→OCR.
Console.WriteLine();
Console.WriteLine("--- UI engine name sanitizer ---");
{
    string s1 = UiTheme.SanitizeUiEngineNames("KoboldCPP timeout");
    string s2 = UiTheme.SanitizeUiEngineNames("WinOCR seeded 3");
    string s3 = UiTheme.SanitizeUiEngineNames("Local-LLM under-read vs OCR");
    string s4 = UiTheme.SanitizeUiEngineNames("kobold failed");
    Check("Sanitize: KoboldCPP → Local-LLM",
        s1.Contains("Local-LLM", StringComparison.Ordinal) &&
        !s1.Contains("OCR engine", StringComparison.OrdinalIgnoreCase) &&
        !s1.Contains("Kobold", StringComparison.OrdinalIgnoreCase),
        s1);
    Check("Sanitize: WinOCR → OCR",
        s2 == "OCR seeded 3" || s2.StartsWith("OCR ", StringComparison.Ordinal),
        s2);
    Check("Sanitize: never Kobold→OCR alone",
        s3.Contains("Local-LLM", StringComparison.Ordinal) &&
        s3.Contains("OCR", StringComparison.Ordinal) &&
        !s3.Contains("Kobold", StringComparison.OrdinalIgnoreCase) &&
        !s3.Contains("WinOCR", StringComparison.OrdinalIgnoreCase),
        s3);
    // Case-insensitive "Kobold" match rewrites "kobold" to canonical "Local-LLM".
    Check("Sanitize: kobold lowercase → Local-LLM (canonical)",
        s4.Contains("Local-LLM", StringComparison.Ordinal) &&
        !s4.Contains("kobold", StringComparison.OrdinalIgnoreCase),
        s4);
}

// Speech cleaner chain:
//   expand abbrevs → strip non-pause punct → ellipsis→. → collapse runs →
//   keep .!? + insert sentence pause after; , → comma pause mark
//   defaults (stock ini): comma=102ms, sentence=502ms,
//   other=52ms, bubble=752ms
//   (Voice tab / [VOICE] CommaPauseMs … — AppSettings.Current)
//   UseCustomPauseEncodings=false skips typed marks / delays entirely.
Console.WriteLine();
Console.WriteLine("--- Speech cleaner ---");
{
    // Ensure defaults for pause-duration assertions (ini may override Current).
    AppSettings.Current.VoiceUseCustomPauseEncodings = true;
    AppSettings.Current.VoiceCommaPauseMs = AppSettings.DefaultCommaPauseMs;
    AppSettings.Current.VoiceSentencePauseMs = AppSettings.DefaultSentencePauseMs;
    AppSettings.Current.VoiceOtherPauseMs = AppSettings.DefaultOtherPauseMs;
    AppSettings.Current.VoiceBubblePauseMs = AppSettings.DefaultBubblePauseMs;
    AppSettings.Current.NormalizeVoiceSettings();
    // Pin lowercase ON for the bulk of cleaner assertions (stable expected forms).
    // Default product setting is OFF; dedicated toggle tests cover that path.
    AppSettings.Current.SpeechForceLowercase = true;

    const string sample =
        "Mr. Smith said you're late, really; yes—ok. Mrs. Jones won't wait—e.g. now! Why?! Really???";
    string cleanedOn = OcrProcessor.SmokeCleanForSpeech(sample, comicBook: true);
    string cleanedOff = OcrProcessor.SmokeCleanForSpeech(sample, comicBook: false);
    string normOn = cleanedOn.Replace("\r\n", "\n", StringComparison.Ordinal);
    string normOff = cleanedOff.Replace("\r\n", "\n", StringComparison.Ordinal);
    int unitsOn = OcrProcessor.SmokeSpeakUnitCount(cleanedOn);
    int unitsOff = OcrProcessor.SmokeSpeakUnitCount(cleanedOff);

    Check("Expand mr. → mister (title stays with name, no leftover period)",
        Regex.IsMatch(normOn, @"mister\s+smith") &&
        !Regex.IsMatch(normOn, @"\bmister\s*\.") &&
        !Regex.IsMatch(normOn, @"mister\s*\n"));
    // Spoken contractions stay as contractions (TTS sounds more natural).
    Check("you're left intact (not expanded to you are)",
        normOn.Contains("you're", StringComparison.Ordinal) &&
        !normOn.Contains("you are", StringComparison.Ordinal));
    Check("won't left intact (not expanded to will not)",
        normOn.Contains("won't", StringComparison.Ordinal) &&
        !normOn.Contains("will not", StringComparison.Ordinal));

    string ambig = OcrProcessor.SmokeCleanForSpeech(
        "You'd been warned. You'd like this. He's been here. It's a trap. " +
        "Ain't that so? Don't go. Aren't we done? I can't.");
    string ambigNorm = ambig.Replace("\r\n", "\n", StringComparison.Ordinal);
    Check("you'd left intact (not expanded to had/would)",
        ambigNorm.Contains("you'd", StringComparison.Ordinal) &&
        !ambigNorm.Contains("you had", StringComparison.Ordinal) &&
        !ambigNorm.Contains("you would", StringComparison.Ordinal));
    Check("you'd apostrophe preserved (not split to you d)",
        ambigNorm.Contains("you'd", StringComparison.Ordinal) &&
        !Regex.IsMatch(ambigNorm, @"\byou\s+d\b"));
    Check("he's left intact (is vs has)",
        ambigNorm.Contains("he's", StringComparison.Ordinal) &&
        !ambigNorm.Contains("he is", StringComparison.Ordinal) &&
        !ambigNorm.Contains("he has", StringComparison.Ordinal));
    Check("it's left intact",
        ambigNorm.Contains("it's", StringComparison.Ordinal) &&
        !ambigNorm.Contains("it is", StringComparison.Ordinal));
    Check("ain't left intact",
        ambigNorm.Contains("ain't", StringComparison.Ordinal));
    Check("don't left intact (not expanded to do not)",
        ambigNorm.Contains("don't", StringComparison.Ordinal) &&
        !ambigNorm.Contains("do not", StringComparison.Ordinal));
    Check("aren't left intact (not expanded to are not)",
        ambigNorm.Contains("aren't", StringComparison.Ordinal) &&
        !ambigNorm.Contains("are not", StringComparison.Ordinal));
    Check("can't left intact (not expanded to cannot)",
        ambigNorm.Contains("can't", StringComparison.Ordinal) &&
        !ambigNorm.Contains("cannot", StringComparison.Ordinal));
    Check("Expand mrs. → missus",
        normOn.Contains("missus", StringComparison.Ordinal));
    Check("Expand e.g. → for example",
        normOn.Contains("for example", StringComparison.Ordinal));

    // Proper name "Max." must not expand to "maximum" (look/Bug.txt + max.png).
    // Catalog used to ship abbrev-max (max.→maximum); that clobbers dialogue names.
    string maxNameClean = OcrProcessor.SmokeCleanForSpeech(
        "Hey, Max. So...I don't get it.", true);
    var maxNameUnits = OcrProcessor.SmokeSpeakUnits(maxNameClean);
    string maxNameJoined = string.Join(" ", maxNameUnits);
    Check("Name Max. stays max (not maximum)",
        Regex.IsMatch(maxNameClean, @"\bmax\b", RegexOptions.IgnoreCase) &&
        !maxNameClean.Contains("maximum", StringComparison.OrdinalIgnoreCase) &&
        !maxNameJoined.Contains("maximum", StringComparison.OrdinalIgnoreCase),
        $"clean={TruncateForSmoke(maxNameClean, 120)} units=[{string.Join(" | ", maxNameUnits)}]");
    Check("min. stays min (not minimum) — dialogue / names",
        !OcrProcessor.SmokeCleanForSpeech("Wait a min. please.", true)
            .Contains("minimum", StringComparison.Ordinal));
    // Old profiles that still list retired abbrev-max/min must drop them on merge.
    {
        var withRetired = SpeechTextRulesCatalog.CreateDefaults();
        withRetired.Add(new SpeechTextRule
        {
            Id = "abbrev-max",
            Name = "max.",
            Stage = SpeechTextRuleStage.Abbrev,
            Pattern = @"\bmax\.(?!\p{L})",
            Replace = "maximum",
            Enabled = true,
            IsBuiltIn = true,
        });
        withRetired.Add(new SpeechTextRule
        {
            Id = "abbrev-min",
            Name = "min.",
            Stage = SpeechTextRuleStage.Abbrev,
            Pattern = @"\bmin\.(?!\p{L})",
            Replace = "minimum",
            Enabled = true,
            IsBuiltIn = true,
        });
        var merged = SpeechTextRulesCatalog.MergeWithDefaults(withRetired);
        Check("Retired abbrev-max dropped on merge",
            !merged.Any(r => r.Id.Equals("abbrev-max", StringComparison.OrdinalIgnoreCase)));
        Check("Retired abbrev-min dropped on merge",
            !merged.Any(r => r.Id.Equals("abbrev-min", StringComparison.OrdinalIgnoreCase)));
        Check("Defaults never ship abbrev-max",
            !SpeechTextRulesCatalog.CreateDefaults().Any(r =>
                r.Id.Equals("abbrev-max", StringComparison.OrdinalIgnoreCase)));
    }

    // Force lowercase toggle (Settings → Speech). Default on; off keeps OCR casing;
    // on normalizes ALL CAPS; Abbrev stage still expands Mr. case-insensitively.
    {
        bool savedForce = AppSettings.Current.SpeechForceLowercase;
        bool savedTitle = AppSettings.Current.SpeechTitleCaseAllCaps;
        try
        {
            AppSettings.Current.SpeechTitleCaseAllCaps = false;
            AppSettings.Current.SpeechForceLowercase = false;
            string lowerOff = OcrProcessor.SmokeCleanForSpeech(
                "Hey, Max. Mr. Smith said hi.", true);
            Check("Force lowercase OFF preserves Hey / Max casing",
                Regex.IsMatch(lowerOff, @"\bHey\b") &&
                Regex.IsMatch(lowerOff, @"\bMax\b"),
                $"clean={TruncateForSmoke(lowerOff, 100)}");
            Check("Force lowercase OFF still expands Mr. → mister",
                lowerOff.Contains("mister", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(lowerOff, @"\bMr\b"),
                TruncateForSmoke(lowerOff, 100));

            AppSettings.Current.SpeechForceLowercase = true;
            string lowerOn = OcrProcessor.SmokeCleanForSpeech(
                "Hey, Max. Mr. Smith said hi.", true);
            Check("Force lowercase ON folds stream",
                lowerOn.Contains("hey", StringComparison.Ordinal) &&
                lowerOn.Contains("max", StringComparison.Ordinal) &&
                !Regex.IsMatch(lowerOn, @"[A-Z]"),
                $"clean={TruncateForSmoke(lowerOn, 100)}");
            Check("Force lowercase ON still expands Mr. → mister",
                lowerOn.Contains("mister", StringComparison.Ordinal),
                TruncateForSmoke(lowerOn, 100));
        }
        finally
        {
            AppSettings.Current.SpeechForceLowercase = savedForce;
            AppSettings.Current.SpeechTitleCaseAllCaps = savedTitle;
        }
    }

    // Title-case ALL CAPS toggle (Settings → Speech → Text rules). Softens full
    // uppercase words only; mixed case and short tokens stay put.
    {
        bool savedForce = AppSettings.Current.SpeechForceLowercase;
        bool savedTitle = AppSettings.Current.SpeechTitleCaseAllCaps;
        try
        {
            AppSettings.Current.SpeechForceLowercase = false;
            AppSettings.Current.SpeechTitleCaseAllCaps = false;
            string titleOff = OcrProcessor.SmokeCleanForSpeech(
                "HELLO Max. WHAT'S up I said.", true);
            Check("Title-case ALL CAPS OFF keeps HELLO",
                Regex.IsMatch(titleOff, @"\bHELLO\b"),
                $"clean={TruncateForSmoke(titleOff, 100)}");

            AppSettings.Current.SpeechTitleCaseAllCaps = true;
            string titleOn = OcrProcessor.SmokeCleanForSpeech(
                "HELLO Max. WHAT'S up I said.", true);
            Check("Title-case ALL CAPS ON softens HELLO → Hello",
                Regex.IsMatch(titleOn, @"\bHello\b") &&
                !Regex.IsMatch(titleOn, @"\bHELLO\b"),
                $"clean={TruncateForSmoke(titleOn, 100)}");
            Check("Title-case ALL CAPS ON softens WHAT'S → What's",
                Regex.IsMatch(titleOn, @"\bWhat's\b") ||
                Regex.IsMatch(titleOn, @"\bWhat'?s\b"),
                $"clean={TruncateForSmoke(titleOn, 100)}");
            Check("Title-case ALL CAPS ON leaves mixed Max alone",
                Regex.IsMatch(titleOn, @"\bMax\b"),
                $"clean={TruncateForSmoke(titleOn, 100)}");
            Check("Title-case ALL CAPS ON leaves single I alone",
                Regex.IsMatch(titleOn, @"\bI\b"),
                $"clean={TruncateForSmoke(titleOn, 100)}");

            // Mutually exclusive: enabling one clears the other.
            AppSettings.Current.SpeechTitleCaseAllCaps = true;
            Check("Title-case on clears Force lowercase",
                AppSettings.Current.SpeechTitleCaseAllCaps &&
                !AppSettings.Current.SpeechForceLowercase);
            AppSettings.Current.SpeechForceLowercase = true;
            Check("Force lowercase on clears Title-case",
                AppSettings.Current.SpeechForceLowercase &&
                !AppSettings.Current.SpeechTitleCaseAllCaps);
            string forceOnly = OcrProcessor.SmokeCleanForSpeech("HELLO Max", true);
            Check("Force lowercase alone → full lower",
                forceOnly.Contains("hello", StringComparison.Ordinal) &&
                forceOnly.Contains("max", StringComparison.Ordinal) &&
                !Regex.IsMatch(forceOnly, @"[A-Z]"),
                $"clean={TruncateForSmoke(forceOnly, 100)}");
        }
        finally
        {
            AppSettings.Current.SpeechForceLowercase = savedForce;
            AppSettings.Current.SpeechTitleCaseAllCaps = savedTitle;
        }
    }

    // Dotted letter-acronyms (E.S.U.) must not become sentence-pause splits;
    // each "e." / "s." / "u." scrap was unusable and only the first letter survived.
    string esuClean = OcrProcessor.SmokeCleanForSpeech(
        "Join the E.S.U. now. F.B.I. called.", true);
    var esuUnits = OcrProcessor.SmokeSpeakUnits(esuClean);
    var esuExpanded = OcrProcessor.SmokeExpandSpeakUnits(new[] { esuClean });
    string esuJoined = string.Join(" ", esuUnits);
    Check("Acronym E.S.U. kept as spaced letters (not pruned to e.)",
        Regex.IsMatch(esuClean, @"\be\s+s\s+u\b") &&
        !Regex.IsMatch(esuClean, @"\be\s*" + "\x1D") &&
        esuJoined.Contains("e s u", StringComparison.Ordinal),
        $"clean={TruncateForSmoke(esuClean, 120)} units=[{string.Join(" | ", esuUnits)}]");
    Check("Acronym F.B.I. kept as spaced letters",
        Regex.IsMatch(esuClean, @"\bf\s+b\s+i\b"),
        $"clean={TruncateForSmoke(esuClean, 120)}");
    Check("Acronym E.S.U. is one usable speak unit (not three scraps)",
        esuExpanded.Any(u => Regex.IsMatch(u, @"\be\s+s\s+u\b")) &&
        !esuExpanded.Any(u => u.Equals("e.", StringComparison.Ordinal) ||
                              u.Equals("e", StringComparison.Ordinal)),
        $"expanded=[{string.Join(" | ", esuExpanded)}]");
    Check("Known u.s.a. still expands (acronym protect runs after catalog)",
        OcrProcessor.SmokeCleanForSpeech("Visit the U.S.A. today.", true)
            .Contains("united states of america", StringComparison.Ordinal));
    Check("Real sentence end still pauses (hello. world)",
        OcrProcessor.SmokeSpeakUnitCount(
            OcrProcessor.SmokeCleanForSpeech("hello. world", true)) == 2);

    // Hyphenated line wraps (comic syllable breaks) → one spoken word.
    // Real failure: Kobold "SOPHISTI-CATED--" became "sophisti cated".
    {
        Console.WriteLine();
        Console.WriteLine("--- Hyphenated wrap rejoin ---");
        string sophCaps = OcrProcessor.SmokeCleanForSpeech(
            "SO SOPHISTI-CATED--", true);
        Check("ALL-CAPS SOPHISTI-CATED rejoins (no space / no dash spoken)",
            sophCaps.Contains("sophisticated", StringComparison.OrdinalIgnoreCase) &&
            !sophCaps.Contains("sophisti cated", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(sophCaps, @"sophisti\s+cated", RegexOptions.IgnoreCase),
            $"clean={TruncateForSmoke(sophCaps, 80)}");

        string sophNl = OcrProcessor.SmokeCleanForSpeech(
            "sophisti-\ncated", true);
        Check("sophisti-\\ncated rejoins across newline",
            Regex.IsMatch(sophNl, @"\bsophisticated\b", RegexOptions.IgnoreCase),
            $"clean={TruncateForSmoke(sophNl, 80)}");

        // Em-dash is a clause break, not a syllable hyphen (regression: wait—e.g.).
        string waitEg = OcrProcessor.SmokeCleanForSpeech(
            "Mrs. Jones won't wait—e.g. now!", true);
        Check("wait—e.g. does not glue to waitfor",
            waitEg.Contains("wait", StringComparison.OrdinalIgnoreCase) &&
            waitEg.Contains("for example", StringComparison.OrdinalIgnoreCase) &&
            !waitEg.Contains("waitfor", StringComparison.OrdinalIgnoreCase),
            $"clean={TruncateForSmoke(waitEg, 100)}");

        string sophSp = OcrProcessor.SmokeCleanForSpeech(
            "sophisti- cated system", true);
        Check("sophisti- cated (hyphen + space) rejoins",
            Regex.IsMatch(sophSp, @"\bsophisticated\b", RegexOptions.IgnoreCase),
            $"clean={TruncateForSmoke(sophSp, 80)}");

        string resp = OcrProcessor.SmokeCleanForSpeech(
            "responsibil-\nity", true);
        Check("responsibil-\\nity rejoins",
            Regex.IsMatch(resp, @"\bresponsibility\b", RegexOptions.IgnoreCase),
            $"clean={TruncateForSmoke(resp, 80)}");

        string well = OcrProcessor.SmokeCleanForSpeech("well-known hero", true);
        Check("well-known stays a compound (not wellknown glue)",
            well.Contains("well known", StringComparison.OrdinalIgnoreCase) ||
            well.Contains("well-known", StringComparison.OrdinalIgnoreCase),
            $"clean={TruncateForSmoke(well, 80)}");

        string xmen = OcrProcessor.SmokeCleanForSpeech("X-Men assemble", true);
        Check("X-Men not glued to xmen",
            xmen.Contains("x men", StringComparison.OrdinalIgnoreCase) ||
            xmen.Contains("x-men", StringComparison.OrdinalIgnoreCase),
            $"clean={TruncateForSmoke(xmen, 80)}");

        string ending = OcrProcessor.SmokeCleanForSpeech("the end-ing was", true);
        Check("end-ing suffix rejoins to ending",
            Regex.IsMatch(ending, @"\bending\b", RegexOptions.IgnoreCase),
            $"clean={TruncateForSmoke(ending, 80)}");
    }

    // Balloons default region order: left→right, top→bottom (Western comic).
    // Side-by-side peers stay L→R even when the right balloon starts higher.
    {
        Console.WriteLine();
        Console.WriteLine("--- Comic reading order (geometry) ---");
        var left = new Rectangle(100, 80, 200, 180);
        var rightHigher = new Rectangle(360, 30, 150, 100);
        var orderedSide = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { rightHigher, left });
        Check("Side-by-side: left balloon before elevated right (L→R)",
            orderedSide.Count == 2 &&
            orderedSide[0].X == left.X &&
            orderedSide[1].X == rightHigher.X,
            $"order=[{string.Join(" | ", orderedSide.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}"))}]");

        // After Grow pad, side balloons often X-overlap; must still L→R.
        var leftGrown = new Rectangle(90, 40, 280, 170);
        var rightGrown = new Rectangle(300, 25, 160, 100);
        var orderedGrown = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { rightGrown, leftGrown });
        Check("Grown/overlapping side balloons still L→R (not right-first)",
            orderedGrown.Count == 2 &&
            orderedGrown[0].X == leftGrown.X &&
            orderedGrown[1].X == rightGrown.X,
            $"order=[{string.Join(" | ", orderedGrown.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}"))}]");

        // Office-panel shape: tall left + short elevated right (look/region order.png).
        var officeL = new Rectangle(160, 50, 240, 200);
        var officeR = new Rectangle(380, 20, 150, 110);
        var orderedOffice = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { officeR, officeL });
        Check("Office dual-balloon: left main before right reply",
            orderedOffice.Count == 2 &&
            orderedOffice[0].X == officeL.X &&
            orderedOffice[1].X == officeR.X,
            $"order=[{string.Join(" | ", orderedOffice.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}"))}]");

        var top = new Rectangle(120, 20, 180, 90);
        var bottom = new Rectangle(130, 200, 170, 100);
        var orderedStack = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { bottom, top });
        Check("Vertical stack: upper balloon before lower",
            orderedStack.Count == 2 &&
            orderedStack[0].Y == top.Y &&
            orderedStack[1].Y == bottom.Y,
            $"order=[{string.Join(" | ", orderedStack.Select(r => $"{r.X},{r.Y}"))}]");

        var tl = new Rectangle(40, 30, 120, 80);
        var tr = new Rectangle(220, 40, 110, 70);
        var bl = new Rectangle(50, 180, 120, 80);
        var br = new Rectangle(230, 190, 110, 70);
        var orderedGrid = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { br, bl, tr, tl });
        Check("2x2 grid is row-major (TL→TR→BL→BR)",
            orderedGrid.Count == 4 &&
            orderedGrid[0].X == tl.X && orderedGrid[0].Y == tl.Y &&
            orderedGrid[1].X == tr.X &&
            orderedGrid[2].X == bl.X &&
            orderedGrid[3].X == br.X,
            $"order=[{string.Join(" | ", orderedGrid.Select(r => $"{r.X},{r.Y}"))}]");

        var strip = new Rectangle(20, 10, 500, 70);
        var callout = new Rectangle(40, 100, 160, 90);
        var orderedCap = OcrProcessor.SmokeSortComicReadingOrder(
            new[] { callout, strip });
        Check("Caption strip before lower call-out",
            orderedCap.Count == 2 &&
            orderedCap[0].Y == strip.Y &&
            orderedCap[1].Y == callout.Y,
            $"order=[{string.Join(" | ", orderedCap.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}"))}]");
    }

    // Crop under-read vs WinOCR word-count metric (category rescue, not area).
    {
        Console.WriteLine();
        Console.WriteLine("--- Coverage under-read metric ---");
        string shortCrop = string.Join(' ', Enumerable.Repeat("word", 29));
        string longOcr = string.Join(' ', Enumerable.Repeat("word", 54));
        Check("Local-LLM under-read vs OCR (29 vs 54 words)",
            OcrProcessor.SmokeKoboldUnderReadsWinOcr(shortCrop, longOcr),
            "expected under-read");
        Check("Matching word counts are not under-read",
            !OcrProcessor.SmokeKoboldUnderReadsWinOcr(
                "hello there friend how are you doing today really well",
                "hello there friend how are you doing today really well"),
            "same text");
    }

    // Merge overlapping islands (Settings → Balloons; default on).
    // Overlap uses Grow bounds + Crop pad (pad unclamped for the test only).
    {
        Console.WriteLine();
        Console.WriteLine("--- Merge overlapping islands ---");
        var a = new Rectangle(100, 100, 120, 80);
        var b = new Rectangle(180, 120, 100, 70); // overlaps a
        var merged = OcrProcessor.SmokeMergeOverlappingIslands(new[] { a, b }, cropPadPx: 0);
        var expected = Rectangle.Union(a, b);
        Check("Two overlapping islands → one union covering both",
            merged.Count == 1 &&
            merged[0].X == expected.X &&
            merged[0].Y == expected.Y &&
            merged[0].Width == expected.Width &&
            merged[0].Height == expected.Height,
            $"merged=[{string.Join(" | ", merged.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}"))}]");

        var c = new Rectangle(250, 140, 80, 60); // overlaps b only → chain A-B-C
        var chain = OcrProcessor.SmokeMergeOverlappingIslands(new[] { a, b, c }, cropPadPx: 0);
        var chainUnion = Rectangle.Union(Rectangle.Union(a, b), c);
        Check("Chain overlap A∩B∩C → single transitive union",
            chain.Count == 1 &&
            chain[0] == chainUnion,
            $"chain=[{string.Join(" | ", chain.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}"))}]");

        var left = new Rectangle(40, 40, 100, 80);
        var right = new Rectangle(200, 50, 100, 80); // gap 60px — no core or pad-16 meet
        var separate = OcrProcessor.SmokeMergeOverlappingIslands(
            new[] { left, right }, cropPadPx: 16);
        Check("Non-overlapping islands stay separate (gap > 2×pad)",
            separate.Count == 2,
            $"sep=[{string.Join(" | ", separate.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}"))}]");

        // Cores do not overlap (gap 20px), but Crop pad 16 on each side would meet.
        var nearL = new Rectangle(40, 40, 100, 80);   // right edge 140
        var nearR = new Rectangle(160, 50, 100, 80);  // left edge 160, gap=20
        var padMerge = OcrProcessor.SmokeMergeOverlappingIslands(
            new[] { nearL, nearR }, cropPadPx: 16);
        var padUnion = Rectangle.Union(nearL, nearR);
        Check("Near islands merge when Crop pad would bridge the gap",
            padMerge.Count == 1 &&
            padMerge[0] == padUnion,
            $"padMerge=[{string.Join(" | ", padMerge.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}"))}]");

        var padOff = OcrProcessor.SmokeMergeOverlappingIslands(
            new[] { nearL, nearR }, cropPadPx: 0);
        Check("Same near islands stay separate when Crop pad is 0",
            padOff.Count == 2,
            $"padOff=[{string.Join(" | ", padOff.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}"))}]");

        Check("ComicMergeOverlappingIslands defaults on",
            AppSettings.Current.ComicMergeOverlappingIslands);

        // Pad merge always applies even for multi-word dialogue (user setting).
        var solidL = new Rectangle(40, 40, 200, 90);
        var solidR = new Rectangle(220, 50, 200, 90); // gap 20 — pad 16 bridges
        var solidPad = OcrProcessor.SmokeMergeOverlappingIslands(
            new[] { solidL, solidR }, cropPadPx: 16);
        Check("Near multi-word islands still pad-merge (merge always)",
            solidPad.Count == 1,
            $"solidPad count={solidPad.Count}");
    }

    // Short dialogue still usable after clean (product requirement).
    {
        Console.WriteLine();
        Console.WriteLine("--- Short dialogue usability ---");
        Check("Real short dialogue still usable",
            OcrProcessor.SmokeIsUsableOcrText("no!") &&
            OcrProcessor.SmokeIsUsableOcrText("AFTERNOON."));
    }

    // Preview / Live / Analytics share one pipe: last capture is the live raw snap
    // (full res). Analytics must not re-sample pipeline frames for storage.
    {
        Console.WriteLine();
        Console.WriteLine("--- Balloons last capture == full-res live snap ---");
        OcrProcessor.SmokeClearDevCaptureCache();
        using var fullSnap = new Bitmap(1918, 999, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(fullSnap))
        {
            g.Clear(Color.FromArgb(30, 30, 35));
            g.FillRectangle(new SolidBrush(Color.FromArgb(200, 200, 205)), 40, 20, 1838, 960);
            g.FillRectangle(Brushes.White, 200, 80, 400, 120);
            g.FillRectangle(Brushes.Black, 240, 120, 200, 16);
        }

        OcrProcessor.SmokePublishLastCapture(fullSnap);
        using var loaded = OcrProcessor.SmokeTryLoadLastOcrCapture();
        Check("Last capture load non-null after publish", loaded != null);
        Check("Last capture width matches live snap (not 1280 cap)",
            loaded != null && loaded.Width == 1918,
            loaded == null ? "null" : $"{loaded.Width}x{loaded.Height}");
        Check("Last capture height matches live snap",
            loaded != null && loaded.Height == 999,
            loaded == null ? "null" : $"{loaded.Width}x{loaded.Height}");
        if (loaded != null)
        {
            string whyPx = "";
            Check("Last capture pixels == live snap (Balloons source identity)",
                OcrProcessor.SmokeBitmapsPixelEqual(fullSnap, loaded, out whyPx),
                whyPx);
        }

        // Simulate Analytics long-edge thumbnail in the wild: publishing full-res
        // must still win over any later accidental downscale in the cache path.
        using var thumb = new Bitmap(1280, 667, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(thumb))
            g.Clear(Color.Magenta);
        // Re-publish full snap after a wrong-size Set would have been a bug —
        // API only exposes PublishLastCapture for live; verify it overwrites.
        OcrProcessor.SmokePublishLastCapture(fullSnap);
        using var again = OcrProcessor.SmokeTryLoadLastOcrCapture();
        Check("Re-publish full snap keeps full dimensions",
            again != null && again.Width == 1918 && again.Height == 999,
            again == null ? "null" : $"{again.Width}x{again.Height}");
        OcrProcessor.SmokeClearDevCaptureCache();
    }

    // Image tab preview must match live OCR input; Default and ComicBook share prep.
    {
        Console.WriteLine();
        Console.WriteLine("--- Image prep preview == live OCR input ---");
        using var panel = new Bitmap(400, 280, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(panel))
        {
            // Letterbox-ish bars + content so trim can run.
            g.Clear(Color.Black);
            g.FillRectangle(new SolidBrush(Color.FromArgb(210, 200, 180)), 40, 20, 320, 240);
            g.FillRectangle(Brushes.White, 80, 50, 120, 70);
            g.FillRectangle(Brushes.Black, 100, 70, 60, 12);
            g.FillRectangle(Brushes.Black, 105, 90, 45, 10);
            g.FillRectangle(new SolidBrush(Color.FromArgb(40, 90, 160)), 220, 140, 90, 60);
        }

        bool savedComic = AppSettings.Current.ComicBook;
        try
        {
            AppSettings.Current.ComicBook = true;
            AppSettings.Current.NormalizeImagePrepSettings();
            using var liveCb = OcrProcessor.SmokeBuildLiveOcrInput(panel);
            using var prevCb = OcrProcessor.PreviewImagePrep(panel, "tone");
            Check("Image preview Display non-null (ComicBook ON)",
                prevCb.Display != null);
            string whyCb = "";
            bool matchCb = prevCb.Display != null &&
                OcrProcessor.SmokeBitmapsPixelEqual(prevCb.Display, liveCb, out whyCb);
            Check("Image preview == live OCR input (ComicBook ON)",
                matchCb,
                whyCb + $" stage={prevCb.StageName} " +
                $"{prevCb.Width}x{prevCb.Height} vs live {liveCb.Width}x{liveCb.Height}");
            Check("Stage name is tone OCR input",
                prevCb.StageName.Contains("tone", StringComparison.OrdinalIgnoreCase),
                prevCb.StageName);

            AppSettings.Current.ComicBook = false;
            AppSettings.Current.NormalizeImagePrepSettings();
            using var liveDef = OcrProcessor.SmokeBuildLiveOcrInput(panel);
            using var prevDef = OcrProcessor.PreviewImagePrep(panel, "tone");
            Check("Image preview Display non-null (Default)",
                prevDef.Display != null);
            string whyDef = "";
            bool matchDef = prevDef.Display != null &&
                OcrProcessor.SmokeBitmapsPixelEqual(prevDef.Display, liveDef, out whyDef);
            Check("Image preview == live OCR input (Default)",
                matchDef,
                whyDef + $" stage={prevDef.StageName} " +
                $"{prevDef.Width}x{prevDef.Height} vs live {liveDef.Width}x{liveDef.Height}");
            Check("Default stage is also tone OCR input (same prep as ComicBook)",
                prevDef.StageName.Contains("tone", StringComparison.OrdinalIgnoreCase),
                prevDef.StageName);

            // Prep is mode-independent: flipping ComicBook must not change pixels.
            Check("Default prep pixels == ComicBook prep pixels (shared pipeline)",
                OcrProcessor.SmokeBitmapsPixelEqual(liveCb, liveDef, out string whySame),
                whySame);
        }
        finally
        {
            AppSettings.Current.ComicBook = savedComic;
        }
    }

    // Dead-island: keep real dialogue; drop empty tiny geometry / weak scrap.
    {
        Console.WriteLine();
        Console.WriteLine("--- Dead-island keep dialogue / drop empty scrap ---");
        using var panel = new Bitmap(320, 280, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(panel))
        {
            g.Clear(Color.FromArgb(95, 105, 115));
            g.FillRectangle(Brushes.White, 30, 20, 140, 70);
            g.FillRectangle(Brushes.Black, 55, 42, 50, 10);
        }

        var balloonBox = new Rectangle(30, 20, 140, 70);
        Check("Speech plate looks like balloon fill",
            OcrProcessor.SmokeLooksLikeSpeechBalloonFill(panel, balloonBox));

        var kept = OcrProcessor.SmokeFilterDeadDetectRegions(
            panel,
            new[]
            {
                (balloonBox, "SORRY"),
                (new Rectangle(10, 10, 20, 15), ""),
                (new Rectangle(200, 20, 100, 80), "I LEFT TOWN WEEKS AGO FOREVER"),
            });
        Check("Dead-island keeps one-word dialogue on balloon plate",
            kept.Any(r => r.Text.Equals("SORRY", StringComparison.OrdinalIgnoreCase)),
            $"kept=[{string.Join(" | ", kept.Select(r => r.Text))}]");
        Check("Dead-island drops empty tiny geometry",
            !kept.Any(r => string.IsNullOrWhiteSpace(r.Text) && r.Bounds.Width < 80),
            $"kept=[{string.Join(" | ", kept.Select(r => $"{r.Bounds.Width}x{r.Bounds.Height}:{r.Text}"))}]");
        Check("Dead-island keeps multi-word dialogue island",
            kept.Any(r => r.Text.Contains("LEFT", StringComparison.OrdinalIgnoreCase)),
            $"kept=[{string.Join(" | ", kept.Select(r => r.Text))}]");
    }

    // Semicolons/dashes/quotes/parens go; commas → pause marks; . ! ? stay and
    // get a pause mark inserted after them. Mid-word apostrophes (you're / won't) stay.
    Check("Non-pause punct stripped (semicolon/dash not left; commas became breaks)",
        !Regex.IsMatch(normOn, @"[;:—–""()]") &&
        !Regex.IsMatch(normOn, @",") &&
        normOn.Contains("you're", StringComparison.Ordinal));
    Check("Commas replaced by pause marks; . ! ? kept for TTS",
        !Regex.IsMatch(normOn, @",") &&
        !Regex.IsMatch(normOff, @",") &&
        Regex.IsMatch(normOn, @"[.!?]") &&
        Regex.IsMatch(normOff, @"[.!?]"));
    // . ! ? , all split. Collapsed runs (!? / ???) count as ONE break.
    // Units: "...late," | "really yes ok." | "...now!" | "Why?" | "Really?"  → 5
    // (unit text still carries the terminal . ! ?)
    Check("Speak units at . ! ? , ; collapsed runs are one break (ON)",
        unitsOn == 5, $"units={unitsOn} text={TruncateForSmoke(normOn, 160)}");
    Check("Speak units at . ! ? , ; collapsed runs are one break (OFF)",
        unitsOff == 5, $"units={unitsOff}");
    Check("Question mark produces a unit break",
        unitsOn >= 2 &&
        normOn.Contains("why", StringComparison.OrdinalIgnoreCase),
        TruncateForSmoke(normOn, 120));
    Check("Comma produces a unit break (pause)",
        OcrProcessor.SmokeSpeakUnitCount(
            OcrProcessor.SmokeCleanForSpeech("hello, world", true)) == 2);

    string commaClean = OcrProcessor.SmokeCleanForSpeech("hello, world now.", true);
    var commaPauses = OcrProcessor.SmokePauseAfterMsList(commaClean);
    Check("Comma pause is default (DefaultCommaPauseMs)",
        commaPauses.Count >= 1 && commaPauses[0] == AppSettings.DefaultCommaPauseMs,
        $"pauses=[{string.Join(",", commaPauses)}] units={OcrProcessor.SmokeSpeakUnitCount(commaClean)}");

    string sentClean = OcrProcessor.SmokeCleanForSpeech("hello world. next line", true);
    var sentPauses = OcrProcessor.SmokePauseAfterMsList(sentClean);
    var sentUnits = OcrProcessor.SmokeSpeakUnits(sentClean);
    Check("Sentence pause is default (DefaultSentencePauseMs)",
        sentPauses.Count >= 1 && sentPauses[0] == AppSettings.DefaultSentencePauseMs,
        $"pauses=[{string.Join(",", sentPauses)}]");
    Check("Sentence unit keeps the period",
        sentUnits.Count >= 1 &&
        sentUnits[0].EndsWith(".", StringComparison.Ordinal),
        $"units=[{string.Join(" | ", sentUnits)}]");

    string ellipsisClean = OcrProcessor.SmokeCleanForSpeech("wait... what now", true);
    var ellipsisPauses = OcrProcessor.SmokePauseAfterMsList(ellipsisClean);
    var ellipsisUnits = OcrProcessor.SmokeSpeakUnits(ellipsisClean);
    Check("Ellipsis becomes period (kept) + sentence pause (default)",
        ellipsisPauses.Count >= 1 && ellipsisPauses[0] == AppSettings.DefaultSentencePauseMs &&
        OcrProcessor.SmokeSpeakUnitCount(ellipsisClean) == 2 &&
        ellipsisUnits[0].EndsWith(".", StringComparison.Ordinal),
        $"pauses=[{string.Join(",", ellipsisPauses)}] units=[{string.Join(" | ", ellipsisUnits)}]");

    // Regression: "Tell you?" cleans to its own unit (keeps ?); coalesce must NOT
    // mash it into the next sentence (was merging because "you" looked incomplete).
    string tellYouClean = OcrProcessor.SmokeCleanForSpeech(
        "Tell you? As if you don't already know! What Matilda did was horrible.");
    var tellYouUnits = OcrProcessor.SmokeSpeakUnits(tellYouClean);
    var afterCoal = OcrProcessor.SmokeCoalesceSpeakUnits(tellYouUnits);
    Check("Clean 'Tell you?' is its own speak unit (keeps ?)",
        tellYouUnits.Count >= 2 &&
        tellYouUnits[0].Equals("tell you?", StringComparison.Ordinal),
        $"units=[{string.Join(" | ", tellYouUnits.Take(4))}]");
    Check("Coalesce does not re-merge 'tell you?' into next sentence",
        afterCoal.Count >= 2 &&
        afterCoal[0].Equals("tell you?", StringComparison.Ordinal) &&
        !afterCoal[0].Contains("already know", StringComparison.Ordinal),
        $"coalesced=[{string.Join(" | ", afterCoal.Take(4))}]");

    // Regression: single short balloons ("No!", "OK!", "Go!") must survive
    // CleanForSpeech → ExpandToSpeakPieces. Luc is kept; pause mark is after.
    // A length<=2 gate used to drop letter-only units and TTS said "unreadable".
    foreach (var (raw, expect) in new[]
    {
        ("No!", "no!"),
        ("OK!", "ok!"),
        ("Go!", "go!"),
        ("Hi!", "hi!"),
        ("Oh!", "oh!"),
        ("Yes!", "yes!"),
    })
    {
        string shortClean = OcrProcessor.SmokeCleanForSpeech(raw, comicBook: true);
        var shortUnits = OcrProcessor.SmokeExpandSpeakUnits(new[] { shortClean });
        Check(
            $"Short balloon '{raw}' stays speakable (not unreadable)",
            shortUnits.Count == 1 &&
            shortUnits[0].Equals(expect, StringComparison.Ordinal) &&
            OcrProcessor.SmokeIsUsableOcrText(shortUnits[0]),
            $"clean={TruncateForSmoke(shortClean, 40)} units=[{string.Join(" | ", shortUnits)}]");
    }

    // Speak-dedupe: drop pure echoes; keep general crop-echo collapse.
    {
        Check("Speak-dedupe drops true short restatement of prior short unit",
            OcrProcessor.SmokeDedupeSpeakUnits(new[] { "really?", "really?" }).Count == 1,
            "duplicate 'really?' should collapse to one");
        // Longer crop echo of a mega unit should still drop.
        var megaEcho = OcrProcessor.SmokeDedupeSpeakUnits(new[]
        {
            "the scouts said they were headed north next week",
            "the scouts said",
        });
        Check("Speak-dedupe still drops multi-word subset echo of longer unit",
            megaEcho.Count == 1 &&
            megaEcho[0].Contains("north", StringComparison.Ordinal),
            $"mega=[{string.Join(" | ", megaEcho)}]");
        Check("ComicSequentialRegions defaults on (Balloons §9)",
            AppSettings.Current.ComicSequentialRegions);
    }
    Check("Bare 'no' (no punct) is usable OCR",
        OcrProcessor.SmokeIsUsableOcrText("no"));
    Check("Empty / single letter still unusable",
        !OcrProcessor.SmokeIsUsableOcrText("") &&
        !OcrProcessor.SmokeIsUsableOcrText("a") &&
        !OcrProcessor.SmokeIsUsableOcrText("!!"));

    // Regression: "…want to." + "It's because I can't." must stay separate pause units.
    string wantToClean = OcrProcessor.SmokeCleanForSpeech(
        "Just about everything... but not because I don't want to. It's because I can't.");
    var wantToUnits = OcrProcessor.SmokeSpeakUnits(wantToClean);
    var wantToCoal = OcrProcessor.SmokeCoalesceSpeakUnits(wantToUnits);
    var wantToPauses = OcrProcessor.SmokePauseAfterMsList(wantToClean);
    Check("Clean splits on ellipsis and period (want to / it's because)",
        wantToUnits.Count >= 3,
        $"units={wantToUnits.Count} [{string.Join(" | ", wantToUnits.Take(5))}]");
    Check("Coalesce keeps pause after 'want to' before 'it's because'",
        wantToCoal.Count >= 3 &&
        wantToCoal.Any(u =>
            u.Contains("want to", StringComparison.Ordinal) &&
            !u.Contains("it's because", StringComparison.Ordinal)) &&
        wantToCoal.Any(u =>
            u.Contains("it's because", StringComparison.Ordinal) ||
            u.Contains("i can't", StringComparison.Ordinal)),
        $"coalesced={wantToCoal.Count} [{string.Join(" | ", wantToCoal.Take(5))}]");
    Check("want-to panel: ellipsis and periods are sentence pauses (default)",
        wantToPauses.Count >= 2 &&
        wantToPauses.All(p => p == AppSettings.DefaultSentencePauseMs),
        $"pauses=[{string.Join(",", wantToPauses)}]");

    // Editable pipeline text rules (Settings → Speech → Text rules).
    {
        var catalog = SpeechTextRulesCatalog.CreateDefaults();
        int ruleCount = AppSettings.Current.SpeechTextRules.Count;
        Check("Speech text rules catalog is non-empty",
            ruleCount >= 20, $"count={ruleCount}");
        Check("CreateDefaults builds full catalog (all patterns compile)",
            catalog.Count >= 50 && catalog.Count == catalog.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            $"defaults={catalog.Count}");
        Check("Speech text rules include Mr. title",
            AppSettings.Current.SpeechTextRules.Any(r =>
                r.Id.Equals("title-mr", StringComparison.OrdinalIgnoreCase) && r.Enabled));
        Check("Speech text rules include noise strip stage",
            AppSettings.Current.SpeechTextRules.Any(r => r.Stage == SpeechTextRuleStage.Noise));
        Check("Speech text rules include decorators stage",
            AppSettings.Current.SpeechTextRules.Any(r => r.Stage == SpeechTextRuleStage.Decorators));

        // Noise: markdown + attach-image junk stripped
        string noiseClean = OcrProcessor.SmokeCleanForSpeech(
            "Hello **world** (attached image: 1) and [link](http://x.test).", true);
        Check("Noise rules strip markdown bold markers",
            noiseClean.Contains("hello", StringComparison.Ordinal) &&
            noiseClean.Contains("world", StringComparison.Ordinal) &&
            !noiseClean.Contains("**", StringComparison.Ordinal),
            TruncateForSmoke(noiseClean, 100));
        Check("Noise rules strip attach-image junk",
            !noiseClean.Contains("attached", StringComparison.Ordinal) &&
            !noiseClean.Contains("image", StringComparison.Ordinal),
            TruncateForSmoke(noiseClean, 100));
        Check("Retired uchar noise strip not in catalog",
            !AppSettings.Current.SpeechTextRules.Any(r =>
                r.Id.Equals("noise-c-type-uchar", StringComparison.OrdinalIgnoreCase)) &&
            !SpeechTextRulesCatalog.CreateDefaults().Any(r =>
                r.Id.Equals("noise-c-type-uchar", StringComparison.OrdinalIgnoreCase)));

        // Decorators: multi-dash between words → period clause
        string decoClean = OcrProcessor.SmokeCleanForSpeech("pay--but wait", true);
        Check("Decorator rules turn word--word into clause break",
            OcrProcessor.SmokeSpeakUnitCount(decoClean) >= 2 ||
            decoClean.Contains('.', StringComparison.Ordinal),
            TruncateForSmoke(decoClean, 80));

        // Disable mr. expansion temporarily → "mr smith" should NOT become "mister".
        var snap = AppSettings.Current.SpeechTextRules.Select(r => r.Clone()).ToList();
        try
        {
            var disabled = snap.Select(r => r.Clone()).ToList();
            foreach (var r in disabled)
            {
                if (r.Id.Equals("title-mr", StringComparison.OrdinalIgnoreCase))
                    r.Enabled = false;
            }
            AppSettings.Current.SetSpeechTextRules(disabled);
            string noMr = OcrProcessor.SmokeCleanForSpeech("Mr. Smith said hi.", true);
            Check("Disabled title-mr leaves mr in stream (not mister)",
                noMr.Contains("mr", StringComparison.Ordinal) &&
                !noMr.Contains("mister", StringComparison.Ordinal),
                TruncateForSmoke(noMr, 80));

            // MergeWithDefaults keeps deliberate omit (delete) of a built-in
            var withoutMr = disabled
                .Where(r => !r.Id.Equals("title-mr", StringComparison.OrdinalIgnoreCase))
                .ToList();
            AppSettings.Current.SetSpeechTextRules(withoutMr);
            Check("Deleted built-in title-mr stays gone after SetSpeechTextRules",
                !AppSettings.Current.SpeechTextRules.Any(r =>
                    r.Id.Equals("title-mr", StringComparison.OrdinalIgnoreCase)));
            string stillNoMr = OcrProcessor.SmokeCleanForSpeech("Mr. Lee ok.", true);
            Check("Deleted title-mr does not expand mister",
                !stillNoMr.Contains("mister", StringComparison.Ordinal),
                TruncateForSmoke(stillNoMr, 80));
        }
        finally
        {
            AppSettings.Current.SetSpeechTextRules(snap);
        }

        // Re-enabled defaults still expand mr.
        string yesMr = OcrProcessor.SmokeCleanForSpeech("Mr. Smith said hi.", true);
        Check("Re-enabled title-mr expands to mister",
            yesMr.Contains("mister", StringComparison.Ordinal),
            TruncateForSmoke(yesMr, 80));

        // Empty SetSpeechTextRules restores catalog (never leave pipeline empty)
        AppSettings.Current.SetSpeechTextRules(Array.Empty<SpeechTextRule>());
        Check("Empty SetSpeechTextRules restores defaults",
            AppSettings.Current.SpeechTextRules.Count >= 50);
        AppSettings.Current.SetSpeechTextRules(snap);

        // Sole OCR prompt resolve (editable from Speech → Prompts).
        Check("OCR prompt resolve non-empty",
            !string.IsNullOrWhiteSpace(AppSettings.Current.ResolveOcrPrompt()));
        string prevOcr = AppSettings.Current.OcrPrompt;
        try
        {
            AppSettings.Current.SetOcrPrompt("CUSTOM_TEST_PROMPT_ONLY_XYZ");
            Check("Custom OcrPrompt is resolved",
                AppSettings.Current.ResolveOcrPrompt().Contains("CUSTOM_TEST_PROMPT_ONLY_XYZ", StringComparison.Ordinal));
            AppSettings.Current.SetOcrPrompt("");
            Check("Blank OcrPrompt falls back to default",
                AppSettings.Current.ResolveOcrPrompt() == AppSettings.DefaultOcrPrompt);
            // Exact default text is stored as blank (PromptForIni)
            AppSettings.Current.SetOcrPrompt(AppSettings.DefaultOcrPrompt);
            Check("Setting OcrPrompt to default text stores as blank (using built-in)",
                AppSettings.Current.IsOcrPromptUsingDefault());
        }
        finally
        {
            AppSettings.Current.OcrPrompt = prevOcr;
        }
    }

    // Custom Voice pause settings must apply to typed boundaries.
    int prevComma = AppSettings.Current.VoiceCommaPauseMs;
    try
    {
        AppSettings.Current.VoiceCommaPauseMs = 750;
        AppSettings.Current.NormalizeVoiceSettings();
        string customClean = OcrProcessor.SmokeCleanForSpeech("hello, world now.", true);
        var customPauses = OcrProcessor.SmokePauseAfterMsList(customClean);
        Check("Custom CommaPauseMs=750 is applied",
            customPauses.Count >= 1 && customPauses[0] == 750,
            $"pauses=[{string.Join(",", customPauses)}]");
    }
    finally
    {
        AppSettings.Current.VoiceCommaPauseMs = prevComma;
        AppSettings.Current.NormalizeVoiceSettings();
    }

    // Voice tab: disable custom pause encodings — keep punctuation, no unit splits/delays.
    bool prevEncode = AppSettings.Current.VoiceUseCustomPauseEncodings;
    try
    {
        AppSettings.Current.VoiceUseCustomPauseEncodings = false;
        AppSettings.Current.NormalizeVoiceSettings();
        string offClean = OcrProcessor.SmokeCleanForSpeech(
            "hello, world. next line! why?", true);
        string offNorm = offClean.Replace("\r\n", "\n", StringComparison.Ordinal);
        int offUnits = OcrProcessor.SmokeSpeakUnitCount(offClean);
        var offPauses = OcrProcessor.SmokePauseAfterMsList(offClean);
        Check("Custom pause encoding off: commas kept (not pause marks)",
            offNorm.Contains(',', StringComparison.Ordinal),
            TruncateForSmoke(offNorm, 120));
        Check("Custom pause encoding off: . ! ? kept for TTS",
            Regex.IsMatch(offNorm, @"[.!?]"),
            TruncateForSmoke(offNorm, 120));
        Check("Custom pause encoding off: single speak unit (no typed splits)",
            offUnits == 1,
            $"units={offUnits} text={TruncateForSmoke(offNorm, 120)}");
        Check("Custom pause encoding off: no Task.Delay pause list",
            offPauses.Count == 0,
            $"pauses=[{string.Join(",", offPauses)}]");
        // Slider values must not apply while encoding is off.
        AppSettings.Current.VoiceCommaPauseMs = 900;
        AppSettings.Current.NormalizeVoiceSettings();
        string offCustom = OcrProcessor.SmokeCleanForSpeech("a, b. c!", true);
        Check("Custom pause encoding off: CommaPauseMs ignored",
            OcrProcessor.SmokeSpeakUnitCount(offCustom) == 1 &&
            OcrProcessor.SmokePauseAfterMsList(offCustom).Count == 0);
    }
    finally
    {
        AppSettings.Current.VoiceUseCustomPauseEncodings = prevEncode;
        AppSettings.Current.VoiceCommaPauseMs = AppSettings.DefaultCommaPauseMs;
        AppSettings.Current.NormalizeVoiceSettings();
    }

    // VL freestyle JSON wrappers — must not speak field names or \n leftovers.
    const string jsonLeakRaw =
        "Wait. You had something to do with it, Alex.\n\n" +
        "{\"text\": \"Something?\\nDarn near everything.\\n" +
        "I got the ball rolling when I told them where the target lived.\\n" +
        "H-how did you find out?\"}";
    string jsonLeakClean = OcrProcessor.SmokeCleanForSpeech(jsonLeakRaw, comicBook: false);
    var jsonLeakUnits = OcrProcessor.SmokeSpeakUnits(jsonLeakClean);
    string jsonLeakJoined = string.Join(" | ", jsonLeakUnits);
    Check("JSON wrapper: does not speak key 'text something'",
        !jsonLeakClean.Contains("text something", StringComparison.Ordinal) &&
        !jsonLeakUnits.Any(u =>
            u.Equals("text something?", StringComparison.Ordinal) ||
            u.StartsWith("text ", StringComparison.Ordinal)),
        $"units=[{TruncateForSmoke(jsonLeakJoined, 200)}]");
    Check("JSON wrapper: no \\n escape leftovers (ndarn / ni / nh)",
        !Regex.IsMatch(jsonLeakClean, @"\bndarn\b") &&
        !Regex.IsMatch(jsonLeakClean, @"\bni got\b") &&
        !Regex.IsMatch(jsonLeakClean, @"\bnh how\b"),
        $"clean={TruncateForSmoke(jsonLeakClean, 200)}");
    Check("JSON wrapper: keeps plain prefix + unwrapped dialogue",
        jsonLeakClean.Contains("alex", StringComparison.Ordinal) &&
        jsonLeakClean.Contains("darn near everything", StringComparison.Ordinal) &&
        jsonLeakClean.Contains("ball rolling", StringComparison.Ordinal) &&
        jsonLeakClean.Contains("how did you find out", StringComparison.Ordinal) &&
        jsonLeakUnits.Any(u => u.Contains("something?", StringComparison.Ordinal) &&
                               !u.Contains("to do with", StringComparison.Ordinal)),
        $"units=[{TruncateForSmoke(jsonLeakJoined, 200)}]");
    string pureJsonClean = OcrProcessor.SmokeCleanForSpeech(
        "{\"text\": \"Hello world.\\nNext balloon.\"}", comicBook: false);
    Check("Pure {\"text\"} object unwraps to prose only",
        pureJsonClean.Contains("hello world", StringComparison.Ordinal) &&
        pureJsonClean.Contains("next balloon", StringComparison.Ordinal) &&
        !pureJsonClean.Contains("text", StringComparison.Ordinal),
        $"clean={TruncateForSmoke(pureJsonClean, 120)}");

    // User speech rules (Settings → Speech tab): replace / strip after abbrev expand.
    var savedRules = AppSettings.Current.SpeechRules.Select(r => r.Clone()).ToList();
    try
    {
        // Natural user form: Find "X-Men" / Say as "Ex-Men" / strip "BRAP"
        // (engine looks up cleaned "x men" / "brap" internally).
        AppSettings.Current.SetSpeechRules(new[]
        {
            new SpeechRule
            {
                Match = "X-Men",
                Replace = "Ex-Men",
                Kind = SpeechMatchKind.Phrase,
                Enabled = true,
            },
            new SpeechRule
            {
                Match = "BRAP",
                Replace = "",
                Kind = SpeechMatchKind.Word,
                Enabled = true,
            },
        });
        string rulesClean = OcrProcessor.SmokeCleanForSpeech(
            "The X-Men and the women heard BRAP!", comicBook: true);
        Check("Speech rule: natural Find X-Men → Say as Ex-Men",
            rulesClean.Contains("Ex-Men", StringComparison.Ordinal),
            $"clean={TruncateForSmoke(rulesClean, 120)}");
        Check("Speech rule: cleaned form 'x men' is not left behind",
            !rulesClean.Contains("x men", StringComparison.Ordinal),
            $"clean={TruncateForSmoke(rulesClean, 120)}");
        Check("Speech rule: word strip BRAP (typed BRAP)",
            !Regex.IsMatch(rulesClean, @"\bbrap\b", RegexOptions.IgnoreCase),
            $"clean={TruncateForSmoke(rulesClean, 120)}");
        Check("Speech rule: other words unchanged (women)",
            rulesClean.Contains("women", StringComparison.Ordinal),
            $"clean={TruncateForSmoke(rulesClean, 120)}");

        // Word boundaries: Find "men" does not rewrite "women".
        AppSettings.Current.SetSpeechRules(new[]
        {
            new SpeechRule
            {
                Match = "men",
                Replace = "dudes",
                Kind = SpeechMatchKind.Word,
                Enabled = true,
            },
        });
        string menClean = OcrProcessor.SmokeCleanForSpeech(
            "The men and the women left.", comicBook: false);
        Check("Speech rule: word match does not hit women via men",
            menClean.Contains("dudes", StringComparison.Ordinal) &&
            menClean.Contains("women", StringComparison.Ordinal) &&
            !menClean.Contains("wodudes", StringComparison.Ordinal),
            $"clean={TruncateForSmoke(menClean, 120)}");

        // Disabled rule is a no-op
        AppSettings.Current.SetSpeechRules(new[]
        {
            new SpeechRule
            {
                Match = "hello",
                Replace = "goodbye",
                Kind = SpeechMatchKind.Word,
                Enabled = false,
            },
        });
        string offRule = OcrProcessor.SmokeCleanForSpeech("hello world", comicBook: false);
        Check("Speech rule: disabled rule does not replace",
            offRule.Contains("hello", StringComparison.Ordinal) &&
            !offRule.Contains("goodbye", StringComparison.Ordinal),
            $"clean={TruncateForSmoke(offRule, 80)}");

        // Hyphenated Find becomes multi-word after clean ("w-what" → "w what").
        // Must use word boundaries so "now what" / bare "what" are not rewritten.
        AppSettings.Current.SetSpeechRules(new[]
        {
            new SpeechRule
            {
                Match = "w-what",
                Replace = "wha-what",
                Kind = SpeechMatchKind.Word,
                Enabled = true,
            },
        });
        string stutterHit = OcrProcessor.SmokeCleanForSpeech(
            "She said w-what is that?", comicBook: false);
        Check("Speech rule: w-what stutter → wha-what (Word)",
            stutterHit.Contains("wha-what", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(stutterHit, @"\bw\s+what\b", RegexOptions.IgnoreCase),
            $"clean={TruncateForSmoke(stutterHit, 120)}");
        string stutterMissNow = OcrProcessor.SmokeCleanForSpeech(
            "Now what do we do?", comicBook: false);
        Check("Speech rule: w-what does not hit 'now what'",
            stutterMissNow.Contains("what", StringComparison.OrdinalIgnoreCase) &&
            !stutterMissNow.Contains("wha-what", StringComparison.OrdinalIgnoreCase),
            $"clean={TruncateForSmoke(stutterMissNow, 120)}");
        string stutterMissBare = OcrProcessor.SmokeCleanForSpeech(
            "What a day.", comicBook: false);
        Check("Speech rule: w-what does not hit bare 'what'",
            Regex.IsMatch(stutterMissBare, @"\bwhat\b", RegexOptions.IgnoreCase) &&
            !stutterMissBare.Contains("wha-what", StringComparison.OrdinalIgnoreCase),
            $"clean={TruncateForSmoke(stutterMissBare, 120)}");

        // Same Find as Phrase: multi-word still bounded (no 'now what' false hit).
        AppSettings.Current.SetSpeechRules(new[]
        {
            new SpeechRule
            {
                Match = "w-what",
                Replace = "wha-what",
                Kind = SpeechMatchKind.Phrase,
                Enabled = true,
            },
        });
        string phraseStutter = OcrProcessor.SmokeCleanForSpeech(
            "w-what now what", comicBook: false);
        Check("Speech rule: Phrase multi-word still bounds ends",
            phraseStutter.Contains("wha-what", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(phraseStutter, @"\bnow\s+what\b", RegexOptions.IgnoreCase) &&
            !phraseStutter.Contains("no wha-what", StringComparison.OrdinalIgnoreCase) &&
            !phraseStutter.Contains("now wha-what", StringComparison.OrdinalIgnoreCase),
            $"clean={TruncateForSmoke(phraseStutter, 120)}");

        // "tax men" must not become "ta Ex-Men" from Find X-Men (multi-word \b).
        AppSettings.Current.SetSpeechRules(new[]
        {
            new SpeechRule
            {
                Match = "X-Men",
                Replace = "Ex-Men",
                Kind = SpeechMatchKind.Word,
                Enabled = true,
            },
        });
        string taxMen = OcrProcessor.SmokeCleanForSpeech(
            "The tax men and the X-Men met.", comicBook: false);
        Check("Speech rule: X-Men Word does not hit tax men",
            taxMen.Contains("Ex-Men", StringComparison.Ordinal) &&
            taxMen.Contains("tax men", StringComparison.OrdinalIgnoreCase) &&
            !taxMen.Contains("ta Ex-Men", StringComparison.OrdinalIgnoreCase),
            $"clean={TruncateForSmoke(taxMen, 120)}");

        // Phrase single-token still allows substring (men ⊂ women).
        AppSettings.Current.SetSpeechRules(new[]
        {
            new SpeechRule
            {
                Match = "men",
                Replace = "dudes",
                Kind = SpeechMatchKind.Phrase,
                Enabled = true,
            },
        });
        string phraseMen = OcrProcessor.SmokeCleanForSpeech(
            "The men and the women left.", comicBook: false);
        Check("Speech rule: Phrase 'men' can hit inside women",
            phraseMen.Contains("dudes", StringComparison.OrdinalIgnoreCase) &&
            phraseMen.Contains("wodudes", StringComparison.OrdinalIgnoreCase),
            $"clean={TruncateForSmoke(phraseMen, 120)}");

        // Contractions: Find don't must match cleaned don't (apostrophe kept).
        AppSettings.Current.SetSpeechRules(new[]
        {
            new SpeechRule
            {
                Match = "don't",
                Replace = "do not",
                Kind = SpeechMatchKind.Word,
                Enabled = true,
            },
        });
        string dontClean = OcrProcessor.SmokeCleanForSpeech(
            "I don't know.", comicBook: false);
        Check("Speech rule: contraction Find don't → do not",
            dontClean.Contains("do not", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(dontClean, @"\bdon't\b", RegexOptions.IgnoreCase),
            $"clean={TruncateForSmoke(dontClean, 120)}");
    }
    finally
    {
        AppSettings.Current.SetSpeechRules(savedRules);
    }

    Console.WriteLine($"  cleaned ON  units={unitsOn}: {TruncateForSmoke(normOn, 160)}");
    Console.WriteLine($"  cleaned OFF units={unitsOff}: {TruncateForSmoke(normOff, 160)}");
    Console.WriteLine(
        $"  tell-you coalesce: before={tellYouUnits.Count} after={afterCoal.Count} " +
        $"u0=\"{TruncateForSmoke(afterCoal.ElementAtOrDefault(0) ?? "", 40)}\"");
    Console.WriteLine(
        $"  want-to coalesce: before={wantToUnits.Count} after={wantToCoal.Count} " +
        $"pauses=[{string.Join(",", wantToPauses)}] " +
        $"[{string.Join(" | ", wantToCoal.Select(u => TruncateForSmoke(u, 36)))}]");
    Console.WriteLine(
        $"  json-unwrap units={jsonLeakUnits.Count}: {TruncateForSmoke(jsonLeakJoined, 200)}");
}

// Local pre-capture only for this test harness (not SpeakRect production paths).
try
{
    Directory.CreateDirectory(debugDir);
    using var snap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(snap))
        g.CopyFromScreen(rect.Location, Point.Empty, rect.Size);
    string pre = Path.Combine(debugDir, "modesmoke_pre_capture.png");
    snap.Save(pre, ImageFormat.Png);
    Console.WriteLine($"pre-capture → {pre} ({snap.Width}x{snap.Height})");
    Check("Pre-capture non-trivial", snap.Width > 100 && snap.Height > 100);
}
catch (Exception ex)
{
    Check("Pre-capture", false, ex.Message);
    return 2;
}

Console.WriteLine();
Console.WriteLine("--- Local-LLM ---");
LocalLlmHost.Start();
var waitSw = Stopwatch.StartNew();
bool ready = LocalLlmHost.WaitUntilReadyAsync(TimeSpan.FromMinutes(4)).GetAwaiter().GetResult();
Check("Local-LLM API ready", ready, ready ? $"in {waitSw.Elapsed.TotalSeconds:0.0}s" : "timeout");
if (!ready)
{
    Console.WriteLine("Cannot run OCR modes without Local-LLM. Aborting mode matrix.");
    return 3;
}

var modes = new (string Name, bool Comic)[]
{
    ("Simple (ComicBook OFF)", false),
    ("ComicBook ON", true),
};

string ocrPath = Path.Combine(debugDir, "last_ocr.txt");
var results = new List<(string Mode, bool Ok, string Preview, long Ms)>();

foreach (var mode in modes)
{
    Console.WriteLine();
    Console.WriteLine($"--- {mode.Name} ---");
    AppSettings.Current.ComicBook = mode.Comic;
    AppSettings.Current.NormalizeModeFlags();
    Console.WriteLine(
        $"  flags: ComicBook={AppSettings.Current.ComicBook}");

    try { if (File.Exists(ocrPath)) File.Delete(ocrPath); } catch { /* ignore */ }
    DateTime t0 = DateTime.UtcNow;
    var sw = Stopwatch.StartNew();

    var ocr = new OcrProcessor(rect);
    ocr.Start();

    string? text = null;
    bool sawFile = false;
    for (int i = 0; i < 180; i++)
    {
        Thread.Sleep(1000);
        if (!File.Exists(ocrPath)) continue;
        try
        {
            var info = new FileInfo(ocrPath);
            if (info.LastWriteTimeUtc < t0.AddSeconds(-2)) continue;
            Thread.Sleep(400);
            text = File.ReadAllText(ocrPath);
            sawFile = true;
            if (text.Contains("winner=", StringComparison.Ordinal) ||
                text.Contains("--- detail ---", StringComparison.Ordinal) ||
                text.Contains("pipeline=", StringComparison.Ordinal) ||
                i > 8)
            {
                long len1 = info.Length;
                Thread.Sleep(1000);
                long len2 = new FileInfo(ocrPath).Length;
                if (len1 == len2 || i > 20)
                    break;
            }
        }
        catch { /* still writing */ }
    }

    sw.Stop();
    ocr.Stop();
    ocr.Dispose();

    string preview = "(no last_ocr.txt)";
    bool usable = false;
    if (!string.IsNullOrEmpty(text))
    {
        string head = text.Split(new[] { "--- detail ---" }, StringSplitOptions.None)[0].Trim();
        preview = head.Length > 120 ? head[..120] + "…" : head;
        usable = head.Length >= 8 &&
                 !head.Equals("(unreadable)", StringComparison.OrdinalIgnoreCase) &&
                 head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }

    try
    {
        if (File.Exists(ocrPath))
        {
            string safe = mode.Name
                .Replace(" ", "_", StringComparison.Ordinal)
                .Replace("(", "", StringComparison.Ordinal)
                .Replace(")", "", StringComparison.Ordinal);
            File.Copy(ocrPath, Path.Combine(debugDir, $"last_ocr_{safe}.txt"), overwrite: true);
        }
    }
    catch { /* ignore */ }

    results.Add((mode.Name, usable, preview, sw.ElapsedMilliseconds));
    Check($"{mode.Name} produced text", usable,
        usable ? $"{sw.ElapsedMilliseconds}ms · {preview}" : $"{sw.ElapsedMilliseconds}ms · {preview}");
    if (!sawFile)
        Console.WriteLine("  note: last_ocr.txt only written by Debug SpeakRect builds (Release/publish = silent)");
}

Console.WriteLine();
Console.WriteLine("=== Summary ===");
foreach (var r in results)
    Console.WriteLine($"  {(r.Ok ? "OK " : "BAD")}  {r.Mode,-28} {r.Ms,6}ms  {r.Preview}");

Console.WriteLine();
if (failed == 0)
{
    Console.WriteLine("ALL MODE SMOKE TESTS PASSED");
    return 0;
}
Console.WriteLine($"FAILED: {failed} check(s)");
return 1;
