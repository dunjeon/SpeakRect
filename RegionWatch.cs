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
        /// <summary>WinOCR saw text; LLM words are in the recognize result (may still be empty).</summary>
        HasText = 2,
    }

    /// <summary>
    /// Watch-region helpers: spoken-word compare and slot geometry.
    /// The overlay timer OCRs on each idle tick and speaks only when the
    /// recognized words differ from the last spoken (or baseline) text.
    /// </summary>
    public static class RegionWatch
    {
        public const int DefaultIntervalMs = 2000;
        public const int MinIntervalMs = 500;
        public const int MaxIntervalMs = 60_000;
        public const int DefaultMinDifferencePercent = 90;

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
        /// Watch gate: WinOCR reported at least one box that contains real words.
        /// Geometry is ignored — Watch still sends the full snap to the LLM.
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
