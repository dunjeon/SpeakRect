using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Balloons preview surface: zoomed base image + interactive reading islands.
    /// Non-POI base is the detect view (gray fog when on). POI base is tone
    /// (Local-LLM / map canvas). Rects are <b>pipeline image coordinates</b>
    /// (display-final grow + crop pad). List order is crop / reading order.
    /// </summary>
    public sealed class RegionRefineSurface : Control
    {
        public const int MaxRegions = 24;
        private const int HandleSize = 7;
        private const int MinBox = 12;

        private enum DragMode
        {
            None,
            Move,
            ResizeNw,
            ResizeNe,
            ResizeSw,
            ResizeSe,
            Create,
        }

        private Bitmap? _baseImage;
        private readonly List<Rectangle> _regions = new();
        private int _selected = -1;
        private bool _dirty;
        private bool _showPoiMarkers;
        private bool _showPoiOutsideFog;

        /// <summary>Same bitmap compose as live/analytics: DrawRegionGuides.</summary>
        private Bitmap? _poiGuideCache;
        private string _poiGuideSig = "";

        private DragMode _drag;
        private Point _dragStartClient;
        private Rectangle _dragOrigRegion;
        private Point _createStartPipe;

        public RegionRefineSurface()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true);
            TabStop = true;
            BackColor = UiTheme.BgDeep;
            Cursor = Cursors.Default;
        }

        /// <summary>True after the user has changed the seed list (move/resize/add/delete/reorder).</summary>
        public bool IsDirty => _dirty;

        public int SelectedIndex => _selected;

        public int RegionCount => _regions.Count;

        public IReadOnlyList<Rectangle> Regions => _regions;

        /// <summary>
        /// When true, preview composes the same POI guide image as live/analytics
        /// via <see cref="ComicPoiGuide.DrawRegionGuides"/> (green boxes ± outside fog).
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowPoiMarkers
        {
            get => _showPoiMarkers;
            set
            {
                if (_showPoiMarkers == value)
                    return;
                _showPoiMarkers = value;
                InvalidatePoiGuideCache();
                Invalidate();
            }
        }

        /// <summary>
        /// Thick fog outside islands — fed into the same DrawRegionGuides compose as live.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowPoiOutsideFog
        {
            get => _showPoiOutsideFog;
            set
            {
                if (_showPoiOutsideFog == value)
                    return;
                _showPoiOutsideFog = value;
                InvalidatePoiGuideCache();
                Invalidate();
            }
        }

        /// <summary>
        /// Kept for Balloons API compat. Preview always edits full-page islands;
        /// live Speak uses orange island VL when Island canvases is on (not shown here).
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowPoiAutoStack
        {
            get => false;
            set { /* preview never swaps to island canvas */ }
        }

        /// <summary>Dead API — canvas gap is fixed in <see cref="ComicPoiGuide"/> (10px).</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int PoiAutoStackGapPx
        {
            get => ComicPoiGuide.DefaultAutoStackGapPx;
            set { /* ignored — fixed compose knobs */ }
        }

        /// <summary>True when preview is showing the full-page POI guide (tone compose).</summary>
        public bool IsShowingPoiGuidePreview =>
            TryGetPoiGuidePreview(out _);

        /// <summary>Always false — preview is full-page edit map, never stack canvas.</summary>
        public bool IsShowingPoiStackPreview => false;

        public event EventHandler? RegionsChanged;
        public event EventHandler? SelectionChanged;

        public void Clear()
        {
            DisposeBase();
            _regions.Clear();
            _selected = -1;
            _dirty = false;
            _drag = DragMode.None;
            Invalidate();
            RaiseChanged();
            RaiseSelection();
        }

        /// <summary>
        /// Seed from an OCR detect preview. Takes ownership of <paramref name="baseImage"/>
        /// (caller must not dispose it). Regions are <b>display-final</b> (grow + crop pad
        /// already applied by <c>PreviewComicRegionsAsync</c>). Speak override uses pad=0.
        /// Clears dirty (fresh auto seed).
        /// </summary>
        public void SetSeed(Bitmap? baseImage, IEnumerable<Rectangle>? regions)
        {
            DisposeBase();
            _baseImage = baseImage;
            _regions.Clear();
            if (regions != null)
            {
                foreach (var r in regions)
                {
                    var c = ClampToImage(Normalize(r));
                    if (c.Width >= MinBox && c.Height >= MinBox && _regions.Count < MaxRegions)
                        _regions.Add(c);
                }
            }
            _selected = _regions.Count > 0 ? 0 : -1;
            _dirty = false;
            _drag = DragMode.None;
            InvalidatePoiStackCache();
            Invalidate();
            // Do NOT RaiseChanged — SetSeed is auto-detect, not a user edit.
            RaiseSelection();
        }

        /// <summary>
        /// Replace the base image but keep current region list (locked refine).
        /// Takes ownership of <paramref name="baseImage"/>. Dirty stays true if regions exist.
        /// </summary>
        public void UpdateBaseKeepRegions(Bitmap? baseImage)
        {
            DisposeBase();
            _baseImage = baseImage;
            InvalidatePoiStackCache();
            for (int i = 0; i < _regions.Count; i++)
                _regions[i] = ClampToImage(_regions[i]);
            if (_selected >= _regions.Count)
                _selected = _regions.Count > 0 ? _regions.Count - 1 : -1;
            if (_regions.Count > 0)
                _dirty = true;
            _drag = DragMode.None;
            Invalidate();
            RaiseSelection();
        }

        /// <summary>
        /// Restore a locked override (regions + optional base). Marks dirty when regions exist.
        /// Takes ownership of <paramref name="baseImage"/>.
        /// </summary>
        public void RestoreLocked(Bitmap? baseImage, IEnumerable<Rectangle>? regions)
        {
            DisposeBase();
            _baseImage = baseImage;
            _regions.Clear();
            if (regions != null)
            {
                foreach (var r in regions)
                {
                    var c = ClampToImage(Normalize(r));
                    if (c.Width >= MinBox && c.Height >= MinBox && _regions.Count < MaxRegions)
                        _regions.Add(c);
                }
            }
            _selected = _regions.Count > 0 ? 0 : -1;
            _dirty = _regions.Count > 0;
            _drag = DragMode.None;
            Invalidate();
            RaiseSelection();
        }

        /// <summary>True when the user has edited the list (not a fresh OCR seed).</summary>
        public bool HasUserOverride => _dirty && _regions.Count > 0;

        public void MarkClean() => _dirty = false;

        /// <summary>Force dirty so session treats list as user override (e.g. after restore).</summary>
        public void MarkDirty()
        {
            if (_regions.Count > 0)
                _dirty = true;
        }

        /// <summary>Clone of the pipeline base image, or null. Caller owns the clone.</summary>
        public Bitmap? CloneBaseImage()
        {
            if (_baseImage == null)
                return null;
            try { return new Bitmap(_baseImage); }
            catch { return null; }
        }

        public int BaseWidth => _baseImage?.Width ?? 0;
        public int BaseHeight => _baseImage?.Height ?? 0;

        public bool MoveSelected(int delta)
        {
            if (_selected < 0 || _selected >= _regions.Count)
                return false;
            int dest = _selected + delta;
            if (dest < 0 || dest >= _regions.Count)
                return false;
            var r = _regions[_selected];
            _regions.RemoveAt(_selected);
            _regions.Insert(dest, r);
            _selected = dest;
            _dirty = true;
            InvalidatePoiStackCache();
            Invalidate();
            RaiseChanged();
            RaiseSelection();
            return true;
        }

        public bool DeleteSelected()
        {
            if (_selected < 0 || _selected >= _regions.Count)
                return false;
            _regions.RemoveAt(_selected);
            if (_regions.Count == 0)
                _selected = -1;
            else
                _selected = Math.Min(_selected, _regions.Count - 1);
            _dirty = true;
            InvalidatePoiStackCache();
            Invalidate();
            RaiseChanged();
            RaiseSelection();
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Full-page POI edit map on tone (same DrawRegionGuides as analytics poi_guide).
            // Speak VL under stock AutoStack is orange per-island canvases — not this map.
            if (TryGetPoiGuidePreview(out var guideBmp) && guideBmp != null)
            {
                var guideDisp = GetDisplayRectFor(guideBmp.Width, guideBmp.Height);
                if (guideDisp.Width > 0 && guideDisp.Height > 0)
                    g.DrawImage(guideBmp, guideDisp);

                var dispPipe = GetDisplayRectFor(guideBmp.Width, guideBmp.Height);
                for (int i = 0; i < _regions.Count; i++)
                {
                    bool sel = i == _selected;
                    var coreClient = PipeToClientOnDisplay(
                        _regions[i], dispPipe, guideBmp.Width, guideBmp.Height);

                    string label = (i + 1).ToString();
                    using var badgeFont = new Font("Segoe UI", 8f, FontStyle.Bold);
                    var badgeSize = g.MeasureString(label, badgeFont);
                    var badge = new RectangleF(
                        coreClient.X + 2,
                        coreClient.Y + 2,
                        badgeSize.Width + 6,
                        badgeSize.Height + 2);
                    using (var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                        g.FillRectangle(bg, badge);
                    using (var fg = new SolidBrush(sel ? UiTheme.AccentHot : Color.Lime))
                        g.DrawString(label, badgeFont, fg, badge.X + 3, badge.Y + 1);

                    if (sel)
                    {
                        using var pen = new Pen(UiTheme.AccentHot, 2.5f);
                        g.DrawRectangle(pen, coreClient);
                        DrawHandles(g, coreClient);
                    }
                }

                if (_regions.Count >= 2)
                {
                    using var badgeFont = new Font("Segoe UI", 8f, FontStyle.Bold);
                    using var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
                    using var fg = new SolidBrush(UiTheme.Warn);
                    string banner = BuildPoiMultiSpeakBanner();
                    var sz = g.MeasureString(banner, badgeFont);
                    g.FillRectangle(bg, 6, 6, sz.Width + 10, sz.Height + 4);
                    g.DrawString(banner, badgeFont, fg, 11, 8);
                }

                using (var border = new Pen(UiTheme.Border, 1f))
                    g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
                return;
            }

            var disp = GetImageDisplayRect();
            if (_baseImage != null && disp.Width > 0 && disp.Height > 0)
            {
                g.DrawImage(_baseImage, disp);
            }
            else
            {
                using var muted = new SolidBrush(UiTheme.FgDim);
                using var font = new Font("Segoe UI", 9f);
                g.DrawString("Load a panel, then Preview to seed OCR boxes.", font, muted,
                    ClientRectangle, new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                    });
                return;
            }

            // Non-POI: solid green boxes (Speak crop preview).
            for (int i = 0; i < _regions.Count; i++)
            {
                bool sel = i == _selected;
                var core = _regions[i];
                var coreClient = PipeToClient(core, disp);

                Color box = sel ? UiTheme.AccentHot : Color.FromArgb(220, 50, 220, 50);
                using (var pen = new Pen(box, sel ? 2.5f : 1.8f))
                    g.DrawRectangle(pen, coreClient);

                string label = (i + 1).ToString();
                using var badgeFont = new Font("Segoe UI", 8f, FontStyle.Bold);
                var badgeSize = g.MeasureString(label, badgeFont);
                var badge = new RectangleF(
                    coreClient.X + 2,
                    coreClient.Y + 2,
                    badgeSize.Width + 6,
                    badgeSize.Height + 2);
                using (var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                    g.FillRectangle(bg, badge);
                using (var fg = new SolidBrush(sel ? UiTheme.AccentHot : Color.Lime))
                    g.DrawString(label, badgeFont, fg, badge.X + 3, badge.Y + 1);

                if (sel)
                    DrawHandles(g, coreClient);
            }

            using (var border = new Pen(UiTheme.Border, 1f))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        /// <summary>
        /// Honest multi-island speak path for the overlay banner (reads live settings).
        /// </summary>
        private static string BuildPoiMultiSpeakBanner()
        {
            var s = AppSettings.Current;
            if (s.ComicPoiAutoStack)
                return "Edit map · multi · Speak = orange island VL ×N (not this page)";
            return "Edit map · multi · Speak = tone crop VL per island (not full-page)";
        }

        /// <summary>
        /// Full-page POI guide — <b>same</b> <see cref="ComicPoiGuide.DrawRegionGuides"/>
        /// as live OCR and Analytics (no separate bullseye paint path).
        /// </summary>
        private bool TryGetPoiGuidePreview(out Bitmap? guide)
        {
            guide = null;
            if (!_showPoiMarkers || _baseImage == null || _regions.Count == 0)
                return false;

            string sig = BuildPoiGuideSig();
            if (_poiGuideCache != null &&
                string.Equals(sig, _poiGuideSig, StringComparison.Ordinal))
            {
                guide = _poiGuideCache;
                return true;
            }

            InvalidatePoiGuideCache();
            try
            {
                _poiGuideCache = ComicPoiGuide.DrawRegionGuides(
                    _baseImage, _regions, detail: null, fogOutside: _showPoiOutsideFog);
                _poiGuideSig = sig;
                guide = _poiGuideCache;
                return guide != null;
            }
            catch
            {
                InvalidatePoiGuideCache();
                return false;
            }
        }

        private string BuildPoiGuideSig()
        {
            var sb = new System.Text.StringBuilder(128);
            sb.Append(_baseImage?.Width ?? 0).Append('x').Append(_baseImage?.Height ?? 0);
            sb.Append("|fog=").Append(_showPoiOutsideFog ? '1' : '0');
            sb.Append('|').Append(_regions.Count);
            foreach (var r in _regions)
            {
                sb.Append('|').Append(r.X).Append(',').Append(r.Y).Append(',')
                    .Append(r.Width).Append('x').Append(r.Height);
            }
            return sb.ToString();
        }

        private void InvalidatePoiGuideCache()
        {
            try { _poiGuideCache?.Dispose(); } catch { /* ignore */ }
            _poiGuideCache = null;
            _poiGuideSig = "";
        }

        private static Rectangle PipeToClientOnDisplay(
            Rectangle pipe, Rectangle disp, int imgW, int imgH)
        {
            if (imgW < 1 || imgH < 1 || disp.Width < 1)
                return Rectangle.Empty;
            float sx = (float)disp.Width / imgW;
            float sy = (float)disp.Height / imgH;
            int x = disp.X + (int)Math.Round(pipe.X * sx);
            int y = disp.Y + (int)Math.Round(pipe.Y * sy);
            int w = Math.Max(1, (int)Math.Round(pipe.Width * sx));
            int h = Math.Max(1, (int)Math.Round(pipe.Height * sy));
            return new Rectangle(x, y, w, h);
        }

        /// <summary>
        /// Stack canvas is never shown in Balloons preview (edit full page only).
        /// Live Speak still builds the stack when AutoStack is on.
        /// </summary>
        private bool TryGetPoiStackPreview(out Bitmap? stack)
        {
            stack = null;
            return false;
        }

        private void InvalidatePoiStackCache()
        {
            // Stack canvas is never cached for preview — only guide map.
            InvalidatePoiGuideCache();
        }

        private Rectangle GetDisplayRectFor(int imgW, int imgH)
        {
            if (imgW < 1 || imgH < 1 || ClientSize.Width < 2 || ClientSize.Height < 2)
                return Rectangle.Empty;
            float ratio = Math.Min(
                (float)ClientSize.Width / imgW,
                (float)ClientSize.Height / imgH);
            int w = Math.Max(1, (int)Math.Round(imgW * ratio));
            int h = Math.Max(1, (int)Math.Round(imgH * ratio));
            int x = (ClientSize.Width - w) / 2;
            int y = (ClientSize.Height - h) / 2;
            return new Rectangle(x, y, w, h);
        }

        private static void DrawHandles(Graphics g, Rectangle r)
        {
            using var fill = new SolidBrush(UiTheme.AccentHot);
            using var edge = new Pen(Color.Black, 1f);
            foreach (var h in HandleRects(r))
            {
                g.FillRectangle(fill, h);
                g.DrawRectangle(edge, h);
            }
        }

        private static Rectangle[] HandleRects(Rectangle r)
        {
            int s = HandleSize;
            int half = s / 2;
            return new[]
            {
                new Rectangle(r.Left - half, r.Top - half, s, s),
                new Rectangle(r.Right - half, r.Top - half, s, s),
                new Rectangle(r.Left - half, r.Bottom - half, s, s),
                new Rectangle(r.Right - half, r.Bottom - half, s, s),
            };
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (_baseImage == null || e.Button != MouseButtons.Left)
                return;

            // Stack preview is display-only (pipe coords ≠ stack coords).
            // Turn auto-stack off to edit green boxes on the full page.
            if (IsShowingPoiStackPreview)
                return;

            var disp = GetImageDisplayRect();
            if (!disp.Contains(e.Location))
                return;

            // Hit handles of selection first
            if (_selected >= 0 && _selected < _regions.Count)
            {
                var coreClient = PipeToClient(_regions[_selected], disp);
                var handles = HandleRects(coreClient);
                DragMode[] modes =
                {
                    DragMode.ResizeNw, DragMode.ResizeNe,
                    DragMode.ResizeSw, DragMode.ResizeSe,
                };
                for (int i = 0; i < handles.Length; i++)
                {
                    if (handles[i].Contains(e.Location))
                    {
                        _drag = modes[i];
                        _dragStartClient = e.Location;
                        _dragOrigRegion = _regions[_selected];
                        Capture = true;
                        return;
                    }
                }
            }

            // Hit body of any region (top-most = last in list for draw, but pick smallest area
            // containing point so nested-ish boxes work; prefer selected).
            int hit = HitTestRegion(e.Location, disp);
            if (hit >= 0)
            {
                if (_selected != hit)
                {
                    _selected = hit;
                    RaiseSelection();
                    Invalidate();
                }
                _drag = DragMode.Move;
                _dragStartClient = e.Location;
                _dragOrigRegion = _regions[_selected];
                Capture = true;
                return;
            }

            // Empty space → start create
            if (_regions.Count >= MaxRegions)
                return;
            var pipe = ClientToPipe(e.Location, disp);
            if (!IsValidPipePoint(pipe))
                return;
            _drag = DragMode.Create;
            _createStartPipe = pipe;
            _dragStartClient = e.Location;
            _selected = -1;
            RaiseSelection();
            // Temporary region for rubber-band
            _regions.Add(new Rectangle(pipe.X, pipe.Y, MinBox, MinBox));
            _selected = _regions.Count - 1;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var disp = GetImageDisplayRect();

            if (_drag == DragMode.None)
            {
                UpdateHoverCursor(e.Location, disp);
                return;
            }

            if (_baseImage == null || _selected < 0 || _selected >= _regions.Count)
                return;

            if (_drag == DragMode.Create)
            {
                var a = _createStartPipe;
                var b = ClientToPipe(e.Location, disp);
                b = ClampPointToImage(b);
                int x1 = Math.Min(a.X, b.X);
                int y1 = Math.Min(a.Y, b.Y);
                int x2 = Math.Max(a.X, b.X);
                int y2 = Math.Max(a.Y, b.Y);
                _regions[_selected] = ClampToImage(new Rectangle(x1, y1, Math.Max(MinBox, x2 - x1), Math.Max(MinBox, y2 - y1)));
                Invalidate();
                return;
            }

            // Map drag delta in client → pipe
            float sx = disp.Width > 0 ? (float)_baseImage.Width / disp.Width : 1f;
            float sy = disp.Height > 0 ? (float)_baseImage.Height / disp.Height : 1f;
            int dx = (int)Math.Round((e.X - _dragStartClient.X) * sx);
            int dy = (int)Math.Round((e.Y - _dragStartClient.Y) * sy);
            var o = _dragOrigRegion;
            Rectangle n = o;

            switch (_drag)
            {
                case DragMode.Move:
                    n = new Rectangle(o.X + dx, o.Y + dy, o.Width, o.Height);
                    break;
                case DragMode.ResizeNw:
                    n = RectFromEdges(o.Right, o.Bottom, o.Left + dx, o.Top + dy);
                    break;
                case DragMode.ResizeNe:
                    n = RectFromEdges(o.Left, o.Bottom, o.Right + dx, o.Top + dy);
                    break;
                case DragMode.ResizeSw:
                    n = RectFromEdges(o.Right, o.Top, o.Left + dx, o.Bottom + dy);
                    break;
                case DragMode.ResizeSe:
                    n = RectFromEdges(o.Left, o.Top, o.Right + dx, o.Bottom + dy);
                    break;
            }

            var next = ClampToImage(Normalize(n));
            if (next != _regions[_selected])
            {
                _regions[_selected] = next;
                // Geometry only — commit dirty on mouse-up if still different from start.
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag == DragMode.None)
                return;

            const int clickSlop = 6; // client px; ignore pure clicks (no real drag)

            if (_drag == DragMode.Create && _selected >= 0 && _selected < _regions.Count)
            {
                var r = _regions[_selected];
                int dragDist =
                    Math.Abs(e.X - _dragStartClient.X) + Math.Abs(e.Y - _dragStartClient.Y);
                // Click on empty space used to commit a MinBox "region" and lock override.
                if (dragDist < clickSlop || r.Width < MinBox || r.Height < MinBox)
                {
                    _regions.RemoveAt(_selected);
                    _selected = _regions.Count > 0 ? _regions.Count - 1 : -1;
                    RaiseSelection();
                }
                else
                {
                    _dirty = true;
                    RaiseChanged();
                }
            }
            else if (_drag is DragMode.Move or DragMode.ResizeNw or DragMode.ResizeNe
                     or DragMode.ResizeSw or DragMode.ResizeSe)
            {
                // Select / click without moving must NOT lock refine or freeze live knobs.
                if (_selected >= 0 &&
                    _selected < _regions.Count &&
                    _regions[_selected] != _dragOrigRegion)
                {
                    _dirty = true;
                    RaiseChanged();
                }
            }

            _drag = DragMode.None;
            Capture = false;
            InvalidatePoiStackCache();
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                if (DeleteSelected())
                    e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up && e.Control)
            {
                if (MoveSelected(-1))
                    e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down && e.Control)
            {
                if (MoveSelected(1))
                    e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                _selected = -1;
                RaiseSelection();
                Invalidate();
                e.Handled = true;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Delete or Keys.Escape)
                return true;
            if (keyData is (Keys.Control | Keys.Up) or (Keys.Control | Keys.Down))
                return true;
            return base.IsInputKey(keyData);
        }

        private void UpdateHoverCursor(Point client, Rectangle disp)
        {
            if (_baseImage == null || !disp.Contains(client))
            {
                Cursor = Cursors.Default;
                return;
            }
            if (_selected >= 0 && _selected < _regions.Count)
            {
                var coreClient = PipeToClient(_regions[_selected], disp);
                var handles = HandleRects(coreClient);
                if (handles[0].Contains(client) || handles[3].Contains(client))
                {
                    Cursor = Cursors.SizeNWSE;
                    return;
                }
                if (handles[1].Contains(client) || handles[2].Contains(client))
                {
                    Cursor = Cursors.SizeNESW;
                    return;
                }
            }
            if (HitTestRegion(client, disp) >= 0)
                Cursor = Cursors.SizeAll;
            else
                Cursor = Cursors.Cross;
        }

        private int HitTestRegion(Point client, Rectangle disp)
        {
            // Prefer selected if hit
            if (_selected >= 0 && _selected < _regions.Count)
            {
                if (PipeToClient(_regions[_selected], disp).Contains(client))
                    return _selected;
            }
            int best = -1;
            int bestArea = int.MaxValue;
            for (int i = 0; i < _regions.Count; i++)
            {
                var c = PipeToClient(_regions[i], disp);
                if (!c.Contains(client))
                    continue;
                int area = Math.Max(1, c.Width * c.Height);
                if (area < bestArea)
                {
                    bestArea = area;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>Zoom letterbox rect of the base image inside this control.</summary>
        public Rectangle GetImageDisplayRect()
        {
            if (_baseImage == null || ClientSize.Width < 2 || ClientSize.Height < 2)
                return Rectangle.Empty;
            float ratio = Math.Min(
                (float)ClientSize.Width / _baseImage.Width,
                (float)ClientSize.Height / _baseImage.Height);
            int w = Math.Max(1, (int)Math.Round(_baseImage.Width * ratio));
            int h = Math.Max(1, (int)Math.Round(_baseImage.Height * ratio));
            int x = (ClientSize.Width - w) / 2;
            int y = (ClientSize.Height - h) / 2;
            return new Rectangle(x, y, w, h);
        }

        private Rectangle PipeToClient(Rectangle pipe, Rectangle disp)
        {
            if (_baseImage == null || _baseImage.Width < 1 || _baseImage.Height < 1)
                return Rectangle.Empty;
            float sx = (float)disp.Width / _baseImage.Width;
            float sy = (float)disp.Height / _baseImage.Height;
            int x = disp.X + (int)Math.Round(pipe.X * sx);
            int y = disp.Y + (int)Math.Round(pipe.Y * sy);
            int w = Math.Max(1, (int)Math.Round(pipe.Width * sx));
            int h = Math.Max(1, (int)Math.Round(pipe.Height * sy));
            return new Rectangle(x, y, w, h);
        }

        private Point ClientToPipe(Point client, Rectangle disp)
        {
            if (_baseImage == null || disp.Width < 1 || disp.Height < 1)
                return Point.Empty;
            float sx = (float)_baseImage.Width / disp.Width;
            float sy = (float)_baseImage.Height / disp.Height;
            int x = (int)Math.Round((client.X - disp.X) * sx);
            int y = (int)Math.Round((client.Y - disp.Y) * sy);
            return new Point(x, y);
        }

        private bool IsValidPipePoint(Point p)
        {
            if (_baseImage == null)
                return false;
            return p.X >= 0 && p.Y >= 0 && p.X < _baseImage.Width && p.Y < _baseImage.Height;
        }

        private Point ClampPointToImage(Point p)
        {
            if (_baseImage == null)
                return p;
            return new Point(
                Math.Clamp(p.X, 0, Math.Max(0, _baseImage.Width - 1)),
                Math.Clamp(p.Y, 0, Math.Max(0, _baseImage.Height - 1)));
        }

        private Rectangle ClampToImage(Rectangle r)
        {
            if (_baseImage == null)
                return r;
            int imgW = _baseImage.Width;
            int imgH = _baseImage.Height;
            r = Normalize(r);
            if (r.Width < MinBox) r.Width = MinBox;
            if (r.Height < MinBox) r.Height = MinBox;
            if (r.Right > imgW) r.X = imgW - r.Width;
            if (r.Bottom > imgH) r.Y = imgH - r.Height;
            if (r.X < 0) r.X = 0;
            if (r.Y < 0) r.Y = 0;
            if (r.Width > imgW) r.Width = imgW;
            if (r.Height > imgH) r.Height = imgH;
            return r;
        }

        private static Rectangle Normalize(Rectangle r)
        {
            int x1 = r.X;
            int y1 = r.Y;
            int x2 = r.X + r.Width;
            int y2 = r.Y + r.Height;
            if (x2 < x1) (x1, x2) = (x2, x1);
            if (y2 < y1) (y1, y2) = (y2, y1);
            return new Rectangle(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
        }

        private static Rectangle RectFromEdges(int fixedX, int fixedY, int freeX, int freeY)
        {
            int x1 = Math.Min(fixedX, freeX);
            int y1 = Math.Min(fixedY, freeY);
            int x2 = Math.Max(fixedX, freeX);
            int y2 = Math.Max(fixedY, freeY);
            return new Rectangle(x1, y1, Math.Max(MinBox, x2 - x1), Math.Max(MinBox, y2 - y1));
        }

        private void RaiseChanged() => RegionsChanged?.Invoke(this, EventArgs.Empty);
        private void RaiseSelection() => SelectionChanged?.Invoke(this, EventArgs.Empty);

        private void DisposeBase()
        {
            InvalidatePoiStackCache();
            if (_baseImage != null)
            {
                try { _baseImage.Dispose(); } catch { /* ignore */ }
                _baseImage = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeBase();
            base.Dispose(disposing);
        }
    }
}
