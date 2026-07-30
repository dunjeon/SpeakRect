using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SpeakRect
{
    /// <summary>
    /// How a user speech rule matches. Users pick this in plain language;
    /// matching against cleaned OCR is handled internally.
    /// </summary>
    public enum SpeechMatchKind
    {
        /// <summary>Whole-word only (e.g. men does not hit women).</summary>
        Word = 0,

        /// <summary>Anywhere / multi-word (e.g. X-Men, character names).</summary>
        Phrase = 1,
    }

    /// <summary>
    /// One user speech rule. Stored as the user typed it (e.g. Find <c>X-Men</c>,
    /// Say as <c>Ex-Men</c>). At speak time the engine matches the cleaned OCR
    /// form behind the scenes so users never need to know about lowercasing or
    /// hyphen stripping.
    /// </summary>
    public sealed class SpeechRule
    {
        public const int MaxRules = 64;
        public const int MaxMatchLength = 128;
        public const int MaxReplaceLength = 256;

        /// <summary>Find text — stored exactly as the user typed it.</summary>
        public string Match { get; set; } = "";

        /// <summary>
        /// What the voice should say instead. Empty = never speak that text.
        /// Inserted as typed (casing and hyphens kept for TTS).
        /// </summary>
        public string Replace { get; set; } = "";

        public SpeechMatchKind Kind { get; set; } = SpeechMatchKind.Word;

        public bool Enabled { get; set; } = true;

        public SpeechRule Clone() => new()
        {
            Match = Match ?? "",
            Replace = Replace ?? "",
            Kind = Kind,
            Enabled = Enabled,
        };

        /// <summary>Short label for list rows.</summary>
        public string DisplaySummary
        {
            get
            {
                string m = (Match ?? "").Trim();
                if (m.Length == 0)
                    return "(empty)";
                string r = (Replace ?? "").Trim();
                string arrow = r.Length == 0 ? "∅ strip" : r;
                string kind = Kind == SpeechMatchKind.Phrase ? "anywhere" : "word";
                string on = Enabled ? "" : "[off] ";
                return $"{on}{m} → {arrow}  ({kind})";
            }
        }

        public static bool TryParseKind(string? raw, out SpeechMatchKind kind)
        {
            kind = SpeechMatchKind.Word;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            string t = raw.Trim();
            if (t.Equals("Word", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("0", StringComparison.Ordinal))
            {
                kind = SpeechMatchKind.Word;
                return true;
            }
            if (t.Equals("Phrase", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("1", StringComparison.Ordinal))
            {
                kind = SpeechMatchKind.Phrase;
                return true;
            }
            return false;
        }

        public static string KindToIni(SpeechMatchKind kind) =>
            kind == SpeechMatchKind.Phrase ? "Phrase" : "Word";

        /// <summary>
        /// Normalize match/replace for storage (trim, length clamp, no newlines).
        /// Returns false if match is empty after normalize.
        /// </summary>
        public static bool TryNormalize(
            string? match,
            string? replace,
            SpeechMatchKind kind,
            bool enabled,
            out SpeechRule rule,
            out string? error)
        {
            rule = new SpeechRule();
            error = null;

            string m = CollapseWs(match);
            string r = CollapseWs(replace);

            if (m.Length == 0)
            {
                error = "Find text is empty.";
                return false;
            }
            if (m.Length > MaxMatchLength)
            {
                error = $"Find text is too long (max {MaxMatchLength} characters).";
                return false;
            }
            if (r.Length > MaxReplaceLength)
            {
                error = $"Say-as text is too long (max {MaxReplaceLength} characters).";
                return false;
            }

            rule.Match = m;
            rule.Replace = r;
            rule.Kind = kind;
            rule.Enabled = enabled;
            return true;
        }

        private static string CollapseWs(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            // Single-line for ini + TTS; keep internal spaces for phrases.
            string t = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return Regex.Replace(t, @"\s+", " ");
        }
    }

    /// <summary>
    /// Applies user speech rules to already-cleaned OCR text (speak pipeline end).
    /// Users type natural forms (X-Men → Ex-Men); this class maps Find to the
    /// cleaned stream and injects Say-as as typed for TTS.
    /// </summary>
    public static class SpeechRulesEngine
    {
        /// <summary>
        /// Run enabled rules in order. Empty replace removes the match.
        /// Collapses leftover multi-spaces (keeps non-space control pause marks).
        /// </summary>
        public static string Apply(string? input, IEnumerable<SpeechRule>? rules)
        {
            if (string.IsNullOrEmpty(input))
                return input ?? "";
            if (rules == null)
                return input;

            string s = input;
            foreach (SpeechRule rule in rules)
            {
                if (rule == null || !rule.Enabled)
                    continue;
                string match = (rule.Match ?? "").Trim();
                if (match.Length == 0)
                    continue;

                s = ApplyOne(s, match, rule.Replace ?? "", rule.Kind);
            }

            // Collapse ordinary spaces only (pause marks are control chars, not space).
            s = Regex.Replace(s, @" {2,}", " ");
            return s.Trim();
        }

        private static string ApplyOne(
            string input, string match, string replace, SpeechMatchKind kind)
        {
            // User may type "X-Men"; cleaned OCR is "x men". Same for any casing /
            // hyphen / punctuation they copy from a comic. Never force users to
            // learn the cleaned form.
            string needle = ToCleanedLookup(match);
            if (needle.Length == 0)
                return input;

            string escaped = Regex.Escape(needle);
            // After clean, hyphens become spaces ("w-what" → "w what", "X-Men" → "x men").
            // Multi-word needles MUST keep word boundaries on both ends so a Find of
            // "w-what" does not fire on the trailing "w what" inside "now what" /
            // "know what". Old code treated multi-word as unbounded Phrase and over-hit.
            //
            // Word  → always \b…\b (single token or multi-token sequence).
            // Phrase (Anywhere) → substring for a single token only (men⊂women);
            //                     multi-word still uses \b…\b (honest phrase unit).
            bool multiWord = needle.IndexOf(' ') >= 0;
            string pattern = (kind == SpeechMatchKind.Word || multiWord)
                ? $@"\b{escaped}\b"
                : escaped;

            // Say-as is left as the user typed it (Ex-Men, "sigh-clops", …) so TTS
            // gets their preferred pronunciation form — not re-run through cleaners.
            string insertion = replace ?? "";

            try
            {
                return Regex.Replace(
                    input,
                    pattern,
                    _ => insertion,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (RegexMatchTimeoutException)
            {
                return input;
            }
            catch (ArgumentException)
            {
                return input;
            }
        }

        /// <summary>
        /// Map a user-facing Find string to the form used in cleaned OCR after
        /// <c>CleanForSpeech</c> punctuation normalize: lowercase, mid-word
        /// apostrophes kept (contractions), other punctuation → space, spaces
        /// collapsed. Public for tests / UI.
        /// </summary>
        public static string ToCleanedLookup(string? match)
        {
            if (string.IsNullOrWhiteSpace(match))
                return "";
            string s = match.ToLowerInvariant();
            // Same curly → ASCII fold as ExpandSpeechAbbreviations so "don’t" finds "don't".
            s = s.Replace('\u2019', '\'')
                 .Replace('\u2018', '\'')
                 .Replace('\u2032', '\'')
                 .Replace('\u02BC', '\'');
            // Keep letters, digits, whitespace, apostrophe — mirrors
            // NormalizeSpeechPunctuation (hyphen/other marks become spaces).
            s = Regex.Replace(s, @"[^\p{L}\p{N}\s']+", " ");
            // Drop orphan apostrophes (not letter'letter).
            s = Regex.Replace(s, @"(?<!\p{L})'|'(?!\p{L})", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }
    }
}
