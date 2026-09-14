using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;

namespace SpeakRect
{
    /// <summary>WinOCR boolean gate for Watch (not spoken text).</summary>
    public enum WatchTextGate
    {
        /// <summary>Snap/engine/cancel — do not treat as “no text” (do not clear last spoken).</summary>
        Unavailable = 0,
        /// <summary>WinOCR ran and saw no text.</summary>
        NoText = 1,
        /// <summary>
        /// WinOCR saw a strong line of words. Local-LLM path confirmed that
        /// pull before the snap was sent; spoken words still come from the
        /// model and may still be empty (uncorroborated description).
        /// </summary>
        HasText = 2,
    }

    /// <summary>
    /// Watch Local-LLM path after the WinOCR yes/no gate.
    /// Independent of overlay MODE.
    /// </summary>
    public enum WatchPipeline
    {
        /// <summary>Raw region pixels. No Image tab. One full-frame LLM.</summary>
        RawSnap = 0,
        /// <summary>Image tab prep, then one full-frame LLM (Default speak path).</summary>
        Image = 1,
        /// <summary>Image tab prep + balloon detect, then per-island LLM (Comic Book).</summary>
        ImageBalloon = 2,
    }

    /// <summary>
    /// Where Watch gets the spoken line after the yes/no gate.
    /// </summary>
    public enum WatchTextSource
    {
        /// <summary>Local-LLM converts the snap / balloons to words. Default.</summary>
        LocalLlm = 0,
        /// <summary>OCR line text (faster; skips the local model).</summary>
        Ocr = 1,
    }

    /// <summary>
    /// Watch-region helpers: spoken-word compare, pipeline pick, and slot geometry.
    /// Each idle tick: OCR boolean (is there text?), Local-LLM confirms with a
    /// <b>strong</b> OCR pull (HUD chips / vowelless scraps → silent, do not send
    /// the snap), then the chosen pipeline only if yes. After Local-LLM returns,
    /// the line must share content tokens with that pull or Watch stays silent
    /// (scene descriptions do not overlap a real OCR line). Speaks when the
    /// recognized words differ from last spoken.
    /// </summary>
    public static class RegionWatch
    {
        /// <summary>
        /// Fraction of Local-LLM content tokens that must also appear in the
        /// OCR confirm pull. Kills long scene descriptions that share one
        /// incidental word with HUD junk.
        /// </summary>
        public const double MinLlmOcrContentCovered = 0.4;
        public const int DefaultIntervalMs = 2000;
        public const int MinIntervalMs = 500;
        public const int MaxIntervalMs = 60_000;
        public const int DefaultMinDifferencePercent = 90;
        public const WatchPipeline DefaultPipeline = WatchPipeline.RawSnap;
        public const WatchTextSource DefaultTextSource = WatchTextSource.LocalLlm;

        private static readonly object StatusLock = new();
        private static string _lastStatus = "Off.";

        /// <summary>Latest watch status line for Settings → Watch.</summary>
        public static string LastStatus
        {
            get { lock (StatusLock) return _lastStatus; }
        }

        public static void SetStatus(string? text)
        {
            string line = string.IsNullOrWhiteSpace(text) ? "Off." : text.Trim();
            lock (StatusLock)
                _lastStatus = line;
        }

        public static WatchPipeline NormalizePipeline(WatchPipeline pipeline) =>
            pipeline is WatchPipeline.RawSnap or WatchPipeline.Image or WatchPipeline.ImageBalloon
                ? pipeline
                : DefaultPipeline;

        public static WatchPipeline ParsePipeline(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return DefaultPipeline;
            string t = raw.Trim();
            if (t.Equals("ImageBalloon", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Image+Balloon", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Balloon", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("2", StringComparison.Ordinal))
                return WatchPipeline.ImageBalloon;
            if (t.Equals("Image", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("ImagePrep", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("1", StringComparison.Ordinal))
                return WatchPipeline.Image;
            if (t.Equals("RawSnap", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Raw", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("0", StringComparison.Ordinal))
                return WatchPipeline.RawSnap;
            return DefaultPipeline;
        }

        public static string PipelineToIni(WatchPipeline pipeline) =>
            NormalizePipeline(pipeline) switch
            {
                WatchPipeline.Image => "Image",
                WatchPipeline.ImageBalloon => "ImageBalloon",
                _ => "RawSnap",
            };

        public static string PipelineDisplayName(WatchPipeline pipeline) =>
            NormalizePipeline(pipeline) switch
            {
                WatchPipeline.Image => "Image",
                WatchPipeline.ImageBalloon => "Image + Balloon",
                _ => "Raw snap",
            };

        public static WatchTextSource NormalizeTextSource(WatchTextSource source) =>
            source is WatchTextSource.LocalLlm or WatchTextSource.Ocr
                ? source
                : DefaultTextSource;

        public static WatchTextSource ParseTextSource(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return DefaultTextSource;
            string t = raw.Trim();
            if (t.Equals("Ocr", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("OCR", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("WinOcr", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("WinOCR", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("1", StringComparison.Ordinal))
                return WatchTextSource.Ocr;
            if (t.Equals("LocalLlm", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Local-LLM", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("LLM", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("0", StringComparison.Ordinal))
                return WatchTextSource.LocalLlm;
            return DefaultTextSource;
        }

        public static string TextSourceToIni(WatchTextSource source) =>
            NormalizeTextSource(source) == WatchTextSource.Ocr ? "Ocr" : "LocalLlm";

        public static string TextSourceDisplayName(WatchTextSource source) =>
            NormalizeTextSource(source) == WatchTextSource.Ocr ? "OCR" : "Local-LLM";

        public static string NormalizeText(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            return Regex.Replace(raw.Trim(), @"\s+", " ");
        }

        public static bool IsUnreadable(string? raw)
        {
            string t = (raw ?? "").Trim();
            return t.Equals("unreadable", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("(unreadable)", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when <paramref name="raw"/> is real OCR text (not empty / unreadable).
        /// </summary>
        public static bool TryNormalizeSpeakable(string? raw, out string normalized)
        {
            normalized = NormalizeText(raw);
            if (normalized.Length == 0 || IsUnreadable(normalized))
            {
                normalized = "";
                return false;
            }
            return true;
        }

        /// <summary>
        /// True when balloon detect reported at least one box that contains
        /// real words. Watch's cheap probe is <see cref="BalloonOcrDetect.ReadNonJunkLinesAsync"/>;
        /// Local-LLM then confirms with <see cref="WinOcrPullConfirmsText"/>. This
        /// helper is for island lists after detect.
        /// </summary>
        public static bool WinOcrFoundSpeakableText(IReadOnlyList<DetectedTextRegion>? regions)
        {
            if (regions == null || regions.Count == 0)
                return false;
            for (int i = 0; i < regions.Count; i++)
            {
                var r = regions[i];
                if (r == null)
                    continue;
                if (r.Bounds.Width < 2 || r.Bounds.Height < 2)
                    continue;
                if (ComicRegionGeometry.CountWords(r.WinOcrText) > 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Local-LLM Watch confirm, junk floor: after the yes/no probe, the same
        /// OCR engine must actually return speakable words. Empty / unusable pull
        /// is a false positive. Prefer <see cref="WinOcrPullStrongEnoughForLlm"/>
        /// before sending the snap (HUD chips still pass this floor).
        /// OCR text source does not use this (it already speaks the pull).
        /// </summary>
        public static bool WinOcrPullConfirmsText(string? pulled)
        {
            if (!TryNormalizeSpeakable(pulled, out string n))
                return false;
            return !SpeechCleaner.IsUnusableOcrText(n);
        }

        /// <summary>
        /// Watch Local-LLM gate: speakable OCR pull that looks like a line of
        /// words, not a HUD chip / digit scrap. Thin pulls skip the model
        /// (it will describe the picture). Punchy 1–2 word balloons
        /// (<c>NO</c>, <c>OK</c>, <c>YES</c>, <c>oh no</c>) still count.
        /// One longer singleton (<c>MENU</c>, <c>Hello</c>, <c>START</c>) does not.
        /// </summary>
        public static bool WinOcrPullStrongEnoughForLlm(string? pulled)
        {
            if (!WinOcrPullConfirmsText(pulled))
                return false;

            var content = ContentTokens(pulled);
            if (content.Count >= 2)
                return true;

            int words = ComicRegionGeometry.CountWords(pulled);
            if (words >= 2 && content.Count >= 1)
                return true;

            return IsPunchyDialoguePull(pulled ?? "");
        }

        /// <summary>
        /// Watch Local-LLM output must be explained by the OCR confirm pull.
        /// Content tokens (letters, length ≥ 3, has a vowel) in the model line
        /// have to overlap that pull — a scene description will not.
        /// Punchy OCR (<c>NO</c>) must appear in the model line, and the model
        /// must not then dump extra content tokens.
        /// </summary>
        public static bool LlmOutputCorroboratedByOcr(string? ocrPull, string? llmText)
        {
            if (!TryNormalizeSpeakable(llmText, out string llmNorm) ||
                SpeechCleaner.IsUnusableOcrText(llmNorm))
                return false;
            if (!WinOcrPullConfirmsText(ocrPull))
                return false;

            var ocrContent = ContentTokens(ocrPull);
            var llmContent = ContentTokens(llmText);

            if (ocrContent.Count == 0)
            {
                // Punchy NO / OK / oh no: every short letter-token must appear,
                // and the model must not have written a caption around it.
                var ocrShort = LetterTokens(ocrPull, minLen: 2);
                var llmShort = new HashSet<string>(LetterTokens(llmText, minLen: 2));
                if (ocrShort.Count == 0)
                    return false;
                for (int i = 0; i < ocrShort.Count; i++)
                {
                    if (!llmShort.Contains(ocrShort[i]))
                        return false;
                }
                return llmContent.Count <= 2;
            }

            if (llmContent.Count == 0)
                return false;

            var ocrSet = new HashSet<string>(ocrContent, StringComparer.Ordinal);
            int overlap = 0;
            for (int i = 0; i < llmContent.Count; i++)
            {
                if (ocrSet.Contains(llmContent[i]))
                    overlap++;
            }
            if (overlap == 0)
                return false;
            double covered = overlap / (double)llmContent.Count;
            return covered + 1e-9 >= MinLlmOcrContentCovered;
        }

        /// <summary>
        /// Unique letter tokens with length ≥ 3 and a vowel. Digits are
        /// stripped (so <c>HP</c> / <c>12</c> do not count).
        /// </summary>
        public static List<string> ContentTokens(string? text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return list;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string w in SpeechCleaner.TokenizeWords(text))
            {
                string letters = LettersOnly(w);
                if (letters.Length < 3 || !HasVowel(letters) || IsAllSameLetter(letters))
                    continue;
                if (!seen.Add(letters))
                    continue;
                list.Add(letters);
            }
            return list;
        }

        private static bool IsPunchyDialoguePull(string pulled)
        {
            var toks = SpeechCleaner.TokenizeWords(pulled);
            if (toks.Count == 0 || toks.Count > 2)
                return false;
            for (int i = 0; i < toks.Count; i++)
            {
                string n = LettersOnly(toks[i]);
                if (n.Length < 2 || n.Length > 3)
                    return false;
                if (IsAllSameLetter(n))
                    return false;
                if (!ComicBestOfFusion.LooksLikeRealDialogueToken(n))
                    return false;
            }
            return true;
        }

        private static List<string> LetterTokens(string? text, int minLen)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return list;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string w in SpeechCleaner.TokenizeWords(text))
            {
                string n = LettersOnly(w);
                if (n.Length < minLen)
                    continue;
                if (!seen.Add(n))
                    continue;
                list.Add(n);
            }
            return list;
        }

        private static string LettersOnly(string w)
        {
            if (string.IsNullOrEmpty(w))
                return "";
            var sb = new System.Text.StringBuilder(w.Length);
            foreach (char c in w)
            {
                if (char.IsLetter(c))
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static bool HasVowel(string n)
        {
            foreach (char c in n)
            {
                char l = char.ToLowerInvariant(c);
                if (l is 'a' or 'e' or 'i' or 'o' or 'u' or 'y')
                    return true;
            }
            return false;
        }

        private static bool IsAllSameLetter(string n)
        {
            if (n.Length == 0)
                return true;
            char first = char.ToLowerInvariant(n[0]);
            for (int i = 1; i < n.Length; i++)
            {
                if (char.ToLowerInvariant(n[i]) != first)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// True when two OCR results are the same spoken words (whitespace collapsed, case ignored).
        /// </summary>
        public static bool SameSpokenWords(string? previous, string? current)
        {
            if (!TryNormalizeSpeakable(previous, out string a) ||
                !TryNormalizeSpeakable(current, out string b))
                return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// How different <paramref name="current"/> is from <paramref name="previous"/>
        /// as a 0–100 percent (Levenshtein / longer length, case-insensitive).
        /// Empty previous vs new text is 100. Both empty is 0.
        /// </summary>
        public static double DifferencePercent(string? previous, string? current)
        {
            string a = NormalizeText(previous);
            string b = NormalizeText(current);
            if (a.Length == 0 && b.Length == 0)
                return 0;
            if (a.Length == 0 || b.Length == 0)
                return 100;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return 0;

            int dist = LevenshteinIgnoreCase(a, b);
            int max = Math.Max(a.Length, b.Length);
            if (max <= 0)
                return 0;
            return 100.0 * dist / max;
        }

        /// <summary>
        /// True when the new words differ from last spoken by at least
        /// <paramref name="minDifferencePercent"/> (0–100).
        /// </summary>
        public static bool DiffersEnough(string? previous, string? current, int minDifferencePercent)
        {
            int need = Math.Clamp(minDifferencePercent, 0, 100);
            double pct = DifferencePercent(previous, current);
            if (need <= 0)
                return pct > 0;
            return pct + 1e-9 >= need;
        }

        private static int LevenshteinIgnoreCase(string a, string b)
        {
            int n = a.Length;
            int m = b.Length;
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++)
                prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                char ca = char.ToLowerInvariant(a[i - 1]);
                for (int j = 1; j <= m; j++)
                {
                    int cost = ca == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    int del = prev[j] + 1;
                    int ins = cur[j - 1] + 1;
                    int sub = prev[j - 1] + cost;
                    int v = del < ins ? del : ins;
                    cur[j] = v < sub ? v : sub;
                }
                var tmp = prev;
                prev = cur;
                cur = tmp;
            }
            return prev[m];
        }

        /// <summary>
        /// Resolve a saved slot to a capture rect (and optional lasso / oval).
        /// </summary>
        public static bool TryGetCapture(
            RegionSlotData? slot,
            out Rectangle bounds,
            out List<Point>? lasso,
            out bool isEllipse)
        {
            bounds = Rectangle.Empty;
            lasso = null;
            isEllipse = false;
            if (slot == null || slot.IsEmpty)
                return false;

            if (slot.IsLassoMode)
            {
                var pts = slot.GetLassoPoints();
                if (pts.Count < 3)
                    return false;
                lasso = pts;
                bounds = BoundingRect(pts);
            }
            else
            {
                bounds = slot.ToRectangle();
                isEllipse = slot.IsOvalMode;
            }

            return !bounds.IsEmpty && bounds.Width >= 8 && bounds.Height >= 8;
        }

        private static Rectangle BoundingRect(List<Point> points)
        {
            int minX = points[0].X, minY = points[0].Y, maxX = points[0].X, maxY = points[0].Y;
            for (int i = 1; i < points.Count; i++)
            {
                var p = points[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new Rectangle(minX, minY, Math.Max(1, maxX - minX + 1), Math.Max(1, maxY - minY + 1));
        }
    }
}
