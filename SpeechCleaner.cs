using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpeakRect
{
    /// <summary>Pre-TTS speech cleaning and speak-unit pipeline.</summary>
    public static class SpeechCleaner
    {
        public static bool IsUnusable(string? text) => IsUnusableOcrText(text);

        public static string CleanForSpeech(string input, bool? comicBook)
        {
            if (comicBook is null) return CleanForSpeech(input);
            bool saved = AppSettings.Current.ComicBook;
            try { AppSettings.Current.ComicBook = comicBook.Value; return CleanForSpeech(input); }
            finally { AppSettings.Current.ComicBook = saved; }
        }


        private static bool UseCustomPauseEncodings =>
            AppSettings.Current.VoiceUseCustomPauseEncodings;

        private static bool ComicBookOff => !AppSettings.Current.ComicBook;

        private static int ClampSpeakPauseMs(int ms) =>
            Math.Clamp(ms, AppSettings.MinSpeakPauseMs, AppSettings.MaxSpeakPauseMs);

        private static int BubblePauseMs =>
            UseCustomPauseEncodings
                ? ClampSpeakPauseMs(AppSettings.Current.VoiceBubblePauseMs)
                : 0;

        private static int SentencePauseMs =>
            UseCustomPauseEncodings
                ? ClampSpeakPauseMs(AppSettings.Current.VoiceSentencePauseMs)
                : 0;

        private static int CommaPauseMs =>
            UseCustomPauseEncodings
                ? ClampSpeakPauseMs(AppSettings.Current.VoiceCommaPauseMs)
                : 0;

        private static int OtherPauseMs =>
            UseCustomPauseEncodings
                ? ClampSpeakPauseMs(AppSettings.Current.VoiceOtherPauseMs)
                : 0;

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s[..max] + "…";
        }

        // Embedded in cleaned OCR between speak units (not spoken). Survives
        // punctuation strip because the allow-list keeps \x1C–\x1F.
        public const char PauseMarkComma = '\x1C';
        public const char PauseMarkSentence = '\x1D';
        public const char PauseMarkOther = '\x1E';
        public const char PauseMarkBubble = '\x1F';

        internal static int CountAlnum(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) n++;
            return n;
        }


        /// <summary>Speak units from one or more OCR parts (text only).</summary>
        internal static List<string> ExpandToSpeakUnits(IEnumerable<string> parts) =>
            ExpandToSpeakPieces(parts).Select(p => p.Text).ToList();


        /// <summary>
        /// Speak pieces from one or more OCR parts. Boundaries inside a part keep
        /// typed pauses (comma/sentence/other/bubble); boundaries <b>between</b>
        /// parts are balloon splits and always use <see cref="BubblePauseMs"/>.
        /// </summary>
        internal static List<SpeakPiece> ExpandToSpeakPieces(IEnumerable<string> parts)
        {
            var pieces = new List<SpeakPiece>();
            if (parts == null)
                return pieces;

            foreach (string part in parts)
            {
                if (IsUnusableOcrText(part)) continue;
                var next = SplitSpeakPieces(part)
                    .Where(p => !IsUnusableOcrText(p.Text))
                    .ToList();
                if (next.Count == 0)
                    continue;

                if (pieces.Count > 0)
                    pieces[^1] = pieces[^1].WithPause(BubblePauseMs);

                pieces.AddRange(next);
            }

            if (pieces.Count > 0)
                pieces[^1] = pieces[^1].WithPause(0);

            return pieces;
        }


        /// <summary>
        /// Dedupe speak pieces by text; when the list changes, remaining boundaries
        /// become bubble pauses (safe default). Unchanged lists keep typed pauses.
        /// </summary>
        internal static List<SpeakPiece> DedupeSpeakPiecesForTts(
            List<SpeakPiece> pieces,
            StringBuilder detail)
        {
            if (pieces == null || pieces.Count <= 1)
                return pieces ?? new List<SpeakPiece>();

            var texts = pieces.Select(p => p.Text).ToList();
            var deduped = DedupeSpeakUnitsForTts(texts, detail);
            if (deduped.Count == pieces.Count &&
                texts.SequenceEqual(deduped, StringComparer.Ordinal))
                return pieces;

            var result = new List<SpeakPiece>(deduped.Count);
            for (int i = 0; i < deduped.Count; i++)
            {
                int pause = i < deduped.Count - 1 ? BubblePauseMs : 0;
                result.Add(new SpeakPiece(deduped[i], pause));
            }
            return result;
        }


        /// <summary>
        /// Pre-TTS filter: drop units that are largely repeats of earlier speech.
        /// Handles mega-crop then small-crop echo (sailors said inside unit 1, again
        /// as unit 4) where symmetric overlap is low because one unit is much longer.
        /// Keeps earlier wording for pure echoes; <b>never</b> drops a unit that adds
        /// real novel content words (e.g. truncated "…wondrous" then full
        /// "…wondrous beginning!"). Prefer replacing the earlier partial with the
        /// longer completion when appropriate.
        /// </summary>
        internal static List<string> DedupeSpeakUnitsForTts(
            List<string> units,
            StringBuilder detail)
        {
            if (units.Count <= 1)
                return units;

            var kept = new List<string>();
            // Running bag of tokens already spoken (union of kept units).
            var spokenTok = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < units.Count; i++)
            {
                string u = units[i];
                if (IsUnusableOcrText(u))
                    continue;

                var uTok = ToTokenSet(u);
                if (uTok.Count == 0)
                    continue;

                int words = ComicRegionGeometry.CountWords(u);
                // Content tokens (len>=3) not yet spoken — tails like "beginning"
                int novelContent = 0;
                foreach (string t in uTok)
                {
                    if (t.Length >= 3 && !spokenTok.Contains(t))
                        novelContent++;
                }

                // How much of this unit is already in prior speech?
                double coverBySpoken = spokenTok.Count == 0
                    ? 0
                    : TokenCoverageOfAByB(u, string.Join(" ", spokenTok));

                // Also: fully covered by any single earlier unit (subset / paraphrase).
                double bestSingle = 0;
                int bestIdx = -1;
                for (int k = 0; k < kept.Count; k++)
                {
                    double c = TokenCoverageOfAByB(u, kept[k]);
                    if (c > bestSingle)
                    {
                        bestSingle = c;
                        bestIdx = k;
                    }
                }

                // Completion: later unit extends an earlier partial (shared body +
                // novel tail). Replace the partial so TTS gets "…beginning!".
                if (bestIdx >= 0 &&
                    novelContent >= 1 &&
                    bestSingle >= 0.55 &&
                    TokenCoverageOfAByB(kept[bestIdx], u) >= 0.68 &&
                    (ComicRegionGeometry.CountWords(u) > ComicRegionGeometry.CountWords(kept[bestIdx]) ||
                     CountAlnum(u) > CountAlnum(kept[bestIdx]) + 2 ||
                     IsClearlyBetterOcr(u, kept[bestIdx])))
                {
                    detail.AppendLine(
                        $"  speak-dedupe complete unit[{i + 1}] ? kept[{bestIdx + 1}] " +
                        $"novel={novelContent} \"{Truncate(kept[bestIdx], 36)}\" ? " +
                        $"\"{Truncate(u, 40)}\"");
                    // Rebuild spokenTok after replace
                    kept[bestIdx] = u;
                    spokenTok.Clear();
                    foreach (string k in kept)
                        foreach (string t in ToTokenSet(k))
                            spokenTok.Add(t);
                    continue;
                }

                // Thresholds: short echoes need high coverage; longer units need more.
                // "two sailors-singapore" inside a fat caption dump ? cover - 0.9+.
                bool isEcho =
                    (words <= 14 && (coverBySpoken >= 0.72 || bestSingle >= 0.72)) ||
                    (words <= 28 && (coverBySpoken >= 0.82 || bestSingle >= 0.85)) ||
                    (words > 28 && bestSingle >= 0.90 && coverBySpoken >= 0.80);

                // Near-duplicate of previous unit (symmetric)
                if (!isEcho && kept.Count > 0)
                {
                    double ov = TokenOverlapRatio(u, kept[^1]);
                    if (ov >= 0.78)
                        isEcho = true;
                }

                // Never drop pure-echo logic when this unit still adds real words
                // (hard case: cover 0.83 on "…wondrous beginning!" vs truncated prior).
                if (isEcho && novelContent >= 1 && words >= 2)
                {
                    detail.AppendLine(
                        $"  speak-dedupe keep-novel unit[{i + 1}] novel={novelContent} " +
                        $"coverSpoken={coverBySpoken:F2} \"{Truncate(u, 44)}\"");
                    isEcho = false;
                }

                // Short balloons often reuse a word from earlier dialogue
                // ("Really?" after "it's really good to see you"). Token bag /
                // bestSingle coverage is 1.0 on the shared lemma, but that is
                // not an OCR echo of a prior unit — only drop when a prior unit
                // of similar length actually restates the same short phrase.
                if (isEcho && words <= 2 && kept.Count > 0)
                {
                    bool similarLengthPrior = false;
                    for (int k = 0; k < kept.Count; k++)
                    {
                        int kw = ComicRegionGeometry.CountWords(kept[k]);
                        if (kw > words + 2)
                            continue;
                        if (TokenCoverageOfAByB(u, kept[k]) >= 0.80 &&
                            TokenCoverageOfAByB(kept[k], u) >= 0.50)
                        {
                            similarLengthPrior = true;
                            break;
                        }
                    }

                    if (!similarLengthPrior)
                    {
                        detail.AppendLine(
                            $"  speak-dedupe keep-short unit[{i + 1}] words={words} " +
                            $"coverSpoken={coverBySpoken:F2} bestSingle={bestSingle:F2}" +
                            (bestIdx >= 0 ? $"~kept[{bestIdx + 1}]" : "") +
                            $" \"{Truncate(u, 44)}\"");
                        isEcho = false;
                    }
                }

                if (isEcho)
                {
                    detail.AppendLine(
                        $"  speak-dedupe drop unit[{i + 1}] words={words} " +
                        $"coverSpoken={coverBySpoken:F2} bestSingle={bestSingle:F2}" +
                        (bestIdx >= 0 ? $"~kept[{bestIdx + 1}]" : "") +
                        $" \"{Truncate(u, 44)}\"");
                    continue;
                }

                kept.Add(u);
                foreach (string t in uTok)
                    spokenTok.Add(t);
            }

            return kept.Count > 0 ? kept : units;
        }


        /// <summary>
        /// True when cleaned OCR is empty, a model refusal, or a prompt echo
        /// (vision model sometimes dumps the task instruction instead of reading).
        /// </summary>
        public static bool IsUnusableOcrText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            string t = text.Trim();

            // No real words / almost no letters (private-use spam, punctuation-only).
            // Do NOT reject length-2 letter words: CleanForSpeech keeps short dialogue
            // like "No!" / "OK!" as "no!" / "ok!" (luc kept; pause mark after), and
            // ExpandToSpeakPieces re-checks each unit — a length<=2 gate made short
            // balloons speak "unreadable" even when OCR was correct.
            if (ComicRegionGeometry.CountWords(t) < 1 || CountAlnum(t) < 2)
                return true;

            // VL sometimes emits long non-Latin junk for hard crops (Storm dual panel
            // right-top returned pages of Linear-B-like codepoints as "usable").
            if (IsMostlyNonLatinLetterNoise(t))
                return true;

            // VL regurgitated our task prompt (or a close variant) ? treat as failure
            // so recovery / crop / WinOCR paths can run.
            if (IsPromptEcho(t))
                return true;

            // Common VL refusals / empty-result phrasings (after CleanForSpeech lowercases)
            if (Regex.IsMatch(t,
                    @"^(?:unreadable|n/?a|none|null|nothing|empty|" +
                    @"no\s+(?:text|content|readable\s+text)(?:\s+found)?|" +
                    @"(?:i\s+)?(?:can(?:not|'t)|could not|unable to)\s+(?:read|see|find|extract|detect).{0,40}|" +
                    @"there is no (?:visible\s+)?text|" +
                    @"no text (?:is )?(?:visible|detected|present|found)|" +
                    @"sorry.? (?:i )?(?:can(?:not|'t)|could not).{0,40})$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;

            // Lone C-type token (unsigned char) left after noise strip — never speak.
            if (Regex.IsMatch(t,
                    @"^(?:unsigned\s+char|u\s*char|uchar)[.!?]*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;

            return false;
        }


        /// <summary>
        /// True when letter content is mostly outside basic Latin (English comics).
        /// Catches private-use / exotic-script spam that still has "letters" for alnum counts.
        /// </summary>
        internal static bool IsMostlyNonLatinLetterNoise(string text)
        {
            int letters = 0;
            int latin = 0;
            foreach (char c in text)
            {
                if (!char.IsLetter(c))
                    continue;
                letters++;
                // Basic Latin + Latin-1 supplement letters used in English OCR
                if (c <= 0x024F)
                    latin++;
            }

            if (letters < 4)
                return false;

            // Fewer than ~1/3 Latin letters → not usable English dialogue
            return latin * 3 < letters;
        }


        /// <summary>
        /// True when cleaned model output is (mostly) one of our OCR task prompts
        /// rather than text from the image. Case-insensitive; expects CleanForSpeech
        /// lowercasing already applied, but re-normalizes just in case.
        /// </summary>
        internal static bool IsPromptEcho(string text)
        {
            string n = Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ").Trim();
            if (n.Length < 3)
                return false;

            // Strip trailing period that CleanForSpeech / model may leave
            string nBare = n.TrimEnd('.', '!', '?', ':', ';', ' ');

            // Active prompts + hard-coded defaults so custom ini still strips echoes.
            // Longest first so longer custom prompts win over shorter substrings.
            foreach (string prompt in AppSettings.Current.AllKnownPrompts()
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.Ordinal)
                         .OrderByDescending(p => p.Length))
            {
                string p = Regex.Replace(prompt.Trim().ToLowerInvariant(), @"\s+", " ").Trim();
                string pBare = p.TrimEnd('.', '!', '?', ':', ';', ' ');
                if (pBare.Length < 2)
                    continue;

                if (nBare == pBare)
                    return true;

                // Response is prompt plus a few trailing chars ("OCR: ", ellipsis, etc.)
                if (nBare.StartsWith(pBare, StringComparison.Ordinal) &&
                    nBare.Length <= pBare.Length + 12)
                    return true;

                // Prompt appears as the bulk of a short dump
                if (pBare.Length >= 16 &&
                    nBare.Contains(pBare, StringComparison.Ordinal) &&
                    nBare.Length <= pBare.Length + 24)
                    return true;
            }

            // Soft patterns: instruction-style dumps that aren't exact prompt matches
            // Short OFF prompt ("extract all text") and longer "from this image" forms
            if (Regex.IsMatch(nBare,
                    @"^(?:please\s+)?extract\s+all\s+text\.?$",
                    RegexOptions.CultureInvariant))
                return true;

            if (Regex.IsMatch(nBare,
                    @"^(?:please\s+)?extract\s+(?:all\s+)?(?:the\s+)?(?:readable\s+)?" +
                    @"text\s+from\s+(?:the\s+|this\s+|provided\s+)*" +
                    @"(?:image|pdf|comic\s+panel|this\s+image\s+crop).{0,100}$",
                    RegexOptions.CultureInvariant))
                return true;

            if (Regex.IsMatch(nBare,
                    @"^(?:ocr|spotting|table\s*recognition|formula\s*recognition)\s*:?\s*$",
                    RegexOptions.CultureInvariant))
                return true;

            return false;
        }

        /// <param name="maxTokens">
        /// Generation ceiling. Use <see cref="CropMaxTokens"/> for bubble crops,
        /// <see cref="FullFrameMaxTokens"/> for whole-selection fallbacks.
        /// </param>
        /// <param name="temperature">
        /// Decode temperature. Primary path uses <see cref="KoboldPrimaryTemperature"/> (0);
        /// recovery may use <see cref="KoboldRecoveryTemperature"/>.
        /// </param>

        public static string CleanForSpeech(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            string s = input;

            // glmocr/VL sometimes wraps OCR as {"text":"..."} or mixes plain prose
            // with a trailing object. Unwrap BEFORE punctuation strip so TTS never
            // speaks the key name "text" or escape leftovers ("n" from \n).
            s = UnwrapModelJsonPayload(s);

            // Hard markdown / VL chrome strip (always on — not only Text rules catalog).
            // Catches fences, headings, bold, lists, "Here is the text:" preambles.
            s = StripMarkdownLlmJunk(s);

            // First post-OCR step: ellipsis → single period (then a normal sentence
            // pause via NormalizeSpeechPunctuation). Avoids spoken "dot dot dot"
            // and must run before CollapseAdjacentPunctuation (which would leave one .).
            s = Regex.Replace(s, @"\.{3,}|\u2026+", ".");

            // Collapse side-by-side punctuation runs to the first mark only
            // (!!! → !, !? → !, ?! → ?). Dashes left alone (see method).
            s = CollapseAdjacentPunctuation(s);

            // ComicBook: continuation dashes between balloons → bubble pause marks
            // so SplitSpeakBlocks / bubble pauses fire ("GARGOYLE--\n--BUT").
            // Skip when custom pause encoding is off (no typed marks / delays).
            if (!ComicBookOff && UseCustomPauseEncodings)
                s = PromoteDashBalloonBoundaries(s);

            // Pipeline Noise rules (Settings → Speech → Text rules): spotting,
            // attach-image junk, markdown — formerly hard-coded regex here.
            s = SpeechTextRulesEngine.Apply(
                s, AppSettings.Current.SpeechTextRules, SpeechTextRuleStage.Noise);

            // Optional casing fold (Settings → Speech → Text rules). Mutually
            // exclusive toggles: title-case ALL CAPS (HELLO → Hello) vs full
            // force-lowercase. Only one should be on; both off keeps OCR casing.
            if (AppSettings.Current.SpeechTitleCaseAllCaps)
                s = TitleCaseAllCapsWords(s);
            else if (AppSettings.Current.SpeechForceLowercase)
                s = s.ToLowerInvariant();

            // Keep spoken contractions intact; expand only abbreviations/honorifics
            // (mr. → mister) so TTS does not spell letters or leave a leftover
            // period after the expansion.
            // Rules live in Settings → Speech → Text rules (Abbrev stage).
            // Then protect unknown dotted letter-acronyms (E.S.U.) so pause
            // encoding does not split them into single-letter scraps.
            s = ExpandSpeechAbbreviations(s);

            // VL often appends (or prefixes) the task instruction after real OCR.
            // Strip those fragments so TTS only speaks image text; pure echoes
            // then collapse to empty → IsUnusableOcrText / WinOCR fallback.
            s = StripPromptContamination(s);

            // Join print/comic syllable hyphens before TTS ("responsibil-\nity")
            // while still preserving intentional compounds (x-men, well-known).
            // Run before decorator strip and before newline→space smash.
            s = JoinLineBreakHyphens(s);

            // Comic balloon "--" / arrows / decorative junk — Text rules Decorators.
            s = StripComicSpeechDecorators(s);

            // Final punctuation chain (after abbrev expand so "mr." is already "mister"):
            //   1) strip everything that is not a pause mark (. ! ? ,) or typed pause char
            //   2) collapse adjacent pause marks (!? / !!! / ?! / ,,) down to ONE mark
            //   3) when custom encodings on: keep . ! ? (TTS prosody) and insert
            //      sentence pause mark AFTER each; replace , → comma pause mark
            // Marks MUST survive — SplitSpeakBlocks + typed delays depend on them.
            s = NormalizeSpeechPunctuation(s);

            // Whitespace normalize while KEEPING typed pause marks (when enabled).
            // blank lines from the model → bubble pause (balloon boundary), else space
            // single \n (line wrap) → space
            s = Regex.Replace(s, @"\r\n?", "\n");
            if (UseCustomPauseEncodings)
                s = Regex.Replace(s, @"\n\s*\n+", PauseMarkBubble.ToString());
            else
                s = Regex.Replace(s, @"\n\s*\n+", " ");
            s = Regex.Replace(s, @"\n", " ");
            s = Regex.Replace(s, @"[^\S\n]+", " ");
            if (UseCustomPauseEncodings)
            {
                // Normalize spacing around pause marks (marks stay single chars).
                s = Regex.Replace(s,
                    $@"\s*([{PauseMarkComma}{PauseMarkSentence}{PauseMarkOther}{PauseMarkBubble}])\s*",
                    " $1 ");
                // Collapse adjacent pause marks to the first (strongest already chosen upstream).
                s = Regex.Replace(s,
                    $@"([{PauseMarkComma}{PauseMarkSentence}{PauseMarkOther}{PauseMarkBubble}])" +
                    $@"(?:\s*[{PauseMarkComma}{PauseMarkSentence}{PauseMarkOther}{PauseMarkBubble}])+",
                    "$1");
            }
            // Catch wraps that only became "word- word" after newline→space
            s = JoinLineBreakHyphens(s);
            s = Regex.Replace(s, @" {2,}", " ");
            s = s.Trim();

            // User speech rules LAST (Settings → Speech, profile-backed).
            // After case-fold, strip, squish, punct, and pause encoding so user
            // replacements are not re-pruned and can restore preferred casing /
            // pronunciation that our cleaner would otherwise remove.
            // Pause-mark control chars are preserved by the engine (not spaces).
            s = SpeechRulesEngine.Apply(s, AppSettings.Current.SpeechRules);
            return s;
        }


        /// <summary>
        /// ComicBook: turn continuation-dash pairs into blank-line balloon
        /// boundaries so TTS gets bubble pauses.
        /// <c>GARGOYLE--\n--BUT</c> / <c>word-- --word</c> → two speak units.
        /// </summary>
        internal static string PromoteDashBalloonBoundaries(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            string s = input;
            const string dashes = @"[\-\u2013\u2014]";

            // Bubble pause mark — full-second balloon boundary.
            static string BreakAfter(string leftWord) => leftWord + PauseMarkBubble;

            // Line / blank ends with -- then next non-empty starts with --
            // "GARGOYLE--\n--BUT WE" / "GARGOYLE--\n\n--BUT WE"
            s = Regex.Replace(
                s,
                $@"(\w)\s*{dashes}{{2,}}\s*\n+\s*{dashes}{{2,}}\s*(\w)",
                m => BreakAfter(m.Groups[1].Value) + m.Groups[2].Value,
                RegexOptions.CultureInvariant);

            // Same-line / spaced: "pay-- --but" or "hope----next"
            s = Regex.Replace(
                s,
                $@"(\w)\s*{dashes}{{2,}}\s+{dashes}{{2,}}\s*(\w)",
                m => BreakAfter(m.Groups[1].Value) + m.Groups[2].Value,
                RegexOptions.CultureInvariant);

            // Trailing balloon dash then capital start after whitespace
            // "GARGOYLE-- BUT WE" mid-paragraph after other cleanup
            s = Regex.Replace(
                s,
                $@"(\w)\s*{dashes}{{2,}}\s+(?=\p{{Lu}})",
                m => BreakAfter(m.Groups[1].Value),
                RegexOptions.CultureInvariant);

            return s;
        }


        /// <summary>
        /// Title-case words that are entirely uppercase (length ≥ 2):
        /// <c>HELLO</c> → <c>Hello</c>, <c>WHAT'S</c> → <c>What's</c>.
        /// Mixed-case words and single-letter tokens (<c>I</c>, <c>A</c>) are
        /// left alone. Used when <see cref="AppSettings.SpeechTitleCaseAllCaps"/>
        /// is on (Settings → Speech → Text rules).
        /// </summary>
        internal static string TitleCaseAllCapsWords(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // 2+ uppercase letters, optional 'S / 'T style suffixes still all-caps.
            return Regex.Replace(
                input,
                @"\b[\p{Lu}]{2,}(?:'[\p{Lu}]+)*\b",
                m =>
                {
                    string w = m.Value;
                    return char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant();
                });
        }


        /// <summary>
        /// Expand common abbreviations and honorifics to full spoken words so TTS
        /// does not spell letters or speak punctuation leftovers
        /// (e.g. <c>mr.</c> / <c>Mr.</c> → <c>mister</c> with the period consumed).
        /// Patterns match case-insensitively (works with or without
        /// <see cref="AppSettings.SpeechForceLowercase"/>).
        /// <para>
        /// <b>Spoken contractions are left intact</b> (including apostrophe) —
        /// expanding <c>don't</c> → <c>do not</c> / <c>aren't</c> → <c>are not</c>
        /// made TTS sound stilted. Same for <c>you're</c>, <c>won't</c>,
        /// <c>can't</c>, <c>you'd</c>, <c>he's</c>, <c>ain't</c>, etc. The
        /// punctuation pass must keep mid-word apostrophes so those words are
        /// not split or turned into blank-line breaks.
        /// </para>
        /// <para>
        /// After catalog rules, unknown dotted letter-acronyms
        /// (<c>E.S.U.</c>, <c>F.B.I.</c>) are rewritten to spaced letters so
        /// sentence-pause encoding does not treat each internal period as a
        /// break (which produced one-letter units that
        /// <see cref="IsUnusableOcrText"/> dropped — e.g. only &quot;E.&quot; spoken).
        /// Known catalog expansions (<c>e.g.</c>, <c>u.s.a.</c>, <c>mr.</c>) already
        /// consumed their periods above. Users can still override pronunciation
        /// via Settings → Speech word rules (e.g. find <c>e s u</c> → preferred say-as).
        /// </para>
        /// </summary>
        internal static string ExpandSpeechAbbreviations(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            string s = input;

            // Normalize curly / typographic apostrophes to ASCII for matching
            // and so mid-word apostrophes survive the punctuation pass.
            // (Not a user rule — fixed pre-step before Abbrev-stage regex.)
            s = s.Replace('\u2019', '\'')
                 .Replace('\u2018', '\'')
                 .Replace('\u2032', '\'')
                 .Replace('\u02BC', '\'');

            // Possessives + abbreviations + titles: Settings → Speech → Text rules.
            s = SpeechTextRulesEngine.Apply(
                s, AppSettings.Current.SpeechTextRules, SpeechTextRuleStage.Abbrev);

            // Unknown dotted acronyms (not covered by catalog): keep as one unit.
            s = ProtectDottedLetterAcronyms(s);
            return s;
        }


        /// <summary>
        /// Final pre-TTS punctuation chain. Order is intentional and must not be
        /// collapsed into one regex:
        /// <list type="number">
        /// <item><b>Strip</b> non-pause punctuation (quotes, colons, dashes,
        /// semicolons, … → space). Keep <c>.</c> <c>!</c> <c>?</c> <c>,</c>,
        /// typed pause marks, and mid-word apostrophes.</item>
        /// <item><b>Collapse</b> adjacent pause marks to the first one only
        /// (<c>!!!</c> → <c>!</c>, <c>!?</c> → <c>!</c>, <c>,,</c> → <c>,</c>).</item>
        /// <item><b>Pause encode</b> <c>,</c> → comma pause mark;
        /// keep <c>.</c> <c>!</c> <c>?</c> for TTS intonation and insert a
        /// sentence pause mark immediately after each (do not replace).
        /// Durations come from <see cref="AppSettings"/> Voice pause settings.</item>
        /// </list>
        /// Abbreviation periods must already have been consumed by
        /// <see cref="ExpandSpeechAbbreviations"/> so they are not treated as
        /// sentence ends.
        /// </summary>
        internal static string NormalizeSpeechPunctuation(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            string s = input;

            // ---- 1) Strip non-pause punctuation ----
            // Keep letters, digits, whitespace, . ! ? , ' and typed pause marks.
            s = Regex.Replace(s,
                $@"[^\p{{L}}\p{{N}}\s.!?',{PauseMarkComma}{PauseMarkSentence}{PauseMarkOther}{PauseMarkBubble}]+",
                " ");
            // Drop orphan apostrophes that are not mid-word (not letter'letter).
            s = Regex.Replace(s, @"(?<!\p{L})'|'(?!\p{L})", " ");

            // ---- 2) Collapse multi pause-marks to the first ----
            s = Regex.Replace(s, @"([.!?,])(?:\s*[.!?,])+", "$1");

            // ---- 3) Typed pause marks (durations applied at speak time) ----
            // Off: leave , . ! ? for the TTS engine (Voice tab "Custom pause encoding").
            if (UseCustomPauseEncodings)
            {
                // Comma is only a clause break — replace with the comma pause mark.
                s = s.Replace(",", PauseMarkComma.ToString(), StringComparison.Ordinal);
                // End-of-sentence luc stays in the unit so TTS can use it for prosody
                // (question rise, etc.). Insert the sentence pause mark AFTER the luc
                // so SplitSpeakPieces still splits and delays correctly.
                s = Regex.Replace(s, @"([.!?])", $"$1{PauseMarkSentence}");
            }

            s = Regex.Replace(s, @"[^\S\n]{2,}", " ");
            return s;
        }

        /// <summary>Map a pause-mark character to delay in ms (0 if not a mark).</summary>

        internal static bool IsPauseMark(char c) =>
            c is PauseMarkComma or PauseMarkSentence or PauseMarkOther or PauseMarkBubble;


        /// <summary>
        /// One speak unit plus the pause to wait <b>after</b> it (before the next).
        /// Last unit has <see cref="PauseAfterMs"/> = 0.
        /// </summary>
        public readonly struct SpeakPiece
        {
            public string Text { get; }
            public int PauseAfterMs { get; }

            public SpeakPiece(string text, int pauseAfterMs)
            {
                Text = text;
                PauseAfterMs = pauseAfterMs;
            }

            public SpeakPiece WithPause(int pauseAfterMs) => new(Text, pauseAfterMs);
        }


        /// <summary>
        /// Pre-TTS / OCR cleanup: any run of 2+ adjacent punctuation
        /// (any combination) is reduced to the first character only.
        /// <list type="bullet">
        /// <item><c>!!!</c> → <c>!</c></item>
        /// <item><c>!?</c> → <c>!</c></item>
        /// <item><c>?!</c> → <c>?</c></item>
        /// <item><c>."</c> → <c>.</c></item>
        /// </list>
        /// Ellipsis (<c>...</c> / <c>…</c>) is handled earlier in
        /// <see cref="CleanForSpeech"/> (→ blank line), not here.
        /// Dashes (hyphen / en / em) are left alone so comic <c>--</c> still
        /// becomes a clause pause in <see cref="StripComicSpeechDecorators"/>
        /// and so <see cref="JoinLineBreakHyphens"/> does not glue balloons
        /// across a leftover single trailing hyphen (<c>pay--!</c> + blank +
        /// <c>krauu</c> must not become <c>paykrauu</c>).
        /// </summary>
        internal static string CollapseAdjacentPunctuation(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // \p{P} minus \p{Pd} (dash punctuation). Keep the first of each run of 2+.
            return Regex.Replace(input, @"([^\P{P}\p{Pd}])[^\P{P}\p{Pd}]+", "$1");
        }


        /// <summary>
        /// Join words split across lines with a hyphen (comic/print syllable breaks)
        /// so TTS says "sophisticated" not "sophisti dash cated" / "sophisti cated".
        /// <list type="bullet">
        /// <item><c>word-\nword</c> / <c>word- word</c> (hyphen + whitespace) → join</item>
        /// <item>soft hyphen U+00AD → remove</item>
        /// <item>no-space breaks (<c>SOPHISTI-CATED</c>, <c>responsibil-ity</c>) → join</item>
        /// <item>intentional compounds (<c>x-men</c>, <c>well-known</c>, <c>re-enter</c>) → keep</item>
        /// </list>
        /// Runs before decorator strip and before hyphens become spaces in
        /// <see cref="NormalizeSpeechPunctuation"/>. Case-insensitive so ALL-CAPS
        /// balloon OCR still rejoins.
        /// </summary>
        internal static string JoinLineBreakHyphens(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            string s = input;

            // Soft hyphen is never spoken
            s = s.Replace("\u00AD", "", StringComparison.Ordinal);

            // Only ASCII hyphen-minus is treated as a line-wrap mark here.
            // En/em dashes are clause punctuation (wait—e.g. / yes—ok) and must
            // not become syllable joins (that produced "waitfor" from wait—for).

            // Classic line-break hyphen: letters- + space/single-newline + letters
            // "responsibil-\nity" / "responsibil- ity" / "SOPHISTI- CATED" → joined
            // Requires 2+ letters on the left so we don't glue "i- am".
            // Do NOT join across blank lines (\n\n) — those are balloon boundaries
            // for bubble pauses ("pay-\n\nkrauu" must stay two units).
            for (int pass = 0; pass < 3; pass++)
            {
                string next = Regex.Replace(
                    s,
                    @"(\p{L}{2,})-(?:[^\S\n]+|\n(?!\n))(\p{L}+)",
                    "$1$2",
                    RegexOptions.CultureInvariant);
                if (next == s)
                    break;
                s = next;
            }

            // No-space syllable / mid-word breaks from OCR that dropped the line
            // break: "SOPHISTI-CATED", "end-ing", "responsibil-ity".
            // IgnoreCase: Force lowercase is off by default; comics are often ALL CAPS.
            // Keep intentional compounds via IsIntentionalHyphenCompound.
            s = Regex.Replace(
                s,
                @"(\p{L}{2,})-(\p{L}{2,})\b",
                static m =>
                {
                    string left = m.Groups[1].Value;
                    string right = m.Groups[2].Value;
                    if (IsIntentionalHyphenCompound(left, right))
                        return m.Value;
                    if (ShouldJoinSyllableHyphen(left, right))
                        return left + right;
                    return m.Value;
                },
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            return s;
        }

        /// <summary>
        /// True for intentional compounds that must keep the hyphen long enough
        /// for later strip-to-space (TTS still hears "x men" / "well known"),
        /// not mid-word line wraps.
        /// </summary>

        /// <summary>
        /// Merge speak units that are clearly mid-sentence fragments so bubble
        /// pauses land between real balloons, not between "i'm" and "okay…".
        /// Fail-closed: only merges obvious scraps / incomplete tails.
        /// </summary>
        internal static List<string> CoalesceFragmentSpeakUnits(
            List<string> units,
            StringBuilder detail)
        {
            if (units == null || units.Count < 2)
                return units ?? new List<string>();

            var result = new List<string>();
            int i = 0;
            while (i < units.Count)
            {
                string cur = units[i].Trim();
                if (cur.Length == 0)
                {
                    i++;
                    continue;
                }

                while (i + 1 < units.Count && ShouldMergeSpeakFragment(cur, units[i + 1]))
                {
                    string next = units[i + 1].Trim();
                    detail.AppendLine(
                        $"  speak-coalesce merge \"{Truncate(cur, 36)}\" + " +
                        $"\"{Truncate(next, 36)}\"");
                    cur = (cur + " " + next).Trim();
                    cur = Regex.Replace(cur, @"\s+", " ");
                    i++;
                }

                result.Add(cur);
                i++;
            }

            return result.Count > 0 ? result : units;
        }


        /// <summary>
        /// True when <paramref name="a"/> looks incomplete and should glue to
        /// <paramref name="b"/> before TTS.
        /// Fail-closed: sentence ends already carry pause marks (and keep .!?)
        /// from <see cref="CleanForSpeech"/>, so do not re-merge short but complete
        /// units ("tell you?", "want to.", "yes!") — only merge clear scraps.
        /// </summary>
        internal static bool ShouldMergeSpeakFragment(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;

            a = a.Trim();
            b = b.Trim();
            int wa = ComicRegionGeometry.CountWords(a);
            int wb = ComicRegionGeometry.CountWords(b);

            // Next unit opens a new beat / sentence — keep the blank-line pause
            // even when the first unit is short ("tell you?" + "as if you…").
            // Note: bare ^i\b does NOT match "it's" (next char is t, no boundary).
            // Include contractions explicitly so "want to." + "it's because…" stays split.
            if (wa >= 2 && wb >= 2 &&
                Regex.IsMatch(b,
                    @"^(but|and|yet|still|well|so|then|now|yes|no|wait|or|though|" +
                    @"however|because|when|if|while|after|before|once|maybe|" +
                    @"perhaps|please|look|listen|hey|oh|ah|ugh|damn|what|who|" +
                    @"where|why|how|which|as|i|we|you|he|she|they|it|tell|stop|" +
                    @"don't|do not|let us|" +
                    @"i'm|i've|i'll|i'd|it's|it'll|" +
                    @"that's|there's|here's|he's|she's|who's|what's|where's|how's|" +
                    @"we're|we've|we'll|we'd|they're|they've|they'll|they'd|" +
                    @"you're|you've|you'll|you'd|let's)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return false;

            // Finished dialogue often ends on infinitive "…want to." / "…have to."
            // (period kept on the unit). Do not treat that trailing "to" as a
            // mid-clause cut (was merging into the next balloon and killing pause).
            if (Regex.IsMatch(a,
                    @"\b(want|have|got|gotta|need|try|going|gonna|used|ought|" +
                    @"supposed|like|mean|love|hate|hope|wish|prefer|refuse|seem|" +
                    @"happen|tend|able|meant|tried|trying)\s+to\s*[.!?]?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return false;

            // Ends with article / preposition / conjunction / bare auxiliary —
            // OCR cut mid-clause; glue forward. Terminal .!? means the unit was
            // a real sentence end — never treat those as incomplete scraps.
            // Do NOT treat trailing pronouns (you/we/i/…) as incomplete when the
            // unit already has 2+ words — "tell you?" is a finished sentence that
            // is its own unit; re-merging it killed the pause.
            // For longer units (wa >= 5), trailing "to" alone is too weak — dialogue
            // balloons often end that way after punctuation already split them.
            if (Regex.IsMatch(a, @"[.!?]\s*$"))
                return false;

            if (Regex.IsMatch(a,
                    @"\b(the|a|an|and|or|but|of|for|with|your|my|our|their|" +
                    @"this|that|those|these|if|when|while|" +
                    @"as|at|in|on|from|into|is|are|was|were|be|been|am|have|has|" +
                    @"had|will|would|could|should|may|might|must|do|does|did|not)\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;

            // Short / medium scraps ending in bare "to" (not covered by complete
            // infinitive endings above) — still glue: "going to" handled above;
            // "said to" / lone "to" mid-cut still merge when short.
            if (wa <= 4 &&
                Regex.IsMatch(a, @"\bto\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;

            // Lone function-word / bare-pronoun scrap only
            if (wa == 1 &&
                Regex.IsMatch(a,
                    @"^(the|a|an|and|or|to|of|for|with|my|our|their|is|are|was|" +
                    @"were|be|am|i|we|you|he|she|they|it|not)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;

            // b is clearly a continuation after a short cut ("name is cyclops" after "the")
            if (wa <= 2 &&
                Regex.IsMatch(b, @"^(name|one|way|time|place|room|leg|news)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;

            // Do not glue a solid caption to a weaker overlapping scrap
            if (wa >= 5 && wb >= 4 &&
                TokenOverlapRatio(a, b) >= 0.28 &&
                CountAlnum(b) + 8 < CountAlnum(a))
                return false;

            // Default: keep the blank-line pause (including short complete units).
            return false;
        }


        /// <summary>
        /// Remove comic/UI decoration that OCR often keeps but TTS should not speak:
        /// balloon continuation dashes (<c>--</c>), arrows (misread from dashes),
        /// bullets, and similar non-dialogue symbols. Single hyphens in words
        /// (e.g. x-force) are kept. Between clauses, markers become a period so
        /// reading still pauses naturally.
        /// </summary>
        internal static string StripComicSpeechDecorators(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            // Settings → Speech → Text rules (Decorators stage).
            return SpeechTextRulesEngine.Apply(
                input, AppSettings.Current.SpeechTextRules, SpeechTextRuleStage.Decorators);
        }


        /// <summary>
        /// Remove our OCR task prompts (and close paraphrases) when the model
        /// dumps them before/after real transcript text. Case-insensitive;
        /// <paramref name="input"/> is already lowercased by CleanForSpeech.
        /// </summary>
        internal static string StripPromptContamination(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            string s = input;

            // Exact known prompts (longest first) — active + hard-coded defaults
            foreach (string prompt in AppSettings.Current.AllKnownPrompts()
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.Ordinal)
                         .OrderByDescending(p => p.Length))
            {
                string p = Regex.Replace(prompt.Trim().ToLowerInvariant(), @"\s+", " ").Trim();
                if (p.Length < 2)
                    continue;

                // Remove every occurrence of the prompt text
                int idx;
                while ((idx = s.IndexOf(p, StringComparison.Ordinal)) >= 0)
                {
                    s = s.Remove(idx, p.Length);
                }
            }

            // Soft tails / freestanding instruction paraphrases (comic + simple)
            s = Regex.Replace(s,
                @"(?:^|[\s.])(?:please\s+)?extract\s+(?:all\s+)?(?:the\s+)?" +
                @"(?:readable\s+)?text\s+from\s+(?:the\s+|this\s+|provided\s+)*" +
                @"(?:image|pdf|comic\s+panel|this\s+image\s+crop)" +
                @"(?:[^.]|\.(?!\s*$))*\.?",
                " ",
                RegexOptions.CultureInvariant);

            s = Regex.Replace(s,
                @"(?:^|[\s.])include\s+every\s+(?:readable\s+)?word\.?",
                " ",
                RegexOptions.CultureInvariant);

            s = Regex.Replace(s,
                @"(?:^|[\s.])output\s+only\s+the\s+text(?:\s*,?\s*nothing\s+else)?\.?",
                " ",
                RegexOptions.CultureInvariant);

            s = Regex.Replace(s,
                @"(?:^|[\s.])read\s+in\s+western\s+comic\s+order(?:[^.]|\.(?!\s*$))*\.?",
                " ",
                RegexOptions.CultureInvariant);

            // Bare short task tags left alone as a whole reply (not mid-sentence "ocr")
            s = Regex.Replace(s,
                @"^\s*(?:ocr|spotting|table\s*recognition|formula\s*recognition)\s*:?\s*$",
                " ",
                RegexOptions.CultureInvariant);

            return s;
        }


        /// <summary>
        /// Strip markdown / assistant chrome that VL models inject around comic OCR.
        /// Runs before Latin allow-list and Text-rule Noise so fence language tags
        /// (json, text) and "Here is the extracted text:" never reach TTS.
        /// Keeps inner dialogue; drops structure only.
        /// </summary>
        public static string StripMarkdownLlmJunk(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            string s = input;

            // Closed fenced blocks (``` or ~~~), any language tag — keep inner body.
            s = Regex.Replace(
                s,
                @"```[\w+-]*\s*\r?\n?([\s\S]*?)\r?\n?```",
                " $1 ",
                RegexOptions.CultureInvariant);
            s = Regex.Replace(
                s,
                @"~~~[\w+-]*\s*\r?\n?([\s\S]*?)\r?\n?~~~",
                " $1 ",
                RegexOptions.CultureInvariant);

            // Unclosed fence from opener to end of string (common VL truncations).
            s = Regex.Replace(
                s,
                @"```[\w+-]*\s*\r?\n?([\s\S]*)$",
                " $1 ",
                RegexOptions.CultureInvariant);
            s = Regex.Replace(
                s,
                @"~~~[\w+-]*\s*\r?\n?([\s\S]*)$",
                " $1 ",
                RegexOptions.CultureInvariant);

            // Orphan fence ticks left after partial strip.
            s = Regex.Replace(s, @"```+|~~~+", " ", RegexOptions.CultureInvariant);

            // Common assistant preambles / postambles (whole line or leading clause).
            s = Regex.Replace(
                s,
                @"(?im)^\s*(?:sure[!.,]?\s+)?(?:here(?:'s| is)\s+(?:the\s+)?(?:extracted\s+|ocr\s+|recognized\s+|full\s+)?(?:text|transcript|dialogue|result|output)|" +
                @"(?:i(?:'ve| have)\s+)?(?:extracted|recognized|read|transcribed)\s+(?:the\s+)?(?:text|dialogue|content)|" +
                @"(?:ocr|transcription|output)\s*result|the\s+text\s+(?:in\s+the\s+image\s+)?(?:is|reads)|" +
                @"as\s+(?:an?\s+)?(?:ocr|vision)\s+(?:model|assistant))\s*[:.\-–—]?\s*",
                " ",
                RegexOptions.CultureInvariant);
            s = Regex.Replace(
                s,
                @"(?im)^\s*(?:let me know if you need anything else|hope that helps|happy to help)[!.]?\s*$",
                " ",
                RegexOptions.CultureInvariant);

            // HTML tags + common entities (before * strip loses structure).
            s = Regex.Replace(s, @"</?[a-zA-Z][^>]*>", " ", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"&nbsp;", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"&amp;", "&", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"&lt;", "<", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"&gt;", ">", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"&quot;", "\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"&#39;", "'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // Images / links — keep visible label only.
            s = Regex.Replace(s, @"!\[([^\]]*)\]\([^)]+\)", " $1 ", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", " $1 ", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"\[([^\]]+)\]\[[^\]]*\]", " $1 ", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"(?m)^\s*\[[^\]]+\]:\s+\S+.*$", " ", RegexOptions.CultureInvariant);
            s = Regex.Replace(
                s,
                @"<(https?://[^>]+|mailto:[^>]+)>",
                " ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // Headings, blockquotes, list markers, task boxes, setext / hr / tables.
            s = Regex.Replace(s, @"(?m)^\s{0,3}#{1,6}\s+", "", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"(?m)\s+#+\s*$", "", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"(?m)^\s{0,3}>\s?", "", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"(?m)^\s{0,3}[-*+]\s+", "", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"(?m)^\s{0,3}\d+[.)]\s+", "", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"(?m)^\s*\[[ xX]\]\s+", "", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"(?m)^[=\-]{2,}\s*$", " ", RegexOptions.CultureInvariant);
            s = Regex.Replace(
                s,
                @"(?m)^\s{0,3}([-*_])(?:\s*\1){2,}\s*$",
                " ",
                RegexOptions.CultureInvariant);
            s = Regex.Replace(
                s,
                @"(?m)^\s*\|?(?:\s*:?-+:?\s*\|)+\s*:?-+:?\s*\|?\s*$",
                " ",
                RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"\|", " ", RegexOptions.CultureInvariant);

            // Emphasis / code — keep inner text (non-greedy, multi-pass for nesting).
            for (int pass = 0; pass < 3; pass++)
            {
                string before = s;
                s = Regex.Replace(s, @"\*\*\*(.+?)\*\*\*", " $1 ", RegexOptions.CultureInvariant);
                s = Regex.Replace(s, @"___(.+?)___", " $1 ", RegexOptions.CultureInvariant);
                s = Regex.Replace(s, @"\*\*(.+?)\*\*", " $1 ", RegexOptions.CultureInvariant);
                s = Regex.Replace(s, @"__(.+?)__", " $1 ", RegexOptions.CultureInvariant);
                s = Regex.Replace(s, @"(?<!\w)\*(?!\s)(.+?)(?<!\s)\*(?!\w)", " $1 ", RegexOptions.CultureInvariant);
                s = Regex.Replace(s, @"(?<!\w)_(?!\s)(.+?)(?<!\s)_(?!\w)", " $1 ", RegexOptions.CultureInvariant);
                s = Regex.Replace(s, @"~~(.+?)~~", " $1 ", RegexOptions.CultureInvariant);
                s = Regex.Replace(s, @"==(.+?)==", " $1 ", RegexOptions.CultureInvariant);
                s = Regex.Replace(s, @"`([^`\n]+)`", " $1 ", RegexOptions.CultureInvariant);
                if (s == before)
                    break;
            }

            // Footnotes / orphan emphasis ticks / backslash escapes.
            s = Regex.Replace(s, @"\[\^[^\]]+\]", " ", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!|>])", "$1", RegexOptions.CultureInvariant);
            s = Regex.Replace(
                s,
                @"(?<!\w)(\*{1,3}|_{1,3}|`{1,3})(?!\w)",
                " ",
                RegexOptions.CultureInvariant);

            // Bare meta tags the model leaves after fence strip (whole token only).
            s = Regex.Replace(
                s,
                @"(?i)(?<!\p{L})(?:json|yaml|xml|html|markdown|plaintext|output|ocr_result|transcript)\s*(?=\r?\n|$)",
                " ",
                RegexOptions.CultureInvariant);

            s = Regex.Replace(s, @"[ \t]{2,}", " ");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>
        /// Known freestyle OCR field names models put in JSON / pseudo-JSON wrappers.
        /// </summary>
        private static readonly string ModelJsonTextKeyAlternation =
            "text|content|ocr|result|output|transcription|message|caption|value";

        /// <summary>
        /// Extract prose from model JSON wrappers. Handles:
        /// <list type="bullet">
        /// <item>Whole reply is <c>{"text":"…"}</c> (also content/ocr/result/…)</item>
        /// <item>Plain text + trailing object (log: Adrienne. then {"text":"Something?\\n…"})</item>
        /// <item>Loose field without braces (log: Hattie panel <c>"text": "HER NAME…"</c>) —
        /// punctuation strip would otherwise leave a spoken word <c>text</c>.</item>
        /// </list>
        /// Non-matching braces are left alone (dialogue with literal '{').
        /// Double quotes are not kept for TTS — they only matter while unwrapping
        /// string values; <see cref="NormalizeSpeechPunctuation"/> drops the rest.
        /// </summary>
        internal static string UnwrapModelJsonPayload(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input ?? "";

            string s = input.Trim();

            // ```json … ``` fences around an object (also handled by StripMarkdownLlmJunk).
            var fence = Regex.Match(
                s,
                @"^```(?:json|JSON)?\s*\r?\n?([\s\S]*?)\r?\n?```\s*$",
                RegexOptions.CultureInvariant);
            if (fence.Success)
                s = fence.Groups[1].Value.Trim();

            // Entire payload is one JSON object
            if (s.Length >= 2 && s[0] == '{' && s[^1] == '}' &&
                TryExtractJsonTextField(s, out string whole) &&
                !string.IsNullOrWhiteSpace(whole))
                return whole.Trim();

            // Mixed: prose + one or more {...} objects with a text-ish string field
            if (s.IndexOf('{') >= 0)
            {
                var sb = new StringBuilder(s.Length);
                int i = 0;
                bool anyUnwrapped = false;
                while (i < s.Length)
                {
                    int brace = s.IndexOf('{', i);
                    if (brace < 0)
                    {
                        sb.Append(s, i, s.Length - i);
                        break;
                    }

                    if (brace > i)
                        sb.Append(s, i, brace - i);

                    int end = FindMatchingJsonBrace(s, brace);
                    if (end < 0)
                    {
                        sb.Append(s, brace, s.Length - brace);
                        break;
                    }

                    string candidate = s.Substring(brace, end - brace + 1);
                    if (TryExtractJsonTextField(candidate, out string extracted) &&
                        !string.IsNullOrWhiteSpace(extracted))
                    {
                        anyUnwrapped = true;
                        // Keep a balloon-style blank line between plain prefix and extract
                        if (sb.Length > 0)
                        {
                            while (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
                                sb.Length--;
                            sb.Append("\n\n");
                        }
                        sb.Append(extracted.Trim());
                    }
                    else
                    {
                        sb.Append(candidate);
                    }

                    i = end + 1;
                }

                if (anyUnwrapped)
                    s = sb.ToString().Trim();
            }

            // Loose "text": "…" without outer braces (common VL freestyle).
            // Must run even when no '{' was present — that is the Hattie failure mode.
            s = UnwrapLooseJsonTextAssignments(s);
            return s.Trim();
        }

        /// <summary>
        /// Peel freestyle <c>"text": "dialogue…"</c> / <c>text: "…"</c> assignments
        /// that are not valid JSON objects. Replaces the key+quotes with the string
        /// value only so TTS never speaks the field name.
        /// </summary>
        internal static string UnwrapLooseJsonTextAssignments(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input ?? "";

            string s = input;

            // "text": "VALUE"  (key always quoted in the Hattie log form)
            s = Regex.Replace(
                s,
                $@"(?is)""(?:{ModelJsonTextKeyAlternation})""\s*:\s*""((?:\\.|[^""\\])*)""",
                m =>
                {
                    string val = UnescapeJsonStringContent(m.Groups[1].Value).Trim();
                    return string.IsNullOrEmpty(val) ? " " : "\n\n" + val + "\n\n";
                },
                RegexOptions.CultureInvariant);

            // text: "VALUE"  (unquoted key — avoid matching dialogue like "type: TOO")
            // Require start / whitespace / brace / comma before the key name.
            s = Regex.Replace(
                s,
                $@"(?is)(?<=^|[\s{{,])(?:{ModelJsonTextKeyAlternation})\s*:\s*""((?:\\.|[^""\\])*)""",
                m =>
                {
                    string val = UnescapeJsonStringContent(m.Groups[1].Value).Trim();
                    return string.IsNullOrEmpty(val) ? " " : "\n\n" + val + "\n\n";
                },
                RegexOptions.CultureInvariant);

            // Residual bare label with no/broken value (e.g. "text": alone on a line)
            s = Regex.Replace(
                s,
                $@"(?im)^[ \t]*[{{,]?[ \t]*""?(?:{ModelJsonTextKeyAlternation})""?[ \t]*:[ \t]*",
                "",
                RegexOptions.CultureInvariant);

            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>
        /// Decode a JSON string <b>body</b> (content between quotes, escapes intact).
        /// </summary>
        internal static string UnescapeJsonStringContent(string content)
        {
            if (string.IsNullOrEmpty(content))
                return "";

            try
            {
                // Parse as a full JSON string literal.
                string? decoded = JsonSerializer.Deserialize<string>("\"" + content + "\"");
                if (decoded != null)
                    return decoded;
            }
            catch (JsonException)
            {
                // fall through
            }
            catch (ArgumentException)
            {
                // fall through
            }

            return content
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\r", "\r", StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }


        /// <summary>
        /// Matching <c>}</c> for <paramref name="openIdx"/>, respecting JSON strings
        /// so braces inside values do not confuse the scan.
        /// </summary>
        internal static int FindMatchingJsonBrace(string s, int openIdx)
        {
            if (openIdx < 0 || openIdx >= s.Length || s[openIdx] != '{')
                return -1;

            int depth = 0;
            bool inString = false;
            bool escape = false;
            for (int i = openIdx; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                        continue;
                    }
                    if (c == '\\')
                    {
                        escape = true;
                        continue;
                    }
                    if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }
                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }


        /// <summary>
        /// If <paramref name="json"/> is an object with a known OCR string field
        /// (or a single string property), return that value (escapes already decoded).
        /// </summary>
        internal static bool TryExtractJsonTextField(string json, out string text)
        {
            text = "";
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                // Preferred wire keys models use when they freestyle a payload
                ReadOnlySpan<string> keys =
                [
                    "text", "content", "ocr", "result", "output",
                    "transcription", "message", "caption", "value"
                ];
                foreach (string key in keys)
                {
                    if (!root.TryGetProperty(key, out var prop))
                        continue;
                    if (prop.ValueKind != JsonValueKind.String)
                        continue;
                    string? v = prop.GetString();
                    if (string.IsNullOrWhiteSpace(v))
                        continue;
                    text = v;
                    return true;
                }

                // Single-property object with a non-empty string value
                string? only = null;
                int count = 0;
                foreach (var p in root.EnumerateObject())
                {
                    count++;
                    if (count > 1)
                        break;
                    if (p.Value.ValueKind == JsonValueKind.String)
                        only = p.Value.GetString();
                }
                if (count == 1 && !string.IsNullOrWhiteSpace(only))
                {
                    text = only!;
                    return true;
                }

                return false;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }


        /// <summary>
        /// Split cleaned OCR into speak units on typed pause marks (and legacy
        /// blank lines). Pause durations are applied by <see cref="SplitSpeakPieces"/>.
        /// </summary>
        internal static List<string> SplitSpeakBlocks(string text) =>
            SplitSpeakPieces(text).Select(p => p.Text).ToList();


        /// <summary>
        /// Split cleaned OCR into speak units with per-boundary pause durations
        /// from Voice settings (comma / sentence / other / bubble ms).
        /// </summary>
        internal static List<SpeakPiece> SplitSpeakPieces(string text)
        {
            var result = new List<SpeakPiece>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            string s = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                           .Replace('\r', '\n');

            // Legacy blank lines (if any survived) → bubble pause mark (when encodings on)
            if (UseCustomPauseEncodings)
                s = Regex.Replace(s, @"\n\s*\n+", PauseMarkBubble.ToString());
            else
                s = Regex.Replace(s, @"\n\s*\n+", " ");
            s = s.Replace('\n', ' ');

            var sb = new StringBuilder();
            void Flush(int pauseAfterMs)
            {
                string unit = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
                sb.Clear();
                if (unit.Length == 0)
                    return;
                result.Add(new SpeakPiece(unit, pauseAfterMs));
            }

            foreach (char c in s)
            {
                if (IsPauseMark(c))
                {
                    Flush(PauseMsForMark(c));
                    continue;
                }
                sb.Append(c);
            }
            Flush(0);

            if (result.Count == 0 && !string.IsNullOrWhiteSpace(s))
            {
                string one = Regex.Replace(s, @"\s+", " ").Trim();
                one = new string(one.Where(ch => !IsPauseMark(ch)).ToArray()).Trim();
                if (one.Length > 0)
                    result.Add(new SpeakPiece(one, 0));
            }

            // Never delay after the final unit (trailing punct still splits correctly).
            if (result.Count > 0)
                result[^1] = result[^1].WithPause(0);

            return result;
        }


        /// <summary>
        /// Coalesce fragment units while preserving pause-after on kept boundaries.
        /// </summary>
        internal static List<SpeakPiece> CoalesceFragmentSpeakPieces(
            List<SpeakPiece> pieces,
            StringBuilder detail)
        {
            if (pieces == null || pieces.Count < 2)
                return pieces ?? new List<SpeakPiece>();

            var result = new List<SpeakPiece>();
            int i = 0;
            while (i < pieces.Count)
            {
                string cur = pieces[i].Text.Trim();
                int pauseAfter = pieces[i].PauseAfterMs;
                if (cur.Length == 0)
                {
                    i++;
                    continue;
                }

                while (i + 1 < pieces.Count && ShouldMergeSpeakFragment(cur, pieces[i + 1].Text))
                {
                    string next = pieces[i + 1].Text.Trim();
                    detail.AppendLine(
                        $"  speak-coalesce merge \"{Truncate(cur, 36)}\" + " +
                        $"\"{Truncate(next, 36)}\"");
                    cur = (cur + " " + next).Trim();
                    cur = Regex.Replace(cur, @"\s+", " ");
                    // Boundary between merged scraps is dropped; keep pause after the
                    // right-hand piece (break that followed the pair).
                    pauseAfter = pieces[i + 1].PauseAfterMs;
                    i++;
                }

                result.Add(new SpeakPiece(cur, pauseAfter));
                i++;
            }

            // Last piece never pauses after
            if (result.Count > 0)
                result[^1] = result[^1].WithPause(0);

            return result.Count > 0 ? result : pieces;
        }

        internal static HashSet<string> ToTokenSet(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string w in TokenizeWords(text))
            {
                string n = NormalizeToken(w);
                if (n.Length > 0)
                    set.Add(n);
            }
            return set;
        }


        /// <summary>
        /// Fraction of tokens in <paramref name="a"/> that also appear in
        /// <paramref name="b"/> (|AnB| / |A|). Used for -do crops cover this full unit?-
        /// </summary>
        internal static double TokenCoverageOfAByB(string a, string b)
        {
            var ta = ToTokenSet(a);
            var tb = ToTokenSet(b);
            if (ta.Count == 0)
                return 0;
            if (tb.Count == 0)
                return 0;
            int shared = 0;
            foreach (string t in ta)
            {
                if (tb.Contains(t))
                    shared++;
            }
            return shared / (double)ta.Count;
        }


        /// <summary>
        /// True when crop wording is clearly preferable to a matching full unit
        /// (same balloon, better OCR - e.g. "sent me" vs "sent the").
        /// </summary>
        internal static bool IsClearlyBetterOcr(string crop, string fullUnit)
        {
            if (SpeechCleaner.IsUnusableOcrText(crop) || string.IsNullOrWhiteSpace(fullUnit))
                return false;
            if (string.Equals(
                    crop.Trim(), fullUnit.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            int qc = OcrTextQualityScore(crop);
            int qf = OcrTextQualityScore(fullUnit);
            // Need a real margin so we do not thrash on tiny differences
            if (qc >= qf + 3)
                return true;

            // Same-ish score but crop has more real words / letters
            int cw = ComicRegionGeometry.CountWords(crop);
            int fw = ComicRegionGeometry.CountWords(fullUnit);
            int ca = SpeechCleaner.CountAlnum(crop);
            int fa = SpeechCleaner.CountAlnum(fullUnit);
            if (cw >= fw && ca >= fa + 4 && qc >= qf)
                return true;
            if (cw > fw && ca >= fa && qc + 1 >= qf)
                return true;

            return false;
        }


        /// <summary>|intersection| / max(|a|,|b|) on normalized tokens.</summary>
        internal static double TokenOverlapRatio(string a, string b)
        {
            var ta = ToTokenSet(a);
            var tb = ToTokenSet(b);
            if (ta.Count == 0 || tb.Count == 0)
                return 0;
            int shared = 0;
            foreach (string t in ta)
            {
                if (tb.Contains(t))
                    shared++;
            }
            int denom = Math.Max(ta.Count, tb.Count);
            return shared / (double)denom;
        }


        /// <summary>
        /// Rewrite dotted single-letter acronyms to spaced letters before pause
        /// encoding. Requires ≥2 letters (e.g. <c>e.s.u.</c> → <c>e s u</c>,
        /// <c>f.b.i</c> → <c>f b i</c>). Does not touch real sentence ends
        /// (<c>hello.</c>), decimals, or multi-letter stubs already expanded
        /// by Abbrev rules.
        /// </summary>
        internal static string ProtectDottedLetterAcronyms(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Word-start chain of single letters with periods; optional trailing
            // period (orthographic, not a sentence end when mid-phrase).
            // Examples: e.s.u. | f.b.i | u.n. | a.m.
            return Regex.Replace(
                input,
                @"\b(?:\p{L}\.){1,}\p{L}\.?(?!\p{L})",
                static m =>
                {
                    var letters = new List<char>(4);
                    foreach (char c in m.Value)
                    {
                        if (char.IsLetter(c))
                            letters.Add(c);
                    }

                    // Pattern guarantees ≥2 letters; defensive fallback keeps match.
                    if (letters.Count < 2)
                        return m.Value;
                    return string.Join(" ", letters);
                },
                RegexOptions.CultureInvariant);
        }


        internal static bool IsIntentionalHyphenCompound(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                return true;

            string l = left.ToLowerInvariant();
            string r = right.ToLowerInvariant();

            // Short productive prefixes: re-enter, co-op, mid-air, non-zero, …
            if (l is "re" or "pre" or "co" or "non" or "mid" or "sub" or "anti"
                or "pro" or "bi" or "tri" or "uni" or "ex" or "un" or "de"
                or "over" or "under" or "out" or "up" or "off" or "all"
                or "self" or "cross" or "half" or "multi" or "semi" or "quasi"
                or "inter" or "intra" or "extra" or "super" or "ultra" or "neo")
                return true;

            // Common compound second halves / titles (x-men, spider-man, well-known).
            if (r is "men" or "man" or "woman" or "women" or "boy" or "girl"
                or "known" or "based" or "free" or "born" or "made" or "sized"
                or "year" or "old" or "time" or "term" or "range" or "class"
                or "level" or "type" or "style" or "like" or "wise" or "aged"
                or "shaped" or "related" or "oriented" or "speaking" or "looking"
                or "shirt" or "mail" or "ray" or "rayed" or "fi" or "up" or "out"
                or "in" or "on" or "off" or "to" or "of" or "by" or "at" or "for"
                or "and" or "or" or "as" or "is" or "it" or "be" or "we" or "he"
                or "she" or "they" or "you" or "me" or "my" or "our" or "the"
                or "a" or "an" or "so" or "if" or "no" or "yes" or "ok" or "oh")
                return true;

            return false;
        }


        /// <summary>
        /// True when <paramref name="left"/>-<paramref name="right"/> looks like a
        /// print/comic syllable break (rejoin) rather than a real compound.
        /// </summary>
        internal static bool ShouldJoinSyllableHyphen(string left, string right)
        {
            if (left.Length < 2 || right.Length < 2)
                return false;

            string r = right.ToLowerInvariant();

            // Right side is (or ends with) a common English suffix — includes
            // "cated" via …ated, "ized", "tion", etc.
            if (Regex.IsMatch(
                    r,
                    @"^(ing|ed|er|est|ly|tion|sion|ness|ment|able|ible|ous|ive|" +
                    @"ity|ies|ied|ian|ial|en|al|ful|less|selves|self|" +
                    @"ated|ized|ised|ating|ening|ance|ence|ency|ancy|" +
                    @"ship|hood|ward|wards|wise|most)$",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
                return true;

            if (Regex.IsMatch(
                    r,
                    @"(ing|ed|er|est|ly|tion|sion|ness|ment|able|ible|ous|ive|" +
                    @"ity|ies|ied|ian|ial|ated|ized|ised|ating|ance|ence)$",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) &&
                right.Length <= 10)
                return true;

            // Longer left stem + short right fragment: "sophisti"+"cated",
            // "respon"+"sibility" pieces OCR often glues without a line break.
            if (left.Length >= 4 && right.Length >= 2 && right.Length <= 8)
                return true;

            // Both sides medium: still a line wrap more often than a compound
            // once denylist in IsIntentionalHyphenCompound has filtered.
            if (left.Length >= 3 && right.Length >= 3 && right.Length <= 6)
                return true;

            return false;
        }


        internal static int PauseMsForMark(char mark) => mark switch
        {
            SpeechCleaner.PauseMarkComma => CommaPauseMs,
            SpeechCleaner.PauseMarkSentence => SentencePauseMs,
            SpeechCleaner.PauseMarkOther => OtherPauseMs,
            SpeechCleaner.PauseMarkBubble => BubblePauseMs,
            _ => 0
        };

        internal static List<string> TokenizeWords(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(s))
                return list;

            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && !char.IsLetterOrDigit(s[i]))
                    i++;
                int start = i;
                while (i < s.Length && char.IsLetterOrDigit(s[i]))
                    i++;
                if (i > start)
                    list.Add(s[start..i].ToLowerInvariant());
            }
            return list;
        }

        internal static string NormalizeToken(string w)
        {
            if (string.IsNullOrEmpty(w)) return "";
            // alnum only, lower - "eyes--" / "EYES" match
            var sb = new StringBuilder(w.Length);
            foreach (char c in w)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Heuristic OCR quality: favors longer letter-words with vowels;
        /// penalizes digits, vowelless scraps, and tiny junk tokens.
        /// </summary>
        internal static int OcrTextQualityScore(string text)
        {
            int score = 0;
            foreach (string raw in TokenizeWords(text))
            {
                string w = NormalizeToken(raw);
                if (w.Length == 0) continue;

                bool hasDigit = w.Any(char.IsDigit);
                bool hasLetter = w.Any(char.IsLetter);
                int vowels = 0;
                foreach (char c in w)
                {
                    char l = char.ToLowerInvariant(c);
                    if (l is 'a' or 'e' or 'i' or 'o' or 'u' or 'y')
                        vowels++;
                }

                if (hasDigit && !hasLetter)
                {
                    score -= 3;
                    continue;
                }

                if (w.Length == 1)
                {
                    score -= 1;
                    continue;
                }

                score += Math.Min(w.Length, 8);
                if (w.Length >= 3 && vowels == 0)
                    score -= 3;
                else if (w.Length >= 3 && vowels > 0)
                    score += 2;
                if (w.Length >= 5 && vowels > 0)
                    score += 1;
                if (hasDigit)
                    score -= 2;
            }

            score += SpeechCleaner.CountAlnum(text) / 8;
            return score;
        }

    }
}
