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
    /// preview) on tone prep. Full-page map is for edit/Analytics — not always VL.
    /// Speak: Island canvases on → each island orange canvas VL one at a time;
    /// canvases off/fail multi → per-island tone crop VL; 1 island + canvases off →
    /// full-page guide VL. Canvas compose knobs: Balloons gap / margin / beef / bottom pad
    /// (stock 10 / 12 / 0 / 0). Optional Island Zoom enlarges small crops (Mag-style)
    /// before the orange canvas / VL send.
    /// </summary>
    public static class ComicPoiGuide
    {
        /// <summary>
        /// Built-in OCR prompt (same text as <see cref="AppSettings.DefaultOcrPrompt"/>).
        /// Live path uses <see cref="AppSettings.ResolveOcrPrompt"/> so Speech → Prompts
        /// overrides apply; this constant is the stock fallback only.
        /// </summary>
        public const string DefaultPrompt = AppSettings.DefaultOcrPrompt;

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

        /// <summary>Gap between auto-stacked POI strips (px).</summary>
        public const int StackStripGapPx = 8;

        /// <summary>
        /// Orange fill for the auto-stack canvas (margins + gaps between strips).
        /// Not white — separates from balloon paper so Local-LLM / preview read
        /// islands as distinct strips. Matches SpeakRect accent orange.
        /// </summary>
        public static readonly Color StackCanvasColor =
            Color.FromArgb(255, 240, 128, 24);

        /// <summary>
        /// Default extra canvas vs content (0 = no beef). Balloons can still retune.
        /// </summary>
        public const double DefaultStackBeefExtra = 0.0;

        /// <summary>
        /// Default share of vertical pad on the bottom (0 with beef 0 = tight canvas).
        /// Higher (0.8–0.9) = bottom-heavy when beef &gt; 0.
        /// </summary>
        public const double DefaultStackBottomPadShare = 0.0;

        /// <summary>
        /// Outer margin (px) on top/left/right/bottom of each Local-LLM island canvas.
        /// </summary>
        public const int LlmSendStackMarginPx = 12;

        /// <summary>Hard cap on auto-stack canvas height before scale-down.</summary>
        public const int StackMaxHeight = 4096;

        /// <summary>
        /// Hard long-edge cap when composing POI/crop stacks (before optional Image-tab
        /// send downscale). Always applied so huge multi-strip stacks stay VL-safe.
        /// </summary>
        public const int StackComposeMaxLongEdge = 2560;

        /// <summary>
        /// Mag-style island zoom: enlarge small balloon crops so lettering uses more
        /// of the Local-LLM pixel budget. Only scales up; large crops are unchanged.
        /// Grow toward this long-edge (px); already-at-or-above are left alone.
        /// </summary>
        public const int IslandZoomTargetLongEdge = 1800;

        /// <summary>
        /// Stock Mag-style max zoom factor (600%). User can raise up to
        /// <see cref="IslandZoomFactorMax"/> via the Balloons slider.
        /// </summary>
        public const double IslandZoomMaxFactor = 6.0;

        /// <summary>Mag zoom floor (125%).</summary>
        public const double IslandZoomFactorMin = 1.25;

        /// <summary>Mag zoom ceiling (1000%).</summary>
        public const double IslandZoomFactorMax = 10.0;

        /// <summary>Mild unsharp after zoom (0 = off). Slightly stronger than stock prep.</summary>
        public const float IslandZoomSharpenAmount = 0.55f;

        /// <summary>
        /// Map a rectangle from one image size to another (uniform letterbox→tone scale).
        /// Uses floor/ceil so glyph edges are not clipped on the low side.
        /// </summary>
        public static Rectangle MapRectBetweenImages(
            Rectangle r,
            int fromW,
            int fromH,
            int toW,
            int toH)
        {
            if (fromW < 1 || fromH < 1 || toW < 1 || toH < 1)
                return r;
            if (fromW == toW && fromH == toH)
            {
                return Rectangle.Intersect(r, new Rectangle(0, 0, toW, toH));
            }

            double sx = (double)toW / fromW;
            double sy = (double)toH / fromH;
            int x = (int)Math.Floor(r.X * sx);
            int y = (int)Math.Floor(r.Y * sy);
            int x2 = (int)Math.Ceiling((r.X + r.Width) * sx);
            int y2 = (int)Math.Ceiling((r.Y + r.Height) * sy);
            x = Math.Clamp(x, 0, Math.Max(0, toW - 1));
            y = Math.Clamp(y, 0, Math.Max(0, toH - 1));
            x2 = Math.Clamp(x2, x + 1, toW);
            y2 = Math.Clamp(y2, y + 1, toH);
            return new Rectangle(x, y, x2 - x, y2 - y);
        }

        /// <summary>
        /// True when <paramref name="hiRes"/> has meaningfully more pixels than
        /// <paramref name="pipe"/> (page was downscaled in Image prep).
        /// </summary>
        public static bool HiResIsRicher(Bitmap pipe, Bitmap hiRes)
        {
            if (pipe == null || hiRes == null)
                return false;
            int pipeLong = Math.Max(pipe.Width, pipe.Height);
            int hiLong = Math.Max(hiRes.Width, hiRes.Height);
            // Need at least ~12% more long-edge (or 64px) to beat re-upscaling tone.
            return hiLong >= pipeLong + 64 && hiLong > pipeLong * 1.12;
        }

        /// <summary>
        /// Min tone-crop height for a panel-spanning wide ribbon (not compact balloons).
        /// Extra real panel pixels beyond the tight box; orange beef is separate.
        /// </summary>
        public const int IslandStripMinHeight = 480;

        /// <summary>Landscape aspect (width/height) floor for wide-ribbon expand.</summary>
        public const double IslandStripWideThinAspect = 2.0;

        /// <summary>Must span this fraction of panel width (or <see cref="IslandStripMinWidthPx"/>).</summary>
        public const double IslandStripMinWidthFrac = 0.45;

        /// <summary>Absolute width floor (px) for wide-ribbon expand.</summary>
        public const int IslandStripMinWidthPx = 400;

        /// <summary>Height must stay under this fraction of panel height (thin band).</summary>
        public const double IslandStripMaxHeightFrac = 0.55;

        /// <summary>
        /// Panel-spanning short ribbon only — not compact side balloons (e.g. 191×88).
        /// </summary>
        public static bool IsWideThinIslandStrip(
            Rectangle tight,
            int frameW,
            int frameH,
            int boxCountOnCanvas,
            int minHeight = IslandStripMinHeight)
        {
            if (boxCountOnCanvas != 1)
                return false;
            if (frameW < 8 || frameH < 8)
                return false;
            if (tight.Width < 8 || tight.Height < 8)
                return false;
            if (tight.Height >= minHeight)
                return false;

            double aspect = tight.Width / (double)tight.Height;
            double wFrac = tight.Width / (double)frameW;
            double hFrac = tight.Height / (double)frameH;

            if (hFrac > IslandStripMaxHeightFrac)
                return false;
            if (aspect < IslandStripWideThinAspect)
                return false;

            bool spansWide =
                wFrac >= IslandStripMinWidthFrac ||
                tight.Width >= IslandStripMinWidthPx;
            return spansWide;
        }

        /// <summary>
        /// Gap (px) kept between an expanded VL crop and other island boxes
        /// so wide-ribbon minH never swallows a neighbor balloon.
        /// </summary>
        public const int IslandExpandNeighborGapPx = 4;

        /// <summary>
        /// Grow <paramref name="hole"/> to min width/height when free space allows.
        /// Places the crop inside the free band (frame + neighbor gaps), always
        /// containing the tight island. Prefer center on the island; when one side
        /// is blocked (frame or neighbor), use the free side — including growing
        /// <b>up</b> when the bottom has no room.
        /// </summary>
        public static Rectangle ExpandIslandCropToMinSize(
            Rectangle hole,
            int frameW,
            int frameH,
            int minWidth = 0,
            int minHeight = IslandStripMinHeight,
            IReadOnlyList<Rectangle>? avoidIslands = null,
            int neighborGapPx = IslandExpandNeighborGapPx)
        {
            if (frameW < 1 || frameH < 1)
                return hole;

            var frame = new Rectangle(0, 0, frameW, frameH);
            var tight = Rectangle.Intersect(hole, frame);
            if (tight.Width < 1 || tight.Height < 1)
                return hole;

            minWidth = Math.Max(0, minWidth);
            minHeight = Math.Max(0, minHeight);
            int needW = Math.Max(tight.Width, Math.Min(minWidth, frameW));
            int needH = Math.Max(tight.Height, Math.Min(minHeight, frameH));
            if (needW == tight.Width && needH == tight.Height &&
                (avoidIslands == null || avoidIslands.Count == 0))
                return tight;

            // Free band that may host the crop (must still contain tight).
            // Classify neighbors from the raw box (not gap-inflated): gap inflate
            // crosses the seam when islands touch, which used to skip limitB and
            // let RecoverMinSize re-grow into the next balloon (double-speak).
            int limitL = 0;
            int limitT = 0;
            int limitR = frameW;
            int limitB = frameH;
            neighborGapPx = Math.Max(0, neighborGapPx);

            if (avoidIslands != null)
            {
                foreach (var raw in avoidIslands)
                {
                    var n = Rectangle.Intersect(raw, frame);
                    if (n.Width < 1 || n.Height < 1)
                        continue;
                    if (IsSameIslandBox(n, tight))
                        continue;

                    // Stop edge includes gap; classification uses raw n.
                    ApplyNeighborFreeBandLimit(
                        tight, n, neighborGapPx,
                        ref limitL, ref limitT, ref limitR, ref limitB);
                }
            }

            // Crop must contain tight and stay in free band.
            limitL = Math.Clamp(limitL, 0, tight.Left);
            limitT = Math.Clamp(limitT, 0, tight.Top);
            limitR = Math.Clamp(limitR, tight.Right, frameW);
            limitB = Math.Clamp(limitB, tight.Bottom, frameH);

            int availW = Math.Max(tight.Width, limitR - limitL);
            int availH = Math.Max(tight.Height, limitB - limitT);
            needW = Math.Min(needW, availW);
            needH = Math.Min(needH, availH);
            needW = Math.Max(needW, tight.Width);
            needH = Math.Max(needH, tight.Height);

            // Prefer center on tight; slide into free band when one side is short.
            int cx = tight.X + tight.Width / 2;
            int cy = tight.Y + tight.Height / 2;
            int x = cx - needW / 2;
            int y = cy - needH / 2;
            if (x < limitL) x = limitL;
            if (y < limitT) y = limitT;
            if (x + needW > limitR) x = Math.Max(limitL, limitR - needW);
            if (y + needH > limitB) y = Math.Max(limitT, limitB - needH);

            // Keep tight fully inside after slide.
            if (x > tight.Left) x = tight.Left;
            if (y > tight.Top) y = tight.Top;
            if (x + needW < tight.Right) x = tight.Right - needW;
            if (y + needH < tight.Bottom) y = tight.Bottom - needH;
            x = Math.Clamp(x, limitL, Math.Max(limitL, limitR - needW));
            y = Math.Clamp(y, limitT, Math.Max(limitT, limitB - needH));

            var expanded = new Rectangle(
                x, y, Math.Max(1, needW), Math.Max(1, needH));
            expanded = Rectangle.Union(expanded, tight);
            expanded.Intersect(frame);
            if (expanded.Width < 1 || expanded.Height < 1)
                return tight;

            // Final safety: never invade neighbors (handles diagonal / nested).
            if (avoidIslands != null && avoidIslands.Count > 0)
            {
                expanded = ClampCropAwayFromNeighbors(
                    tight, expanded, avoidIslands, frameW, frameH, neighborGapPx);
                // After clamp, reclaim free room on the opposite side if min size
                // was cut (e.g. bottom blocked → grow further up). Free-band limits
                // already exclude neighbor+gap so recover cannot re-enter them.
                expanded = RecoverMinSizeInFreeBand(
                    tight, expanded, needW, needH,
                    limitL, limitT, limitR, limitB, frameW, frameH);
                // Belt-and-suspenders: recover must never undo the neighbor clamp.
                expanded = ClampCropAwayFromNeighbors(
                    tight, expanded, avoidIslands, frameW, frameH, neighborGapPx);
            }

            return expanded;
        }

        /// <summary>
        /// Near-identical box check used when skipping self in avoid lists.
        /// </summary>
        private static bool IsSameIslandBox(Rectangle a, Rectangle b) =>
            a.Equals(b) ||
            (Math.Abs(a.X - b.X) <= 1 &&
             Math.Abs(a.Y - b.Y) <= 1 &&
             Math.Abs(a.Width - b.Width) <= 2 &&
             Math.Abs(a.Height - b.Height) <= 2);

        /// <summary>
        /// Tighten free-band limits so the crop stays outside <paramref name="n"/>
        /// (plus gap). Uses the raw neighbor for above/below/left/right — not a
        /// gap-inflated rect — so edge-touching islands still block expansion.
        /// </summary>
        private static void ApplyNeighborFreeBandLimit(
            Rectangle tight,
            Rectangle n,
            int gapPx,
            ref int limitL,
            ref int limitT,
            ref int limitR,
            ref int limitB)
        {
            gapPx = Math.Max(0, gapPx);
            double acx = tight.Left + tight.Width / 2.0;
            double acy = tight.Top + tight.Height / 2.0;
            double ocx = n.Left + n.Width / 2.0;
            double ocy = n.Top + n.Height / 2.0;
            double dx = Math.Abs(acx - ocx);
            double dy = Math.Abs(acy - ocy);

            // X / Y overlap of the cores (or nearly so) — stacked / side-by-side.
            bool xNear = tight.Left < n.Right && tight.Right > n.Left;
            bool yNear = tight.Top < n.Bottom && tight.Bottom > n.Top;
            // Also treat near-miss (within gap) as near so pad/minH stops short.
            bool xNearGap = tight.Left - gapPx < n.Right && tight.Right + gapPx > n.Left;
            bool yNearGap = tight.Top - gapPx < n.Bottom && tight.Bottom + gapPx > n.Top;

            bool primarilyStacked = dy >= dx * 0.75;
            bool primarilySideBySide = dx > dy * 0.75 && !primarilyStacked;

            // Vertical: neighbor at/below or at/above tight (including touch).
            if (xNearGap && (primarilyStacked || (!primarilySideBySide && dy > 0)))
            {
                if (n.Top >= tight.Bottom - 1 ||
                    (ocy > acy && n.Top >= tight.Top + Math.Max(1, tight.Height / 3)))
                {
                    // Stop at neighbor top minus gap; never above tight.Bottom.
                    int stop = n.Top - gapPx;
                    limitB = Math.Min(limitB, Math.Max(tight.Bottom, stop));
                }
                else if (n.Bottom <= tight.Top + 1 ||
                         (ocy < acy && n.Bottom <= tight.Bottom - Math.Max(1, tight.Height / 3)))
                {
                    int stop = n.Bottom + gapPx;
                    limitT = Math.Max(limitT, Math.Min(tight.Top, stop));
                }
            }

            // Horizontal: neighbor at/right or at/left of tight.
            if (yNearGap && primarilySideBySide)
            {
                if (n.Left >= tight.Right - 1 ||
                    (ocx > acx && n.Left >= tight.Left + Math.Max(1, tight.Width / 3)))
                {
                    int stop = n.Left - gapPx;
                    limitR = Math.Min(limitR, Math.Max(tight.Right, stop));
                }
                else if (n.Right <= tight.Left + 1 ||
                         (ocx < acx && n.Right <= tight.Right - Math.Max(1, tight.Width / 3)))
                {
                    int stop = n.Right + gapPx;
                    limitL = Math.Max(limitL, Math.Min(tight.Left, stop));
                }
            }

            // Already-overlapping cores (merge-off residual): still fence the free band
            // to the midline so minH cannot swallow the other island's text.
            if (xNear && yNear)
            {
                if (ocy > acy)
                {
                    int mid = Math.Max(tight.Bottom, (tight.Bottom + n.Top) / 2);
                    limitB = Math.Min(limitB, mid);
                }
                else if (ocy < acy)
                {
                    int mid = Math.Min(tight.Top, (n.Bottom + tight.Top) / 2);
                    limitT = Math.Max(limitT, mid);
                }
                else if (ocx > acx)
                {
                    int mid = Math.Max(tight.Right, (tight.Right + n.Left) / 2);
                    limitR = Math.Min(limitR, mid);
                }
                else if (ocx < acx)
                {
                    int mid = Math.Min(tight.Left, (n.Right + tight.Left) / 2);
                    limitL = Math.Max(limitL, mid);
                }
            }
        }

        /// <summary>
        /// After neighbor clamp shrunk a crop, grow back toward <paramref name="needW"/>/
        /// <paramref name="needH"/> using free band room (up/left when down/right blocked).
        /// </summary>
        private static Rectangle RecoverMinSizeInFreeBand(
            Rectangle tight,
            Rectangle expanded,
            int needW,
            int needH,
            int limitL,
            int limitT,
            int limitR,
            int limitB,
            int frameW,
            int frameH)
        {
            var frame = new Rectangle(0, 0, Math.Max(1, frameW), Math.Max(1, frameH));
            tight = Rectangle.Intersect(tight, frame);
            expanded = Rectangle.Intersect(expanded, frame);
            if (tight.Width < 1 || tight.Height < 1)
                return expanded;

            expanded = Rectangle.Union(expanded, tight);
            limitL = Math.Clamp(limitL, 0, tight.Left);
            limitT = Math.Clamp(limitT, 0, tight.Top);
            limitR = Math.Clamp(limitR, tight.Right, frameW);
            limitB = Math.Clamp(limitB, tight.Bottom, frameH);

            int deficitH = needH - expanded.Height;
            if (deficitH > 0)
            {
                int roomUp = Math.Max(0, expanded.Top - limitT);
                int roomDown = Math.Max(0, limitB - expanded.Bottom);
                int takeUp = Math.Min(deficitH, roomUp);
                if (takeUp > 0)
                {
                    expanded.Y -= takeUp;
                    expanded.Height += takeUp;
                    deficitH -= takeUp;
                }
                int takeDown = Math.Min(deficitH, roomDown);
                if (takeDown > 0)
                    expanded.Height += takeDown;
            }

            int deficitW = needW - expanded.Width;
            if (deficitW > 0)
            {
                int roomLeft = Math.Max(0, expanded.Left - limitL);
                int roomRight = Math.Max(0, limitR - expanded.Right);
                int takeLeft = Math.Min(deficitW, roomLeft);
                if (takeLeft > 0)
                {
                    expanded.X -= takeLeft;
                    expanded.Width += takeLeft;
                    deficitW -= takeLeft;
                }
                int takeRight = Math.Min(deficitW, roomRight);
                if (takeRight > 0)
                    expanded.Width += takeRight;
            }

            expanded = Rectangle.Union(expanded, tight);
            expanded.Intersect(frame);
            if (expanded.Width < 1 || expanded.Height < 1)
                return tight;
            return expanded;
        }

        /// <summary>
        /// Shrink <paramref name="expanded"/> so it does not enter other island boxes
        /// (with gap), while always containing <paramref name="tight"/>.
        /// Classification uses the raw neighbor; gap is applied at the stop edge so
        /// edge-touching islands still clamp (gap-inflate must not hide the seam).
        /// </summary>
        public static Rectangle ClampCropAwayFromNeighbors(
            Rectangle tight,
            Rectangle expanded,
            IReadOnlyList<Rectangle> others,
            int frameW,
            int frameH,
            int gapPx = IslandExpandNeighborGapPx)
        {
            var frame = new Rectangle(0, 0, Math.Max(1, frameW), Math.Max(1, frameH));
            tight = Rectangle.Intersect(tight, frame);
            expanded = Rectangle.Intersect(expanded, frame);
            if (tight.Width < 1 || tight.Height < 1)
                return expanded;

            // Always keep the detect island fully inside the crop.
            expanded = Rectangle.Union(expanded, tight);
            gapPx = Math.Max(0, gapPx);

            foreach (var raw in others)
            {
                var n = Rectangle.Intersect(raw, frame);
                if (n.Width < 1 || n.Height < 1)
                    continue;
                // Skip self / near-identical to tight.
                if (IsSameIslandBox(n, tight))
                    continue;

                // Forbidden zone = neighbor + gap (for intersection tests only).
                var blocked = Rectangle.Inflate(n, gapPx, gapPx);
                blocked.Intersect(frame);
                if (blocked.Width < 1 || blocked.Height < 1)
                    continue;
                if (!expanded.IntersectsWith(blocked) && !expanded.IntersectsWith(n))
                    continue;

                double acy = tight.Top + tight.Height / 2.0;
                double ocy = n.Top + n.Height / 2.0;
                double acx = tight.Left + tight.Width / 2.0;
                double ocx = n.Left + n.Width / 2.0;

                // Vertical: raw neighbor at/below or at/above (including touch).
                if (n.Top >= tight.Bottom - 1 ||
                    (ocy > acy && n.Top >= tight.Top + Math.Max(1, tight.Height / 3)))
                {
                    int maxBottom = Math.Max(tight.Bottom, n.Top - gapPx);
                    if (expanded.Bottom > maxBottom)
                        expanded.Height = Math.Max(tight.Height, maxBottom - expanded.Top);
                }
                else if (n.Bottom <= tight.Top + 1 ||
                         (ocy < acy && n.Bottom <= tight.Bottom - Math.Max(1, tight.Height / 3)))
                {
                    int minTop = Math.Min(tight.Top, n.Bottom + gapPx);
                    if (expanded.Top < minTop)
                    {
                        int bottom = Math.Max(expanded.Bottom, tight.Bottom);
                        expanded.Y = minTop;
                        expanded.Height = Math.Max(tight.Height, bottom - expanded.Y);
                    }
                }

                // Horizontal: raw neighbor at/right or at/left.
                if (n.Left >= tight.Right - 1 ||
                    (ocx > acx && n.Left >= tight.Left + Math.Max(1, tight.Width / 3)))
                {
                    int maxRight = Math.Max(tight.Right, n.Left - gapPx);
                    if (expanded.Right > maxRight)
                        expanded.Width = Math.Max(tight.Width, maxRight - expanded.Left);
                }
                else if (n.Right <= tight.Left + 1 ||
                         (ocx < acx && n.Right <= tight.Right - Math.Max(1, tight.Width / 3)))
                {
                    int minLeft = Math.Min(tight.Left, n.Right + gapPx);
                    if (expanded.Left < minLeft)
                    {
                        int right = Math.Max(expanded.Right, tight.Right);
                        expanded.X = minLeft;
                        expanded.Width = Math.Max(tight.Width, right - expanded.X);
                    }
                }

                // Still overlapping (diagonal / nested / residual merge-off): cut
                // expanded margin using raw neighbor + gap, never the tight core.
                if (expanded.IntersectsWith(blocked) || expanded.IntersectsWith(n))
                {
                    var core = tight;
                    if (n.Top >= core.Top + core.Height / 2 || ocy > acy)
                    {
                        int maxBottom = Math.Max(core.Bottom, n.Top - gapPx);
                        if (expanded.Bottom > maxBottom)
                            expanded.Height = Math.Max(core.Height, maxBottom - expanded.Top);
                    }
                    else if (n.Bottom <= core.Top + core.Height / 2 || ocy < acy)
                    {
                        int minTop = Math.Min(core.Top, n.Bottom + gapPx);
                        if (expanded.Top < minTop)
                        {
                            int bottom = Math.Max(expanded.Bottom, core.Bottom);
                            expanded.Y = minTop;
                            expanded.Height = Math.Max(core.Height, bottom - expanded.Y);
                        }
                    }
                }
            }

            expanded = Rectangle.Union(expanded, tight);
            expanded.Intersect(frame);
            if (expanded.Width < 1 || expanded.Height < 1)
                return tight;
            return expanded;
        }

        /// <summary>
        /// Canvas for a composed stack: content stays native; canvas is
        /// <paramref name="beefExtra"/> larger on each axis (pad only).
        /// Horizontal pad is centered; vertical pad uses
        /// <paramref name="bottomPadShare"/> (0=all top, 0.5=center, 1=all bottom).
        /// </summary>
        public static void ComputeBeefyStackCanvas(
            int contentW,
            int contentH,
            out int canvasW,
            out int canvasH,
            out float offsetX,
            out float offsetY,
            double beefExtra = DefaultStackBeefExtra,
            double bottomPadShare = DefaultStackBottomPadShare)
        {
            contentW = Math.Max(1, contentW);
            contentH = Math.Max(1, contentH);
            beefExtra = Math.Clamp(beefExtra, 0.0, 1.5);
            bottomPadShare = Math.Clamp(bottomPadShare, 0.0, 1.0);

            double factor = 1.0 + beefExtra;
            canvasW = Math.Max(contentW, (int)Math.Ceiling(contentW * factor));
            canvasH = Math.Max(contentH, (int)Math.Ceiling(contentH * factor));

            int padX = canvasW - contentW;
            int padY = canvasH - contentH;
            offsetX = padX * 0.5f;
            // bottomPadShare of vertical beef below content; rest above.
            offsetY = padY * (1.0f - (float)bottomPadShare);
        }

        /// <summary>
        /// Resolve stack beef knobs from current settings (Balloons A/B).
        /// Prefers frozen speak-run knobs when OCR is mid-pipeline.
        /// </summary>
        public static void ResolveStackBeefFromSettings(
            out double beefExtra,
            out double bottomPadShare)
        {
            beefExtra = SpeakRunSettings.GetComicPoiStackBeefExtra();
            bottomPadShare = SpeakRunSettings.GetComicPoiStackBottomPadShare();
        }

        /// <summary>Default gap (px) between multi-strip island bands on the orange canvas.</summary>
        public const int DefaultAutoStackGapPx = 10;

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
        /// Mag-style zoom for a single island crop. When <see cref="SpeakRunSettings.GetComicIslandZoom"/>
        /// is on and the crop long edge is below <see cref="IslandZoomTargetLongEdge"/>,
        /// returns a new upscaled bitmap (input is disposed). Otherwise returns
        /// <paramref name="strip"/> unchanged. Cap at the user Mag factor
        /// (<see cref="SpeakRunSettings.GetComicIslandZoomFactor"/>, stock 6× / 600%).
        /// Progressive =2× steps keep lettering sharper than one huge jump.
        /// Live OCR prefers <c>OcrProcessor</c> Lanczos zoom when available; this is the
        /// shared fallback (tests + compose helpers).
        /// </summary>
        /// <param name="scaleToSize">
        /// Optional high-quality scaler (destW, destH) → new bitmap. When null, uses
        /// progressive high-quality bicubic.
        /// </param>
        public static Bitmap ApplyIslandZoomIfEnabled(
            Bitmap strip,
            StringBuilder? detail = null,
            string? logTag = null,
            Func<Bitmap, int, int, Bitmap>? scaleToSize = null)
        {
            if (strip == null)
                throw new ArgumentNullException(nameof(strip));

            if (!SpeakRunSettings.GetComicIslandZoom())
                return strip;

            int w = strip.Width;
            int h = strip.Height;
            if (w < 4 || h < 4)
                return strip;

            int srcLong = Math.Max(w, h);
            int target = IslandZoomTargetLongEdge;
            if (srcLong >= target)
            {
                detail?.AppendLine(
                    $"  {logTag ?? "island-zoom"}: skip {w}x{h} (long≥{target})");
                return strip;
            }

            double maxFactor = SpeakRunSettings.GetComicIslandZoomFactor();
            double scale = (double)target / srcLong;
            if (scale > maxFactor)
                scale = maxFactor;
            if (scale <= 1.01)
                return strip;

            int tw = Math.Max(1, (int)Math.Round(w * scale));
            int th = Math.Max(1, (int)Math.Round(h * scale));
            // Never exceed stack long-edge safety (canvas compose has its own cap too).
            int outLong = Math.Max(tw, th);
            if (outLong > StackComposeMaxLongEdge)
            {
                double edgeFit = (double)StackComposeMaxLongEdge / outLong;
                tw = Math.Max(1, (int)Math.Round(tw * edgeFit));
                th = Math.Max(1, (int)Math.Round(th * edgeFit));
                scale *= edgeFit;
            }

            Bitmap zoomed = scaleToSize != null
                ? scaleToSize(strip, tw, th)
                : ScaleBitmapProgressiveBicubic(strip, tw, th);

            Bitmap result = zoomed;
            if (IslandZoomSharpenAmount > 0.001f &&
                result.Width >= 3 && result.Height >= 3)
            {
                var sharp = LightUnsharp(result, IslandZoomSharpenAmount);
                if (!ReferenceEquals(sharp, result))
                {
                    result.Dispose();
                    result = sharp;
                }
            }

            detail?.AppendLine(
                $"  {logTag ?? "island-zoom"}: {w}x{h} → {result.Width}x{result.Height} " +
                $"(×{scale:F2} targetLong={target}" +
                (scaleToSize != null ? " lanczos" : " progressive-bicubic") + ")");

            try { strip.Dispose(); } catch { /* ignore */ }
            return result;
        }

        /// <summary>
        /// Progressive =2× high-quality bicubic, then final step to exact size.
        /// Better than one big GDI DrawImage for comic lettering.
        /// </summary>
        public static Bitmap ScaleBitmapProgressiveBicubic(Bitmap source, int destW, int destH)
        {
            if (destW < 1 || destH < 1)
                return (Bitmap)source.Clone();
            if (source.Width == destW && source.Height == destH)
                return (Bitmap)source.Clone();

            Bitmap current = (Bitmap)source.Clone();
            try
            {
                while (current.Width * 2 < destW || current.Height * 2 < destH)
                {
                    int nw = Math.Min(destW, Math.Max(current.Width + 1, current.Width * 2));
                    int nh = Math.Min(destH, Math.Max(current.Height + 1, current.Height * 2));
                    if (current.Width >= destW) nw = destW;
                    if (current.Height >= destH) nh = destH;
                    var next = ScaleBitmapBicubicOnce(current, nw, nh);
                    current.Dispose();
                    current = next;
                }

                if (current.Width != destW || current.Height != destH)
                {
                    var final = ScaleBitmapBicubicOnce(current, destW, destH);
                    current.Dispose();
                    current = final;
                }

                var result = current;
                current = null!;
                return result;
            }
            finally
            {
                try { current?.Dispose(); } catch { /* ignore */ }
            }
        }

        private static Bitmap ScaleBitmapBicubicOnce(Bitmap source, int w, int h)
        {
            var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, w, h));
            }
            return scaled;
        }

        /// <summary>
        /// Mild unsharp for post-zoom lettering (same idea as pipeline unsharp).
        /// </summary>
        private static Bitmap LightUnsharp(Bitmap source, float amount)
        {
            if (source.Width < 3 || source.Height < 3)
                return source;
            amount = Math.Clamp(amount, 0.01f, 2.0f);

            var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, source.Width, source.Height);
            var srcData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* s0 = (byte*)srcData.Scan0;
                    byte* d0 = (byte*)dstData.Scan0;
                    int sStride = srcData.Stride;
                    int dStride = dstData.Stride;
                    int w = source.Width;
                    int h = source.Height;

                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int sumB = 0, sumG = 0, sumR = 0, count = 0;
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                int yy = Math.Clamp(y + dy, 0, h - 1);
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int xx = Math.Clamp(x + dx, 0, w - 1);
                                    byte* p = s0 + yy * sStride + xx * 4;
                                    sumB += p[0];
                                    sumG += p[1];
                                    sumR += p[2];
                                    count++;
                                }
                            }

                            byte* sp = s0 + y * sStride + x * 4;
                            byte* dp = d0 + y * dStride + x * 4;
                            double blurB = sumB / (double)count;
                            double blurG = sumG / (double)count;
                            double blurR = sumR / (double)count;
                            dp[0] = ClampByte(sp[0] + amount * (sp[0] - blurB));
                            dp[1] = ClampByte(sp[1] + amount * (sp[1] - blurG));
                            dp[2] = ClampByte(sp[2] + amount * (sp[2] - blurR));
                            dp[3] = sp[3];
                        }
                    }
                }
            }
            finally
            {
                source.UnlockBits(srcData);
                result.UnlockBits(dstData);
            }

            return result;
        }

        private static byte ClampByte(double v) =>
            (byte)(v < 0 ? 0 : (v > 255 ? 255 : (int)Math.Round(v)));

        /// <summary>
        /// POI path: clone island boxes from <paramref name="source"/>, then
        /// <see cref="ComposeVerticalStripStack"/> (orange canvas + Balloons beef).
        /// Caller owns result.
        /// </summary>
        /// <param name="avoidIslands">
        /// All page islands (optional). When per-island VL passes a single box,
        /// pass the full island list so wide-ribbon expand cannot swallow neighbors.
        /// </param>
        /// <param name="hiResSource">
        /// Optional pre-downscale letterbox (richer pixels). When Zoom is on and
        /// this is richer than <paramref name="source"/>, islands are cut here first.
        /// </param>
        /// <param name="prepareHiResCropOwned">
        /// Owns the hi-res color crop and returns gray/tone-prepped strip for VL.
        /// Required when cutting from <paramref name="hiResSource"/> (letterbox is
        /// not already toned).
        /// </param>
        /// <param name="scaleToSize">
        /// Optional Lanczos (or other) scaler for Zoom; default progressive bicubic.
        /// </param>
        public static Bitmap? BuildVerticalStack(
            Bitmap source,
            IReadOnlyList<Rectangle> boxes,
            StringBuilder? detail = null,
            bool paintBullseyes = true,
            int stripGapPx = StackStripGapPx,
            int marginPx = 0,
            IReadOnlyList<Rectangle>? avoidIslands = null,
            Bitmap? hiResSource = null,
            Func<Bitmap, Bitmap>? prepareHiResCropOwned = null,
            Func<Bitmap, int, int, Bitmap>? scaleToSize = null)
        {
            if (source == null || boxes == null || boxes.Count == 0)
                return null;

            // paintBullseyes kept for call-site compat; stacks use green boxes instead.
            _ = paintBullseyes;

            bool useHiRes =
                SpeakRunSettings.GetComicIslandZoom() &&
                hiResSource != null &&
                HiResIsRicher(source, hiResSource) &&
                prepareHiResCropOwned != null;

            var strips = new List<Bitmap>();
            try
            {
                var imgBounds = new Rectangle(0, 0, source.Width, source.Height);
                // Neighbors for clamp: full page list, else co-strips on this canvas.
                var avoid = avoidIslands ?? boxes;

                for (int i = 0; i < boxes.Count; i++)
                {
                    var tight = Rectangle.Intersect(boxes[i], imgBounds);
                    if (tight.Width < 4 || tight.Height < 4)
                    {
                        detail?.AppendLine($"  poi-stack strip[{i + 1}]: skip empty");
                        continue;
                    }

                    // Panel-spanning short ribbons only (not compact balloons).
                    // Gate is per-strip geometry; expand clamps against avoidIslands
                    // so a top ribbon never swallows a lower balloon island.
                    Rectangle hole = tight;
                    bool wantWide = IsWideThinIslandStrip(
                        tight, source.Width, source.Height,
                        boxCountOnCanvas: 1);
                    if (wantWide)
                    {
                        // Build avoid list excluding current tight (approx).
                        var others = new List<Rectangle>(avoid.Count);
                        foreach (var a in avoid)
                        {
                            var n = Rectangle.Intersect(a, imgBounds);
                            if (n.Width < 1 || n.Height < 1)
                                continue;
                            if (Math.Abs(n.X - tight.X) <= 1 &&
                                Math.Abs(n.Y - tight.Y) <= 1 &&
                                Math.Abs(n.Width - tight.Width) <= 2 &&
                                Math.Abs(n.Height - tight.Height) <= 2)
                                continue;
                            others.Add(n);
                        }

                        hole = ExpandIslandCropToMinSize(
                            tight, source.Width, source.Height,
                            minWidth: 0,
                            minHeight: IslandStripMinHeight,
                            avoidIslands: others.Count > 0 ? others : null);
                    }

                    Bitmap strip;
                    string srcNote;
                    if (useHiRes)
                    {
                        // Mag-like: cut native letterbox pixels, prep, then zoom.
                        var hiHole = MapRectBetweenImages(
                            hole,
                            source.Width, source.Height,
                            hiResSource!.Width, hiResSource.Height);
                        // Slight pad so stroked glyph edges survive mapping.
                        hiHole = Rectangle.Inflate(hiHole, 2, 2);
                        hiHole = Rectangle.Intersect(
                            hiHole,
                            new Rectangle(0, 0, hiResSource.Width, hiResSource.Height));
                        if (hiHole.Width < 4 || hiHole.Height < 4)
                        {
                            strip = source.Clone(hole, PixelFormat.Format32bppArgb);
                            srcNote = "tone-fallback";
                        }
                        else
                        {
                            var rawCrop = hiResSource.Clone(
                                hiHole, PixelFormat.Format32bppArgb);
                            strip = prepareHiResCropOwned!(rawCrop);
                            srcNote =
                                $"hires {hiHole.Width}x{hiHole.Height}" +
                                $"@{hiHole.X},{hiHole.Y}";
                        }
                    }
                    else
                    {
                        strip = source.Clone(hole, PixelFormat.Format32bppArgb);
                        srcNote = "tone";
                    }

                    // Mag-style: enlarge tiny balloon crops before orange canvas.
                    strip = ApplyIslandZoomIfEnabled(
                        strip,
                        detail,
                        logTag: $"poi-stack strip[{i + 1}] zoom",
                        scaleToSize: scaleToSize);
                    if (hole != tight)
                    {
                        string clampNote = hole.Height < IslandStripMinHeight
                            ? $" clampedH={hole.Height}<{IslandStripMinHeight}"
                            : "";
                        detail?.AppendLine(
                            $"  poi-stack strip[{i + 1}]: {strip.Width}x{strip.Height} " +
                            $"@({hole.X},{hole.Y}) src={srcNote} " +
                            $"(wide-ribbon tight {tight.Width}x{tight.Height} " +
                            $"→ minH={IslandStripMinHeight}{clampNote})");
                    }
                    else
                    {
                        detail?.AppendLine(
                            $"  poi-stack strip[{i + 1}]: {strip.Width}x{strip.Height} " +
                            $"@({hole.X},{hole.Y}) src={srcNote}");
                    }
                    strips.Add(strip);
                }

                return ComposeVerticalStripStack(
                    strips,
                    detail,
                    stripGapPx,
                    marginPx,
                    paintGreenBoxes: true,
                    logPrefix: "poi-stack");
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
        /// Shared canvas compose for POI island stack and Comic Book crop-stack.
        /// Orange fill, Balloons beef / bottom-pad share (stock beef 0), optional green
        /// strip boxes. Hard long-edge cap 2560 (independent of Image-tab downscale).
        /// Does not dispose <paramref name="strips"/> — caller owns them.
        /// Caller owns the returned bitmap.
        /// </summary>
        public static Bitmap? ComposeVerticalStripStack(
            IReadOnlyList<Bitmap> strips,
            StringBuilder? detail,
            int stripGapPx,
            int marginPx = 0,
            bool paintGreenBoxes = true,
            string logPrefix = "stack")
        {
            if (strips == null || strips.Count == 0)
                return null;

            int margin = Math.Max(0, marginPx);
            int contentW = strips.Max(s => s.Width);
            int gap = Math.Max(0, stripGapPx);
            int contentH = strips.Sum(s => s.Height) + gap * Math.Max(0, strips.Count - 1);
            // Outer margin on all four sides of the content band (inside beef).
            int width = contentW + margin * 2;
            int rawH = contentH + margin * 2;
            double fit = 1.0;
            if (rawH > StackMaxHeight)
                fit = (double)StackMaxHeight / rawH;

            int outW = Math.Max(1, (int)Math.Round(width * fit));
            int outH = Math.Max(1, (int)Math.Round(rawH * fit));
            // Always apply — even when Image-tab send downscale is off.
            const int stackMaxLongEdge = StackComposeMaxLongEdge;
            int maxEdge = Math.Max(outW, outH);
            if (maxEdge > stackMaxLongEdge)
            {
                double edgeFit = (double)stackMaxLongEdge / maxEdge;
                outW = Math.Max(1, (int)Math.Round(outW * edgeFit));
                outH = Math.Max(1, (int)Math.Round(outH * edgeFit));
                fit *= edgeFit;
            }

            ResolveStackBeefFromSettings(out double beefExtra, out double bottomShare);
            ComputeBeefyStackCanvas(
                outW, outH,
                out int canvasW, out int canvasH,
                out float ox, out float oy,
                beefExtra, bottomShare);

            var canvas = new Bitmap(canvasW, canvasH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(StackCanvasColor);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                float m = (float)(margin * fit);
                float y = oy + m;
                float contentDrawW = Math.Max(1f, outW - m * 2f);
                for (int i = 0; i < strips.Count; i++)
                {
                    var s = strips[i];
                    float dw = (float)(s.Width * fit);
                    float dh = (float)(s.Height * fit);
                    // Center strips in the content band (inside left/right margin).
                    float x = ox + m + Math.Max(0, (contentDrawW - dw) * 0.5f);
                    g.DrawImage(s, x, y, dw, dh);

                    if (paintGreenBoxes)
                    {
                        var box = Rectangle.Round(new RectangleF(x, y, dw, dh));
                        if (box.Width > 2 && box.Height > 2)
                            PaintOneGuideBox(g, box);
                    }

                    y += dh;
                    if (i < strips.Count - 1)
                        y += (float)(gap * fit);
                }
            }

            detail?.AppendLine(
                $"{logPrefix} composed: strips={strips.Count} {canvasW}x{canvasH} " +
                $"(content={contentW}x{contentH}→{outW}x{outH} margin={margin}px gap={gap} " +
                $"fit={fit:F3} beef+{beefExtra:0.###} bottomShare={bottomShare:0.##} " +
                $"orange-canvas{(paintGreenBoxes ? " green-boxes" : "")})");
            return canvas;
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
