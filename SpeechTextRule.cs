using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SpeakRect
{
    /// <summary>
    /// Where a pipeline text rule runs inside <c>CleanForSpeech</c>.
    /// Order of stages is fixed; rules within a stage run top→bottom.
    /// </summary>
    public enum SpeechTextRuleStage
    {
        /// <summary>After JSON unwrap / punct collapse — strip model noise, attach-images, spotting, markdown.</summary>
        Noise = 0,

        /// <summary>
        /// After optional lowercasing — abbreviations, titles, possessives.
        /// Matching is always case-insensitive so Mr./mr. both expand when
        /// <c>SpeechForceLowercase</c> is off.
        /// </summary>
        Abbrev = 1,

        /// <summary>After hyphen-join — comic dashes, arrows, decorative bullets.</summary>
        Decorators = 2,
    }

    /// <summary>
    /// One editable pre-TTS text rule (regex find → replace). Built-ins ship with
    /// stable <see cref="Id"/>s so profiles can override them; users may add customs.
    /// </summary>
    public sealed class SpeechTextRule
    {
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(80);

        /// <summary>Stable id for built-ins (<c>abbrev-mr</c>) or <c>custom-…</c>.</summary>
        public string Id { get; set; } = "";

        /// <summary>Short UI label.</summary>
        public string Name { get; set; } = "";

        public SpeechTextRuleStage Stage { get; set; } = SpeechTextRuleStage.Abbrev;

        /// <summary>Regex pattern (applied to the cleaned stream at this stage).</summary>
        public string Pattern { get; set; } = "";

        /// <summary>Replacement (supports <c>$1</c> groups). Empty = strip match.</summary>
        public string Replace { get; set; } = "";

        public bool Enabled { get; set; } = true;

        /// <summary>Add <see cref="RegexOptions.IgnoreCase"/> (Noise/Decorators often need this).</summary>
        public bool IgnoreCase { get; set; }

        /// <summary>True when this came from the shipped catalog (resettable by id).</summary>
        public bool IsBuiltIn { get; set; }

        public SpeechTextRule Clone() => new()
        {
            Id = Id ?? "",
            Name = Name ?? "",
            Stage = Stage,
            Pattern = Pattern ?? "",
            Replace = Replace ?? "",
            Enabled = Enabled,
            IgnoreCase = IgnoreCase,
            IsBuiltIn = IsBuiltIn,
        };

        public string DisplaySummary
        {
            get
            {
                string on = Enabled ? "" : "[off] ";
                string stage = StageToIni(Stage);
                string n = string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();
                return $"{on}{n}  ({stage})";
            }
        }

        public static bool TryParseStage(string? raw, out SpeechTextRuleStage stage)
        {
            stage = SpeechTextRuleStage.Abbrev;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            string t = raw.Trim();
            if (t.Equals("Noise", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("0", StringComparison.Ordinal))
            {
                stage = SpeechTextRuleStage.Noise;
                return true;
            }
            if (t.Equals("Abbrev", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Abbreviation", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("1", StringComparison.Ordinal))
            {
                stage = SpeechTextRuleStage.Abbrev;
                return true;
            }
            if (t.Equals("Decorators", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Decorator", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("2", StringComparison.Ordinal))
            {
                stage = SpeechTextRuleStage.Decorators;
                return true;
            }
            return false;
        }

        public static string StageToIni(SpeechTextRuleStage stage) => stage switch
        {
            SpeechTextRuleStage.Noise => "Noise",
            SpeechTextRuleStage.Decorators => "Decorators",
            _ => "Abbrev",
        };

        public static string StageLabel(SpeechTextRuleStage stage) => stage switch
        {
            SpeechTextRuleStage.Noise => "Noise strip",
            SpeechTextRuleStage.Decorators => "Decorators",
            _ => "Abbreviations",
        };

        /// <summary>
        /// Normalize for storage. Validates regex compiles with a short timeout.
        /// </summary>
        public static bool TryNormalize(
            string? id,
            string? name,
            SpeechTextRuleStage stage,
            string? pattern,
            string? replace,
            bool enabled,
            bool ignoreCase,
            bool isBuiltIn,
            out SpeechTextRule rule,
            out string? error)
        {
            rule = new SpeechTextRule();
            error = null;

            string cleanId = SanitizeId(id);
            string cleanName = CollapseOneLine(name);
            string cleanPat = (pattern ?? "").Trim();
            // Replacement may intentionally include spaces; do not collapse internals.
            string cleanRepl = (replace ?? "").Replace('\r', ' ').Replace('\n', ' ');

            if (cleanId.Length == 0)
            {
                error = "Rule id is empty.";
                return false;
            }
            if (cleanName.Length == 0)
                cleanName = cleanId;
            if (cleanPat.Length == 0)
            {
                error = "Regex pattern is empty.";
                return false;
            }

            if (!TryCompile(cleanPat, ignoreCase, out _, out string? compileErr))
            {
                error = compileErr ?? "Invalid regex.";
                return false;
            }

            rule.Id = cleanId;
            rule.Name = cleanName;
            rule.Stage = stage;
            rule.Pattern = cleanPat;
            rule.Replace = cleanRepl;
            rule.Enabled = enabled;
            rule.IgnoreCase = ignoreCase;
            rule.IsBuiltIn = isBuiltIn;
            return true;
        }

        public static bool TryCompile(
            string pattern,
            bool ignoreCase,
            out Regex? regex,
            out string? error)
        {
            regex = null;
            error = null;
            if (string.IsNullOrEmpty(pattern))
            {
                error = "Pattern is empty.";
                return false;
            }

            try
            {
                var opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;
                if (ignoreCase)
                    opts |= RegexOptions.IgnoreCase;
                regex = new Regex(pattern, opts, MatchTimeout);
                // Touch once so bad patterns fail here, not at speak time.
                _ = regex.IsMatch("");
                return true;
            }
            catch (ArgumentException ex)
            {
                error = "Invalid regex: " + ex.Message;
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                error = "Regex timed out while validating (too expensive).";
                return false;
            }
        }

        public static string SanitizeId(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
            {
                if (char.IsLetterOrDigit(c) || c is '-' or '_')
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static string NewCustomId() =>
            "custom-" + Guid.NewGuid().ToString("N")[..10];

        private static string CollapseOneLine(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return Regex.Replace(s.Replace('\r', ' ').Replace('\n', ' ').Trim(), @"\s+", " ");
        }
    }

    /// <summary>
    /// Shipped default pipeline rules (former hard-coded regex in OcrProcessor).
    /// </summary>
    public static class SpeechTextRulesCatalog
    {
        /// <summary>Full default list in pipeline order within each stage.</summary>
        public static List<SpeechTextRule> CreateDefaults()
        {
            var list = new List<SpeechTextRule>(64);

            // ---- Noise (pre-lowercase) — order matches former OcrProcessor ----
            // Ellipsis is handled earlier in CleanForSpeech (before collapse).

            // Spotting geometry first
            Add(list, "noise-spot-quad", "Spotting quad boxes",
                SpeechTextRuleStage.Noise,
                @"\[\s*\[\s*-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?\s*\](?:\s*,\s*\[\s*-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?\s*\]){1,7}\s*\]",
                " ", ignoreCase: false);

            Add(list, "noise-spot-num-array", "Spotting number arrays",
                SpeechTextRuleStage.Noise,
                @"\[\s*-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?){3,15}\s*\]",
                " ", ignoreCase: false);

            Add(list, "noise-spot-num-paren", "Spotting number parens",
                SpeechTextRuleStage.Noise,
                @"\(\s*-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?){3,15}\s*\)",
                " ", ignoreCase: false);

            Add(list, "noise-spot-conf", "Spotting conf/score",
                SpeechTextRuleStage.Noise,
                @"\b(?:conf(?:idence)?|score)\s*[:=]?\s*-?\d+(?:\.\d+)?\b",
                " ", ignoreCase: true);

            Add(list, "noise-spot-labels", "Spotting bbox/quad labels",
                SpeechTextRuleStage.Noise,
                @"\b(?:bbox|bounding\s*box|quad|polygon|coords?)\b\s*[:=]?",
                " ", ignoreCase: true);

            Add(list, "noise-spot-num-line", "Spotting number-only lines",
                SpeechTextRuleStage.Noise,
                @"(?m)^\s*[\d\s,.\-\[\]\(\)]+\s*$",
                " ", ignoreCase: false);

            // Model attach-image junk
            Add(list, "noise-attach-images-paren", "Attach images (paren)",
                SpeechTextRuleStage.Noise,
                @"[\(\[\{]\s*att?a[ct]h(?:ed|tched)?\s*images?\s*[:\-]?\s*\d*\s*[\)\]\}]",
                " ", ignoreCase: true);

            Add(list, "noise-attach-images-bare", "Attach images (bare)",
                SpeechTextRuleStage.Noise,
                @"\batt?a[ct]h(?:ed|tched)?\s+images?\s*[:\-]?\s*\d*\b",
                " ", ignoreCase: true);

            Add(list, "noise-images-num-paren", "Image N (paren)",
                SpeechTextRuleStage.Noise,
                @"[\(\[\{]\s*(?:images?|imgs?)\s*[:\-]?\s*\d+\s*[\)\]\}]",
                " ", ignoreCase: true);

            Add(list, "noise-bare-num-paren", "Bare (number) boxes",
                SpeechTextRuleStage.Noise,
                @"[\(\[\{]\s*\d+\s*[\)\]\}]",
                " ", ignoreCase: false);

            // Markdown
            // Prefer SpeechCleaner.StripMarkdownLlmJunk (always-on). These catalog
            // rules remain as a second pass if users keep BuiltIn noise rules on.
            Add(list, "noise-md-fence-tick", "Markdown ``` fences",
                SpeechTextRuleStage.Noise,
                @"```[\w+-]*\s*\r?\n?([\s\S]*?)\r?\n?```",
                " $1 ", ignoreCase: false);

            Add(list, "noise-md-fence-tilde", "Markdown ~~~ fences",
                SpeechTextRuleStage.Noise,
                @"~~~[\w+-]*\s*\r?\n?([\s\S]*?)\r?\n?~~~",
                " $1 ", ignoreCase: false);

            Add(list, "noise-md-fence-unclosed", "Markdown unclosed ``` fence",
                SpeechTextRuleStage.Noise,
                @"```[\w+-]*\s*\r?\n?([\s\S]*)$",
                " $1 ", ignoreCase: false);

            Add(list, "noise-md-inline-code", "Markdown inline code",
                SpeechTextRuleStage.Noise,
                @"`([^`]+)`",
                "$1", ignoreCase: false);

            Add(list, "noise-md-image", "Markdown images",
                SpeechTextRuleStage.Noise,
                @"!\[([^\]]*)\]\([^)]+\)",
                "$1", ignoreCase: false);

            Add(list, "noise-md-link", "Markdown links",
                SpeechTextRuleStage.Noise,
                @"\[([^\]]+)\]\([^)]+\)",
                "$1", ignoreCase: false);

            Add(list, "noise-md-ref-link", "Markdown ref links",
                SpeechTextRuleStage.Noise,
                @"\[([^\]]+)\]\[[^\]]*\]",
                "$1", ignoreCase: false);

            Add(list, "noise-md-ref-def", "Markdown ref definitions",
                SpeechTextRuleStage.Noise,
                @"(?m)^\s*\[[^\]]+\]:\s+\S+.*$",
                " ", ignoreCase: false);

            Add(list, "noise-md-auto-link", "Markdown auto-links",
                SpeechTextRuleStage.Noise,
                @"<(https?://[^>]+|mailto:[^>]+|[^@\s>]+@[^@\s>]+\.[^@\s>]+)>",
                "$1", ignoreCase: false);

            // HTML tags / entities intentionally omitted: comic dialogue often uses
            // <…> lettering; Local-LLM should not emit real HTML. Residual < > &
            // fall through to deco-strip-symbols / NormalizeSpeechPunctuation.

            Add(list, "noise-md-heading", "Markdown headings",
                SpeechTextRuleStage.Noise, @"(?m)^\s{0,3}#{1,6}\s+", "", ignoreCase: false);
            Add(list, "noise-md-trail-hash", "Markdown trailing #",
                SpeechTextRuleStage.Noise, @"(?m)\s+#+\s*$", "", ignoreCase: false);
            Add(list, "noise-md-setext", "Markdown setext underlines",
                SpeechTextRuleStage.Noise, @"(?m)^[=\-]{2,}\s*$", " ", ignoreCase: false);
            Add(list, "noise-md-hr", "Markdown horizontal rules",
                SpeechTextRuleStage.Noise, @"(?m)^\s{0,3}([-*_])(?:\s*\1){2,}\s*$", " ", ignoreCase: false);
            Add(list, "noise-md-blockquote", "Markdown blockquotes",
                SpeechTextRuleStage.Noise, @"(?m)^\s{0,3}>\s?", "", ignoreCase: false);
            Add(list, "noise-md-ul", "Markdown bullet lists",
                SpeechTextRuleStage.Noise, @"(?m)^\s{0,3}[-*+]\s+", "", ignoreCase: false);
            Add(list, "noise-md-ol", "Markdown numbered lists",
                SpeechTextRuleStage.Noise, @"(?m)^\s{0,3}\d+[.)]\s+", "", ignoreCase: false);
            Add(list, "noise-md-task", "Markdown task checkboxes",
                SpeechTextRuleStage.Noise, @"(?m)^\s*\[[ xX]\]\s+", "", ignoreCase: false);
            Add(list, "noise-md-table-sep", "Markdown table separators",
                SpeechTextRuleStage.Noise,
                @"(?m)^\s*\|?(?:\s*:?-+:?\s*\|)+\s*:?-+:?\s*\|?\s*$", " ", ignoreCase: false);
            Add(list, "noise-md-pipe", "Markdown table pipes",
                SpeechTextRuleStage.Noise, @"\|", " ", ignoreCase: false);
            Add(list, "noise-md-bold3", "Markdown *** / ___",
                SpeechTextRuleStage.Noise, @"(\*\*\*|___)(.+?)\1", "$2", ignoreCase: false);
            Add(list, "noise-md-bold2", "Markdown ** / __",
                SpeechTextRuleStage.Noise, @"(\*\*|__)(.+?)\1", "$2", ignoreCase: false);
            Add(list, "noise-md-italic", "Markdown * / _ italic",
                SpeechTextRuleStage.Noise, @"(?<!\w)(\*|_)([^*_\n]+?)\1(?!\w)", "$2", ignoreCase: false);
            Add(list, "noise-md-strike", "Markdown ~~strike~~",
                SpeechTextRuleStage.Noise, @"~~(.+?)~~", "$1", ignoreCase: false);
            Add(list, "noise-md-mark", "Markdown ==highlight==",
                SpeechTextRuleStage.Noise, @"==(.+?)==", "$1", ignoreCase: false);
            Add(list, "noise-md-footnote", "Markdown footnotes",
                SpeechTextRuleStage.Noise, @"\[\^[^\]]+\]", " ", ignoreCase: false);
            Add(list, "noise-md-escape", "Markdown escapes",
                SpeechTextRuleStage.Noise, @"\\([\\`*_{}\[\]()#+\-.!|>])", "$1", ignoreCase: false);
            Add(list, "noise-md-orphan-marks", "Markdown orphan marks",
                SpeechTextRuleStage.Noise, @"(?<!\w)(\*{1,3}|_{1,3}|`{1,3})(?!\w)", " ", ignoreCase: false);

            // ---- Abbrev (post-lowercase) ----
            // Curly-apostrophe normalize stays in code (char replace, not regex rule).

            Add(list, "abbrev-possessive-s", "Possessive 's → s",
                SpeechTextRuleStage.Abbrev,
                @"\b(?!(?:he|she|it|that|what|who|where|how|there|here|one|" +
                @"someone|somebody|anyone|anybody|everyone|everybody)'s\b)" +
                @"(\p{L}{2,})'s\b",
                "$1s", ignoreCase: false);

            Add(list, "abbrev-plural-possessive", "Plural possessive s'",
                SpeechTextRuleStage.Abbrev,
                @"(\p{L})s'\b",
                "$1s", ignoreCase: false);

            // Dotted / multi-letter abbreviations (period consumed). Longer first.
            AddAbbrev(list, "abbrev-usa", "U.S.A.",
                @"\bu\.s\.a\.?(?!\p{L})", "united states of america");
            AddAbbrev(list, "abbrev-us", "U.S.",
                @"\bu\.s\.?(?!\p{L})", "united states");
            AddAbbrev(list, "abbrev-uk", "U.K.",
                @"\bu\.k\.?(?!\p{L})", "united kingdom");
            AddAbbrev(list, "abbrev-eg", "e.g.",
                @"\be\.g\.?(?!\p{L})", "for example");
            AddAbbrev(list, "abbrev-ie", "i.e.",
                @"\bi\.e\.?(?!\p{L})", "that is");
            AddAbbrev(list, "abbrev-nb", "n.b.",
                @"\bn\.b\.?(?!\p{L})", "note");
            AddAbbrev(list, "abbrev-ps", "p.s.",
                @"\bp\.s\.?(?!\p{L})", "postscript");
            AddAbbrev(list, "abbrev-etc", "etc.",
                @"\betc\.(?!\p{L})", "et cetera");
            AddAbbrev(list, "abbrev-vs", "vs.",
                @"\bvs\.(?!\p{L})", "versus");
            AddAbbrev(list, "abbrev-approx", "approx.",
                @"\bapprox\.(?!\p{L})", "approximately");
            AddAbbrev(list, "abbrev-dept", "dept.",
                @"\bdept\.(?!\p{L})", "department");
            AddAbbrev(list, "abbrev-avg", "avg.",
                @"\bavg\.(?!\p{L})", "average");
            // Do NOT expand min./max. — they collide with proper names (Max.)
            // and dialogue ("wait a min.") more often than they mean minimum/
            // maximum. Users can still add a custom Text rule if needed.
            AddAbbrev(list, "abbrev-vol", "vol.",
                @"\bvol\.(?!\p{L})", "volume");
            AddAbbrev(list, "abbrev-ch", "ch.",
                @"\bch\.(?!\p{L})", "chapter");
            AddAbbrev(list, "abbrev-fig", "fig.",
                @"\bfig\.(?!\p{L})", "figure");
            AddAbbrev(list, "abbrev-mt", "mt.",
                @"\bmt\.(?!\p{L})", "mount");
            AddAbbrev(list, "abbrev-ave", "ave.",
                @"\bave\.(?!\p{L})", "avenue");
            AddAbbrev(list, "abbrev-blvd", "blvd.",
                @"\bblvd\.(?!\p{L})", "boulevard");
            AddAbbrev(list, "abbrev-rd", "rd.",
                @"\brd\.(?!\p{L})", "road");
            AddAbbrev(list, "abbrev-asap", "ASAP",
                @"\basap\b", "as soon as possible");
            AddAbbrev(list, "abbrev-aka", "AKA",
                @"\baka\b", "also known as");

            // Honorifics — longer first (mrs before mr/ms).
            AddAbbrev(list, "title-mrs", "Mrs.",
                @"\bmrs\.?(?!\p{L})", "missus");
            AddAbbrev(list, "title-ms", "Ms.",
                @"\bms\.?(?!\p{L})", "miss");
            AddAbbrev(list, "title-mr", "Mr.",
                @"\bmr\.?(?!\p{L})", "mister");
            AddAbbrev(list, "title-dr", "Dr.",
                @"\bdr\.?(?!\p{L})", "doctor");
            AddAbbrev(list, "title-prof", "Prof.",
                @"\bprof\.?(?!\p{L})", "professor");
            AddAbbrev(list, "title-rev", "Rev.",
                @"\brev\.?(?!\p{L})", "reverend");
            AddAbbrev(list, "title-sgt", "Sgt.",
                @"\bsgt\.?(?!\p{L})", "sergeant");
            AddAbbrev(list, "title-capt", "Capt.",
                @"\bcapt\.?(?!\p{L})", "captain");
            AddAbbrev(list, "title-cmdr", "Cmdr.",
                @"\bcmdr\.?(?!\p{L})", "commander");
            AddAbbrev(list, "title-col", "Col.",
                @"\bcol\.?(?!\p{L})", "colonel");
            AddAbbrev(list, "title-gen", "Gen.",
                @"\bgen\.?(?!\p{L})", "general");
            AddAbbrev(list, "title-maj", "Maj.",
                @"\bmaj\.?(?!\p{L})", "major");
            AddAbbrev(list, "title-lt", "Lt.",
                @"\blt\.?(?!\p{L})", "lieutenant");
            AddAbbrev(list, "title-sr", "Sr.",
                @"\bsr\.?(?!\p{L})", "senior");
            AddAbbrev(list, "title-jr", "Jr.",
                @"\bjr\.?(?!\p{L})", "junior");
            AddAbbrev(list, "title-st", "St.",
                @"\bst\.?(?!\p{L})", "saint");
            AddAbbrev(list, "title-fr", "Fr.",
                @"\bfr\.?(?!\p{L})", "father");

            // ---- Decorators (comic / UI junk) ----
            const string arrows =
                "\u2192\u2190\u2194\u21D2\u21D0\u00BB\u00AB\u203A\u2039\u25B6\u25C0\u25BA\u25C4";
            const string dashes = "-\u2013\u2014";
            const string bullets =
                "\u2022\u25CF\u25CB\u25C6\u25C7\u25A0\u25A1\u2605\u2606\u25AA\u25AB\u25B2\u25B3\u25BC\u25BD\u2666";

            Add(list, "deco-arrow-dash-clause", "Arrow/dash clause → period",
                SpeechTextRuleStage.Decorators,
                $@"(?<=\w)\s*(?:[{arrows}<>]+|[{dashes}]{{2,}})\s*(?=\w)",
                ". ", ignoreCase: false);

            Add(list, "deco-arrows", "Arrows / chevrons",
                SpeechTextRuleStage.Decorators,
                $@"[{arrows}]+",
                " ", ignoreCase: false);

            Add(list, "deco-multi-dash", "Multi-dash runs",
                SpeechTextRuleStage.Decorators,
                $@"[{dashes}]{{2,}}",
                " ", ignoreCase: false);

            Add(list, "deco-lead-dash", "Line-leading dashes",
                SpeechTextRuleStage.Decorators,
                $@"(?m)^\s*[{dashes}]+\s*",
                "", ignoreCase: false);

            Add(list, "deco-trail-dash", "Line-trailing dashes",
                SpeechTextRuleStage.Decorators,
                $@"(?m)\s*[{dashes}]+\s*$",
                "", ignoreCase: false);

            Add(list, "deco-bullets", "Decorative bullets",
                SpeechTextRuleStage.Decorators,
                $@"[{bullets}]+",
                " ", ignoreCase: false);

            Add(list, "deco-dot-run", "Long dot runs",
                SpeechTextRuleStage.Decorators,
                @"(?<!\.)\.{4,}(?!\.)",
                " ", ignoreCase: false);

            Add(list, "deco-symbol-run", "Tilde/backtick runs",
                SpeechTextRuleStage.Decorators,
                @"[~`^|\\]{2,}",
                " ", ignoreCase: false);

            Add(list, "deco-dot-space-dot", "Spaced double dots",
                SpeechTextRuleStage.Decorators,
                @"\s*\.\s*\.+\s*",
                ". ", ignoreCase: false);

            Add(list, "deco-double-period", "Double periods",
                SpeechTextRuleStage.Decorators,
                @"\.\s*\.",
                ".", ignoreCase: false);

            // Residual symbols TTS should not speak (< > [ ] { } ( ) * # ^ & …).
            // Keep ASCII hyphen so English compounds (well-known, X-Men) stay joined;
            // keep . ! ? , ' and typed pause marks for the final punctuation chain.
            // Runs last in Decorators so arrow/dash/bullet rewrites finish first.
            Add(list, "deco-strip-symbols", "Strip residual symbols (keep hyphen)",
                SpeechTextRuleStage.Decorators,
                @"[^\p{L}\p{N}\s.!?',\-\x1C-\x1F]+",
                " ", ignoreCase: false);

            return list;
        }

        /// <summary>
        /// Former catalog ids that must not load from old profiles. Dropped in
        /// <see cref="MergeWithDefaults"/> so shipped mistakes (e.g. max.→maximum
        /// clobbering the name Max) do not stick after an upgrade.
        /// </summary>
        private static readonly HashSet<string> RetiredBuiltInIds =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "abbrev-max",
                "abbrev-min",
                // One-token VL invention gate — removed (kill-list P1); not a category fix.
                "noise-c-type-uchar",
                // HTML tag / entity strips: ate comic <WHERE ARE YOU…> as fake tags.
                "noise-md-html",
                "noise-entity-nbsp",
                "noise-entity-amp",
                "noise-entity-lt",
                "noise-entity-gt",
                "noise-entity-quot",
                "noise-entity-apos",
            };

        /// <summary>
        /// Load stored rules: validate, drop corrupt/duplicate rows, mark built-ins.
        /// Null or empty → full catalog. Non-empty is authoritative (deleted built-ins
        /// stay gone). Use Speech tab “Reset all” to restore the full shipped set;
        /// new catalog entries are not auto-merged into existing profiles.
        /// Retired catalog ids are stripped even when still present in the ini.
        /// </summary>
        public static List<SpeechTextRule> MergeWithDefaults(IEnumerable<SpeechTextRule>? stored)
        {
            var defaults = CreateDefaults();
            var byId = defaults.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

            if (stored == null)
                return defaults;

            var storedList = stored.Where(r => r != null).ToList();
            if (storedList.Count == 0)
                return defaults;

            var result = new List<SpeechTextRule>(storedList.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in storedList)
            {
                string id = SpeechTextRule.SanitizeId(s.Id);
                if (id.Length == 0 || !seen.Add(id))
                    continue;
                if (RetiredBuiltInIds.Contains(id))
                    continue;

                bool isBuiltIn = byId.ContainsKey(id) || s.IsBuiltIn;
                if (!SpeechTextRule.TryNormalize(
                        id, s.Name, s.Stage, s.Pattern, s.Replace,
                        s.Enabled, s.IgnoreCase, isBuiltIn,
                        out SpeechTextRule clean, out _))
                {
                    // Fall back to default for that built-in id if pattern broke.
                    if (byId.TryGetValue(id, out var def))
                        result.Add(def.Clone());
                    continue;
                }
                clean.IsBuiltIn = byId.ContainsKey(clean.Id);
                result.Add(clean);
            }

            return result.Count > 0 ? result : defaults;
        }

        private static void AddAbbrev(
            List<SpeechTextRule> list, string id, string name, string pattern, string replace) =>
            Add(list, id, name, SpeechTextRuleStage.Abbrev, pattern, replace, ignoreCase: false);

        private static void Add(
            List<SpeechTextRule> list,
            string id,
            string name,
            SpeechTextRuleStage stage,
            string pattern,
            string replace,
            bool ignoreCase)
        {
            if (!SpeechTextRule.TryNormalize(
                    id, name, stage, pattern, replace,
                    enabled: true, ignoreCase, isBuiltIn: true,
                    out SpeechTextRule rule, out string? err))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SpeechTextRules] bad default {id}: {err}");
                return;
            }
            list.Add(rule);
        }
    }

    /// <summary>
    /// Applies enabled pipeline text rules for one stage (safe timeouts).
    /// </summary>
    public static class SpeechTextRulesEngine
    {
        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, Regex> Cache = new(StringComparer.Ordinal);

        public static string Apply(
            string? input,
            IEnumerable<SpeechTextRule>? rules,
            SpeechTextRuleStage stage)
        {
            if (string.IsNullOrEmpty(input) || rules == null)
                return input ?? "";

            string s = input;
            foreach (SpeechTextRule rule in rules)
            {
                if (rule == null || !rule.Enabled || rule.Stage != stage)
                    continue;
                string pat = rule.Pattern ?? "";
                if (pat.Length == 0)
                    continue;

                try
                {
                    // Abbrev stage is always case-insensitive so honorifics /
                    // titles match when Force lowercase is off (Mr. vs mr.).
                    bool ignoreCase = rule.IgnoreCase ||
                        rule.Stage == SpeechTextRuleStage.Abbrev;
                    Regex rx = GetOrCreate(pat, ignoreCase);
                    s = rx.Replace(s, rule.Replace ?? "");
                }
                catch (RegexMatchTimeoutException)
                {
                    // Skip this rule; leave text as-is for remaining rules.
                }
                catch (ArgumentException)
                {
                    // Invalid pattern slipped through — skip.
                }
            }

            return s;
        }

        /// <summary>Clear compiled regex cache (after bulk rule edits).</summary>
        public static void ClearCache()
        {
            lock (CacheLock)
                Cache.Clear();
        }

        private static Regex GetOrCreate(string pattern, bool ignoreCase)
        {
            string key = (ignoreCase ? "i:" : "c:") + pattern;
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out Regex? existing))
                    return existing;
            }

            var opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (ignoreCase)
                opts |= RegexOptions.IgnoreCase;
            var rx = new Regex(pattern, opts, SpeechTextRule.MatchTimeout);

            lock (CacheLock)
            {
                Cache[key] = rx;
                // Bound cache size for long sessions with many custom edits.
                if (Cache.Count > 256)
                {
                    // Drop arbitrary half (simple; patterns recompile on demand).
                    foreach (var k in Cache.Keys.Take(128).ToList())
                        Cache.Remove(k);
                }
            }

            return rx;
        }
    }
}
