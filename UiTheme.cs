using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Shared black / dark-ink UI with orange accents.
    /// Surfaces stay near-black; orange is for headers, selection, primary actions.
    /// </summary>
    public static class UiTheme
    {
        /// <summary>
        /// Strip internal engine brand names from strings shown in the UI
        /// (Analytics detail, Settings logs, etc.).
        /// Two-axis map: Local-LLM host brands → "Local-LLM"; Windows.Media.Ocr detect → "OCR".
        /// Never maps Kobold* → "OCR" (that conflates recognition with detect).
        /// </summary>
        public static string SanitizeUiEngineNames(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";

            // Longest host brands first, then detect brands.
            // Host brands always become canonical "Local-LLM" (case-insensitive match).
            // Detect brands become canonical "OCR".
            return text
                .Replace("KoboldCPP", "Local-LLM", StringComparison.OrdinalIgnoreCase)
                .Replace("KoboldCpp", "Local-LLM", StringComparison.OrdinalIgnoreCase)
                .Replace("Kobold", "Local-LLM", StringComparison.OrdinalIgnoreCase)
                .Replace("WinOCR", "OCR", StringComparison.OrdinalIgnoreCase)
                .Replace("WinOcr", "OCR", StringComparison.OrdinalIgnoreCase);
        }

        // ---- Surfaces (black / dark ink — no brown cast) ----
        public static readonly Color BgDeep = Color.FromArgb(8, 8, 10);
        public static readonly Color Bg = Color.FromArgb(14, 14, 16);
        public static readonly Color BgRaised = Color.FromArgb(22, 22, 26);
        public static readonly Color BgPanel = Color.FromArgb(20, 20, 24);
        public static readonly Color BgInput = Color.FromArgb(28, 28, 32);
        public static readonly Color BgList = Color.FromArgb(16, 16, 18);
        public static readonly Color BgStatus = Color.FromArgb(12, 12, 14);
        public static readonly Color BgBar = Color.FromArgb(10, 10, 12);

        // ---- Text (cool near-white / gray ink) ----
        public static readonly Color Fg = Color.FromArgb(236, 236, 240);
        public static readonly Color FgMuted = Color.FromArgb(150, 150, 158);
        public static readonly Color FgDim = Color.FromArgb(100, 100, 108);
        public static readonly Color FgHeader = Color.FromArgb(255, 152, 48);

        // ---- Accent (orange only) ----
        public static readonly Color Accent = Color.FromArgb(240, 128, 24);
        public static readonly Color AccentHot = Color.FromArgb(255, 160, 48);
        public static readonly Color AccentDim = Color.FromArgb(120, 64, 16);
        public static readonly Color AccentSoft = Color.FromArgb(70, 240, 128, 24);

        // ---- Buttons ----
        public static readonly Color Button = Color.FromArgb(32, 32, 36);
        public static readonly Color ButtonBorder = Color.FromArgb(90, 90, 98);
        public static readonly Color ButtonPrimary = Color.FromArgb(200, 100, 16);
        public static readonly Color ButtonPrimaryBorder = Color.FromArgb(255, 150, 40);

        // ---- Status ----
        public static readonly Color Ok = Color.FromArgb(130, 190, 110);
        public static readonly Color Warn = Color.FromArgb(255, 170, 70);
        public static readonly Color Bad = Color.FromArgb(255, 120, 100);

        // ---- Borders / lines ----
        public static readonly Color Border = Color.FromArgb(48, 48, 54);
        public static readonly Color GridLine = Color.FromArgb(36, 36, 42);

        /// <summary>
        /// Call once at process start (before first window) so system scrollbars /
        /// menus can use dark chrome with <see cref="ApplyDarkChromeTree"/>.
        /// </summary>
        public static void InitAppDarkMode()
        {
            try
            {
                // 0=Default 1=AllowDark 2=ForceDark 3=ForceLight (undocumented uxtheme)
                _ = SetPreferredAppMode(2);
                FlushMenuThemes();
            }
            catch
            {
                // Older Windows / missing ordinal — ignore.
            }
        }

        /// <summary>
        /// Apply surface + body text, dark title bar, dark scrollbars, and a
        /// sensible Tab order when the form is shown.
        /// </summary>
        public static void ApplyForm(Form form)
        {
            form.BackColor = Bg;
            form.ForeColor = Fg;
            ApplyDarkTitleBar(form);
            form.Shown -= Form_ShownChrome;
            form.Shown += Form_ShownChrome;
            // Also when handle appears (embedded forms may not fire Shown the same way).
            form.HandleCreated -= Form_HandleCreatedChrome;
            form.HandleCreated += Form_HandleCreatedChrome;
        }

        private static void Form_HandleCreatedChrome(object? sender, EventArgs e)
        {
            if (sender is Control c)
                ApplyDarkChromeTree(c);
        }

        private static void Form_ShownChrome(object? sender, EventArgs e)
        {
            if (sender is not Control c || c.IsDisposed)
                return;
            ApplyDarkChromeTree(c);
            ApplyTabOrder(c);
        }

        /// <summary>
        /// Windows 10/11: immersive dark mode + (Win11) caption/border colors.
        /// Safe no-op on older builds or if DWM rejects the attributes.
        /// </summary>
        public static void ApplyDarkTitleBar(Form form)
        {
            if (form == null)
                return;

            void apply()
            {
                if (form.IsDisposed || !form.IsHandleCreated)
                    return;
                IntPtr hwnd = form.Handle;
                // 20 = Win10 20H1+; 19 = older 1809 builds
                TryDwmBool(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, true);
                TryDwmBool(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, true);
                // Win11: paint caption/border to match ink theme (COLORREF BGR).
                TryDwmColor(hwnd, DWMWA_CAPTION_COLOR, BgBar);
                TryDwmColor(hwnd, DWMWA_BORDER_COLOR, Border);
                TryDwmColor(hwnd, DWMWA_TEXT_COLOR, Fg);
                AllowDarkModeForWindow(hwnd, true);
                TrySetWindowTheme(hwnd, "DarkMode_Explorer", null);
            }

            if (form.IsHandleCreated)
                apply();
            else
                form.HandleCreated += (_, _) => apply();
        }

        /// <summary>
        /// Dark system scrollbars / list chrome on a control tree (ListView,
        /// DataGridView, RichTextBox, AutoScroll panels, etc.).
        /// </summary>
        public static void ApplyDarkChromeTree(Control root)
        {
            if (root == null || root.IsDisposed)
                return;

            void wire(Control c)
            {
                if (c == null || c.IsDisposed)
                    return;

                void onHandle()
                {
                    if (c.IsDisposed || !c.IsHandleCreated)
                        return;
                    AllowDarkModeForWindow(c.Handle, true);
                    // DarkMode_Explorer darkens scrollbars on Win10 1809+ / Win11.
                    TrySetWindowTheme(c.Handle, "DarkMode_Explorer", null);
                }

                if (c.IsHandleCreated)
                    onHandle();
                else
                {
                    EventHandler? handler = null;
                    handler = (_, _) =>
                    {
                        c.HandleCreated -= handler!;
                        onHandle();
                    };
                    c.HandleCreated += handler;
                }

                c.ControlAdded -= ControlAdded_DarkChrome;
                c.ControlAdded += ControlAdded_DarkChrome;

                foreach (Control child in c.Controls)
                    wire(child);
            }

            wire(root);
        }

        private static void ControlAdded_DarkChrome(object? sender, ControlEventArgs e)
        {
            if (e.Control != null)
                ApplyDarkChromeTree(e.Control);
        }

        /// <summary>
        /// Assign <see cref="Control.TabIndex"/> in reading order (top→bottom,
        /// left→right) for keyboard Tab navigation. Skips pure layout chrome.
        /// Returns next free index after this tree (for chaining sections).
        /// </summary>
        public static int ApplyTabOrder(Control root, int startIndex = 0)
        {
            if (root == null || root.IsDisposed)
                return startIndex;

            var stops = new List<Control>();
            CollectTabStops(root, stops);

            stops.Sort((a, b) =>
            {
                try
                {
                    Point pa = a.PointToScreen(Point.Empty);
                    Point pb = b.PointToScreen(Point.Empty);
                    int dy = pa.Y - pb.Y;
                    // Same row tolerance (padding / DPI)
                    if (Math.Abs(dy) <= 14)
                        return pa.X.CompareTo(pb.X);
                    return dy;
                }
                catch
                {
                    return a.TabIndex.CompareTo(b.TabIndex);
                }
            });

            for (int i = 0; i < stops.Count; i++)
            {
                var c = stops[i];
                c.TabIndex = startIndex + i;
                // Interactive controls should receive Tab unless explicitly excluded.
                if (c is not Label and not LinkLabel)
                    c.TabStop = true;
            }

            return startIndex + stops.Count;
        }

        private static void CollectTabStops(Control parent, List<Control> list)
        {
            foreach (Control c in parent.Controls)
            {
                if (c == null || c.IsDisposed)
                    continue;

                // Layout / pure display containers — walk children only.
                if (IsLayoutContainer(c))
                {
                    CollectTabStops(c, list);
                    continue;
                }

                if (IsTabStopCandidate(c))
                    list.Add(c);

                // Composite editors own their children (don't Tab into internal edit).
                if (c is ComboBox or NumericUpDown or DateTimePicker or DomainUpDown)
                    continue;

                if (c.HasChildren)
                    CollectTabStops(c, list);
            }
        }

        private static bool IsLayoutContainer(Control c) =>
            c is Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer
                or GroupBox or TabControl or TabPage or Form;

        private static bool IsTabStopCandidate(Control c)
        {
            if (!c.Enabled)
                return false;

            // Labels / static text / pure paint surfaces
            if (c is Label || c is PictureBox || c is ProgressBar || c is ToolStrip)
                return false;

            // Typical interactive controls
            if (c is ButtonBase          // Button, CheckBox, RadioButton
                || c is TextBoxBase     // TextBox, RichTextBox, …
                || c is ListControl      // ComboBox, ListBox, …
                || c is ListView
                || c is TreeView
                || c is DataGridView
                || c is NumericUpDown
                || c is TrackBar
                || c is DateTimePicker
                || c is MonthCalendar
                || c is PropertyGrid)
                return true;

            // Custom controls that already opt into Tab
            return c.TabStop && c.CanFocus;
        }

        // https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        private static void TryDwmBool(IntPtr hwnd, int attr, bool value)
        {
            int v = value ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, attr, ref v, sizeof(int));
        }

        private static void TryDwmColor(IntPtr hwnd, int attr, Color color)
        {
            // COLORREF: 0x00bbggrr
            int colorref = color.R | (color.G << 8) | (color.B << 16);
            _ = DwmSetWindowAttribute(hwnd, attr, ref colorref, sizeof(int));
        }

        private static void TrySetWindowTheme(IntPtr hwnd, string? app, string? id)
        {
            try { _ = SetWindowTheme(hwnd, app, id); }
            catch { /* ignore */ }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(
            IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

        // Undocumented dark-mode helpers (ordinal exports; fail soft if missing).
        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        private static extern int SetPreferredAppMode(int preferredAppMode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        private static extern void FlushMenuThemes();

        [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        public static void StylePrimaryButton(Button btn)
        {
            btn.FlatStyle = FlatStyle.Flat;
            btn.BackColor = ButtonPrimary;
            btn.ForeColor = Fg;
            btn.FlatAppearance.BorderColor = ButtonPrimaryBorder;
            btn.Cursor = Cursors.Hand;
        }

        public static void StyleButton(Button btn)
        {
            btn.FlatStyle = FlatStyle.Flat;
            btn.BackColor = Button;
            btn.ForeColor = Fg;
            btn.FlatAppearance.BorderColor = ButtonBorder;
            btn.Cursor = Cursors.Hand;
        }

        public static void StyleCombo(ComboBox c)
        {
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = BgInput;
            c.ForeColor = Fg;
        }

        public static void StyleTextBox(TextBox t)
        {
            t.BackColor = BgInput;
            t.ForeColor = Fg;
            t.BorderStyle = BorderStyle.FixedSingle;
        }

        public static void StyleLabel(Label lbl, bool muted = false, bool header = false)
        {
            lbl.ForeColor = header ? FgHeader : muted ? FgMuted : Fg;
        }

        public static void StyleStatusLabel(Label lbl, bool bad = false)
        {
            lbl.BackColor = BgStatus;
            lbl.ForeColor = bad ? Bad : Ok;
        }

        public static void StyleSectionLabel(Label lbl)
        {
            lbl.ForeColor = FgHeader;
            lbl.Font = new Font(lbl.Font.FontFamily, Math.Max(8f, lbl.Font.Size - 0.5f), FontStyle.Bold);
        }

        /// <summary>Dark-ink DataGridView with orange selection (no white chrome).</summary>
        public static void StyleDataGridView(DataGridView grid)
        {
            // BgList matches ListView body so any unfilled edge stays ink, not white.
            grid.BackgroundColor = BgList;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = Border;
            grid.EnableHeadersVisualStyles = false;

            grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = BgInput,
                ForeColor = Fg,
                SelectionBackColor = AccentDim,
                SelectionForeColor = Fg,
                Font = grid.Font,
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 4, 0),
                WrapMode = DataGridViewTriState.False,
            };
            grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = BgRaised,
                ForeColor = Fg,
                SelectionBackColor = AccentDim,
                SelectionForeColor = Fg,
            };
            if (grid.ColumnHeadersVisible)
            {
                grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = BgRaised,
                    ForeColor = FgHeader,
                    SelectionBackColor = BgRaised,
                    SelectionForeColor = FgHeader,
                    Font = new Font(grid.Font, FontStyle.Bold),
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Padding = new Padding(8, 0, 4, 0),
                };
            }
        }

        /// <summary>
        /// Dark-ink list with orange selection; owner-draw headers avoid white chrome.
        /// The last column is kept flush with the client edge so the native SysHeader32
        /// never leaves a white dead zone after a column resize (see screenshot look/white.png).
        /// Shared by every themed ListView (Speech, Regions, …).
        /// </summary>
        /// <param name="list">Target list.</param>
        /// <param name="drawStandardRows">
        /// When true (default), wires standard row/cell owner-draw.
        /// When false, only headers + fit-last-column + selection baseline are wired —
        /// the caller supplies <see cref="ListView.DrawSubItem"/> (e.g. Regions color chips).
        /// Call <see cref="ExtendListViewLastColumnCell"/> from custom cell paint so the
        /// right edge stays themed if anything still peeks past the last cell.
        /// </param>
        public static void StyleListView(ListView list, bool drawStandardRows = true)
        {
            list.BackColor = BgList;
            list.ForeColor = Fg;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.OwnerDraw = true;
            list.HideSelection = false; // keep orange selection when unfocused
            list.FullRowSelect = true;
            list.GridLines = false;
            // Reduce flicker; private on ListView.
            try
            {
                typeof(ListView).InvokeMember(
                    "DoubleBuffered",
                    System.Reflection.BindingFlags.SetProperty
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic,
                    null,
                    list,
                    new object[] { true });
            }
            catch { /* optional */ }

            list.DrawColumnHeader -= List_DrawColumnHeader;
            list.DrawItem -= List_DrawItem;
            list.DrawSubItem -= List_DrawSubItem;
            list.ColumnWidthChanged -= List_ColumnWidthChanged;
            list.ColumnWidthChanging -= List_ColumnWidthChanging;
            list.Resize -= List_ResizeFitLast;
            list.HandleCreated -= List_HandleCreatedThemeHeader;
            list.DrawColumnHeader += List_DrawColumnHeader;
            list.DrawItem += List_DrawItem;
            list.ColumnWidthChanged += List_ColumnWidthChanged;
            list.ColumnWidthChanging += List_ColumnWidthChanging;
            list.Resize += List_ResizeFitLast;
            list.HandleCreated += List_HandleCreatedThemeHeader;
            if (drawStandardRows)
                list.DrawSubItem += List_DrawSubItem;
            // Dark scrollbars when the native handle exists.
            ApplyDarkChromeTree(list);
            if (list.IsHandleCreated)
            {
                ThemeListViewHeaderHwnd(list);
                FitListViewLastColumn(list);
            }
        }

        /// <summary>
        /// Stretch the last column so column widths fill the list client width.
        /// Eliminates the system-white SysHeader32 strip past the last header cell.
        /// Safe to call after manual column sizing.
        /// </summary>
        public static void FitListViewLastColumn(ListView list)
        {
            if (list == null || list.IsDisposed || list.Columns.Count == 0)
                return;
            if (_fittingLastColumn)
                return;

            int others = 0;
            for (int i = 0; i < list.Columns.Count - 1; i++)
                others += Math.Max(0, list.Columns[i].Width);

            int client = list.ClientSize.Width;
            if (client <= 0)
                return;
            // Vertical scrollbar steals client width when visible.
            if (ListViewHasVerticalScroll(list))
                client -= SystemInformation.VerticalScrollBarWidth;

            // Small inset for border so we don't force a phantom H-scroll.
            int want = Math.Max(48, client - others - 2);
            var last = list.Columns[list.Columns.Count - 1];
            if (Math.Abs(last.Width - want) <= 1)
                return;

            _fittingLastColumn = true;
            try
            {
                last.Width = want;
            }
            finally
            {
                _fittingLastColumn = false;
            }

            // Native header can lag one frame — force it to repaint dark.
            InvalidateListViewHeader(list);
            list.Invalidate(true);
        }

        /// <summary>
        /// For custom <see cref="ListView.DrawSubItem"/> handlers: widen the last column’s
        /// paint rect to the list client edge so resize does not leave a white strip.
        /// </summary>
        public static Rectangle ExtendListViewLastColumnCell(
            ListView? list, DrawListViewSubItemEventArgs e) =>
            ExtendLastColumnBounds(list, e);

        private static bool _fittingLastColumn;

        private static void List_ResizeFitLast(object? sender, EventArgs e)
        {
            if (sender is ListView list && !list.IsDisposed)
                FitListViewLastColumn(list);
        }

        private static void List_HandleCreatedThemeHeader(object? sender, EventArgs e)
        {
            if (sender is ListView list && !list.IsDisposed)
            {
                ThemeListViewHeaderHwnd(list);
                // Columns may already exist; seal the last one to the edge.
                list.BeginInvoke(new Action(() =>
                {
                    if (!list.IsDisposed)
                        FitListViewLastColumn(list);
                }));
            }
        }

        private static void List_ColumnWidthChanging(object? sender, ColumnWidthChangingEventArgs e)
        {
            // While dragging a non-last column, keep the last column soaking leftover
            // width so the white header strip never appears mid-drag.
            if (sender is not ListView list || list.IsDisposed || list.Columns.Count == 0)
                return;
            if (e.ColumnIndex == list.Columns.Count - 1)
                return; // last column is managed by FitListViewLastColumn
            if (_fittingLastColumn)
                return;

            int others = e.NewWidth;
            for (int i = 0; i < list.Columns.Count - 1; i++)
            {
                if (i == e.ColumnIndex)
                    continue;
                others += Math.Max(0, list.Columns[i].Width);
            }

            int client = list.ClientSize.Width;
            if (ListViewHasVerticalScroll(list))
                client -= SystemInformation.VerticalScrollBarWidth;
            int want = Math.Max(48, client - others - 2);

            _fittingLastColumn = true;
            try
            {
                list.Columns[list.Columns.Count - 1].Width = want;
            }
            finally
            {
                _fittingLastColumn = false;
            }
        }

        private static void List_ColumnWidthChanged(object? sender, ColumnWidthChangedEventArgs e)
        {
            if (_fittingLastColumn)
                return;
            if (sender is not ListView list || list.IsDisposed)
                return;
            FitListViewLastColumn(list);
        }

        private static bool ListViewHasVerticalScroll(ListView list)
        {
            try
            {
                // WS_VSCROLL = 0x00200000
                int style = GetWindowLong(list.Handle, GWL_STYLE);
                return (style & WS_VSCROLL) != 0;
            }
            catch
            {
                return list.Items.Count > 12; // rough fallback
            }
        }

        private static void ThemeListViewHeaderHwnd(ListView list)
        {
            try
            {
                if (!list.IsHandleCreated)
                    return;
                IntPtr header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (header == IntPtr.Zero)
                    return;
                AllowDarkModeForWindow(header, true);
                // DarkMode_ItemsView / Explorer both used by Win10/11 list headers.
                TrySetWindowTheme(header, "DarkMode_ItemsView", null);
                TrySetWindowTheme(header, "DarkMode_Explorer", null);
                ListHeaderPainter.Attach(header);
            }
            catch { /* older OS / no header */ }
        }

        private static void InvalidateListViewHeader(ListView list)
        {
            try
            {
                if (!list.IsHandleCreated)
                    return;
                IntPtr header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero)
                    InvalidateRect(header, IntPtr.Zero, true);
            }
            catch { /* ignore */ }
        }

        private static void List_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = false;

            using var bg = new SolidBrush(BgRaised);
            using var border = new Pen(Border);
            using var fg = new SolidBrush(FgHeader);
            e.Graphics.FillRectangle(bg, e.Bounds);
            e.Graphics.DrawRectangle(border, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            var rect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(4, e.Bounds.Width - 8), e.Bounds.Height);
            using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            e.Graphics.DrawString(e.Header?.Text ?? "", font, fg, rect, sf);
        }

        /// <summary>
        /// Native SysHeader32 painter: fills any residual area past the last column
        /// with raised ink so a white flash cannot linger during column drag.
        /// </summary>
        private sealed class ListHeaderPainter : NativeWindow
        {
            private static readonly Dictionary<IntPtr, ListHeaderPainter> Live = new();

            public static void Attach(IntPtr headerHwnd)
            {
                if (headerHwnd == IntPtr.Zero)
                    return;
                if (Live.ContainsKey(headerHwnd))
                    return;
                try
                {
                    var p = new ListHeaderPainter();
                    p.AssignHandle(headerHwnd);
                    Live[headerHwnd] = p;
                }
                catch { /* ignore */ }
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_PAINT = 0x000F;
                const int WM_ERASEBKGND = 0x0014;
                const int WM_NCPAINT = 0x0085;

                if (m.Msg == WM_ERASEBKGND)
                {
                    // Swallow default white erase; we paint solid ink in WM_PAINT.
                    m.Result = (IntPtr)1;
                    return;
                }

                base.WndProc(ref m);

                if (m.Msg is WM_PAINT or WM_NCPAINT)
                    PaintDeadZone();
            }

            private void PaintDeadZone()
            {
                try
                {
                    if (Handle == IntPtr.Zero)
                        return;
                    if (!GetClientRect(Handle, out RECT rc) || rc.Right <= rc.Left)
                        return;

                    // Sum header item widths to find the dead zone.
                    int count = (int)SendMessage(Handle, HDM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                    int used = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (Header_GetItemRect(Handle, i, out RECT item) && item.Right > used)
                            used = item.Right;
                    }

                    if (used >= rc.Right)
                        return;

                    using var g = Graphics.FromHwnd(Handle);
                    using var brush = new SolidBrush(BgRaised);
                    g.FillRectangle(brush, used, 0, rc.Right - used, rc.Bottom - rc.Top);
                    using var edge = new Pen(Border);
                    g.DrawLine(edge, used, rc.Bottom - 1, rc.Right, rc.Bottom - 1);
                }
                catch { /* ignore paint races */ }
            }

            private static bool Header_GetItemRect(IntPtr header, int index, out RECT rc)
            {
                rc = default;
                // HDM_GETITEMRECT = HDM_FIRST + 7
                return SendMessageRect(header, HDM_GETITEMRECT, (IntPtr)index, ref rc) != IntPtr.Zero;
            }
        }

        private const int LVM_GETHEADER = 0x101F;
        private const int HDM_FIRST = 0x1200;
        private const int HDM_GETITEMCOUNT = HDM_FIRST + 0;
        private const int HDM_GETITEMRECT = HDM_FIRST + 7;
        private const int GWL_STYLE = -16;
        private const int WS_VSCROLL = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageRect(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        private static void List_DrawItem(object? sender, DrawListViewItemEventArgs e)
        {
            // Owner-draw Details: always paint the full row background here.
            // Leaving this empty (DrawDefault=false only) can leave the last item
            // blank/clipped until the control is clicked and gets a full repaint.
            e.DrawDefault = false;
            if (e.Item == null)
                return;

            bool selected = e.Item.Selected;
            Color bg = selected
                ? AccentDim
                : (e.ItemIndex % 2 == 0 ? BgList : BgRaised);

            Rectangle bounds = e.Bounds;
            if (sender is ListView lv && bounds.Right < lv.ClientRectangle.Right)
                bounds.Width = Math.Max(bounds.Width, lv.ClientRectangle.Right - bounds.X);

            using var brush = new SolidBrush(bg);
            e.Graphics.FillRectangle(brush, bounds);
        }

        private static void List_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
        {
            // Solid section headers (e.g. Speech → Text rules stage bands).
            // Paint a continuous bar so Details-view column seams do not cut the title.
            if (e.Item?.Tag is ListSectionHeader header)
            {
                DrawListSectionHeader(sender as ListView, e, header.Title);
                return;
            }

            bool selected = e.Item?.Selected == true;
            Color bg = selected ? AccentDim : (e.ItemIndex % 2 == 0 ? BgList : BgRaised);
            Color fg = selected ? Fg : (e.Item?.ForeColor ?? Fg);

            Rectangle cell = ExtendLastColumnBounds(sender as ListView, e);

            using (var brush = new SolidBrush(bg))
                e.Graphics.FillRectangle(brush, cell);

            if (e.ColumnIndex == 0 && selected)
            {
                using var edge = new Pen(AccentHot, 2);
                e.Graphics.DrawLine(edge, e.Bounds.Left + 1, e.Bounds.Top + 2, e.Bounds.Left + 1, e.Bounds.Bottom - 2);
            }

            string text = e.SubItem?.Text ?? "";
            // Clip text to the real column width (not the extended gutter).
            var rect = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, Math.Max(4, e.Bounds.Width - 6), e.Bounds.Height);
            using var textBrush = new SolidBrush(fg);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };
            e.Graphics.DrawString(text, e.Item?.Font ?? SystemFonts.DefaultFont, textBrush, rect, sf);
        }

        /// <summary>
        /// For the last column, widen the paint rect to the list client edge so
        /// empty space after column resize stays themed (not system white).
        /// </summary>
        private static Rectangle ExtendLastColumnBounds(ListView? list, DrawListViewSubItemEventArgs e)
        {
            Rectangle b = e.Bounds;
            if (list == null || e.Item == null)
                return b;
            int last = Math.Max(0, Math.Min(list.Columns.Count, e.Item.SubItems.Count) - 1);
            if (e.ColumnIndex != last)
                return b;
            int right = list.ClientSize.Width;
            if (right <= b.Right)
                return b;
            return new Rectangle(b.X, b.Y, right - b.X, b.Height);
        }

        private static void DrawListSectionHeader(
            ListView? list, DrawListViewSubItemEventArgs e, string title)
        {
            if (e.Item == null)
                return;

            // Full row to client right (includes gutter past last column).
            Rectangle row = e.Item.Bounds;
            if (list != null && list.ClientSize.Width > row.Right)
                row = new Rectangle(row.X, row.Y, list.ClientSize.Width - row.X, row.Height);

            using (var brush = new SolidBrush(BgRaised))
                e.Graphics.FillRectangle(brush, row);

            // Accent + title only after the last column so nothing paints over it.
            int lastCol = Math.Max(0, e.Item.SubItems.Count - 1);
            if (e.ColumnIndex != lastCol)
                return;

            using (var edge = new Pen(Accent, 3))
                e.Graphics.DrawLine(edge, row.Left, row.Top + 2, row.Left, row.Bottom - 2);
            using var font = new Font("Segoe UI", 8.25f, FontStyle.Bold);
            using var fg = new SolidBrush(FgHeader);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            var textRect = new Rectangle(row.X + 10, row.Y, Math.Max(8, row.Width - 14), row.Height);
            e.Graphics.DrawString(title ?? "", font, fg, textRect, sf);
        }
    }

    /// <summary>
    /// Mark a ListView row as a solid section header (owner-draw paints a continuous band
    /// with no column-line bleed). Put an instance in <see cref="ListViewItem.Tag"/>.
    /// </summary>
    public sealed class ListSectionHeader
    {
        public string Title { get; }
        public ListSectionHeader(string title) => Title = title ?? "";
    }

    /// <summary>
    /// TabControl painted in black/ink with orange selected tab — no white system tabs.
    /// </summary>
    public sealed class ThemedTabControl : TabControl
    {
        public ThemedTabControl()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            Appearance = TabAppearance.Normal;
            ItemSize = new Size(100, 32);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            Padding = new Point(10, 5);
            // Fully paint the chrome (header strip + frame). Page content is a child.
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Fg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(UiTheme.BgBar);

            // Content area under the tabs
            Rectangle page = DisplayRectangle;
            // Fill the strip above the page (and any side margins) already cleared to BgBar.
            using (var pageBg = new SolidBrush(UiTheme.Bg))
                g.FillRectangle(pageBg, page);

            using (var border = new Pen(UiTheme.Border))
            {
                // Frame around the page
                g.DrawRectangle(border, page.X - 1, page.Y - 1, page.Width + 1, page.Height + 1);
                // Hairline under the whole tab row
                int headerBottom = page.Y - 1;
                g.DrawLine(border, 0, headerBottom, Width, headerBottom);
            }

            for (int i = 0; i < TabCount; i++)
                DrawTab(g, i);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Avoid system white flash behind owner-drawn chrome.
            e.Graphics.Clear(UiTheme.BgBar);
        }

        private void DrawTab(Graphics g, int index)
        {
            if (index < 0 || index >= TabPages.Count)
                return;

            Rectangle r = GetTabRect(index);
            // Slight inset so tabs don't clip the border line
            r = new Rectangle(r.X, r.Y + 2, r.Width, Math.Max(1, r.Height - 2));

            bool selected = index == SelectedIndex;
            Color fill = selected ? UiTheme.BgRaised : UiTheme.BgBar;
            Color text = selected ? UiTheme.FgHeader : UiTheme.FgMuted;

            using (var brush = new SolidBrush(fill))
                g.FillRectangle(brush, r);

            if (selected)
            {
                // Orange underline on the active tab
                using var accent = new Pen(UiTheme.AccentHot, 2);
                int y = r.Bottom - 1;
                g.DrawLine(accent, r.Left + 6, y, r.Right - 6, y);

                // Soft top edge
                using var top = new Pen(UiTheme.Border);
                g.DrawLine(top, r.Left, r.Top, r.Right - 1, r.Top);
                g.DrawLine(top, r.Left, r.Top, r.Left, r.Bottom - 1);
                g.DrawLine(top, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1);
            }
            else
            {
                using var edge = new Pen(UiTheme.GridLine);
                g.DrawLine(edge, r.Right - 1, r.Top + 6, r.Right - 1, r.Bottom - 4);
            }

            string title = TabPages[index].Text ?? "";
            var textRect = new Rectangle(r.X + 2, r.Y, r.Width - 4, r.Height - 2);
            TextRenderer.DrawText(
                g,
                title,
                Font,
                textRect,
                text,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            Invalidate();
        }
    }

    /// <summary>
    /// Thin indeterminate progress strip for Image / Balloons (and similar) work.
    /// Dark track + sliding orange fill — matches <see cref="UiTheme"/> ink chrome.
    /// Call <see cref="BeginWork"/> / <see cref="EndWork"/> (nestable) around async ops.
    /// </summary>
    public sealed class ThemeProgressBar : Control
    {
        private readonly System.Windows.Forms.Timer _anim;
        private int _busyDepth;
        private float _phase; // 0..1 marquee position
        private const int PreferredHeight = 5;

        public ThemeProgressBar()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor,
                true);
            TabStop = false;
            Height = PreferredHeight;
            MinimumSize = new Size(40, PreferredHeight);
            MaximumSize = new Size(0, PreferredHeight);
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Bg;
            Cursor = Cursors.Default;

            _anim = new System.Windows.Forms.Timer { Interval = 18 };
            _anim.Tick += (_, _) =>
            {
                if (_busyDepth <= 0)
                    return;
                // Full cycle ~1.1s
                _phase += 0.018f;
                if (_phase > 1f)
                    _phase -= 1f;
                Invalidate();
            };
        }

        /// <summary>True while at least one BeginWork has not been paired with EndWork.</summary>
        public bool IsBusy => _busyDepth > 0;

        public void BeginWork()
        {
            if (IsDisposed)
                return;
            _busyDepth++;
            if (_busyDepth == 1)
            {
                _phase = 0f;
                try { _anim.Start(); } catch { /* ignore */ }
                Invalidate();
            }
        }

        public void EndWork()
        {
            if (IsDisposed)
                return;
            if (_busyDepth <= 0)
                return;
            _busyDepth--;
            if (_busyDepth == 0)
            {
                try { _anim.Stop(); } catch { /* ignore */ }
                Invalidate();
            }
        }

        /// <summary>Force idle (e.g. form closing mid-run).</summary>
        public void Reset()
        {
            _busyDepth = 0;
            try { _anim.Stop(); } catch { /* ignore */ }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _anim.Stop(); } catch { /* ignore */ }
                try { _anim.Dispose(); } catch { /* ignore */ }
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var bounds = ClientRectangle;
            if (bounds.Width < 4 || bounds.Height < 2)
                return;

            // Track
            using (var track = new SolidBrush(UiTheme.BgDeep))
                g.FillRectangle(track, bounds);
            using (var edge = new Pen(UiTheme.Border))
                g.DrawRectangle(edge, 0, 0, bounds.Width - 1, bounds.Height - 1);

            if (_busyDepth <= 0)
            {
                // Idle: faint accent tick at left so the strip reads as a progress slot.
                using var idle = new SolidBrush(UiTheme.AccentDim);
                int tip = Math.Max(2, bounds.Width / 28);
                g.FillRectangle(idle, 1, 1, tip, Math.Max(1, bounds.Height - 2));
                return;
            }

            // Marquee: ~28% width block that slides L→R with wrap.
            int barW = Math.Max(16, (int)(bounds.Width * 0.28f));
            float travel = bounds.Width + barW;
            float x = _phase * travel - barW;
            var fill = new RectangleF(x, 1, barW, Math.Max(1, bounds.Height - 2));

            // Soft leading edge
            using (var dim = new SolidBrush(UiTheme.AccentDim))
            {
                var trail = fill;
                trail.X -= barW * 0.35f;
                trail.Width = barW * 0.45f;
                g.FillRectangle(dim, trail);
            }
            using (var hot = new SolidBrush(UiTheme.Accent))
                g.FillRectangle(hot, fill);
            using (var tip = new SolidBrush(UiTheme.AccentHot))
            {
                var nose = fill;
                nose.X = fill.Right - Math.Max(3, barW * 0.12f);
                nose.Width = Math.Max(3, barW * 0.12f);
                g.FillRectangle(tip, nose);
            }
        }
    }
}
