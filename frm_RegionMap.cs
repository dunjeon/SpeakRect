using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Regions tab (Settings): map + list of the eight fixed capture slots
    /// (default Shift+F1–F8). Clear selected only — draw/edit on the overlay.
    /// </summary>
    public sealed class frm_RegionMap : Form
    {
        private readonly Action? _onRegionsChanged;
        private readonly Action? _onRequestClose;
        private readonly bool _embedded;

        private readonly Panel _mapPanel;
        private readonly ListView _list;
        private readonly TableLayoutPanel _body;
        private readonly int _listRowIndex;
        private readonly Label _lblFollow;
        private readonly Label _lblStatus;
        private readonly Button _btnRefresh;
        private readonly Button _btnClear;
        private readonly Button? _btnClose;

        private int _selectedSlot = 0; // 0..7
        /// <summary>Coalesce deferred layout passes after tab host sizes the embed.</summary>
        private bool _slotsLayoutQueued;

        public frm_RegionMap(
            Action? onRegionsChanged = null,
            bool embedded = false,
            Action? onRequestClose = null)
        {
            _onRegionsChanged = onRegionsChanged;
            _embedded = embedded;
            _onRequestClose = onRequestClose;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Regions";
            if (_embedded)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = false;
                ControlBox = false;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.SizableToolWindow;
                StartPosition = FormStartPosition.CenterScreen;
                MinimumSize = new Size(560, 620);
                ClientSize = new Size(600, 680);
                TopMost = true;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
            }
            KeyPreview = true;
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Fg;
            Font = new Font("Segoe UI", 9.5f);

            // ---- Bottom bar ----
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 56,
                BackColor = UiTheme.BgBar,
                Padding = new Padding(12, 10, 12, 10),
            };
            _btnRefresh = MakeButton("Refresh");
            _btnRefresh.Click += (_, _) => ReloadFromSettings();
            _btnClear = MakeButton("Clear selected");
            _btnClear.Click += (_, _) => ClearSelected_Click();
            bottom.Controls.Add(_btnRefresh);
            bottom.Controls.Add(_btnClear);
            if (!_embedded)
            {
                _btnClose = MakeButton("Close");
                _btnClose.Click += (_, _) =>
                {
                    if (_onRequestClose != null)
                        _onRequestClose();
                    else
                        Close();
                };
                bottom.Controls.Add(_btnClose);
            }
            bottom.Resize += (_, _) => LayoutBottomButtons(bottom);
            LayoutBottomButtons(bottom);

            // ---- Status ----
            _lblStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                ForeColor = UiTheme.FgHeader,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9f),
                Padding = new Padding(16, 0, 12, 0),
                BackColor = UiTheme.BgStatus,
                Text = "Ready.",
            };

            // ---- Scrollable body ----
            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(16, 14, 8, 8),
                BackColor = UiTheme.Bg,
            };

            _body = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                BackColor = UiTheme.Bg,
                Padding = new Padding(0, 0, 16, 12),
            };
            _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int row = 0;
            void AddFull(Control c, int height)
            {
                _body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                _body.Controls.Add(c, 0, row);
                row++;
            }

            AddFull(MakeIntro(
                "Eight fixed capture slots (default Shift+F1–F8). Draw them on the overlay; " +
                "this map shows where they sit on your desktop. Follow (slot 9) tracks the mouse — see the Follow tab."), 48);

            AddFull(MakeSection("MAP"), 28);

            _mapPanel = new Panel
            {
                Height = 240,
                Dock = DockStyle.Fill,
                BackColor = UiTheme.BgDeep,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _mapPanel.Paint += MapPanel_Paint;
            _mapPanel.Resize += (_, _) => _mapPanel.Invalidate();
            AddFull(_mapPanel, 248);

            AddFull(MakeHint("Monitors are outlined. Colored boxes are set slots (R1–R8). Active slot has an orange outline."), 28);

            AddFull(MakeSection("SLOTS"), 28);

            // Exactly 8 slots — size the control so all rows fit; no internal scroll.
            // Owner-draw ListView often under-paints R8 until focus/click; see
            // RealizeSlotsListPaint / QueueSlotsLayout.
            _list = new ListView
            {
                View = View.Details,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font("Segoe UI", 9f),
                Height = EstimateSlotsListHeight(),
                Dock = DockStyle.Fill,
                // Internal scroll + owner-draw first paint often hid R8 until a click.
                Scrollable = false,
            };
            _list.Columns.Add("Slot", 48);
            _list.Columns.Add("Hotkey", 100);
            _list.Columns.Add("Shape", 72);
            _list.Columns.Add("Geometry", 280);
            // Shared theme: dark headers + right-gutter fill (no white bar on column resize).
            // Custom rows only — slot color chip on column 0.
            UiTheme.StyleListView(_list, drawStandardRows: false);
            _list.DrawSubItem += List_DrawSubItem;
            _list.SelectedIndexChanged += (_, _) =>
            {
                if (_list.SelectedIndices.Count > 0)
                {
                    _selectedSlot = Math.Clamp(_list.SelectedIndices[0], 0, 7);
                    _mapPanel.Invalidate();
                    UpdateClearEnabled();
                }
            };
            // Entering the control (tab / click) must always fully realize all rows.
            _list.Enter += (_, _) => RealizeSlotsListPaint();
            _listRowIndex = row;
            AddFull(_list, EstimateSlotsListHeight() + 8);

            AddFull(MakeSection("FOLLOW (REGION 9)"), 28);
            _lblFollow = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.FgMuted,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.TopLeft,
            };
            AddFull(_lblFollow, 36);

            AddFull(MakeSection("HOW IT WORKS"), 28);
            AddFull(MakeHint(
                "Overlay open + slot hotkey  Switch active slot (does not speak)\n" +
                "Overlay closed + slot hotkey  Speak that saved region\n" +
                "Draw on overlay  Set or replace the active slot\n" +
                "Delete (overlay)  Clear the active slot\n" +
                "Clear selected (here)  Remove a slot without opening the overlay\n" +
                "Remap keys  Settings → Key Map"), 110);

            scroll.Controls.Add(_body);

            Controls.Add(scroll);
            Controls.Add(_lblStatus);
            Controls.Add(bottom);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape && !_embedded)
                {
                    e.Handled = true;
                    if (_onRequestClose != null)
                        _onRequestClose();
                    else
                        Close();
                }
            };

            Load += (_, _) =>
            {
                LayoutBottomButtons(bottom);
                ReloadFromSettings();
            };
            // Embedded tab pages often report ClientSize 0 until after first show/layout.
            VisibleChanged += (_, _) =>
            {
                if (Visible)
                    QueueSlotsLayout();
            };
            Resize += (_, _) => QueueSlotsLayout();
            Shown += (_, _) => QueueSlotsLayout();
        }

        public void ReloadFromSettings()
        {
            var s = AppSettings.Current;
            int active = Math.Clamp(s.ActiveRegionSlot, 0, 7);

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                for (int i = 0; i < 8; i++)
                {
                    var slot = s.RegionSlots[i];
                    string hotkey = FormatHotkey(s.HotkeyRegions[i]);
                    string shape;
                    string geom;
                    if (slot.IsEmpty)
                    {
                        shape = "—";
                        geom = "empty";
                    }
                    else if (slot.IsLassoMode)
                    {
                        shape = "Lasso";
                        int n = slot.GetLassoPoints().Count;
                        geom = $"{n} points";
                    }
                    else
                    {
                        shape = slot.IsOvalMode ? "Oval" : "Rect";
                        geom = $"{slot.W}×{slot.H} @ ({slot.X}, {slot.Y})";
                    }

                    string slotLabel = i == active ? $"R{i + 1}*" : $"R{i + 1}";
                    var item = new ListViewItem(new[] { slotLabel, hotkey, shape, geom })
                    {
                        Tag = i,
                        ForeColor = slot.IsEmpty ? UiTheme.FgDim : UiTheme.Fg,
                    };
                    _list.Items.Add(item);
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            // Restore selection
            _selectedSlot = Math.Clamp(_selectedSlot, 0, 7);
            if (_list.Items.Count > _selectedSlot)
            {
                _list.Items[_selectedSlot].Selected = true;
                _list.EnsureVisible(_selectedSlot);
            }

            // Follow summary
            s.NormalizeFollowSettings();
            string followHk = FormatHotkey(s.HotkeyFollowRegion);
            string shapeName = s.FollowIsEllipse ? "Ellipse" : "Rectangle";
            _lblFollow.Text =
                $"{s.FollowWidth}×{s.FollowHeight} {shapeName} · offset ({s.FollowOffsetX}, {s.FollowOffsetY}) · speak {followHk}  (not a fixed box — open Follow tab)";

            int setCount = 0;
            for (int i = 0; i < 8; i++)
            {
                if (!s.RegionSlots[i].IsEmpty)
                    setCount++;
            }
            string activeHk = FormatHotkey(s.HotkeyRegions[active]);
            _lblStatus.Text =
                $"{setCount} of 8 slots set · active R{active + 1} · {activeHk}";

            UpdateClearEnabled();
            ApplySlotsLayoutAndPaint();
            // Host tab finishes sizing after SelectedIndexChanged — deferred passes.
            QueueSlotsLayout();
        }

        /// <summary>
        /// Called when Settings selects the Regions tab (in addition to Reload).
        /// Focus + realize so owner-draw paints R1–R8 without requiring a click.
        /// </summary>
        public void OnTabSelected()
        {
            if (IsDisposed)
                return;
            ReloadFromSettings();
            try
            {
                if (_list.CanFocus)
                    _list.Focus();
            }
            catch { /* ignore */ }
            RealizeSlotsListPaint();
            QueueSlotsLayout();
        }

        /// <summary>
        /// Schedule column + height + paint realization after the current layout message.
        /// Embedded tabs often have a partial client size on the first SelectedIndexChanged.
        /// </summary>
        private void QueueSlotsLayout()
        {
            if (_slotsLayoutQueued || IsDisposed || !IsHandleCreated)
                return;
            _slotsLayoutQueued = true;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _slotsLayoutQueued = false;
                    if (IsDisposed || !IsHandleCreated)
                        return;
                    ApplySlotsLayoutAndPaint();
                    // Second pass after the first layout/paint — matches “works after click”.
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed || !IsHandleCreated)
                                return;
                            ApplySlotsLayoutAndPaint();
                        }));
                    }
                    catch { /* ignore */ }
                }));
            }
            catch
            {
                _slotsLayoutQueued = false;
            }
        }

        private void ApplySlotsLayoutAndPaint()
        {
            SizeListColumns();
            FitSlotsListToContent();
            RealizeSlotsListPaint();
            _mapPanel.Invalidate();
        }

        /// <summary>
        /// Owner-draw Details ListView often stops short of the last row until focus
        /// or a click. Scroll last item into view, restore selection, focus, repaint.
        /// </summary>
        private void RealizeSlotsListPaint()
        {
            if (_list.IsDisposed || !_list.IsHandleCreated || _list.Items.Count == 0)
                return;

            int last = _list.Items.Count - 1;
            int keep = Math.Clamp(_selectedSlot, 0, last);

            try
            {
                // Bottom then selection — same idea as “nav to bottom, then back up”.
                _list.EnsureVisible(last);
                _list.EnsureVisible(keep);
                _list.Items[keep].Selected = true;
                _list.Items[keep].Focused = true;
                if (Visible && _list.CanFocus)
                    _list.Focus();
            }
            catch { /* handle/layout race */ }

            try
            {
                _list.Invalidate(true);
                _list.Update();
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Size the slots ListView (and its table row) so all eight rows + header fit.
        /// Never shrink below the font-based estimate (avoids locking in a clipped height
        /// from a premature GetItemRect while the tab was still sizing).
        /// </summary>
        private void FitSlotsListToContent()
        {
            if (_list.IsDisposed)
                return;

            int estimate = EstimateSlotsListHeight();
            int measured = MeasureSlotsListHeight();
            // Prefer the larger of estimate vs measure so a bad early measure cannot clip R8.
            int want = Math.Max(estimate, measured);
            // Extra cushion for FixedSingle border + owner-draw last-row clipping.
            want = Math.Clamp(want + 8, 180, 480);

            if (Math.Abs(_list.Height - want) > 1)
                _list.Height = want;

            if (_listRowIndex >= 0 && _listRowIndex < _body.RowStyles.Count)
            {
                float rowH = want + 8;
                if (Math.Abs(_body.RowStyles[_listRowIndex].Height - rowH) > 0.5f)
                    _body.RowStyles[_listRowIndex].Height = rowH;
            }

            _body.PerformLayout();
        }

        /// <summary>Best-effort height before items/handle exist (constructor / fallback).</summary>
        private int EstimateSlotsListHeight()
        {
            // Generous row metrics so DPI / theme padding never undershoots 8 rows.
            int rowH = Math.Max(22, TextRenderer.MeasureText("Ag", _list?.Font ?? Font).Height + 10);
            int headerH = Math.Max(24, rowH + 4);
            return headerH + rowH * 8 + 10;
        }

        /// <summary>Measured height for header + every list item, or 0 if not ready.</summary>
        private int MeasureSlotsListHeight()
        {
            if (!_list.IsHandleCreated || _list.Items.Count == 0)
                return 0;

            try
            {
                // Item 0 top is the header height in Details view.
                Rectangle first = _list.GetItemRect(0, ItemBoundsPortion.Entire);
                if (first.Height <= 0)
                    return 0;

                int rowH = Math.Max(first.Height, TextRenderer.MeasureText("Ag", _list.Font).Height + 8);
                int rows = _list.Items.Count;
                int headerH = Math.Max(first.Top, rowH);

                Rectangle last = _list.GetItemRect(rows - 1, ItemBoundsPortion.Entire);
                int border = Math.Max(4, _list.Height - _list.ClientSize.Height);
                if (last.Bottom > first.Top)
                    return Math.Max(last.Bottom + border, headerH + rowH * rows + border);

                return headerH + rowH * rows + border;
            }
            catch
            {
                return 0;
            }
        }

        private void ClearSelected_Click()
        {
            int i = _selectedSlot;
            if (i < 0 || i > 7)
                return;

            var s = AppSettings.Current;
            if (s.RegionSlots[i].IsEmpty)
            {
                _lblStatus.Text = $"R{i + 1} is already empty.";
                return;
            }

            string hotkey = FormatHotkey(s.HotkeyRegions[i]);
            if (MessageBox.Show(this,
                    $"Clear region slot R{i + 1} ({hotkey})?\n\nThe saved capture area will be removed. You can draw it again on the overlay.",
                    "Clear region",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            s.RegionSlots[i].Clear();
            s.Save();
            try { _onRegionsChanged?.Invoke(); } catch { /* host */ }
            ReloadFromSettings();
            _lblStatus.Text = $"Cleared R{i + 1}.";
        }

        private void UpdateClearEnabled()
        {
            var s = AppSettings.Current;
            int i = Math.Clamp(_selectedSlot, 0, 7);
            _btnClear.Enabled = !s.RegionSlots[i].IsEmpty;
        }

        private static string FormatHotkey(HotkeyChord chord) =>
            chord.IsEmpty ? "—" : chord.ToIniString();

        // ---- Map paint ----

        private void MapPanel_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(UiTheme.BgDeep);

            var vs = SystemInformation.VirtualScreen;
            if (vs.Width <= 0 || vs.Height <= 0)
            {
                TextRenderer.DrawText(g, "No virtual screen.", Font, _mapPanel.ClientRectangle,
                    UiTheme.FgDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            RectangleF dest = FitRect(vs.Size, _mapPanel.ClientRectangle, pad: 10);
            float scaleX = dest.Width / vs.Width;
            float scaleY = dest.Height / vs.Height;

            PointF MapPt(int x, int y) => new(
                dest.X + (x - vs.X) * scaleX,
                dest.Y + (y - vs.Y) * scaleY);

            RectangleF MapRect(Rectangle r) => new(
                dest.X + (r.X - vs.X) * scaleX,
                dest.Y + (r.Y - vs.Y) * scaleY,
                Math.Max(2, r.Width * scaleX),
                Math.Max(2, r.Height * scaleY));

            // Virtual desktop backdrop
            using (var bg = new SolidBrush(UiTheme.BgRaised))
            using (var border = new Pen(UiTheme.Border, 1))
            {
                g.FillRectangle(bg, dest);
                g.DrawRectangle(border, dest.X, dest.Y, dest.Width, dest.Height);
            }

            // Monitors
            var screens = Screen.AllScreens;
            for (int m = 0; m < screens.Length; m++)
            {
                var b = screens[m].Bounds;
                var mr = MapRect(b);
                using var monFill = new SolidBrush(Color.FromArgb(40, 40, 40, 50));
                using var monPen = new Pen(UiTheme.ButtonBorder, 1);
                g.FillRectangle(monFill, mr);
                g.DrawRectangle(monPen, mr.X, mr.Y, mr.Width, mr.Height);
                string monLabel = screens[m].Primary ? $"M{m + 1} (primary)" : $"M{m + 1}";
                using var monFont = new Font("Segoe UI", 7.5f);
                using var monBrush = new SolidBrush(UiTheme.FgDim);
                g.DrawString(monLabel, monFont, monBrush, mr.X + 3, mr.Y + 2);
            }

            var s = AppSettings.Current;
            int active = Math.Clamp(s.ActiveRegionSlot, 0, 7);

            // Draw slots
            for (int i = 0; i < 8; i++)
            {
                var slot = s.RegionSlots[i];
                if (slot.IsEmpty) continue;

                bool isActive = i == active;
                bool isSelected = i == _selectedSlot;
                Color fill = RegionSlotColors.GetFill(i);
                // Slightly stronger fill when selected in the list
                if (isSelected)
                    fill = Color.FromArgb(Math.Min(255, fill.A + 40), fill.R, fill.G, fill.B);

                using var brush = new SolidBrush(fill);
                using var pen = new Pen(
                    isActive ? UiTheme.AccentHot : (isSelected ? UiTheme.Fg : Color.FromArgb(180, 255, 255, 255)),
                    isActive ? 2.5f : 1.5f);

                using var labelFont = new Font("Segoe UI", 8f, FontStyle.Bold);
                using var labelBrush = new SolidBrush(UiTheme.Fg);

                if (slot.IsLassoMode)
                {
                    var pts = slot.GetLassoPoints();
                    if (pts.Count < 3) continue;
                    var mapped = new PointF[pts.Count];
                    for (int p = 0; p < pts.Count; p++)
                        mapped[p] = MapPt(pts[p].X, pts[p].Y);
                    g.FillPolygon(brush, mapped);
                    g.DrawPolygon(pen, mapped);
                    g.DrawString($"R{i + 1}", labelFont, labelBrush, mapped[0]);
                }
                else
                {
                    var r = slot.ToRectangle();
                    if (r.IsEmpty) continue;
                    var mr = MapRect(r);
                    if (slot.IsOvalMode)
                    {
                        g.FillEllipse(brush, mr);
                        g.DrawEllipse(pen, mr);
                    }
                    else
                    {
                        g.FillRectangle(brush, mr);
                        g.DrawRectangle(pen, mr.X, mr.Y, mr.Width, mr.Height);
                    }
                    g.DrawString($"R{i + 1}", labelFont, labelBrush, mr.X + 2, mr.Y + 1);
                }
            }

            // Empty-state hint
            bool any = false;
            for (int i = 0; i < 8; i++)
            {
                if (!s.RegionSlots[i].IsEmpty) { any = true; break; }
            }
            if (!any)
            {
                using var hintFont = new Font("Segoe UI", 9f);
                string msg = "No regions set yet.\nOpen the overlay (Shift+Tab) and draw a box.";
                var sz = g.MeasureString(msg, hintFont);
                using var hintBrush = new SolidBrush(UiTheme.FgDim);
                g.DrawString(msg, hintFont, hintBrush,
                    dest.X + (dest.Width - sz.Width) / 2,
                    dest.Y + (dest.Height - sz.Height) / 2);
            }
        }

        private static RectangleF FitRect(Size content, Rectangle bounds, int pad)
        {
            float availW = Math.Max(1, bounds.Width - pad * 2);
            float availH = Math.Max(1, bounds.Height - pad * 2);
            float scale = Math.Min(availW / content.Width, availH / content.Height);
            float w = content.Width * scale;
            float h = content.Height * scale;
            float x = bounds.X + (bounds.Width - w) / 2f;
            float y = bounds.Y + (bounds.Height - h) / 2f;
            return new RectangleF(x, y, w, h);
        }

        // ---- List owner-draw rows (headers/gutter from UiTheme.StyleListView) ----

        private void List_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
        {
            if (e.Item == null || e.SubItem == null)
            {
                e.DrawDefault = false;
                return;
            }

            bool selected = e.Item.Selected;
            // Selected = orange dim (focused or not — HideSelection is false).
            Color bg = selected
                ? UiTheme.AccentDim
                : (e.ItemIndex % 2 == 0 ? UiTheme.BgList : UiTheme.BgRaised);
            Color textColor = selected
                ? UiTheme.Fg
                : (e.Item.ForeColor.IsEmpty ? UiTheme.Fg : e.Item.ForeColor);

            // Last column fills to client right — no white strip after column resize.
            Rectangle cell = UiTheme.ExtendListViewLastColumnCell(sender as ListView, e);
            using (var b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, cell);

            if (selected && e.ColumnIndex == 0)
            {
                using var edge = new Pen(UiTheme.AccentHot, 2);
                e.Graphics.DrawLine(
                    edge,
                    e.Bounds.Left + 1, e.Bounds.Top + 2,
                    e.Bounds.Left + 1, e.Bounds.Bottom - 2);
            }

            if (e.ColumnIndex == 0 && e.Item.Tag is int slotIdx)
            {
                int chip = 12;
                int cx = e.Bounds.X + 6;
                int cy = e.Bounds.Y + (e.Bounds.Height - chip) / 2;
                using var chipBrush = new SolidBrush(RegionSlotColors.GetSolid(slotIdx));
                e.Graphics.FillRectangle(chipBrush, cx, cy, chip, chip);
                using var chipPen = new Pen(UiTheme.ButtonBorder);
                e.Graphics.DrawRectangle(chipPen, cx, cy, chip, chip);

                var textBounds = new Rectangle(
                    e.Bounds.X + chip + 12, e.Bounds.Y, Math.Max(4, e.Bounds.Width - chip - 14), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, textBounds,
                    textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix);
            }
            else
            {
                var textBounds = new Rectangle(
                    e.Bounds.X + 6, e.Bounds.Y, Math.Max(4, e.Bounds.Width - 8), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, textBounds,
                    textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix);
            }
        }

        private void SizeListColumns()
        {
            if (_list.Columns.Count < 4) return;
            _list.Columns[0].Width = 56;
            _list.Columns[1].Width = 110;
            _list.Columns[2].Width = 72;
            // Last column (Geometry) fills remaining width — no white SysHeader strip.
            UiTheme.FitListViewLastColumn(_list);
        }

        private void LayoutBottomButtons(Panel bottom)
        {
            int y = Math.Max(8, (bottom.ClientSize.Height - _btnRefresh.Height) / 2);
            _btnRefresh.Location = new Point(12, y);
            _btnClear.Location = new Point(_btnRefresh.Right + 8, y);
            if (_btnClose != null)
            {
                _btnClose.Location = new Point(
                    Math.Max(12, bottom.ClientSize.Width - _btnClose.Width - 12), y);
            }
        }

        private static Label MakeIntro(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgMuted,
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.TopLeft,
        };

        private static Label MakeSection(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgHeader,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 10, 0, 2),
        };

        private static Label MakeHint(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgMuted,
            Font = new Font("Segoe UI", 8.5f),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 2, 8, 4),
        };

        private static Button MakeButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(110, 32),
                Padding = new Padding(12, 4, 12, 4),
                Font = new Font("Segoe UI", 9f),
            };
            UiTheme.StyleButton(btn);
            return btn;
        }
    }
}
