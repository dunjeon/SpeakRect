using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace SpeakRect
{
    /// <summary>One drawn capture for a region slot (screen coordinates).</summary>
    public sealed class RegionSlotData
    {
        /// <summary>Empty, Rect, Oval, or Lasso.</summary>
        public string Mode { get; set; } = "";

        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }

        /// <summary>Lasso vertices as "x,y;x,y;…".</summary>
        public string Points { get; set; } = "";

        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Mode) ||
            Mode.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            (IsBoxMode && (W <= 0 || H <= 0)) ||
            (IsLassoMode && CountPoints(Points) < 3);

        public bool IsBoxMode =>
            Mode.Equals("Rect", StringComparison.OrdinalIgnoreCase) ||
            Mode.Equals("Rectangle", StringComparison.OrdinalIgnoreCase) ||
            Mode.Equals("Oval", StringComparison.OrdinalIgnoreCase) ||
            Mode.Equals("Ellipse", StringComparison.OrdinalIgnoreCase);

        public bool IsLassoMode =>
            Mode.Equals("Lasso", StringComparison.OrdinalIgnoreCase);

        public bool IsOvalMode =>
            Mode.Equals("Oval", StringComparison.OrdinalIgnoreCase) ||
            Mode.Equals("Ellipse", StringComparison.OrdinalIgnoreCase);

        public Rectangle ToRectangle() =>
            W > 0 && H > 0 ? new Rectangle(X, Y, W, H) : Rectangle.Empty;

        public void Clear()
        {
            Mode = "";
            X = Y = W = H = 0;
            Points = "";
        }

        public void SetBox(string mode, Rectangle r)
        {
            Mode = mode;
            X = r.X;
            Y = r.Y;
            W = r.Width;
            H = r.Height;
            Points = "";
        }

        public void SetLasso(IReadOnlyList<Point> pts)
        {
            Mode = "Lasso";
            X = Y = W = H = 0;
            Points = FormatPoints(pts);
        }

        public List<Point> GetLassoPoints() => ParsePoints(Points);

        public string ToIniString()
        {
            if (IsEmpty) return "";
            if (IsLassoMode)
                return "Lasso:" + (Points ?? "");
            string kind = IsOvalMode ? "Oval" : "Rect";
            return $"{kind}:{X},{Y},{W},{H}";
        }

        public static RegionSlotData Parse(string? raw)
        {
            var slot = new RegionSlotData();
            if (string.IsNullOrWhiteSpace(raw))
                return slot;

            string t = raw.Trim();
            int colon = t.IndexOf(':');
            if (colon <= 0)
                return slot;

            string kind = t[..colon].Trim();
            string body = t[(colon + 1)..].Trim();
            if (kind.Equals("Lasso", StringComparison.OrdinalIgnoreCase))
            {
                slot.Mode = "Lasso";
                // Normalize to pipe separators (see FormatPoints).
                slot.Points = NormalizePointList(body);
                if (CountPoints(slot.Points) < 3)
                    slot.Clear();
                return slot;
            }

            string[] parts = body.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4 ||
                !int.TryParse(parts[0], out int x) ||
                !int.TryParse(parts[1], out int y) ||
                !int.TryParse(parts[2], out int w) ||
                !int.TryParse(parts[3], out int h) ||
                w <= 0 || h <= 0)
            {
                return slot;
            }

            if (kind.Equals("Oval", StringComparison.OrdinalIgnoreCase) ||
                kind.Equals("Ellipse", StringComparison.OrdinalIgnoreCase))
                slot.Mode = "Oval";
            else if (kind.Equals("Rect", StringComparison.OrdinalIgnoreCase) ||
                     kind.Equals("Rectangle", StringComparison.OrdinalIgnoreCase))
                slot.Mode = "Rect";
            else
                return slot;

            slot.X = x;
            slot.Y = y;
            slot.W = w;
            slot.H = h;
            return slot;
        }

        /// <summary>
        /// Point list for ini: use '|' between vertices (NOT ';').
        /// Semicolons are treated as comments by <see cref="ReadIni"/> for short keys,
        /// which used to chop lasso geometry after the first vertex.
        /// </summary>
        public static string FormatPoints(IReadOnlyList<Point> pts)
        {
            if (pts == null || pts.Count == 0) return "";
            var sb = new StringBuilder(pts.Count * 12);
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(pts[i].X).Append(',').Append(pts[i].Y);
            }
            return sb.ToString();
        }

        public static List<Point> ParsePoints(string? raw)
        {
            var list = new List<Point>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            // Accept '|' (current) and legacy ';' separators.
            foreach (string token in SplitPointTokens(raw))
            {
                string[] xy = token.Split(',', StringSplitOptions.TrimEntries);
                if (xy.Length >= 2 &&
                    int.TryParse(xy[0], out int x) &&
                    int.TryParse(xy[1], out int y))
                {
                    list.Add(new Point(x, y));
                }
            }
            return list;
        }

        private static string NormalizePointList(string raw) =>
            FormatPoints(ParsePoints(raw));

        private static IEnumerable<string> SplitPointTokens(string raw)
        {
            // Prefer '|'; also split on ';' for older files that survived comment stripping.
            char sep = raw.Contains('|') ? '|' : ';';
            return raw.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static int CountPoints(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            int n = 0;
            foreach (string token in SplitPointTokens(raw))
            {
                string[] xy = token.Split(',');
                if (xy.Length >= 2) n++;
            }
            return n;
        }
    }

}
