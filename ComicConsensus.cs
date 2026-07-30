using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SpeakRect
{
    /// <summary>
    /// Pure diversified-decode consensus helpers (text agreement, strong-A gate,
    /// 2-of-3 winner pick). HTTP multi-pass orchestration stays on <see cref="OcrProcessor"/>.
    /// </summary>
    public static class ComicConsensus
    {
        public const int StrongMinWords = 8;
        public const int StrongMinAlnum = 28;
        public const int StrongMinQuality = 36;

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length <= max) return s;
            return s.Substring(0, Math.Max(0, max - 1)) + "…";
        }
        public static string NormalizeOcrCompare(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";
            // Collapse whitespace; strip curly quotes variants the model sometimes swaps
            string t = s.Trim().ToLowerInvariant();
            // Curly quotes: ‘ ’ “ ”
            t = t.Replace('\u2018', '\'').Replace('\u2019', '\'')
                 .Replace('\u201C', '"').Replace('\u201D', '"');
            return Regex.Replace(t, @"\s+", " ");
        }

        /// <summary>
        /// Loose OCR string agreement for consensus voting: whitespace, punctuation,
        /// and near-duplicates (one contains the other) still count as a match.
        /// </summary>
        public static bool OcrTextsAgree(string a, string b)
        {
            string na = NormalizeOcrCompare(a);
            string nb = NormalizeOcrCompare(b);
            if (na.Length == 0 || nb.Length == 0)
                return false;
            if (string.Equals(na, nb, StringComparison.Ordinal))
                return true;

            string pa = Regex.Replace(na, @"[^\w\s]", "", RegexOptions.CultureInvariant);
            string pb = Regex.Replace(nb, @"[^\w\s]", "", RegexOptions.CultureInvariant);
            pa = Regex.Replace(pa, @"\s+", " ").Trim();
            pb = Regex.Replace(pb, @"\s+", " ").Trim();
            if (string.Equals(pa, pb, StringComparison.Ordinal))
                return true;

            int minLen = Math.Min(pa.Length, pb.Length);
            if (minLen >= 8 &&
                (pa.Contains(pb, StringComparison.Ordinal) ||
                 pb.Contains(pa, StringComparison.Ordinal)))
                return true;

            // Token overlap: majority of the shorter side's content tokens shared
            double ov = SpeechCleaner.TokenOverlapRatio(pa, pb);
            if (ov >= 0.82 && minLen >= 6)
                return true;

            return false;
        }

        /// True when T=0 primary is solid enough to skip extra consensus passes
        /// without sacrificing accuracy. Intentionally strict: short balloons and
        /// weak/garbage reads always take the full multi-pass path.
        /// </summary>
        public static bool IsStrongConsensusPrimary(string clean, out string reason)
        {
            reason = "";
            if (SpeechCleaner.IsUnusableOcrText(clean))
            {
                reason = "unusable";
                return false;
            }

            int words = ComicRegionGeometry.CountWords(clean);
            int alnum = SpeechCleaner.CountAlnum(clean);
            int q = SpeechCleaner.OcrTextQualityScore(clean);

            if (words < StrongMinWords)
            {
                reason = $"words={words}<{StrongMinWords}";
                return false;
            }
            if (alnum < StrongMinAlnum)
            {
                reason = $"alnum={alnum}<{StrongMinAlnum}";
                return false;
            }
            if (q < StrongMinQuality)
            {
                reason = $"q={q}<{StrongMinQuality}";
                return false;
            }

            // Truncation / dump smell: very high digit ratio or tiny avg token length
            var toks = SpeechCleaner.TokenizeWords(clean);
            if (toks.Count >= 4)
            {
                int digitish = toks.Count(t => t.Any(char.IsDigit) && t.Length <= 3);
                if (digitish >= toks.Count / 2)
                {
                    reason = "digit-heavy";
                    return false;
                }
            }

            reason =
                $"words={words} alnum={alnum} q={q}";
            return true;
        }

        /// <summary>
        /// Majority (2+) agreement group wins; within group pick higher quality /
        /// longer. No majority → best single usable by quality score.
        /// </summary>
        public static string? PickConsensusWinner(
            List<(string Label, string Raw, string Clean)> reads,
            StringBuilder detail,
            string logTag)
        {
            var usable = reads
                .Where(r => !SpeechCleaner.IsUnusableOcrText(r.Clean))
                .Select(r => (r.Label, Clean: r.Clean!))
                .ToList();

            if (usable.Count == 0)
            {
                detail.AppendLine($"{logTag} consensus: none usable");
                return null;
            }

            if (usable.Count == 1)
            {
                detail.AppendLine(
                    $"{logTag} consensus: single usable [{usable[0].Label}] " +
                    $"words={ComicRegionGeometry.CountWords(usable[0].Clean)}");
                return usable[0].Clean;
            }

            // Greedy groups by OcrTextsAgree
            var groups = new List<List<(string Label, string Clean)>>();
            foreach (var u in usable)
            {
                bool placed = false;
                foreach (var g in groups)
                {
                    if (OcrTextsAgree(g[0].Clean, u.Clean))
                    {
                        g.Add(u);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                    groups.Add(new List<(string, string)> { u });
            }

            var bestGroup = groups
                .OrderByDescending(g => g.Count)
                .ThenByDescending(g => g.Max(x => SpeechCleaner.OcrTextQualityScore(x.Clean)))
                .ThenByDescending(g => g.Max(x => ComicRegionGeometry.CountWords(x.Clean)))
                .First();

            var pick = bestGroup
                .OrderByDescending(x => SpeechCleaner.OcrTextQualityScore(x.Clean))
                .ThenByDescending(x => ComicRegionGeometry.CountWords(x.Clean))
                .ThenByDescending(x => x.Clean.Length)
                .First();

            bool majority = bestGroup.Count >= 2;
            detail.AppendLine(
                $"{logTag} consensus: {(majority ? "2-of-3" : "best-of")} " +
                $"[{pick.Label}] votes={bestGroup.Count}/{usable.Count} " +
                $"q={SpeechCleaner.OcrTextQualityScore(pick.Clean)} words={ComicRegionGeometry.CountWords(pick.Clean)} " +
                $"\"{Truncate(pick.Clean, 56)}\"");

            return pick.Clean;
        }
    }
}