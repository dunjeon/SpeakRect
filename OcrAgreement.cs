using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpeakRect
{
    /// <summary>
    /// WinOCR text-source agreement: keep reading until <c>need</c> samples
    /// match, or <c>of</c> tries are spent (then silent — no best-of).
    /// Default 1 of 1. Live / Follow / Balloons / Watch share the same knobs.
    /// </summary>
    public static class OcrAgreement
    {
        public const int DefaultNeed = 1;
        public const int DefaultOf = 1;
        public const int Min = 1;
        public const int MaxOf = 7;

        public static void Normalize(ref int need, ref int of)
        {
            of = Math.Clamp(of, Min, MaxOf);
            need = Math.Clamp(need, Min, of);
        }

        public static (int Need, int Of) NormalizePair(int need, int of)
        {
            Normalize(ref need, ref of);
            return (need, of);
        }

        public static string ToDisplay(int need, int of)
        {
            Normalize(ref need, ref of);
            return need == 1 && of == 1
                ? "1 of 1"
                : $"{need} of {of}";
        }

        /// <summary>
        /// True when at least <paramref name="need"/> usable reads agree
        /// (<see cref="ComicConsensus.OcrTextsAgree"/>). Winner is the best
        /// text in that group.
        /// </summary>
        public static bool TryTakeWinner(
            IReadOnlyList<string> texts,
            int need,
            out string winner)
        {
            winner = "";
            if (texts == null || texts.Count == 0)
                return false;
            need = Math.Clamp(need, Min, MaxOf);

            var usable = new List<string>();
            for (int i = 0; i < texts.Count; i++)
            {
                string t = texts[i] ?? "";
                if (SpeechCleaner.IsUnusableOcrText(t) || RegionWatch.IsUnreadable(t))
                    continue;
                usable.Add(t);
            }
            if (usable.Count < need)
                return false;

            var groups = new List<List<string>>();
            foreach (string u in usable)
            {
                bool placed = false;
                foreach (var g in groups)
                {
                    if (ComicConsensus.OcrTextsAgree(g[0], u))
                    {
                        g.Add(u);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                    groups.Add(new List<string> { u });
            }

            List<string>? hit = null;
            foreach (var g in groups)
            {
                if (g.Count < need)
                    continue;
                if (hit == null ||
                    g.Count > hit.Count ||
                    (g.Count == hit.Count &&
                     BestScore(g) > BestScore(hit)))
                    hit = g;
            }
            if (hit == null)
                return false;

            winner = PickBest(hit);
            return winner.Length > 0;
        }

        /// <summary>
        /// Run <paramref name="sample"/> until <paramref name="need"/> agree.
        /// 1 of 1 is a single call. No agreement after <paramref name="of"/>
        /// tries → <paramref name="empty"/> (silent; no best-of).
        /// </summary>
        public static async Task<T> RunAsync<T>(
            Func<Task<T>> sample,
            Func<T, string> toText,
            T empty,
            int need,
            int of,
            StringBuilder? detail,
            CancellationToken token)
        {
            if (sample == null)
                throw new ArgumentNullException(nameof(sample));
            if (toText == null)
                throw new ArgumentNullException(nameof(toText));

            Normalize(ref need, ref of);
            if (of <= 1)
                return await sample().ConfigureAwait(false);

            var items = new List<T>(of);
            var texts = new List<string>(of);

            for (int i = 0; i < of; i++)
            {
                token.ThrowIfCancellationRequested();
                T item = await sample().ConfigureAwait(false);
                string raw = toText(item) ?? "";
                items.Add(item);
                texts.Add(raw);
                detail?.AppendLine(
                    $"ocr-agree: pass {i + 1}/{of} need={need} chars={raw.Length}");

                if (TryTakeWinner(texts, need, out string win))
                {
                    detail?.AppendLine(
                        $"ocr-agree: {ToDisplay(need, of)} after {i + 1} pass(es)");
                    return ItemMatching(items, toText, win, item);
                }
            }

            detail?.AppendLine(
                $"ocr-agree: no {ToDisplay(need, of)} — silent");
            return empty;
        }

        public static Task<string> RunAsync(
            Func<Task<string>> sample,
            int need,
            int of,
            StringBuilder? detail,
            CancellationToken token)
            => RunAsync(sample, t => t ?? "", "", need, of, detail, token);

        private static T ItemMatching<T>(
            List<T> items,
            Func<T, string> toText,
            string want,
            T fallback)
        {
            for (int i = 0; i < items.Count; i++)
            {
                string t = toText(items[i]) ?? "";
                if (ComicConsensus.OcrTextsAgree(t, want) ||
                    string.Equals(t, want, StringComparison.Ordinal))
                    return items[i];
            }
            return fallback;
        }

        private static int BestScore(List<string> g)
        {
            int best = int.MinValue;
            foreach (string s in g)
            {
                int q = SpeechCleaner.OcrTextQualityScore(s);
                if (q > best) best = q;
            }
            return best;
        }

        private static string PickBest(List<string> g)
        {
            string best = g[0];
            int bestQ = SpeechCleaner.OcrTextQualityScore(best);
            int bestW = ComicRegionGeometry.CountWords(best);
            for (int i = 1; i < g.Count; i++)
            {
                string s = g[i];
                int q = SpeechCleaner.OcrTextQualityScore(s);
                int w = ComicRegionGeometry.CountWords(s);
                if (q > bestQ || (q == bestQ && w > bestW) ||
                    (q == bestQ && w == bestW && s.Length > best.Length))
                {
                    best = s;
                    bestQ = q;
                    bestW = w;
                }
            }
            return best;
        }
    }
}
