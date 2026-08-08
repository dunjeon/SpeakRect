using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace SpeakRect
{
    /// <summary>
    /// Pure comic / balloon geometry: merge-overlap (grow + crop pad), separate
    /// grow-overlaps (Balloons §4), and proximity-chain Western reading-order
    /// sort (upper-left seed, tight stacks + L→R peers). No WinRT OCR,
    /// no Local-LLM HTTP. Best-of / residual fusion lives in
    /// <see cref="ComicBestOfFusion"/>; diversified-decode voting in
    /// <see cref="ComicConsensus"/>.
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
        /// Balloons §4 (test): shrink grow-inflated islands that share positive-area
        /// overlap so each box stops at the other's border. Prefer carving the
        /// larger island. Never shrink past each island's WinOCR
        /// <paramref name="cores"/> (lettering stays covered). Multi-pass for
        /// multi-way overlaps. When cores themselves overlap, residual core
        /// overlap may remain (cannot invent non-overlap without cutting text).
        /// </summary>
        public static List<DetectedTextRegion> SeparateOverlappingIslands(
            List<DetectedTextRegion> regions,
            IReadOnlyList<Rectangle> cores,
            int capW,
            int capH)
        {
            if (regions == null || regions.Count <= 1)
                return regions ?? new List<DetectedTextRegion>();

            var boxes = regions.Select(r => r.Bounds).ToArray();
            var coreArr = new Rectangle[boxes.Length];
            for (int i = 0; i < boxes.Length; i++)
            {
                if (cores != null && i < cores.Count &&
                    cores[i].Width > 0 && cores[i].Height > 0)
                    coreArr[i] = cores[i];
                else
                    coreArr[i] = boxes[i];
            }

            var frame = new Rectangle(0, 0, capW, capH);

            Rectangle Floor(int idx, Rectangle proposed)
            {
                var c = coreArr[idx];
                int l = Math.Min(proposed.Left, c.Left);
                int t = Math.Min(proposed.Top, c.Top);
                int r = Math.Max(proposed.Right, c.Right);
                int b = Math.Max(proposed.Bottom, c.Bottom);
                var u = Rectangle.FromLTRB(l, t, r, b);
                u.Intersect(frame);
                return u.Width >= 1 && u.Height >= 1 ? u : proposed;
            }

            // Pairwise; several passes so multi-way overlaps settle.
            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                for (int i = 0; i < boxes.Length; i++)
                {
                    for (int j = i + 1; j < boxes.Length; j++)
                    {
                        var a = boxes[i];
                        var b = boxes[j];
                        var inter = Rectangle.Intersect(a, b);
                        if (inter.Width <= 0 || inter.Height <= 0)
                            continue;

                        double acx = a.Left + a.Width / 2.0;
                        double acy = a.Top + a.Height / 2.0;
                        double bcx = b.Left + b.Width / 2.0;
                        double bcy = b.Top + b.Height / 2.0;
                        double dx = Math.Abs(acx - bcx);
                        double dy = Math.Abs(acy - bcy);

                        long aArea = (long)a.Width * a.Height;
                        long bArea = (long)b.Width * b.Height;
                        // Prefer carving the larger island so small balloons keep
                        // their crop and still reach Local-LLM as separate islands.
                        bool preferShrinkA = aArea >= bArea * 3 / 2;
                        bool preferShrinkB = bArea >= aArea * 3 / 2;

                        // Core overlap residual: Floor can re-expand to core and
                        // leave a shared band — still try a clean mid-split.
                        var coreInter = Rectangle.Intersect(coreArr[i], coreArr[j]);
                        bool coresDisjoint =
                            coreInter.Width <= 0 || coreInter.Height <= 0;

                        if (dy >= dx)
                        {
                            // Stacked / vertical: shared horizontal band → cut Y.
                            bool aAbove = acy <= bcy;
                            if (aAbove)
                            {
                                if (TrySeparateVertical(
                                        i, j, a, b, coreArr, coresDisjoint,
                                        preferShrinkA, preferShrinkB,
                                        aIsUpper: true, Floor,
                                        out var na, out var nb))
                                {
                                    boxes[i] = na;
                                    boxes[j] = nb;
                                    changed = true;
                                }
                            }
                            else
                            {
                                if (TrySeparateVertical(
                                        j, i, b, a, coreArr, coresDisjoint,
                                        preferShrinkB, preferShrinkA,
                                        aIsUpper: true, Floor,
                                        out var nb2, out var na2))
                                {
                                    boxes[i] = na2;
                                    boxes[j] = nb2;
                                    changed = true;
                                }
                            }
                        }
                        else
                        {
                            // Side-by-side: cut X.
                            bool aLeft = acx <= bcx;
                            if (aLeft)
                            {
                                if (TrySeparateHorizontal(
                                        i, j, a, b, coreArr, coresDisjoint,
                                        preferShrinkA, preferShrinkB,
                                        aIsLeft: true, Floor,
                                        out var na, out var nb))
                                {
                                    boxes[i] = na;
                                    boxes[j] = nb;
                                    changed = true;
                                }
                            }
                            else
                            {
                                if (TrySeparateHorizontal(
                                        j, i, b, a, coreArr, coresDisjoint,
                                        preferShrinkB, preferShrinkA,
                                        aIsLeft: true, Floor,
                                        out var nb2, out var na2))
                                {
                                    boxes[i] = na2;
                                    boxes[j] = nb2;
                                    changed = true;
                                }
                            }
                        }
                    }
                }
                if (!changed)
                    break;
            }

            // Final hard clip when cores are disjoint: force zero-area intersection
            // by stopping each edge at the neighbor core (border = limit).
            for (int i = 0; i < boxes.Length; i++)
            {
                for (int j = i + 1; j < boxes.Length; j++)
                {
                    var coreInter = Rectangle.Intersect(coreArr[i], coreArr[j]);
                    if (coreInter.Width > 0 && coreInter.Height > 0)
                        continue; // cannot invent non-overlap without cutting text

                    var a = boxes[i];
                    var b = boxes[j];
                    var inter = Rectangle.Intersect(a, b);
                    if (inter.Width <= 0 || inter.Height <= 0)
                        continue;

                    double acx = a.Left + a.Width / 2.0;
                    double acy = a.Top + a.Height / 2.0;
                    double bcx = b.Left + b.Width / 2.0;
                    double bcy = b.Top + b.Height / 2.0;
                    if (Math.Abs(acy - bcy) >= Math.Abs(acx - bcx))
                    {
                        // Vertical fence at the facing core edges (or mid if tied).
                        if (acy <= bcy)
                        {
                            int fence = Math.Max(coreArr[i].Bottom, Math.Min(coreArr[j].Top, inter.Top + inter.Height / 2));
                            // Upper stops at fence; lower starts at fence.
                            boxes[i] = Floor(i, Rectangle.FromLTRB(a.Left, a.Top, a.Right, Math.Min(a.Bottom, fence)));
                            boxes[j] = Floor(j, Rectangle.FromLTRB(b.Left, Math.Max(b.Top, fence), b.Right, b.Bottom));
                        }
                        else
                        {
                            int fence = Math.Max(coreArr[j].Bottom, Math.Min(coreArr[i].Top, inter.Top + inter.Height / 2));
                            boxes[j] = Floor(j, Rectangle.FromLTRB(b.Left, b.Top, b.Right, Math.Min(b.Bottom, fence)));
                            boxes[i] = Floor(i, Rectangle.FromLTRB(a.Left, Math.Max(a.Top, fence), a.Right, a.Bottom));
                        }
                    }
                    else
                    {
                        if (acx <= bcx)
                        {
                            int fence = Math.Max(coreArr[i].Right, Math.Min(coreArr[j].Left, inter.Left + inter.Width / 2));
                            boxes[i] = Floor(i, Rectangle.FromLTRB(a.Left, a.Top, Math.Min(a.Right, fence), a.Bottom));
                            boxes[j] = Floor(j, Rectangle.FromLTRB(Math.Max(b.Left, fence), b.Top, b.Right, b.Bottom));
                        }
                        else
                        {
                            int fence = Math.Max(coreArr[j].Right, Math.Min(coreArr[i].Left, inter.Left + inter.Width / 2));
                            boxes[j] = Floor(j, Rectangle.FromLTRB(b.Left, b.Top, Math.Min(b.Right, fence), b.Bottom));
                            boxes[i] = Floor(i, Rectangle.FromLTRB(Math.Max(a.Left, fence), a.Top, a.Right, a.Bottom));
                        }
                    }
                }
            }

            var result = new List<DetectedTextRegion>(regions.Count);
            for (int i = 0; i < regions.Count; i++)
            {
                var b = Floor(i, boxes[i]);
                b.Intersect(frame);
                if (b.Width < 1 || b.Height < 1)
                    continue;
                result.Add(new DetectedTextRegion
                {
                    Bounds = b,
                    WinOcrText = regions[i].WinOcrText
                });
            }
            return result.Count > 0 ? result : regions;
        }

        /// <summary>
        /// Vertical split for upper/lower pair. <paramref name="upperIdx"/> is the
        /// visually higher island. Returns floored proposed boxes when a shrink applies.
        /// </summary>
        private static bool TrySeparateVertical(
            int upperIdx,
            int lowerIdx,
            Rectangle upper,
            Rectangle lower,
            Rectangle[] cores,
            bool coresDisjoint,
            bool preferShrinkUpper,
            bool preferShrinkLower,
            bool aIsUpper,
            Func<int, Rectangle, Rectangle> floor,
            out Rectangle newUpper,
            out Rectangle newLower)
        {
            _ = aIsUpper;
            newUpper = upper;
            newLower = lower;
            var inter = Rectangle.Intersect(upper, lower);
            if (inter.Width <= 0 || inter.Height <= 0)
                return false;

            // Prefer shrink larger: stop upper bottom at lower top (or core floor).
            if (preferShrinkUpper && !preferShrinkLower)
            {
                int newBottom = Math.Min(upper.Bottom, Math.Max(lower.Top, cores[upperIdx].Bottom));
                if (coresDisjoint)
                    newBottom = Math.Min(newBottom, cores[lowerIdx].Top);
                if (newBottom - upper.Top >= 8)
                {
                    newUpper = floor(upperIdx, Rectangle.FromLTRB(upper.Left, upper.Top, upper.Right, newBottom));
                    newLower = floor(lowerIdx, lower);
                    return true;
                }
            }
            if (preferShrinkLower && !preferShrinkUpper)
            {
                int newTop = Math.Max(lower.Top, Math.Min(upper.Bottom, cores[lowerIdx].Top));
                if (coresDisjoint)
                    newTop = Math.Max(newTop, cores[upperIdx].Bottom);
                if (lower.Bottom - newTop >= 8)
                {
                    newLower = floor(lowerIdx, Rectangle.FromLTRB(lower.Left, newTop, lower.Right, lower.Bottom));
                    newUpper = floor(upperIdx, upper);
                    return true;
                }
            }

            // Default: split at mid of intersection (or core fence when disjoint).
            int midY = inter.Top + inter.Height / 2;
            if (coresDisjoint)
            {
                int coreFence = Math.Max(cores[upperIdx].Bottom, Math.Min(cores[lowerIdx].Top, midY));
                midY = coreFence;
            }
            newUpper = floor(upperIdx, Rectangle.FromLTRB(upper.Left, upper.Top, upper.Right, Math.Min(upper.Bottom, midY)));
            newLower = floor(lowerIdx, Rectangle.FromLTRB(lower.Left, Math.Max(lower.Top, midY), lower.Right, lower.Bottom));
            return true;
        }

        /// <summary>
        /// Horizontal split for left/right pair. <paramref name="leftIdx"/> is the
        /// left island.
        /// </summary>
        private static bool TrySeparateHorizontal(
            int leftIdx,
            int rightIdx,
            Rectangle left,
            Rectangle right,
            Rectangle[] cores,
            bool coresDisjoint,
            bool preferShrinkLeft,
            bool preferShrinkRight,
            bool aIsLeft,
            Func<int, Rectangle, Rectangle> floor,
            out Rectangle newLeft,
            out Rectangle newRight)
        {
            _ = aIsLeft;
            newLeft = left;
            newRight = right;
            var inter = Rectangle.Intersect(left, right);
            if (inter.Width <= 0 || inter.Height <= 0)
                return false;

            if (preferShrinkLeft && !preferShrinkRight)
            {
                int newRightEdge = Math.Min(left.Right, Math.Max(right.Left, cores[leftIdx].Right));
                if (coresDisjoint)
                    newRightEdge = Math.Min(newRightEdge, cores[rightIdx].Left);
                if (newRightEdge - left.Left >= 8)
                {
                    newLeft = floor(leftIdx, Rectangle.FromLTRB(left.Left, left.Top, newRightEdge, left.Bottom));
                    newRight = floor(rightIdx, right);
                    return true;
                }
            }
            if (preferShrinkRight && !preferShrinkLeft)
            {
                int newLeftEdge = Math.Max(right.Left, Math.Min(left.Right, cores[rightIdx].Left));
                if (coresDisjoint)
                    newLeftEdge = Math.Max(newLeftEdge, cores[leftIdx].Right);
                if (right.Right - newLeftEdge >= 8)
                {
                    newRight = floor(rightIdx, Rectangle.FromLTRB(newLeftEdge, right.Top, right.Right, right.Bottom));
                    newLeft = floor(leftIdx, left);
                    return true;
                }
            }

            // Default: shrink left to mid (protect right balloon lettering edge).
            int midX = inter.Left + inter.Width / 2;
            if (coresDisjoint)
                midX = Math.Max(cores[leftIdx].Right, Math.Min(cores[rightIdx].Left, midX));
            int leftRight = Math.Min(left.Right, Math.Max(midX, cores[leftIdx].Right));
            newLeft = floor(leftIdx, Rectangle.FromLTRB(left.Left, left.Top, leftRight, left.Bottom));
            newRight = floor(rightIdx, right);
            // If left shrink alone did not clear, also pull right left edge to fence.
            var still = Rectangle.Intersect(newLeft, newRight);
            if (still.Width > 0 && still.Height > 0 && coresDisjoint)
            {
                int fence = Math.Max(cores[leftIdx].Right, Math.Min(cores[rightIdx].Left, midX));
                newRight = floor(rightIdx, Rectangle.FromLTRB(Math.Max(newRight.Left, fence), newRight.Top, newRight.Right, newRight.Bottom));
            }
            return true;
        }

        /// <summary>
        /// Snap Concept Mode: one axis-aligned envelope from the top-left of the
        /// highest/leftmost WinOCR island to the bottom-right of the lowest/rightmost.
        /// Combined text is joined in Western reading order. Caller applies Extra
        /// margin (<c>ComicRegionPadding</c>) as usual on the single box.
        /// </summary>
        public static List<DetectedTextRegion> CollapseToSnapEnvelope(
            List<DetectedTextRegion> regions,
            int capW,
            int capH)
        {
            if (regions == null || regions.Count <= 1)
                return regions ?? new List<DetectedTextRegion>();

            var ordered = SortComicReadingOrderRegions(regions);
            if (ordered.Count == 0)
                return regions;

            Rectangle bounds = ordered[0].Bounds;
            var texts = new List<string>();
            foreach (var r in ordered)
            {
                if (r.Bounds.Width > 0 && r.Bounds.Height > 0)
                    bounds = Rectangle.Union(bounds, r.Bounds);
                if (!string.IsNullOrWhiteSpace(r.WinOcrText))
                    texts.Add(r.WinOcrText.Trim());
            }

            bounds.Intersect(new Rectangle(0, 0, capW, capH));
            if (bounds.Width < 1 || bounds.Height < 1)
                return regions;

            return new List<DetectedTextRegion>
            {
                new DetectedTextRegion
                {
                    Bounds = bounds,
                    WinOcrText = string.Join(" ", texts)
                }
            };
        }

        /// <summary>
        /// Western comic order (geometry only). Starts at the upper-left island,
        /// then chains to nearby successors: tight vertical stacks (same column,
        /// small gap) and tight right peers (same band, L→R). When nothing is
        /// close enough, reseeds at the upper-left remaining island so distant
        /// balloons are not dragged forward by a false "same row" Y-overlap.
        /// Proximity is paramount; L→R still wins among equal neighbors.
        /// </summary>
        public static List<DetectedTextRegion> SortComicReadingOrderRegions(
            List<DetectedTextRegion> regions)
        {
            if (regions.Count <= 1)
                return regions.ToList();

            return SortComicReadingOrderByProximity(regions);
        }

        /// <summary>
        /// Reading-order "visual top" - biased toward the box top edge so full-width
        /// caption strips (top-0) sort before balloons that sit lower even when their
        /// centers land mid-strip.
        /// </summary>
        public static double ReadingOrderTopKey(Rectangle b)
            => b.Top + b.Height * 0.12;

        /// <summary>
        /// Edge-to-edge gaps (0 when intervals overlap). Proximity uses these, not
        /// centers — touching stacks/peers score as zero gap.
        /// </summary>
        public static void ReadingOrderEdgeGaps(
            Rectangle a, Rectangle b, out double gapX, out double gapY)
        {
            if (a.Right <= b.Left)
                gapX = b.Left - a.Right;
            else if (b.Right <= a.Left)
                gapX = a.Left - b.Right;
            else
                gapX = 0;

            if (a.Bottom <= b.Top)
                gapY = b.Top - a.Bottom;
            else if (b.Bottom <= a.Top)
                gapY = a.Top - b.Bottom;
            else
                gapY = 0;
        }

        /// <summary>
        /// Horizontal overlap as a fraction of the narrower width (0..1+).
        /// </summary>
        public static double ReadingOrderXOverlapRatio(Rectangle a, Rectangle b)
        {
            double ov = Math.Max(0,
                Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
            double minW = Math.Max(1.0, Math.Min(a.Width, b.Width));
            return ov / minW;
        }

        /// <summary>
        /// Vertical overlap as a fraction of the narrower height (0..1+).
        /// </summary>
        public static double ReadingOrderYOverlapRatio(Rectangle a, Rectangle b)
        {
            double ov = Math.Max(0,
                Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
            double minH = Math.Max(1.0, Math.Min(a.Height, b.Height));
            return ov / minH;
        }

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
        /// same reading row (side-by-side balloons).
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
        /// Proximity-chain Western reading order. Prefer tight column stacks and
        /// rightward peers over global row bands; reseed upper-left when the chain
        /// has no nearby successor.
        /// </summary>
        public static List<DetectedTextRegion> SortComicReadingOrderByProximity(
            List<DetectedTextRegion> regions)
        {
            if (regions.Count <= 1)
                return regions.ToList();

            var remaining = regions.ToList();
            var ordered = new List<DetectedTextRegion>(regions.Count);

            double medianScale = remaining
                .Select(r => (double)Math.Min(
                    Math.Max(1, r.Bounds.Width),
                    Math.Max(1, r.Bounds.Height)))
                .OrderBy(x => x)
                .ElementAt(remaining.Count / 2);
            medianScale = Math.Max(24.0, medianScale);

            DetectedTextRegion? current = null;
            while (remaining.Count > 0)
            {
                int pick;
                if (current == null)
                {
                    pick = IndexOfReadingOrderSeed(remaining);
                }
                else
                {
                    pick = IndexOfProximitySuccessor(
                        current.Bounds, remaining, medianScale);
                    if (pick < 0)
                        pick = IndexOfReadingOrderSeed(remaining);
                }

                current = remaining[pick];
                ordered.Add(current);
                remaining.RemoveAt(pick);
            }

            return ordered;
        }

        /// <summary>
        /// Legacy row-band sort kept for smoke/diagnostics. Live path uses
        /// <see cref="SortComicReadingOrderByProximity"/>.
        /// </summary>
        public static List<DetectedTextRegion> SortComicReadingOrderByRows(
            List<DetectedTextRegion> regions)
        {
            if (regions.Count <= 1)
                return regions.ToList();

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

                foreach (var r in row.OrderBy(x => x.Bounds.Left)
                             .ThenBy(x => ReadingOrderTopKey(x.Bounds)))
                    ordered.Add(r);
            }

            return ordered;
        }

        /// <summary>
        /// Upper-left seed: leftmost island in the top band (so a slightly elevated
        /// right reply does not beat a left main balloon).
        /// </summary>
        public static int IndexOfReadingOrderSeed(
            IReadOnlyList<DetectedTextRegion> regions)
        {
            if (regions == null || regions.Count == 0)
                return -1;
            if (regions.Count == 1)
                return 0;

            double medH = regions
                .Select(r => (double)Math.Max(1, r.Bounds.Height))
                .OrderBy(h => h)
                .ElementAt(regions.Count / 2);
            double slack = Math.Max(36.0, medH * 0.55);
            double minTop = regions.Min(r => ReadingOrderTopKey(r.Bounds));

            int best = -1;
            int bestLeft = int.MaxValue;
            double bestTop = double.MaxValue;
            for (int i = 0; i < regions.Count; i++)
            {
                var b = regions[i].Bounds;
                double t = ReadingOrderTopKey(b);
                if (t > minTop + slack)
                    continue;
                if (b.Left < bestLeft ||
                    (b.Left == bestLeft && t < bestTop))
                {
                    best = i;
                    bestLeft = b.Left;
                    bestTop = t;
                }
            }

            if (best >= 0)
                return best;

            // Fallback: pure top then left.
            best = 0;
            bestTop = ReadingOrderTopKey(regions[0].Bounds);
            bestLeft = regions[0].Bounds.Left;
            for (int i = 1; i < regions.Count; i++)
            {
                var b = regions[i].Bounds;
                double t = ReadingOrderTopKey(b);
                if (t < bestTop - 0.01 ||
                    (Math.Abs(t - bestTop) < 0.01 && b.Left < bestLeft))
                {
                    best = i;
                    bestTop = t;
                    bestLeft = b.Left;
                }
            }
            return best;
        }

        /// <summary>
        /// Closest readable successor of <paramref name="from"/> among remaining
        /// islands, or -1 when nothing is near enough (caller reseeds).
        /// </summary>
        public static int IndexOfProximitySuccessor(
            Rectangle from,
            IReadOnlyList<DetectedTextRegion> remaining,
            double medianScale)
        {
            if (remaining == null || remaining.Count == 0)
                return -1;

            double scale = Math.Max(24.0, medianScale);
            int best = -1;
            double bestCost = double.MaxValue;

            for (int i = 0; i < remaining.Count; i++)
            {
                var to = remaining[i].Bounds;
                if (!TryReadingOrderStepCost(
                        from, to, scale, out double cost, out bool closeEnough))
                    continue;
                if (!closeEnough)
                    continue;
                bool better = cost < bestCost - 1e-9;
                if (!better && Math.Abs(cost - bestCost) < 1e-9 && best >= 0)
                {
                    var bb = remaining[best].Bounds;
                    better =
                        to.Left < bb.Left ||
                        (to.Left == bb.Left &&
                         ReadingOrderTopKey(to) < ReadingOrderTopKey(bb));
                }
                if (better || best < 0)
                {
                    bestCost = cost;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// Step cost from <paramref name="from"/> → <paramref name="to"/>.
        /// <paramref name="closeEnough"/> is false when the jump is too far to
        /// continue the chain (reseed instead).
        /// </summary>
        public static bool TryReadingOrderStepCost(
            Rectangle from,
            Rectangle to,
            double medianScale,
            out double cost,
            out bool closeEnough)
        {
            cost = double.MaxValue;
            closeEnough = false;

            if (from.Width < 1 || from.Height < 1 || to.Width < 1 || to.Height < 1)
                return false;

            double scale = Math.Max(
                24.0,
                Math.Min(
                    medianScale,
                    Math.Min(
                        Math.Min(from.Width, from.Height),
                        Math.Min(to.Width, to.Height))));

            ReadingOrderEdgeGaps(from, to, out double gapX, out double gapY);
            double prox = Math.Sqrt(gapX * gapX + gapY * gapY);
            double xOv = ReadingOrderXOverlapRatio(from, to);
            double yOv = ReadingOrderYOverlapRatio(from, to);

            double fromCx = from.Left + from.Width / 2.0;
            double toCx = to.Left + to.Width / 2.0;
            double fromCy = from.Top + from.Height / 2.0;
            double toCy = to.Top + to.Height / 2.0;

            bool nested = BoxesNestedOrMostlyContained(from, to);

            // --- Tight vertical stack (same column, below, small gap) ---
            bool stackDown =
                !nested &&
                toCy > fromCy &&
                xOv >= 0.40 &&
                gapY <= Math.Max(18.0, scale * 0.55) &&
                // Not a rightward peer that merely droops lower.
                (xOv >= 0.55 || Math.Abs(toCx - fromCx) <= scale * 0.85);

            // --- Tight right peer (same band, to the right) ---
            // Y-band uses mutual geometry so a tall balloon that only clips the
            // bottom of a left caption is NOT a peer (finish the column / reseed).
            double yOvPx = Math.Max(0.0,
                Math.Min(from.Bottom, to.Bottom) - Math.Max(from.Top, to.Top));
            double yOvFrom = yOvPx / Math.Max(1.0, from.Height);
            double yOvTo = yOvPx / Math.Max(1.0, to.Height);
            double topDelta = Math.Abs(from.Top - to.Top);
            bool sameReadingBand =
                topDelta <= Math.Max(22.0, scale * 0.45) ||
                Math.Abs(fromCy - toCy) <=
                    Math.Max(from.Height, to.Height) * 0.50 ||
                (yOvFrom >= 0.30 && yOvTo >= 0.30) ||
                yOvFrom >= 0.45 ||
                (gapY <= Math.Max(16.0, Math.Min(from.Height, to.Height) * 0.40) &&
                 topDelta <= Math.Max(40.0, scale * 0.70));

            bool rightPeer =
                !nested &&
                toCx > fromCx &&
                Math.Abs(toCx - fromCx) >=
                    Math.Max(1.0, Math.Min(from.Width, to.Width) * 0.18) &&
                gapX <= Math.Max(22.0, scale * 0.80) &&
                sameReadingBand;

            // A clear column stack should not also count as "right peer".
            if (stackDown && xOv >= 0.55 && to.Top >= from.Top - 4)
                rightPeer = false;

            // Nested strip / inner balloon: higher top first (small step if near).
            if (nested)
            {
                double nestTopDelta =
                    ReadingOrderTopKey(to) - ReadingOrderTopKey(from);
                // Prefer reading the higher box first — only accept downward/same nest.
                if (nestTopDelta < -Math.Max(12.0, scale * 0.2))
                    return false;
                cost = 0.15 + prox / scale +
                       Math.Max(0, nestTopDelta) / (scale * 4.0);
                closeEnough = prox <= Math.Max(36.0, scale * 1.1);
                return true;
            }

            if (stackDown && rightPeer)
            {
                // Comparable neighbors: prefer the closer edge; when equal/near,
                // stack wins so a left caption column is read before drifting right.
                if (gapY <= gapX * 1.15)
                {
                    cost = 0.08 + gapY / scale;
                    closeEnough = true;
                    return true;
                }

                cost = 0.18 + gapX / scale;
                closeEnough = true;
                return true;
            }

            if (stackDown)
            {
                cost = 0.08 + gapY / scale;
                closeEnough = true;
                return true;
            }

            if (rightPeer)
            {
                cost = 0.18 + gapX / scale;
                // Mild penalty when the right balloon sits clearly above (still L→R).
                if (to.Bottom < from.Top)
                    cost += 0.35;
                closeEnough = true;
                return true;
            }

            // General near neighbor (diagonal / moderate gap) — still local only.
            // Touching a tall balloon that only clips our bottom edge is NOT local
            // continuation; those islands are reached by reseed (upper-left remaining).
            double nearThr = Math.Max(28.0, scale * 0.70);
            if (prox > nearThr)
                return false;

            // Reject upward / leftward jumps and "above us" bulk.
            if (to.Bottom + Math.Max(8.0, scale * 0.15) < from.Top)
                return false;
            if (to.Right + Math.Max(8.0, scale * 0.15) < from.Left)
                return false;
            if (toCy < from.Top)
                return false;
            if (to.Top + scale * 0.45 < from.Top && yOvFrom < 0.50)
                return false;

            cost = 0.55 + prox / scale;
            if (toCx < fromCx)
                cost += 0.45 + (fromCx - toCx) / (scale * 3.0);
            if (toCy < fromCy)
                cost += 0.35 + (fromCy - toCy) / (scale * 3.0);
            closeEnough = true;
            return true;
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