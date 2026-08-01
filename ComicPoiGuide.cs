using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace SpeakRect
{
    /// <summary>
    /// Comic Book alternate POI guide: WinOCR finds text islands, optional thick fog
    /// outside them, then <b>bright green region boxes</b> (same idea as Balloons
    /// preview) on the ink-gray prep. No free-floating markers that can cover text.
    /// Multi-island speak uses sequential OCR; single-island uses this full-page guide.
    /// </summary>
    public static class ComicPoiGuide
    {
        /// <summary>
        /// Built-in default POI prompt (same text as <see cref="AppSettings.DefaultPoiPrompt"/>).
        /// Live path uses <see cref="AppSettings.ResolvePoiPrompt"/> so Speech → Prompts
        /// overrides apply; this constant is the stock fallback only.
        /// </summary>
        public const string DefaultPrompt = AppSettings.DefaultPoiPrompt;

        /// <summary>Bright green stroke (matches Balloons preview solid boxes).</summary>
        public static readonly Color GuideBoxGreen = Color.FromArgb(255, 40, 220, 60);

        /// <summary>Gold outer stroke so the box stays visible on light gray fog.</summary>
        public static readonly Color GuideBoxOuter = Color.FromArgb(255, 255, 210, 40);

        /// <summary>Thick outside-fog blend (0..1) toward mid gray — hides art/UI outside islands.</summary>
        public const float OutsideFogAmount = 0.92f;

        /// <summary>Target gray for outside fog.</summary>
        public const byte OutsideFogGrayLevel = 150;
        /// <summary>Extra clear pad (px) around each island so ink on the rim is not fogged.</summary>
        public const int OutsideFogClearPadPx = 4;

        /// <summary>White gap between auto-stacked POI strips (px).</summary>
        public const int StackStripGapPx = 8;

        /// <summary>
        /// Local-LLM send experiment: outer margin (px) on top/left/right/bottom of
        /// the forced island stack before the 640 long-edge cap.
        /// </summary>
        public const int LlmSendStackMarginPx = 12;

        /// <summary>Hard cap on auto-stack canvas height before scale-down.</summary>
        public const int StackMaxHeight = 4096;

        /// <summary>Default gap threshold: stack when consecutive islands are farther than this.</summary>
        public const int DefaultAutoStackGapPx = 8;

        /// <summary>
        /// Radius of the outer bullseye ring, scaled to island size.
        /// </summary>
        public static int MarkerRadiusFor(IReadOnlyList<Rectangle> regions, int imageMinEdge)
        {
            int medianH = 24;
            if (regions != null && regions.Count > 0)
            {
                var hs = new int[regions.Count];
                for (int i = 0; i < regions.Count; i++)
                    hs[i] = Math.Max(1, regions[i].Height);
                Array.Sort(hs);
                medianH = hs[hs.Length / 2];
            }

            // ~1/3 of island height, clamped so markers stay crisp but small.
            int r = Math.Max(6, Math.Min(18, medianH / 3));
            if (imageMinEdge > 0)
                r = Math.Min(r, Math.Max(6, imageMinEdge / 40));
            return r;
        }

        /// <summary>
        /// Outer paint radius beyond the nominal bullseye radius (white halo).
        /// Placement must reserve this so the painted mark never clips text.
        /// </summary>
        public static int MarkerHaloExtra(int markerRadius) =>
            Math.Max(2, Math.Max(4, markerRadius) / 5);

        /// <summary>Full disc radius including halo used for collision tests.</summary>
        public static int MarkerFootprintRadius(int markerRadius) =>
            Math.Max(4, markerRadius) + MarkerHaloExtra(markerRadius);

        /// <summary>
        /// Place marker centers strictly outside every text island. Priority:
        /// upper-left (NW), left, top-middle — only then right / below as last resort.
        /// If no free slot, omit the marker (never cover text).
        /// Kept for optional legacy bullseye use; full-page POI prefers region boxes.
        /// </summary>
        public static List<Point> PlaceMarkerCenters(
            IReadOnlyList<Rectangle> regions,
            int imageW,
            int imageH,
            int markerRadius)
        {
            var centers = new List<Point>(regions?.Count ?? 0);
            if (regions == null || regions.Count == 0 || imageW < 4 || imageH < 4)
                return centers;

            int r = Math.Max(4, markerRadius);
            int foot = MarkerFootprintRadius(r);
            int margin = foot + 1;
            int baseGap = Math.Max(4, r / 2 + 2);

            for (int i = 0; i < regions.Count; i++)
            {
                var box = regions[i];
                if (box.Width < 2 || box.Height < 2)
                    continue;

                int cy = box.Y + box.Height / 2;
                int cxMid = box.X + box.Width / 2;
                // Upper-left of the rect (outside, slightly above-left of top-left corner).
                int yTopBand = box.Y + Math.Min(box.Height / 4, Math.Max(8, r));

                Point chosen = default;
                bool found = false;

                for (int ring = 0; ring < 3 && !found; ring++)
                {
                    int gap = baseGap + ring * Math.Max(6, r);
                    int d = gap + foot;
                    // Preferred first, last-ditch last.
                    var candidates = new[]
                    {
                        new Point(box.X - d, box.Y - d),              // 1 upper-left (NW)
                        new Point(box.X - d, yTopBand),               // 1b upper-left along left edge
                        new Point(box.X - d, cy),                     // 2 left mid
                        new Point(cxMid, box.Y - d),                  // 3 top middle
                        new Point(box.Right - 1 + d, box.Y - d),      // NE (still top-ish)
                        new Point(box.Right - 1 + d, yTopBand),       // right upper
                        // Last ditch: right mid / below
                        new Point(box.Right - 1 + d, cy),             // right
                        new Point(cxMid, box.Bottom - 1 + d),         // below
                        new Point(box.X - d, box.Bottom - 1 + d),     // SW
                        new Point(box.Right - 1 + d, box.Bottom - 1 + d), // SE
                    };

                    foreach (var c in candidates)
                    {
                        if (c.X < margin || c.Y < margin ||
                            c.X > imageW - 1 - margin || c.Y > imageH - 1 - margin)
                            continue;
                        if (MarkerHitsAnyText(c, foot, regions))
                            continue;
                        if (MarkerHitsPriorCenters(c, foot, centers))
                            continue;
                        chosen = c;
                        found = true;
                        break;
                    }
                }

                if (found)
                    centers.Add(chosen);
            }

            return centers;
        }

        /// <summary>
        /// True if the marker footprint disc intersects any text island rectangle.
        /// Checks <b>all</b> regions (including the owner) — text must stay clear.
        /// </summary>
        public static bool MarkerHitsAnyText(
            Point center,
            int footprintRadius,
            IReadOnlyList<Rectangle> regions)
        {
            int R = Math.Max(1, footprintRadius);
            // Axis-aligned disc bounds with 1px safety.
            var marker = new Rectangle(
                center.X - R - 1,
                center.Y - R - 1,
                R * 2 + 2,
                R * 2 + 2);
            for (int i = 0; i < regions.Count; i++)
            {
                var box = regions[i];
                if (box.Width < 1 || box.Height < 1)
                    continue;
                if (marker.IntersectsWith(box))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Edge-to-edge gap between two boxes (0 if they overlap/touch).
        /// Uses axis separation; diagonal when separated on both axes.
        /// </summary>
        public static int BoxSeparationPx(Rectangle a, Rectangle b)
        {
            int dx = 0;
            if (a.Right < b.Left)
                dx = b.Left - a.Right;
            else if (b.Right < a.Left)
                dx = a.Left - b.Right;

            int dy = 0;
            if (a.Bottom < b.Top)
                dy = b.Top - a.Bottom;
            else if (b.Bottom < a.Top)
                dy = a.Top - b.Bottom;

            if (dx == 0 && dy == 0)
                return 0;
            if (dx == 0)
                return dy;
            if (dy == 0)
                return dx;
            return (int)Math.Round(Math.Sqrt((double)dx * dx + (double)dy * dy));
        }

        /// <summary>
        /// Largest gap between consecutive boxes in list order (reading order).
        /// </summary>
        public static int MaxConsecutiveSeparation(IReadOnlyList<Rectangle> boxes)
        {
            if (boxes == null || boxes.Count < 2)
                return 0;
            int max = 0;
            for (int i = 0; i < boxes.Count - 1; i++)
                max = Math.Max(max, BoxSeparationPx(boxes[i], boxes[i + 1]));
            return max;
        }

        /// <summary>
        /// True when auto-stack should run: 2+ islands and either threshold is 0
        /// (always) or max consecutive gap exceeds <paramref name="gapThresholdPx"/>.
        /// </summary>
        public static bool ShouldAutoStack(
            IReadOnlyList<Rectangle> boxes,
            int gapThresholdPx,
            out int maxConsecutiveGap)
        {
            maxConsecutiveGap = MaxConsecutiveSeparation(boxes);
            if (boxes == null || boxes.Count < 2)
                return false;
            // 0 = always stack when 2+ islands (user "use stack all the time").
            if (gapThresholdPx <= 0)
                return true;
            return maxConsecutiveGap > gapThresholdPx;
        }

        /// <summary>
        /// Vertical stack of island crops (reading order) with a white gap and a
        /// green guide box around each strip. Caller owns result.
        /// </summary>
        public static Bitmap? BuildVerticalStack(
            Bitmap source,
            IReadOnlyList<Rectangle> boxes,
            StringBuilder? detail = null,
            bool paintBullseyes = true,
            int stripGapPx = StackStripGapPx,
            int marginPx = 0)
        {
            if (source == null || boxes == null || boxes.Count == 0)
                return null;

            // paintBullseyes kept for call-site compat; stacks use green boxes instead.
            _ = paintBullseyes;

            var strips = new List<Bitmap>();
            try
            {
                var imgBounds = new Rectangle(0, 0, source.Width, source.Height);
                for (int i = 0; i < boxes.Count; i++)
                {
                    var hole = Rectangle.Intersect(boxes[i], imgBounds);
                    if (hole.Width < 4 || hole.Height < 4)
                    {
                        detail?.AppendLine($"  poi-stack strip[{i + 1}]: skip empty");
                        continue;
                    }

                    var strip = source.Clone(hole, PixelFormat.Format32bppArgb);
                    detail?.AppendLine(
                        $"  poi-stack strip[{i + 1}]: {strip.Width}x{strip.Height} " +
                        $"@({hole.X},{hole.Y})");
                    strips.Add(strip);
                }

                if (strips.Count == 0)
                    return null;

                int margin = Math.Max(0, marginPx);
                int contentW = strips.Max(s => s.Width);
                int gap = Math.Max(0, stripGapPx);
                int contentH = strips.Sum(s => s.Height) + gap * Math.Max(0, strips.Count - 1);
                // Outer margin on all four sides of the final canvas.
                int width = contentW + margin * 2;
                int rawH = contentH + margin * 2;
                double fit = 1.0;
                if (rawH > StackMaxHeight)
                    fit = (double)StackMaxHeight / rawH;

                int outW = Math.Max(1, (int)Math.Round(width * fit));
                int outH = Math.Max(1, (int)Math.Round(rawH * fit));
                const int stackMaxLongEdge = 2560;
                int maxEdge = Math.Max(outW, outH);
                if (maxEdge > stackMaxLongEdge)
                {
                    double edgeFit = (double)stackMaxLongEdge / maxEdge;
                    outW = Math.Max(1, (int)Math.Round(outW * edgeFit));
                    outH = Math.Max(1, (int)Math.Round(outH * edgeFit));
                    fit *= edgeFit;
                }

                var canvas = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    float m = (float)(margin * fit);
                    float y = m;
                    float contentDrawW = Math.Max(1f, outW - m * 2f);
                    for (int i = 0; i < strips.Count; i++)
                    {
                        var s = strips[i];
                        float dw = (float)(s.Width * fit);
                        float dh = (float)(s.Height * fit);
                        // Center strips in the content band (inside left/right margin).
                        float x = m + Math.Max(0, (contentDrawW - dw) * 0.5f);
                        g.DrawImage(s, x, y, dw, dh);

                        // Green guide box around the strip (stroke only — text untouched).
                        var box = Rectangle.Round(new RectangleF(x, y, dw, dh));
                        if (box.Width > 2 && box.Height > 2)
                            PaintOneGuideBox(g, box);

                        y += dh;
                        if (i < strips.Count - 1)
                            y += (float)(gap * fit);
                    }
                }

                detail?.AppendLine(
                    $"poi-stack composed: strips={strips.Count} {outW}x{outH} " +
                    $"(content={contentW}x{contentH} margin={margin}px gap={gap} " +
                    $"fit={fit:F3} green-boxes)");
                return canvas;
            }
            finally
            {
                foreach (var s in strips)
                {
                    try { s.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>
        /// Clone <paramref name="source"/>, optionally thick-fog outside islands, then
        /// stroke each island with a bright green guide box (Balloons-preview style).
        /// Stroke only — never fills or covers text interior. Caller owns the bitmap.
        /// </summary>
        public static Bitmap DrawRegionGuides(
            Bitmap source,
            IReadOnlyList<Rectangle> regions,
            StringBuilder? detail = null,
            bool fogOutside = false)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            var boxes = regions ?? Array.Empty<Rectangle>();

            Bitmap work;
            if (fogOutside && boxes.Count > 0)
            {
                work = FogOutsideRegions(
                    source, boxes, OutsideFogClearPadPx, detail);
            }
            else
            {
                work = (Bitmap)source.Clone();
                if (fogOutside && boxes.Count == 0)
                    detail?.AppendLine("poi-guide: outside-fog skipped (no islands)");
            }

            int drawn = 0;
            using (var g = Graphics.FromImage(work))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var imgBounds = new Rectangle(0, 0, work.Width, work.Height);
                foreach (var raw in boxes)
                {
                    var r = Rectangle.Intersect(raw, imgBounds);
                    if (r.Width < 4 || r.Height < 4)
                        continue;
                    PaintOneGuideBox(g, r);
                    drawn++;
                }
            }

            detail?.AppendLine(
                $"poi-guide: green-boxes={drawn}/{boxes.Count} " +
                $"(stroke only; text interior clear" +
                (fogOutside ? "; outside thick fog" : "") + ")");
            return work;
        }

        /// <summary>
        /// Legacy name — full-page POI now uses green region boxes (not bullseyes).
        /// </summary>
        public static Bitmap DrawBullseyes(
            Bitmap source,
            IReadOnlyList<Rectangle> regions,
            StringBuilder? detail = null,
            bool fogOutside = false)
            => DrawRegionGuides(source, regions, detail, fogOutside);

        /// <summary>
        /// Gold outer + bright green inner stroke around a region. Does not fill.
        /// </summary>
        public static void PaintOneGuideBox(Graphics g, Rectangle r)
        {
            if (r.Width < 2 || r.Height < 2)
                return;
            // Outer gold for contrast on gray fog / cream balloons.
            using (var outer = new Pen(GuideBoxOuter, 3.5f))
            {
                outer.Alignment = PenAlignment.Inset;
                g.DrawRectangle(outer, r);
            }
            using (var inner = new Pen(GuideBoxGreen, 2.25f))
            {
                inner.Alignment = PenAlignment.Inset;
                var inset = Rectangle.Inflate(r, -1, -1);
                if (inset.Width > 2 && inset.Height > 2)
                    g.DrawRectangle(inner, inset);
                else
                    g.DrawRectangle(inner, r);
            }
        }

        /// <summary>
        /// Thick gray fog over everything except clear holes for each region
        /// (plus a small pad). Keeps Local-LLM from reading art/UI outside islands.
        /// Caller owns the returned bitmap.
        /// </summary>
        public static Bitmap FogOutsideRegions(
            Bitmap source,
            IReadOnlyList<Rectangle> regions,
            int clearPadPx = OutsideFogClearPadPx,
            StringBuilder? detail = null,
            float amount = OutsideFogAmount,
            byte grayLevel = OutsideFogGrayLevel)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            amount = Math.Clamp(amount, 0f, 1f);
            int pad = Math.Max(0, clearPadPx);
            int w = source.Width;
            int h = source.Height;

            // 1) Fully fogged copy of the page.
            var fogged = BlendTowardGray(source, amount, grayLevel);

            if (regions == null || regions.Count == 0 || amount <= 0.001f)
            {
                detail?.AppendLine(
                    $"poi-outside-fog: full-frame only amount={amount:0.##} (no clear holes)");
                return fogged;
            }

            // 2) Punch clear windows from the original prep into the fog.
            using (var g = Graphics.FromImage(fogged))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                int holes = 0;
                foreach (var raw in regions)
                {
                    var hole = Rectangle.Intersect(
                        Rectangle.Inflate(raw, pad, pad),
                        new Rectangle(0, 0, w, h));
                    if (hole.Width < 2 || hole.Height < 2)
                        continue;

                    g.DrawImage(
                        source,
                        hole,
                        hole,
                        GraphicsUnit.Pixel);
                    holes++;
                }

                detail?.AppendLine(
                    $"poi-outside-fog: amount={amount:0.##} gray={grayLevel} " +
                    $"clearHoles={holes} pad={pad}px");
            }

            return fogged;
        }

        /// <summary>out = src*(1-amount) + gray*amount (32bpp ARGB).</summary>
        public static Bitmap BlendTowardGray(Bitmap source, float amount, byte grayLevel)
        {
            amount = Math.Clamp(amount, 0f, 1f);
            if (amount <= 0.001f)
                return (Bitmap)source.Clone();

            var src = source.PixelFormat == PixelFormat.Format32bppArgb
                ? source
                : source.Clone(
                    new Rectangle(0, 0, source.Width, source.Height),
                    PixelFormat.Format32bppArgb);
            bool disposeSrc = !ReferenceEquals(src, source);
            try
            {
                var result = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, src.Width, src.Height);
                var srcData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                float keep = 1f - amount;
                float g = grayLevel;
                try
                {
                    unsafe
                    {
                        byte* s0 = (byte*)srcData.Scan0;
                        byte* d0 = (byte*)dstData.Scan0;
                        int sStride = srcData.Stride;
                        int dStride = dstData.Stride;
                        int w = src.Width;
                        int h = src.Height;
                        for (int y = 0; y < h; y++)
                        {
                            byte* s = s0 + y * sStride;
                            byte* d = d0 + y * dStride;
                            for (int x = 0; x < w; x++)
                            {
                                int i = x * 4;
                                d[i] = (byte)Math.Clamp(
                                    (int)(s[i] * keep + g * amount + 0.5f), 0, 255);
                                d[i + 1] = (byte)Math.Clamp(
                                    (int)(s[i + 1] * keep + g * amount + 0.5f), 0, 255);
                                d[i + 2] = (byte)Math.Clamp(
                                    (int)(s[i + 2] * keep + g * amount + 0.5f), 0, 255);
                                d[i + 3] = s[i + 3];
                            }
                        }
                    }
                }
                finally
                {
                    src.UnlockBits(srcData);
                    result.UnlockBits(dstData);
                }

                return result;
            }
            finally
            {
                if (disposeSrc)
                    src.Dispose();
            }
        }

        /// <summary>
        /// Paint one vivid red bullseye on (usually) ink-gray prep: gold outer ring,
        /// red disc, white core. Color is intentional — gray already ran.
        /// </summary>
        public static void PaintOneBullseye(Graphics g, Point center, int radius)
        {
            int r = Math.Max(4, radius);
            // Soft pale halo so the mark lifts off dark gray art.
            int rHalo = r + Math.Max(2, r / 5);
            using (var brush = new SolidBrush(Color.FromArgb(210, 255, 255, 255)))
            {
                g.FillEllipse(
                    brush,
                    center.X - rHalo, center.Y - rHalo, rHalo * 2, rHalo * 2);
            }

            // Outer gold ring for contrast on both light and dark gray.
            using (var pen = new Pen(Color.FromArgb(255, 255, 210, 40), Math.Max(2f, r * 0.30f)))
            {
                g.DrawEllipse(pen, center.X - r, center.Y - r, r * 2, r * 2);
            }

            // Solid red disc
            int rMid = Math.Max(3, (int)(r * 0.72));
            using (var brush = new SolidBrush(Color.FromArgb(255, 230, 28, 28)))
            {
                g.FillEllipse(brush, center.X - rMid, center.Y - rMid, rMid * 2, rMid * 2);
            }

            // Thin dark outline so red still reads if gray was light.
            using (var pen = new Pen(Color.FromArgb(220, 20, 20, 20), 1.25f))
            {
                g.DrawEllipse(pen, center.X - rMid, center.Y - rMid, rMid * 2, rMid * 2);
            }

            // White core (classic bullseye)
            int rCore = Math.Max(2, r / 4);
            using (var brush = new SolidBrush(Color.White))
            {
                g.FillEllipse(
                    brush,
                    center.X - rCore, center.Y - rCore, rCore * 2, rCore * 2);
            }
        }

        private static bool MarkerHitsPriorCenters(
            Point center,
            int radius,
            List<Point> prior)
        {
            int minDistSq = (radius * 2 + 2) * (radius * 2 + 2);
            foreach (var p in prior)
            {
                int dx = p.X - center.X;
                int dy = p.Y - center.Y;
                if (dx * dx + dy * dy < minDistSq)
                    return true;
            }
            return false;
        }
    }
}
