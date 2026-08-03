using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace SpeakRect
{
    /// <summary>
    /// Pure comic / balloon geometry: merge-overlap (grow + crop pad) and Western
    /// reading-order sort. No WinRT OCR, no Local-LLM HTTP.
    /// Best-of / residual fusion lives in <see cref="ComicBestOfFusion"/>;
    /// diversified-decode voting in <see cref="ComicConsensus"/>.
    /// </summary>
    public static class ComicRegionGeometry
    {
        /// <summary>
        /// Local-LLM crop under-read vs OCR detect for the same island (common when one
        /// mega box holds several balloons and the model starts mid-panel).
        /// </summary>
        public static bool KoboldUnderReadsWinOcr(string? kobold, string? winOcr)
        {
            int wK = CountWords(kobold);
            int wW = CountWords(winOcr);
            if (wW < 10)
                return false;
            // Need a clear gap — not a small wording difference.
            int minKeep = Math.Max(8, (int)Math.Ceiling(wW * 0.55));
            return wK < minKeep;
        }

        /// <summary>Word count used by under-read and usability heuristics.</summary>
        public static int CountWords(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return 0;
            int n = 0;
            bool inWord = false;
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (!inWord)
                    {
                        n++;
                        inWord = true;
                    }
                }
                else
                    inWord = false;
            }
            return n;
        }
        /// <summary>
        /// Effective box used only for merge-overlap tests: grow-inflated
        /// <paramref name="bounds"/> plus unclamped Crop pad (same pad that green
        /// dashed outlines / OCR crops use). Neighbor clamping is intentionally
        /// skipped here — if pads would meet, the islands should merge instead.
        /// </summary>
        public static Rectangle ExpandBoundsForMergeOverlapTest(
            Rectangle bounds, int capW, int capH, int cropPadPx)
        {
            if (bounds.Width < 1 || bounds.Height < 1)
                return bounds;
            if (cropPadPx <= 0)
            {
                var c = bounds;
                c.Intersect(new Rectangle(0, 0, capW, capH));
                return c;
            }
            var e = Rectangle.Inflate(bounds, cropPadPx, cropPadPx);
            e.Intersect(new Rectangle(0, 0, capW, capH));
            return e.Width < 1 || e.Height < 1 ? bounds : e;
        }

        /// <summary>
        /// Union any pair of islands whose effective boxes overlap (grow bounds +
        /// Crop pad; positive area intersection). Transitive: A∩B and B∩C become
        /// one union of the stored (grow) bounds covering all three so nothing is
        /// crop-cut. Does not merge when even padded boxes stay separated by a gap.
        /// Merge-on-pad always applies (Settings → Balloons).
        /// </summary>
        public static List<DetectedTextRegion> MergeOverlappingIslands(
            List<DetectedTextRegion> regions,
            int capW,
            int capH,
            int? cropPadOverride = null)
        {
            if (regions.Count <= 1)
                return regions;

            // Honor Crop pad (and grow already baked into Bounds from ImproveDetectedRegions)
            // so merge follows the same box rules as green preview / OCR crops.
            int cropPad = Math.Max(0, cropPadOverride ?? 0);

            int n = regions.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a != b) parent[b] = a;
            }

            // Precompute effective (grow + pad) boxes for O(n²) overlap tests.
            var effective = new Rectangle[n];
            for (int i = 0; i < n; i++)
            {
                effective[i] = ExpandBoundsForMergeOverlapTest(
                    regions[i].Bounds, capW, capH, cropPad);
            }

            for (int i = 0; i < n; i++)
            {
                var a = effective[i];
                if (a.Width < 1 || a.Height < 1)
                    continue;
                for (int j = i + 1; j < n; j++)
                {
                    var b = effective[j];
                    if (b.Width < 1 || b.Height < 1)
                        continue;
                    var inter = Rectangle.Intersect(a, b);
                    if (inter.Width > 0 && inter.Height > 0)
                        Union(i, j);
                }
            }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!groups.TryGetValue(r, out var list))
                {
                    list = new List<int>();
                    groups[r] = list;
                }
                list.Add(i);
            }

            if (groups.Count == n)
                return regions;

            var frame = new Rectangle(0, 0, capW, capH);
            var blocks = new List<DetectedTextRegion>(groups.Count);
            foreach (var idxs in groups.Values)
            {
                var ordered = idxs
                    .OrderBy(i => regions[i].Bounds.Top + regions[i].Bounds.Height / 2.0)
                    .ThenBy(i => regions[i].Bounds.Left)
                    .ToList();

                Rectangle bounds = regions[ordered[0]].Bounds;
                var texts = new List<string>();
                foreach (int i in ordered)
                {
                    bounds = Rectangle.Union(bounds, regions[i].Bounds);
                    if (!string.IsNullOrWhiteSpace(regions[i].WinOcrText))
                        texts.Add(regions[i].WinOcrText.Trim());
                }

                bounds.Intersect(frame);
                if (bounds.Width < 1 || bounds.Height < 1)
                    continue;

                blocks.Add(new DetectedTextRegion
                {
                    Bounds = bounds,
                    WinOcrText = string.Join(" ", texts)
                });
            }

            return blocks.Count > 0 ? blocks : regions;
        }

        /// <summary>
        /// Western comic order (geometry only): rows top→bottom, left→right in each row.
        /// Greedy row pick from the current topmost island; same-row is vertical
        /// geometry only (Y overlap / top proximity) so grow-pad X-overlap cannot
        /// split side-by-side balloons into “right first”. Nested caption strips
        /// stay their own row so the higher strip still precedes an inner balloon.
        /// </summary>
        public static List<DetectedTextRegion> SortComicReadingOrderRegions(
            List<DetectedTextRegion> regions)
        {
            if (regions.Count <= 1)
                return regions.ToList();

            return SortComicReadingOrderByRows(regions);
        }

        /// <summary>
        /// Reading-order "visual top" - biased toward the box top edge so full-width
        /// caption strips (top-0) sort before balloons that sit lower even when their
        /// centers land mid-strip.
        /// </summary>
        public static double ReadingOrderTopKey(Rectangle b)
            => b.Top + b.Height * 0.12;

        /// <summary>
        /// True when two boxes share a Western comic reading row (side-by-side
        /// balloons). Uses vertical geometry only — intentional: after Grow pad,
        /// neighbors often overlap in X and must still L→R together.
        /// Nested strip/balloon pairs are never the same row.
        /// </summary>
        public static bool IsSameReadingRow(Rectangle a, Rectangle b)
        {
            if (BoxesNestedOrMostlyContained(a, b))
                return false;

            // Completely stacked with a real gap → different rows.
            if (a.Bottom <= b.Top || b.Bottom <= a.Top)
                return false;

            double minH = Math.Max(1.0, Math.Min(a.Height, b.Height));
            double maxH = Math.Max(a.Height, b.Height);
            double aCy = a.Top + a.Height / 2.0;
            double bCy = b.Top + b.Height / 2.0;
            double yOverlap = Math.Max(0.0,
                Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
            double topDelta = Math.Abs(a.Top - b.Top);

            // Solid vertical overlap (side-by-side, any heights).
            if (yOverlap / minH >= 0.18)
                return true;

            // Tops nearly aligned.
            if (topDelta <= Math.Max(22.0, minH * 0.45))
                return true;

            // Centers on the same band (taller peer vs short elevated reply).
            if (Math.Abs(aCy - bCy) <= maxH * 0.55)
                return true;

            // One box’s top sits inside the other’s vertical body.
            if (a.Top >= b.Top && a.Top <= b.Bottom - minH * 0.12)
                return true;
            if (b.Top >= a.Top && b.Top <= a.Bottom - minH * 0.12)
                return true;

            return false;
        }

        /// <summary>
        /// True when two boxes are clear left/right dialogue peers on roughly the
        /// same reading row (side-by-side balloons). Used by stack preference.
        /// </summary>
        public static bool AreHorizontalReadingPeers(Rectangle a, Rectangle b)
        {
            if (!IsSameReadingRow(a, b))
                return false;

            double minW = Math.Max(1.0, Math.Min(a.Width, b.Width));
            double aCx = a.Left + a.Width / 2.0;
            double bCx = b.Left + b.Width / 2.0;

            // Clear horizontal center separation (same column → not L/R peers).
            if (Math.Abs(aCx - bCx) < minW * 0.22)
                return false;

            return true;
        }

        /// <summary>
        /// Greedy Western reading rows: pick topmost remaining island, grow its row
        /// transitively via <see cref="IsSameReadingRow"/>, emit L→R, repeat.
        /// </summary>
        public static List<DetectedTextRegion> SortComicReadingOrderByRows(
            List<DetectedTextRegion> regions)
        {
            if (regions.Count <= 1)
                return regions.ToList();

            // Stable seed: top then left (so topmost anchor is well-defined).
            var remaining = regions
                .OrderBy(r => ReadingOrderTopKey(r.Bounds))
                .ThenBy(r => r.Bounds.Left)
                .ToList();

            var ordered = new List<DetectedTextRegion>(regions.Count);
            while (remaining.Count > 0)
            {
                var anchor = remaining[0];
                var row = new List<DetectedTextRegion> { anchor };
                remaining.RemoveAt(0);

                // Grow row transitively until no more same-row peers join.
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    for (int i = remaining.Count - 1; i >= 0; i--)
                    {
                        var cand = remaining[i];
                        bool joins = row.Any(x =>
                            IsSameReadingRow(x.Bounds, cand.Bounds));
                        if (!joins)
                            continue;
                        row.Add(cand);
                        remaining.RemoveAt(i);
                        grew = true;
                    }
                }

                // Within row: left → right (Left edge; stable for inflated boxes).
                foreach (var r in row.OrderBy(x => x.Bounds.Left)
                             .ThenBy(x => ReadingOrderTopKey(x.Bounds)))
                    ordered.Add(r);
            }

            // Light stack preference only for 3+ islands (column continuity).
            if (ordered.Count <= 2)
                return ordered;
            return ApplyLightStackPreference(ordered);
        }

        /// <summary>
        /// True when one rect is largely nested inside the other (geometry peers
        /// for L/R / same-row would mis-order caption strips vs inner balloons).
        /// </summary>
        public static bool BoxesNestedOrMostlyContained(Rectangle a, Rectangle b)
        {
            var inter = Rectangle.Intersect(a, b);
            if (inter.Width <= 0 || inter.Height <= 0)
                return false;

            double ia = inter.Width * (double)inter.Height;
            double aa = Math.Max(1.0, a.Width * (double)a.Height);
            double ba = Math.Max(1.0, b.Width * (double)b.Height);
            // Smaller box mostly inside the larger
            if (ia >= Math.Min(aa, ba) * 0.55)
                return true;

            // Wide strip vertically covers a shorter balloon (Y-containment) even if
            // the balloon only uses part of the strip's width.
            bool aWide = a.Width >= b.Width * 1.8;
            bool bWide = b.Width >= a.Width * 1.8;
            double yOverlap = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
            if (aWide && yOverlap >= b.Height * 0.70 && a.Top <= b.Top + 8)
                return true;
            if (bWide && yOverlap >= a.Height * 0.70 && b.Top <= a.Top + 8)
                return true;

            return false;
        }

        /// <summary>
        /// Light stack preference on top of band order.
        /// <list type="bullet">
        /// <item>Same-column stack: upper before lower.</item>
        /// <item>Same-row: left before right.</item>
        /// <item>Nested strip vs inner balloon: higher top first.</item>
        /// </list>
        /// </summary>
        public static List<DetectedTextRegion> ApplyLightStackPreference(
            List<DetectedTextRegion> bandOrdered)
        {
            int n = bandOrdered.Count;
            if (n <= 2)
                return bandOrdered;

            // Fixed index space matching bandOrdered (tie-break uses this order).
            var boxes = new Rectangle[n];
            for (int i = 0; i < n; i++)
                boxes[i] = bandOrdered[i].Bounds;

            // Edge i ? j means i must be spoken before j.
            var succ = new List<int>[n];
            var indeg = new int[n];
            for (int i = 0; i < n; i++)
                succ[i] = new List<int>();

            void AddBefore(int earlier, int later)
            {
                if (earlier == later) return;
                // Avoid duplicate edges
                if (succ[earlier].Contains(later)) return;
                succ[earlier].Add(later);
                indeg[later]++;
            }

            // Pairwise: higher top edge first, then stack + same-row (geometry only).
            double medianH = boxes.Select(b => (double)b.Height).OrderBy(h => h)
                .ElementAt(n / 2);
            int topSlack = Math.Max(24, (int)(medianH * 0.35));

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    // Nested strip vs inner balloon: higher top always first.
                    if (BoxesNestedOrMostlyContained(boxes[i], boxes[j]))
                    {
                        if (boxes[i].Top + topSlack / 2 < boxes[j].Top)
                            AddBefore(i, j);
                        else if (boxes[j].Top + topSlack / 2 < boxes[i].Top)
                            AddBefore(j, i);
                        else if (ReadingOrderTopKey(boxes[i]) <= ReadingOrderTopKey(boxes[j]))
                            AddBefore(i, j);
                        else
                            AddBefore(j, i);
                        continue;
                    }

                    // Side-by-side balloons: L→R owns order (do not let a slightly
                    // higher right balloon force right-before-left via top-tier).
                    bool sidePeers = AreHorizontalReadingPeers(boxes[i], boxes[j]);
                    if (sidePeers)
                    {
                        double iCx = boxes[i].Left + boxes[i].Width / 2.0;
                        double jCx = boxes[j].Left + boxes[j].Width / 2.0;
                        if (iCx <= jCx) AddBefore(i, j);
                        else AddBefore(j, i);
                        continue;
                    }

                    // Clear top-tier separation (not nested / not L-R peers)
                    if (boxes[i].Top + topSlack < boxes[j].Top)
                        AddBefore(i, j);
                    else if (boxes[j].Top + topSlack < boxes[i].Top)
                        AddBefore(j, i);

                    if (IsVerticalStackPair(boxes[i], boxes[j], out bool iAboveJ))
                    {
                        if (iAboveJ) AddBefore(i, j);
                        else AddBefore(j, i);
                    }

                    if (IsSameRowLeftRight(boxes[i], boxes[j], out bool iLeftOfJ))
                    {
                        if (iLeftOfJ) AddBefore(i, j);
                        else AddBefore(j, i);
                    }
                }
            }

            // Kahn topo; when several are ready, keep band order (stable L→R / top bias)
            var ready = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (indeg[i] == 0)
                    ready.Add(i);
            }

            var result = new List<DetectedTextRegion>(n);
            var placed = new bool[n];
            while (ready.Count > 0)
            {
                // Lowest band index among ready = prefer original band order
                ready.Sort();
                int u = ready[0];
                ready.RemoveAt(0);
                if (placed[u]) continue;
                placed[u] = true;
                result.Add(bandOrdered[u]);

                foreach (int v in succ[u])
                {
                    indeg[v]--;
                    if (indeg[v] == 0 && !placed[v])
                        ready.Add(v);
                }
            }

            // Cycle / incomplete (should be rare) - fall back to band order
            if (result.Count != n)
                return bandOrdered;

            return result;
        }

        /// <summary>
        /// True when <paramref name="a"/> and <paramref name="b"/> form a vertical
        /// speech stack (same column). <paramref name="aAboveB"/> is set when a is
        /// the upper balloon.
        /// </summary>
        public static bool IsVerticalStackPair(
            Rectangle a, Rectangle b, out bool aAboveB)
        {
            aAboveB = false;
            // Nested strip / contained balloon is not a column stack pair
            if (BoxesNestedOrMostlyContained(a, b))
                return false;

            double aCy = a.Top + a.Height / 2.0;
            double bCy = b.Top + b.Height / 2.0;
            double minH = Math.Max(1.0, Math.Min(a.Height, b.Height));
            double minW = Math.Max(1.0, Math.Min(a.Width, b.Width));

            // Need clear vertical separation (not the same row)
            double centerDy = Math.Abs(aCy - bCy);
            if (centerDy < minH * 0.28)
                return false;

            // Horizontal overlap as a fraction of the narrower box
            double xOverlap = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
            if (xOverlap / minW < 0.40)
                return false;

            // Centers should not be side-by-side dominant
            double aCx = a.Left + a.Width / 2.0;
            double bCx = b.Left + b.Width / 2.0;
            if (Math.Abs(aCx - bCx) > minW * 0.85 && xOverlap / minW < 0.55)
                return false;

            aAboveB = aCy < bCy;
            return true;
        }

        /// <summary>
        /// True when boxes share a row and one is clearly left of the other.
        /// Rejects nested / strip-vs-inner pairs (geometry only - no color).
        /// Side-by-side dialogue peers always count even when tops differ modestly.
        /// </summary>
        public static bool IsSameRowLeftRight(
            Rectangle a, Rectangle b, out bool aLeftOfB)
        {
            aLeftOfB = false;

            // Nested or wide-strip-covering-short-balloon → not L/R peers.
            if (BoxesNestedOrMostlyContained(a, b))
                return false;

            double aCx = a.Left + a.Width / 2.0;
            double bCx = b.Left + b.Width / 2.0;
            double minW = Math.Max(1.0, Math.Min(a.Width, b.Width));
            if (Math.Abs(aCx - bCx) < minW * 0.20)
                return false;

            // Explicit L/R dialogue peers (handles elevated short right balloon).
            if (AreHorizontalReadingPeers(a, b))
            {
                aLeftOfB = aCx < bCx;
                return true;
            }

            double aCy = a.Top + a.Height / 2.0;
            double bCy = b.Top + b.Height / 2.0;
            double minH = Math.Max(1.0, Math.Min(a.Height, b.Height));
            double maxH = Math.Max(a.Height, b.Height);

            double yOverlap = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
            double yOverlapRatio = yOverlap / minH;
            double centerDy = Math.Abs(aCy - bCy);

            // Distinct top tiers → not the same reading row
            double topDelta = Math.Abs(a.Top - b.Top);
            if (topDelta >= Math.Max(40.0, minH * 0.55) &&
                topDelta >= maxH * 0.18)
                return false;

            bool sameRow =
                centerDy <= maxH * 0.55 ||
                yOverlapRatio >= 0.35;
            if (!sameRow)
                return false;

            aLeftOfB = aCx < bCx;
            return true;
        }
    }
}