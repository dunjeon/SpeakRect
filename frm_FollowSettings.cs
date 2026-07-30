using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Follow tab (Settings): size, shape, and offset for Follow / region 9
    /// (mouse-float capture). Speak via Region 9 hotkey; Enter only locks.
    /// Includes a live diagram of the capture shape vs. mouse offset.
    /// </summary>
    public sealed class frm_FollowSettings : Form
    {
        private readonly NumericUpDown _numWidth;
        private readonly NumericUpDown _numHeight;
        private readonly ComboBox _cmbShape;
        private readonly NumericUpDown _numOffsetX;
        private readonly NumericUpDown _numOffsetY;
        private readonly FollowPreviewPanel _preview;
        private readonly Label _lblStatus;
        private readonly Button _btnReset;
        private readonly Button? _btnClose;
        private readonly Action? _onRequestClose;
        private readonly bool _embedded;

        private bool _loading;
        private bool _dirty;
        private readonly System.Windows.Forms.Timer _diskSaveTimer;
        private bool _diskSavePending;

        private readonly Action? _onChanged;

        public frm_FollowSettings(
            Action? onChanged = null,
            bool embedded = false,
            Action? onRequestClose = null)
        {
            _onChanged = onChanged;
            _embedded = embedded;
            _onRequestClose = onRequestClose;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Follow (Region 9)";
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
                MinimumSize = new Size(720, 580);
                ClientSize = new Size(780, 620);
                TopMost = true;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
            }
            KeyPreview = true;
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Fg;
            Font = new Font("Segoe UI", 9.5f);

            // Memory knobs update every tick; disk write is debounced so spin stays snappy.
            _diskSaveTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _diskSaveTimer.Tick += (_, _) =>
            {
                _diskSaveTimer.Stop();
                FlushDiskSave(force: false);
            };

            // ---- Bottom bar first (Dock.Bottom so it is never clipped) ----
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 56,
                BackColor = UiTheme.BgBar,
                Padding = new Padding(12, 10, 12, 10),
            };
            _btnReset = MakeButton("Reset defaults");
            _btnReset.Click += (_, _) => Reset_Click();
            bottom.Controls.Add(_btnReset);
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

            // ---- Status strip above buttons ----
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
            };

            // ---- Scrollable body ----
            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(16, 14, 8, 8),
                BackColor = UiTheme.Bg,
            };

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                BackColor = UiTheme.Bg,
                Padding = new Padding(0, 0, 16, 12),
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int bodyRow = 0;
            void AddFull(Control c, int height)
            {
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                body.Controls.Add(c, 0, bodyRow);
                bodyRow++;
            }

            AddFull(MakeIntro(
                "Floating capture box that tracks the mouse.\n" +
                "Overlay: click FOLLOW to arm; Ctrl+click FOLLOW opens Settings → Follow. Speak = Follow hotkey."), 52);

            // ---- SIZE / SHAPE / OFFSET (left) + live diagram (right) ----
            var mid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = UiTheme.Bg,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320f));
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            mid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var fields = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = UiTheme.Bg,
                Padding = new Padding(0, 0, 12, 0),
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int fRow = 0;
            void AddFieldRow(Control label, Control field, int height = 40)
            {
                fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                fields.Controls.Add(label, 0, fRow);
                fields.Controls.Add(field, 1, fRow);
                fRow++;
            }

            void AddFieldFull(Control c, int height)
            {
                fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                fields.SetColumnSpan(c, 2);
                fields.Controls.Add(c, 0, fRow);
                fRow++;
            }

            AddFieldFull(MakeSection("SIZE"), 28);
            _numWidth = MakeNum(40, 4000, AppSettings.DefaultFollowWidth);
            _numHeight = MakeNum(20, 3000, AppSettings.DefaultFollowHeight);
            AddFieldRow(MakeLabel("Width (px)"), WrapField(_numWidth));
            AddFieldRow(MakeLabel("Height (px)"), WrapField(_numHeight));
            AddFieldFull(MakeHint("Capture region size in screen pixels."), 26);

            AddFieldFull(MakeSection("SHAPE"), 28);
            _cmbShape = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 180,
                Font = new Font("Segoe UI", 10f),
            };
            UiTheme.StyleCombo(_cmbShape);
            _cmbShape.Items.Add("Rectangle");
            _cmbShape.Items.Add("Ellipse (oval)");
            _cmbShape.SelectedIndex = 0;
            AddFieldRow(MakeLabel("Shape"), WrapField(_cmbShape));
            AddFieldFull(MakeHint("Rectangle or oval. Freehand lasso is only for drawn selections."), 30);

            AddFieldFull(MakeSection("OFFSET FROM CURSOR"), 28);
            _numOffsetX = MakeNum(-2000, 2000, AppSettings.DefaultFollowOffsetX);
            _numOffsetY = MakeNum(-2000, 2000, AppSettings.DefaultFollowOffsetY);
            AddFieldRow(MakeLabel("Offset X"), WrapField(_numOffsetX));
            AddFieldRow(MakeLabel("Offset Y"), WrapField(_numOffsetY));
            AddFieldFull(MakeHint(
                "Top-left of the region relative to the cursor.\n" +
                "+X = right of cursor · +Y = below cursor"), 40);

            _preview = new FollowPreviewPanel
            {
                Dock = DockStyle.Fill,
                MinimumSize = new Size(220, 280),
            };

            mid.Controls.Add(fields, 0, 0);
            mid.Controls.Add(_preview, 1, 0);

            // Fixed mid height so the diagram has room; fields stay on the left.
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 360f));
            body.Controls.Add(mid, 0, bodyRow);
            bodyRow++;

            AddFull(MakeSection("KEYS"), 28);
            AddFull(MakeHint(
                "Click FOLLOW  Arm / turn off floating follow\n" +
                "Ctrl+click FOLLOW  Open Settings → Follow tab\n" +
                "↑ / ↓  Float follow / stop (overlay keyboard)\n" +
                "Enter  Lock / unlock the box (does not speak)\n" +
                "Follow hotkey  Speak at mouse (default Shift+F9)"), 100);

            scroll.Controls.Add(body);

            // Dock order: Fill first, then Bottom controls (last added = outer edge)
            Controls.Add(scroll);
            Controls.Add(_lblStatus);
            Controls.Add(bottom);

            // Spin arrows / dropdown — commit + preview immediately.
            _numWidth.ValueChanged += (_, _) => OnFieldChanged();
            _numHeight.ValueChanged += (_, _) => OnFieldChanged();
            _numOffsetX.ValueChanged += (_, _) => OnFieldChanged();
            _numOffsetY.ValueChanged += (_, _) => OnFieldChanged();
            _cmbShape.SelectedIndexChanged += (_, _) => OnFieldChanged();

            // Enter / typing live in the *inner* edit boxes (ProcessDialogKey never
            // sees those keys while the UpDownEdit TextBox has focus).
            WireNumericEditor(_numWidth);
            WireNumericEditor(_numHeight);
            WireNumericEditor(_numOffsetX);
            WireNumericEditor(_numOffsetY);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    ApplyEditorsAndRefreshPreview();
                    return;
                }
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
                LoadFromSettings();
                // Inner TextBoxes are created with the handle — re-wire if needed.
                WireNumericEditor(_numWidth);
                WireNumericEditor(_numHeight);
                WireNumericEditor(_numOffsetX);
                WireNumericEditor(_numOffsetY);
            };
            FormClosing += (_, _) =>
            {
                if (_dirty || _diskSavePending)
                    Persist(writeDiskNow: true);
                try { _diskSaveTimer.Stop(); } catch { /* ignore */ }
                try { _diskSaveTimer.Dispose(); } catch { /* ignore */ }
            };
        }

        /// <summary>
        /// Enter before child controls eat it (KeyPreview is on).
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter || keyData == Keys.Return)
            {
                ApplyEditorsAndRefreshPreview();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Enter || keyData == (Keys.Enter | Keys.Modifiers)
                || (keyData & Keys.KeyCode) == Keys.Enter)
            {
                ApplyEditorsAndRefreshPreview();
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        /// <summary>
        /// Hook the spin box AND its internal edit TextBox. Focus lives on the
        /// TextBox while typing — form-level ProcessDialogKey alone is not enough.
        /// Safe to call more than once (handlers are removed before re-add).
        /// </summary>
        private void WireNumericEditor(NumericUpDown nud)
        {
            nud.KeyDown -= NumericEditor_KeyDown;
            nud.KeyDown += NumericEditor_KeyDown;
            nud.ControlAdded -= NumericEditor_ControlAdded;
            nud.ControlAdded += NumericEditor_ControlAdded;

            foreach (Control child in nud.Controls)
                AttachNumericEditChild(child);
        }

        private void NumericEditor_ControlAdded(object? sender, ControlEventArgs e)
        {
            if (e.Control != null)
                AttachNumericEditChild(e.Control);
        }

        private void AttachNumericEditChild(Control edit)
        {
            if (edit is not TextBox tb)
                return;
            tb.KeyDown -= NumericEditor_KeyDown;
            tb.KeyDown += NumericEditor_KeyDown;
            tb.TextChanged -= NumericEditor_TextChanged;
            tb.TextChanged += NumericEditor_TextChanged;
        }

        private void NumericEditor_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            ApplyEditorsAndRefreshPreview();
        }

        /// <summary>Live diagram while typing — no disk write until Enter / ValueChanged.</summary>
        private void NumericEditor_TextChanged(object? sender, EventArgs e)
        {
            if (_loading)
                return;
            RefreshPreviewFromEditorText();
        }

        /// <summary>
        /// Parse every size/offset spin box (including uncommitted typed text),
        /// save settings, and redraw the live preview. Called on Enter.
        /// </summary>
        public void ApplyEditorsAndRefreshPreview()
        {
            if (_loading)
                return;

            // Suppress per-field ValueChanged spam while we force-parse all four.
            _loading = true;
            try
            {
                ForceParseNumeric(_numWidth);
                ForceParseNumeric(_numHeight);
                ForceParseNumeric(_numOffsetX);
                ForceParseNumeric(_numOffsetY);
            }
            finally
            {
                _loading = false;
            }

            _dirty = true;
            Persist(writeDiskNow: true);
            _lblStatus.Text = "Saved · " + StatusLine(AppSettings.Current);
            UpdatePreview();
            try { _onChanged?.Invoke(); } catch { /* ignore host */ }
        }

        /// <summary>
        /// Force NumericUpDown to accept whatever is currently in its edit field.
        /// Uses the private ParseEditText path (same as leaving the control).
        /// </summary>
        private static void ForceParseNumeric(NumericUpDown nud)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(NumericUpDown).GetMethod("ParseEditText", flags)?.Invoke(nud, null);
                typeof(NumericUpDown).GetMethod("ValidateEditText", flags)?.Invoke(nud, null);
                typeof(NumericUpDown).GetMethod("UpdateEditText", flags)?.Invoke(nud, null);
            }
            catch
            {
                // Fallback: parse the inner TextBox ourselves.
                string text = ReadNumericEditText(nud);
                if (decimal.TryParse(text, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                        CultureInfo.CurrentCulture, out decimal parsed)
                    || decimal.TryParse(text, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out parsed))
                {
                    if (parsed < nud.Minimum) parsed = nud.Minimum;
                    if (parsed > nud.Maximum) parsed = nud.Maximum;
                    nud.Value = parsed;
                }
            }
        }

        private static string ReadNumericEditText(NumericUpDown nud)
        {
            foreach (Control child in nud.Controls)
            {
                if (child is TextBox tb)
                    return (tb.Text ?? string.Empty).Trim();
            }
            return (nud.Text ?? string.Empty).Trim();
        }

        /// <summary>Preview only — uses typed text even before Value is committed.</summary>
        private void RefreshPreviewFromEditorText()
        {
            _preview.SetGeometry(
                ReadIntFromEditor(_numWidth),
                ReadIntFromEditor(_numHeight),
                ReadIntFromEditor(_numOffsetX),
                ReadIntFromEditor(_numOffsetY),
                isEllipse: _cmbShape.SelectedIndex == 1);
        }

        private static int ReadIntFromEditor(NumericUpDown nud)
        {
            string text = ReadNumericEditText(nud);
            if (int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                    CultureInfo.CurrentCulture, out int v)
                || int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out v))
            {
                int min = (int)nud.Minimum;
                int max = (int)nud.Maximum;
                if (v < min) v = min;
                if (v > max) v = max;
                return v;
            }
            return (int)nud.Value;
        }

        private void LayoutBottomButtons(Panel bottom)
        {
            int btnH = _btnClose?.Height ?? _btnReset.Height;
            int y = Math.Max(8, (bottom.ClientSize.Height - btnH) / 2);
            _btnReset.Location = new Point(12, y);
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

        private static Label MakeLabel(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 0, 10, 0),
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

        private static Control WrapField(Control field)
        {
            var p = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 4, 0, 4),
            };
            field.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            field.Location = new Point(0, 4);
            p.Controls.Add(field);
            return p;
        }

        private static NumericUpDown MakeNum(int min, int max, int value) => new()
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = 140,
            Height = 28,
            BackColor = UiTheme.BgInput,
            ForeColor = UiTheme.Fg,
            BorderStyle = BorderStyle.FixedSingle,
            ThousandsSeparator = false,
            Font = new Font("Segoe UI", 10f),
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

        /// <summary>Refresh controls from <see cref="AppSettings"/> (e.g. after profile load).</summary>
        public void ReloadFromSettings() => LoadFromSettings();

        /// <summary>
        /// Commit spin-box edit text and write Follow knobs to <see cref="AppSettings"/>
        /// (profile save / Settings close — typed values may not have ValueChanged yet).
        /// </summary>
        public void FlushToSettings()
        {
            if (_loading) return;
            try
            {
                ForceParseNumeric(_numWidth);
                ForceParseNumeric(_numHeight);
                ForceParseNumeric(_numOffsetX);
                ForceParseNumeric(_numOffsetY);
            }
            catch { /* keep last Value */ }
            Persist(writeDiskNow: true);
        }

        private void LoadFromSettings()
        {
            _loading = true;
            try
            {
                var s = AppSettings.Current;
                s.NormalizeFollowSettings();
                _numWidth.Value = s.FollowWidth;
                _numHeight.Value = s.FollowHeight;
                _numOffsetX.Value = s.FollowOffsetX;
                _numOffsetY.Value = s.FollowOffsetY;
                _cmbShape.SelectedIndex = s.FollowIsEllipse ? 1 : 0;
                _dirty = false;
                _lblStatus.Text = StatusLine(s);
                UpdatePreview();
            }
            finally
            {
                _loading = false;
            }
        }

        private void OnFieldChanged()
        {
            if (_loading) return;
            _dirty = true;
            Persist(writeDiskNow: false);
            _lblStatus.Text = "Saved · " + StatusLine(AppSettings.Current);
            UpdatePreview();
            try { _onChanged?.Invoke(); } catch { /* ignore host */ }
        }

        private void UpdatePreview()
        {
            _preview.SetGeometry(
                (int)_numWidth.Value,
                (int)_numHeight.Value,
                (int)_numOffsetX.Value,
                (int)_numOffsetY.Value,
                isEllipse: _cmbShape.SelectedIndex == 1);
        }

        /// <summary>
        /// Write Follow knobs into <see cref="AppSettings.Current"/> immediately so the
        /// overlay updates live. Disk write is debounced unless <paramref name="writeDiskNow"/>.
        /// </summary>
        private void Persist(bool writeDiskNow = false)
        {
            var s = AppSettings.Current;
            s.FollowWidth = (int)_numWidth.Value;
            s.FollowHeight = (int)_numHeight.Value;
            s.FollowOffsetX = (int)_numOffsetX.Value;
            s.FollowOffsetY = (int)_numOffsetY.Value;
            s.FollowShape = _cmbShape.SelectedIndex == 1 ? "Ellipse" : "Rectangle";
            s.NormalizeFollowSettings();
            _dirty = false;

            if (writeDiskNow)
                FlushDiskSave(force: true);
            else
                ScheduleDiskSave();
        }

        private void ScheduleDiskSave()
        {
            _diskSavePending = true;
            try
            {
                _diskSaveTimer.Stop();
                _diskSaveTimer.Start();
            }
            catch { /* ignore */ }
        }

        private void FlushDiskSave(bool force = false)
        {
            try { _diskSaveTimer.Stop(); } catch { /* ignore */ }
            if (!force && !_diskSavePending)
                return;
            _diskSavePending = false;
            try { AppSettings.Current.Save(); } catch { /* keep in-memory */ }
        }

        private void Reset_Click()
        {
            _loading = true;
            try
            {
                _numWidth.Value = AppSettings.DefaultFollowWidth;
                _numHeight.Value = AppSettings.DefaultFollowHeight;
                _numOffsetX.Value = AppSettings.DefaultFollowOffsetX;
                _numOffsetY.Value = AppSettings.DefaultFollowOffsetY;
                _cmbShape.SelectedIndex = 0;
            }
            finally
            {
                _loading = false;
            }

            _dirty = true;
            Persist(writeDiskNow: true);
            _lblStatus.Text = "Reset to defaults · " + StatusLine(AppSettings.Current);
            UpdatePreview();
            try { _onChanged?.Invoke(); } catch { /* ignore */ }
        }

        private static string StatusLine(AppSettings s) =>
            $"{s.FollowWidth}×{s.FollowHeight}  {s.FollowShape}  " +
            $"offset ({s.FollowOffsetX}, {s.FollowOffsetY})  ·  " +
            $"speak {s.HotkeyFollowRegion.ToIniString()}";

        /// <summary>
        /// Diagram of the Follow capture box vs. mouse. Offset is top-left of the
        /// region relative to the cursor, so the mouse sits at (-ox, -oy) in box space.
        /// </summary>
        private sealed class FollowPreviewPanel : Panel
        {
            private int _w = AppSettings.DefaultFollowWidth;
            private int _h = AppSettings.DefaultFollowHeight;
            private int _ox = AppSettings.DefaultFollowOffsetX;
            private int _oy = AppSettings.DefaultFollowOffsetY;
            private bool _ellipse;

            public FollowPreviewPanel()
            {
                DoubleBuffered = true;
                BackColor = UiTheme.BgDeep;
                BorderStyle = BorderStyle.FixedSingle;
                ResizeRedraw = true;
            }

            public void SetGeometry(int width, int height, int offsetX, int offsetY, bool isEllipse)
            {
                _w = Math.Max(1, width);
                _h = Math.Max(1, height);
                _ox = offsetX;
                _oy = offsetY;
                _ellipse = isEllipse;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(BackColor);

                int pad = 18;
                int legendH = 36;
                var plot = new Rectangle(
                    pad,
                    pad + 18,
                    Math.Max(40, ClientSize.Width - pad * 2),
                    Math.Max(40, ClientSize.Height - pad * 2 - legendH - 18));

                using (var titleFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                using (var titleBrush = new SolidBrush(UiTheme.FgHeader))
                {
                    g.DrawString("PREVIEW", titleFont, titleBrush, pad, 6);
                }

                // Model space: box [0..W] x [0..H]; mouse at (-ox, -oy).
                float mouseX = -_ox;
                float mouseY = -_oy;

                float minX = Math.Min(0f, mouseX);
                float minY = Math.Min(0f, mouseY);
                float maxX = Math.Max(_w, mouseX);
                float maxY = Math.Max(_h, mouseY);

                // Room for the cursor marker so it never clips.
                const float markerPad = 14f;
                minX -= markerPad;
                minY -= markerPad;
                maxX += markerPad;
                maxY += markerPad;

                float contentW = Math.Max(1f, maxX - minX);
                float contentH = Math.Max(1f, maxY - minY);
                float scale = Math.Min(plot.Width / contentW, plot.Height / contentH);
                // Cap absurd zoom when W/H are tiny so the diagram stays readable.
                scale = Math.Min(scale, 2.5f);

                float usedW = contentW * scale;
                float usedH = contentH * scale;
                float originX = plot.X + (plot.Width - usedW) / 2f;
                float originY = plot.Y + (plot.Height - usedH) / 2f;

                PointF Map(float x, float y) => new(
                    originX + (x - minX) * scale,
                    originY + (y - minY) * scale);

                // Subtle grid
                using (var gridPen = new Pen(UiTheme.GridLine, 1f))
                {
                    for (int i = 1; i < 4; i++)
                    {
                        float gx = plot.X + plot.Width * i / 4f;
                        float gy = plot.Y + plot.Height * i / 4f;
                        g.DrawLine(gridPen, gx, plot.Y, gx, plot.Bottom);
                        g.DrawLine(gridPen, plot.X, gy, plot.Right, gy);
                    }
                }

                // Capture region (orange accent)
                PointF tl = Map(0, 0);
                PointF br = Map(_w, _h);
                var box = RectangleF.FromLTRB(
                    Math.Min(tl.X, br.X),
                    Math.Min(tl.Y, br.Y),
                    Math.Max(tl.X, br.X),
                    Math.Max(tl.Y, br.Y));

                using (var fill = new SolidBrush(Color.FromArgb(70, 240, 128, 24)))
                using (var edge = new Pen(UiTheme.AccentHot, 2f))
                {
                    if (_ellipse)
                    {
                        g.FillEllipse(fill, box);
                        g.DrawEllipse(edge, box);
                    }
                    else
                    {
                        g.FillRectangle(fill, box);
                        g.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
                    }
                }

                // Light cross at box center for orientation
                PointF center = Map(_w / 2f, _h / 2f);
                using (var centerPen = new Pen(Color.FromArgb(80, UiTheme.AccentHot), 1f) { DashStyle = DashStyle.Dot })
                {
                    g.DrawLine(centerPen, box.Left, center.Y, box.Right, center.Y);
                    g.DrawLine(centerPen, center.X, box.Top, center.X, box.Bottom);
                }

                // Dashed line from mouse → box top-left (the offset vector)
                PointF mouse = Map(mouseX, mouseY);
                PointF boxTl = Map(0, 0);
                using (var linkPen = new Pen(Color.FromArgb(160, UiTheme.Warn), 1.5f) { DashStyle = DashStyle.Dash })
                {
                    g.DrawLine(linkPen, mouse, boxTl);
                }

                // Mouse marker (cursor stand-in)
                DrawMouseMarker(g, mouse);

                // Labels
                using var labelFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
                using var mutedFont = new Font("Segoe UI", 7f);
                using var captureBrush = new SolidBrush(UiTheme.FgHeader);
                using var mouseBrush = new SolidBrush(UiTheme.Warn);
                using var mutedBrush = new SolidBrush(UiTheme.FgMuted);

                string capLabel = _ellipse ? "capture (oval)" : "capture";
                g.DrawString(capLabel, labelFont, captureBrush, box.X + 4, box.Y + 3);
                g.DrawString("mouse", labelFont, mouseBrush, mouse.X + 10, mouse.Y - 16);

                string sizeTxt = $"{_w}×{_h} px";
                string offTxt = $"offset ({_ox}, {_oy})";
                g.DrawString(sizeTxt, mutedFont, mutedBrush, pad, ClientSize.Height - legendH + 4);
                g.DrawString(offTxt, mutedFont, mutedBrush, pad, ClientSize.Height - legendH + 18);

                // Legend chips
                float lx = ClientSize.Width - pad - 118;
                float ly = ClientSize.Height - legendH + 6;
                using (var capChip = new SolidBrush(UiTheme.AccentDim))
                using (var mouseChip = new SolidBrush(UiTheme.Warn))
                {
                    g.FillRectangle(capChip, lx, ly, 12, 12);
                    g.FillEllipse(mouseChip, lx, ly + 16, 12, 12);
                }
                g.DrawString("region", mutedFont, mutedBrush, lx + 16, ly - 1);
                g.DrawString("cursor", mutedFont, mutedBrush, lx + 16, ly + 15);
            }

            private static void DrawMouseMarker(Graphics g, PointF p)
            {
                // Hotspot disc + crosshair + simple arrow head
                using (var glow = new SolidBrush(Color.FromArgb(50, UiTheme.Warn)))
                    g.FillEllipse(glow, p.X - 12, p.Y - 12, 24, 24);

                using (var core = new SolidBrush(UiTheme.Warn))
                using (var ring = new Pen(UiTheme.FgHeader, 1.5f))
                {
                    g.FillEllipse(core, p.X - 4.5f, p.Y - 4.5f, 9f, 9f);
                    g.DrawEllipse(ring, p.X - 4.5f, p.Y - 4.5f, 9f, 9f);
                }

                using (var cross = new Pen(UiTheme.FgHeader, 1.2f))
                {
                    g.DrawLine(cross, p.X - 11, p.Y, p.X - 6, p.Y);
                    g.DrawLine(cross, p.X + 6, p.Y, p.X + 11, p.Y);
                    g.DrawLine(cross, p.X, p.Y - 11, p.X, p.Y - 6);
                    g.DrawLine(cross, p.X, p.Y + 6, p.X, p.Y + 11);
                }

                // Tiny pointer arrow (classic cursor silhouette, simplified)
                PointF[] arrow =
                {
                    new(p.X, p.Y),
                    new(p.X + 1, p.Y + 14),
                    new(p.X + 5, p.Y + 10),
                    new(p.X + 10, p.Y + 16),
                    new(p.X + 12, p.Y + 14),
                    new(p.X + 7, p.Y + 8),
                    new(p.X + 12, p.Y + 8),
                };
                using (var arrowFill = new SolidBrush(UiTheme.Fg))
                using (var arrowEdge = new Pen(UiTheme.BgDeep, 1f))
                {
                    g.FillPolygon(arrowFill, arrow);
                    g.DrawPolygon(arrowEdge, arrow);
                }
            }
        }
    }
}
