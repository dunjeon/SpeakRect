using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Remap keyboard + gamepad bindings (Key Map tab inside Settings).
    /// Custom rows use the same capture path as built-ins (grid click → press keys / pad).
    /// A low-level keyboard hook is used during key capture so Win combos work.
    /// Profiles live on the parent Settings window, not here.
    /// </summary>
    public sealed class frm_HotkeyMap : Form
    {
        /// <summary>
        /// Keyboard = input trigger chord (RegisterHotKey).
        /// Gamepad = input pad button.
        /// SendChord = custom KeyTap output (the keys we inject).
        /// </summary>
        private enum CaptureTarget { None, Keyboard, Gamepad, SendChord }

        private readonly Action _onHotkeysChanged;
        private readonly Action? _onRequestClose;
        private readonly bool _embedded;
        private readonly Panel _body;
        private readonly Panel _headerBar;
        private readonly Label[] _headerLabels;
        private readonly DataGridView _grid;
        private readonly Label _status;
        private readonly Panel _bottom;
        private readonly Button _btnReset;
        private readonly Button _btnAddCustom;
        private readonly Button _btnEditCustom;
        private readonly Button _btnRemoveCustom;
        private readonly Button? _btnClose;

        /// <summary>Current grid model (built-in + custom), rebuilt with the list.</summary>
        private List<MapRowRef> _rows = new();

        private int _captureIndex = -1;
        private CaptureTarget _captureTarget = CaptureTarget.None;

        /// <summary>True while listening for a keyboard/gamepad binding.</summary>
        public bool IsCapturing => _captureTarget != CaptureTarget.None;

        /// <summary>Raised when capture starts or ends (so host can suppress gamepad actions).</summary>
        public event EventHandler? CaptureStateChanged;

        private PadEdgeState _capPrev;
        private readonly System.Windows.Forms.Timer _captureTimer = new() { Interval = 33 };

        // Low-level keyboard hook while capturing chords (Win + combos)
        private IntPtr _kbHook = IntPtr.Zero;
        private LowLevelKeyboardProc? _kbProc;
        private bool _hookApplying; // re-entrancy guard

        // Column indices
        private const int ColAction = 0;
        private const int ColKeyboard = 1;
        private const int ColGamepad = 2;
        private const int ColScope = 3;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private static readonly string[] HeaderTitles = { "Action", "Keyboard", "Gamepad", "Scope" };

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        /// <param name="onHotkeysChanged">Re-register bindings after a remap.</param>
        /// <param name="onBeforeProfileSave">Reserved / unused (profiles owned by Settings shell).</param>
        /// <param name="onAfterProfileLoad">Reserved / unused (profiles owned by Settings shell).</param>
        /// <param name="embedded">When true, host inside Settings (no window chrome / Close).</param>
        /// <param name="onRequestClose">Optional close request (Settings shell).</param>
        public frm_HotkeyMap(
            Action onHotkeysChanged,
            Action? onBeforeProfileSave = null,
            Action? onAfterProfileLoad = null,
            bool embedded = false,
            Action? onRequestClose = null)
        {
            _onHotkeysChanged = onHotkeysChanged;
            _ = onBeforeProfileSave;
            _ = onAfterProfileLoad;
            _embedded = embedded;
            _onRequestClose = onRequestClose;

            // Font-only auto-scale: avoid double-resizing of absolute layouts under PerMonitorV2.
            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Key Map";
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
                MinimumSize = new Size(720, 560);
                ClientSize = new Size(780, 640);
                TopMost = true;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = true;
            }
            KeyPreview = true;
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Fg;
            Font = new Font("Segoe UI", 9f);

            // ---- Custom header strip ----
            // Native ListView / DataGridView column headers stay blank on this
            // dark + DPI + SizableToolWindow setup. We draw titles ourselves.
            _headerBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = UiTheme.BgRaised,
                Padding = Padding.Empty,
            };
            _headerLabels = new Label[HeaderTitles.Length];
            var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            for (int i = 0; i < HeaderTitles.Length; i++)
            {
                var lbl = new Label
                {
                    Text = HeaderTitles[i],
                    AutoSize = false,
                    ForeColor = UiTheme.Fg,
                    BackColor = UiTheme.BgRaised,
                    Font = headerFont,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(8, 0, 4, 0),
                    // Temporary equal slices until LayoutHeaderLabels runs with real widths
                    Bounds = new Rectangle(i * 180, 0, 180, 32),
                };
                _headerLabels[i] = lbl;
                _headerBar.Controls.Add(lbl);
            }
            // Belt-and-suspenders: also paint titles on the panel itself so text
            // still appears even if a Label is zero-sized during early layout.
            _headerBar.Paint += HeaderBar_Paint;

            // ---- Grid (no native headers — titles live in _headerBar) ----
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                ColumnHeadersVisible = false, // critical: native headers never painted here
                BackgroundColor = UiTheme.BgList,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = UiTheme.Border,
                RowTemplate = { Height = 26 },
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ScrollBars = ScrollBars.Vertical,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable,
                EnableHeadersVisualStyles = false,
            };

            // Selection must be set on Default + Alternating + RowsDefault or
            // WinForms falls back to system blue/white for some states.
            var cellStyle = new DataGridViewCellStyle
            {
                BackColor = UiTheme.BgInput,
                ForeColor = UiTheme.Fg,
                SelectionBackColor = UiTheme.AccentDim,
                SelectionForeColor = UiTheme.Fg,
                Font = new Font("Segoe UI", 9f),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 4, 0),
                WrapMode = DataGridViewTriState.False,
            };
            _grid.DefaultCellStyle = cellStyle;
            _grid.RowsDefaultCellStyle = new DataGridViewCellStyle(cellStyle)
            {
                BackColor = UiTheme.BgInput,
                SelectionBackColor = UiTheme.AccentDim,
                SelectionForeColor = UiTheme.Fg,
            };
            _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle(cellStyle)
            {
                BackColor = UiTheme.BgRaised,
                SelectionBackColor = UiTheme.AccentDim,
                SelectionForeColor = UiTheme.Fg,
            };

            // Named columns with FillWeight for proportional sizing
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Action",
                HeaderText = "Action",
                FillWeight = 34,
                MinimumWidth = 140,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Keyboard",
                HeaderText = "Keyboard",
                FillWeight = 28,
                MinimumWidth = 110,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Gamepad",
                HeaderText = "Gamepad",
                FillWeight = 24,
                MinimumWidth = 100,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Scope",
                HeaderText = "Scope",
                FillWeight = 14,
                MinimumWidth = 70,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });

            _grid.CellMouseClick += Grid_CellMouseClick;
            _grid.CellMouseDown += Grid_CellMouseDown;
            _grid.ColumnWidthChanged += (_, _) => LayoutHeaderLabels();
            _grid.Resize += (_, _) => LayoutHeaderLabels();
            _grid.RowsAdded += (_, _) => BeginInvokeLayoutHeaders();
            _grid.Scroll += (_, _) => LayoutHeaderLabels();

            var rowMenu = new ContextMenuStrip();
            rowMenu.Items.Add("Clear keyboard (unbind)", null, (_, _) => ClearSelectedBinding(hotkey: true, gamepad: false));
            rowMenu.Items.Add("Clear gamepad (unbind)", null, (_, _) => ClearSelectedBinding(hotkey: false, gamepad: true));
            rowMenu.Items.Add("Clear both", null, (_, _) => ClearSelectedBinding(hotkey: true, gamepad: true));
            rowMenu.Items.Add(new ToolStripSeparator());
            rowMenu.Items.Add("Mouse speed…", null, (_, _) => EditSelectedMouseSpeed());
            rowMenu.Items.Add("Edit custom action…", null, (_, _) => EditSelectedCustom());
            rowMenu.Items.Add("Remove custom", null, (_, _) => RemoveSelectedCustom());
            rowMenu.Items.Add("Add custom…", null, (_, _) => AddCustom());
            _grid.ContextMenuStrip = rowMenu;
            _grid.CellDoubleClick += Grid_CellDoubleClick;

            // Body hosts header + grid so they share the same width for column alignment.
            // WinForms docks higher z-order first: add Fill first, then Top (so Top
            // claims space and Fill gets the remainder). BringToFront on Fill after
            // that would cover the header — never do that.
            _body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = UiTheme.BgInput,
            };
            _body.SuspendLayout();
            _grid.Dock = DockStyle.Fill;
            _headerBar.Dock = DockStyle.Top;
            _body.Controls.Add(_grid);      // Fill — lower z-order
            _body.Controls.Add(_headerBar); // Top — higher z-order, docks first
            _body.ResumeLayout(false);

            // ---- Status + bottom ----
            _status = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = 32,
                Padding = new Padding(12, 6, 12, 4),
                Text = "Ready.",
                ForeColor = UiTheme.Ok,
            };
            _bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = UiTheme.BgBar,
            };
            _btnReset = new Button
            {
                Text = "Reset defaults",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(120, 30),
                Padding = new Padding(10, 4, 10, 4),
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Button,
                ForeColor = UiTheme.Fg,
                Cursor = Cursors.Hand,
            };
            _btnReset.FlatAppearance.BorderColor = UiTheme.ButtonBorder;
            _btnReset.Click += BtnReset_Click;
            _btnAddCustom = MakeBottomButton("Add custom…", UiTheme.AccentDim);
            _btnEditCustom = MakeBottomButton("Edit action…", UiTheme.Button);
            _btnRemoveCustom = MakeBottomButton("Remove custom", Color.FromArgb(72, 28, 24));
            _btnAddCustom.Click += (_, _) => AddCustom();
            _btnEditCustom.Click += (_, _) => EditSelectedCustom();
            _btnRemoveCustom.Click += (_, _) => RemoveSelectedCustom();
            if (!_embedded)
            {
                _btnClose = new Button
                {
                    Text = "Close",
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    MinimumSize = new Size(90, 30),
                    Padding = new Padding(12, 4, 12, 4),
                };
                UiTheme.StylePrimaryButton(_btnClose);
                _btnClose.Click += (_, _) =>
                {
                    if (_onRequestClose != null)
                        _onRequestClose();
                    else
                        Close();
                };
            }
            _bottom.Controls.Add(_btnReset);
            _bottom.Controls.Add(_btnAddCustom);
            _bottom.Controls.Add(_btnEditCustom);
            _bottom.Controls.Add(_btnRemoveCustom);
            if (_btnClose != null)
                _bottom.Controls.Add(_btnClose);
            _bottom.Resize += (_, _) => LayoutBottomButtons();

            // Form dock order (add Fill first; later = higher z = docks first from edge):
            //   bottom bar at very bottom, status above it, body Fill
            SuspendLayout();
            Controls.Add(_body);       // Fill
            Controls.Add(_status);     // Bottom (lower z → sits above bottom bar)
            Controls.Add(_bottom);     // Bottom (higher z → claims bottom edge)
            ResumeLayout(true);

            KeyDown += Frm_KeyDown;
            _captureTimer.Tick += CaptureTimer_Tick;
            Resize += (_, _) =>
            {
                LayoutBottomButtons();
                LayoutHeaderLabels();
            };

            Load += (_, _) =>
            {
                _headerBar.Height = Math.Max(ScaleUi(30), Font.Height + 12);
                LayoutBottomButtons();
                _body.PerformLayout();
                PerformLayout();
                RebuildList();
                UpdatePadStatusReady();
                LayoutHeaderLabels();
            };
            Shown += (_, _) =>
            {
                PerformLayout();
                LayoutHeaderLabels();
            };
            FormClosing += (_, _) =>
            {
                CancelCapture();
                _captureTimer.Stop();
                UninstallKeyboardHook();
            };
            FormClosed += (_, _) =>
            {
                UninstallKeyboardHook();
                _captureTimer.Dispose();
            };
        }

        private void InstallKeyboardHook()
        {
            if (_kbHook != IntPtr.Zero)
                return;
            // Keep a rooted delegate so the GC cannot collect the hook callback.
            _kbProc = KeyboardHookProc;
            IntPtr hMod = GetModuleHandle(null);
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
            if (_kbHook == IntPtr.Zero)
                Debug.WriteLine(
                    $"[KeyMap] SetWindowsHookEx keyboard failed err={Marshal.GetLastWin32Error()}");
        }

        private void UninstallKeyboardHook()
        {
            if (_kbHook == IntPtr.Zero)
                return;
            UnhookWindowsHookEx(_kbHook);
            _kbHook = IntPtr.Zero;
            _kbProc = null;
        }

        /// <summary>
        /// Low-level hook so Win+ combos and multi-modifier chords reach capture
        /// (WinForms KeyDown never sees most Win combos).
        /// </summary>
        private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 &&
                !_hookApplying &&
                (_captureTarget is CaptureTarget.Keyboard or CaptureTarget.SendChord) &&
                (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                try
                {
                    var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)info.vkCode;
                    var chord = HotkeyChord.FromHookVk(vk);
                    if (!chord.IsEmpty)
                    {
                        _hookApplying = true;
                        try
                        {
                            // UI thread — hook may run on this thread for LL hooks in this process
                            if (IsHandleCreated && !IsDisposed)
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    if (_captureTarget is CaptureTarget.Keyboard or CaptureTarget.SendChord)
                                        ApplyCapturedChord(chord);
                                }));
                            }
                        }
                        finally
                        {
                            _hookApplying = false;
                        }
                        // Swallow so Win+= doesn't fire system shortcuts while mapping
                        return (IntPtr)1;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[KeyMap] hook: {ex.Message}");
                }
            }
            return CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        private void HeaderBar_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(UiTheme.BgRaised);

            int[] widths = GetHeaderColumnWidths(_headerBar.ClientSize.Width);
            int headerH = Math.Max(1, _headerBar.ClientSize.Height);
            using var font = new Font("Segoe UI", 9f, FontStyle.Bold);
            int x = 0;
            for (int i = 0; i < HeaderTitles.Length; i++)
            {
                int w = widths[i];
                var rect = new Rectangle(x + 8, 0, Math.Max(4, w - 12), headerH);
                TextRenderer.DrawText(
                    g,
                    HeaderTitles[i],
                    font,
                    rect,
                    UiTheme.FgHeader,
                    TextFormatFlags.Left
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix
                    | TextFormatFlags.SingleLine);
                x += w;
            }

            using var pen = new Pen(UiTheme.ButtonBorder);
            int y = headerH - 1;
            g.DrawLine(pen, 0, y, _headerBar.ClientSize.Width, y);
        }

        /// <summary>
        /// Column pixel widths for the header strip. Prefer live DGV widths;
        /// fall back to FillWeight proportions when the grid has not laid out yet.
        /// </summary>
        private int[] GetHeaderColumnWidths(int totalWidth)
        {
            var widths = new int[HeaderTitles.Length];
            if (totalWidth <= 0)
                totalWidth = Math.Max(400, ClientSize.Width);

            bool anyPositive = false;
            if (_grid is { IsDisposed: false } && _grid.Columns.Count >= HeaderTitles.Length)
            {
                int sum = 0;
                for (int i = 0; i < HeaderTitles.Length; i++)
                {
                    widths[i] = Math.Max(0, _grid.Columns[i].Width);
                    sum += widths[i];
                    if (widths[i] > 0) anyPositive = true;
                }
                if (anyPositive && sum > 0)
                {
                    // Stretch/shrink last column so the row matches the strip width
                    widths[HeaderTitles.Length - 1] += totalWidth - sum;
                    if (widths[HeaderTitles.Length - 1] < 40)
                        widths[HeaderTitles.Length - 1] = 40;
                    return widths;
                }
            }

            // Proportional fallback from column FillWeights (34/28/24/14)
            float[] weights = { 34f, 28f, 24f, 14f };
            float wsum = 100f;
            int used = 0;
            for (int i = 0; i < HeaderTitles.Length; i++)
            {
                if (i == HeaderTitles.Length - 1)
                    widths[i] = Math.Max(40, totalWidth - used);
                else
                {
                    widths[i] = Math.Max(40, (int)Math.Round(totalWidth * (weights[i] / wsum)));
                    used += widths[i];
                }
            }
            return widths;
        }

        /// <summary>
        /// Align custom header Labels with DataGridView column widths.
        /// </summary>
        private void LayoutHeaderLabels()
        {
            if (_headerBar.IsDisposed)
                return;

            int totalW = _headerBar.ClientSize.Width;
            if (totalW <= 0)
                totalW = Math.Max(400, ClientSize.Width);

            int headerH = _headerBar.ClientSize.Height;
            if (headerH <= 0)
                headerH = Math.Max(ScaleUi(30), Font.Height + 12);

            int[] widths = GetHeaderColumnWidths(totalW);
            int x = 0;
            for (int i = 0; i < HeaderTitles.Length; i++)
            {
                int w = Math.Max(40, widths[i]);
                _headerLabels[i].SetBounds(x, 0, w, headerH);
                _headerLabels[i].Text = HeaderTitles[i];
                _headerLabels[i].Visible = true;
                x += w;
            }

            _headerBar.Invalidate();
        }

        private void BeginInvokeLayoutHeaders()
        {
            if (!IsHandleCreated || IsDisposed)
                return;
            try
            {
                BeginInvoke(new Action(LayoutHeaderLabels));
            }
            catch (InvalidOperationException)
            {
                // handle not ready
            }
        }

        private Button MakeBottomButton(string text, Color back)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(100, 30),
                Padding = new Padding(10, 4, 10, 4),
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = UiTheme.Fg,
                Cursor = Cursors.Hand,
            };
            btn.FlatAppearance.BorderColor = UiTheme.ButtonBorder;
            return btn;
        }

        private void LayoutBottomButtons()
        {
            int y = Math.Max(8, (_bottom.ClientSize.Height - _btnReset.Height) / 2);
            int x = 12;
            _btnReset.Location = new Point(x, y);
            x += _btnReset.Width + 8;
            _btnAddCustom.Location = new Point(x, y);
            x += _btnAddCustom.Width + 8;
            _btnEditCustom.Location = new Point(x, y);
            x += _btnEditCustom.Width + 8;
            _btnRemoveCustom.Location = new Point(x, y);
            if (_btnClose != null)
            {
                _btnClose.Location = new Point(
                    Math.Max(12, _bottom.ClientSize.Width - _btnClose.Width - 12), y);
            }
        }

        private int ScaleUi(int px96)
        {
            float dpi = DeviceDpi > 0 ? DeviceDpi : 96f;
            return (int)Math.Round(px96 * (dpi / 96f));
        }

        /// <summary>Abort in-progress key/pad capture (e.g. before profile load).</summary>
        public void CancelActiveCapture() => CancelCapture();

        public void ReloadFromSettings()
        {
            CancelCapture();
            RebuildList();
            UpdatePadStatusReady();
        }

        private void SetStatus(string text, bool bad = false)
        {
            _status.Text = text;
            _status.ForeColor = bad
                ? UiTheme.Bad
                : UiTheme.Ok;
        }

        private void UpdatePadStatusReady()
        {
            int idx = Math.Clamp(AppSettings.Current.GamepadControllerIndex, 0, 3);
            bool connected = XInputPoller.IsControllerConnected(idx);
            string profile = AppSettings.Current.ActiveProfileName;
            string pad = connected
                ? $"Controller {idx} connected"
                : $"No controller on slot {idx}";
            _status.Text = $"Profile “{profile}”. {pad}.";
            _status.ForeColor = connected
                ? UiTheme.Ok
                : UiTheme.FgMuted;
        }

        private void RebuildList()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            var s = AppSettings.Current;
            _rows = HotkeyMapModel.BuildRows(s);
            foreach (var row in _rows)
            {
                // Keyboard column = INPUT trigger (when to fire)
                string chord = row.Getter(s).ToIniString();
                string pad;
                if (row.IsCustom && row.Custom != null && row.Custom.UsesAnalogStick)
                    pad = CustomActionCatalog.StickSourceDisplay(row.Custom.Action);
                else
                    pad = row.GamepadGetter(s).ToIniString();

                string scope = row.IsCustom
                    ? "Custom"
                    : (row.IsGlobal ? "Global" : "Overlay");

                // Action: KeyTap = send chord; mouse move/stick includes “spd N”
                string label;
                if (row.IsCustom && row.Custom != null)
                {
                    if (row.Custom.Action == CustomActionKind.KeyTap)
                    {
                        string send = string.IsNullOrWhiteSpace(row.Custom.Arg)
                            ? "(click to set send keys)"
                            : row.Custom.Arg.Trim();
                        label = "★ Send " + send;
                    }
                    else
                        label = "★ " + row.Custom.DisplayLabel; // includes · spd N when applicable
                }
                else
                    label = row.Label;

                int i = _grid.Rows.Add(
                    label,
                    string.IsNullOrEmpty(chord) ? "—" : chord,
                    string.IsNullOrEmpty(pad) ? "—" : pad,
                    scope);
                _grid.Rows[i].Tag = row.Id;
                if (row.IsCustom)
                {
                    _grid.Rows[i].DefaultCellStyle.ForeColor = UiTheme.Ok;
                    _grid.Rows[i].DefaultCellStyle.SelectionBackColor = UiTheme.AccentDim;
                    _grid.Rows[i].DefaultCellStyle.SelectionForeColor = UiTheme.Fg;
                }
            }
            ApplyCaptureRowStyle();
            _grid.ResumeLayout();
            LayoutHeaderLabels();
        }

        private bool TryGetRow(int index, out MapRowRef row)
        {
            row = default;
            if (index < 0 || index >= _rows.Count)
                return false;
            row = _rows[index];
            return true;
        }

        private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || !TryGetRow(e.RowIndex, out var row))
                return;
            if (!row.IsCustom || e.ColumnIndex != ColAction)
                return;
            // Speed-capable mouse → edit speed; otherwise change action type
            if (row.Custom != null && CustomActionCatalog.HasEditableSpeed(row.Custom.Action))
                EditMouseSpeedAt(e.RowIndex);
            else
                EditSelectedCustom();
        }

        private void AddCustom()
        {
            CancelCapture();
            if (AppSettings.Current.CustomHotkeys.Count >= CustomHotkeyBinding.MaxBindings)
            {
                SetStatus($"Max {CustomHotkeyBinding.MaxBindings} custom bindings.", bad: true);
                return;
            }

            var draft = new CustomHotkeyBinding
            {
                Action = CustomActionKind.KeyTap,
                Label = "",
                Arg = "",
            };
            // Type only — all key/pad binding happens on the grid like built-in rows
            if (!ShowCustomTypePicker(draft, isNew: true))
                return;

            draft.Id = AppSettings.Current.NextCustomHotkeyId();
            if (draft.UsesAnalogStick)
                draft.Gamepad = default;
            // Persist default speed so it's editable/visible immediately
            if (CustomActionCatalog.HasEditableSpeed(draft.Action) &&
                string.IsNullOrWhiteSpace(draft.Arg))
            {
                draft.Arg = SystemInput.FormatSpeed(SystemInput.DefaultMouseSpeed);
            }
            if (AppSettings.Current.AddCustomHotkey(draft) == null)
            {
                SetStatus("Could not add custom binding.", bad: true);
                return;
            }
            AppSettings.Current.PersistCustomHotkeys();
            RebuildList();
            int idx = _rows.FindIndex(r => r.Id == draft.Id);
            if (idx < 0)
            {
                NotifyChanged();
                return;
            }

            _grid.ClearSelection();
            _grid.Rows[idx].Selected = true;
            _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, idx);

            // Same flow as built-ins: immediately listen on the grid
            if (draft.Action == CustomActionKind.KeyTap)
            {
                // Action column = chord to send; then user binds Gamepad/Keyboard
                StartCapture(idx, CaptureTarget.SendChord);
            }
            else if (CustomActionCatalog.HasEditableSpeed(draft.Action))
            {
                // Let user tune speed first (default is gentle); stick has no pad capture
                SetStatus(
                    $"Added “{draft.DisplayLabel}”. Click Action to change speed, " +
                    (draft.UsesAnalogStick
                        ? "stick is automatic."
                        : "then Gamepad to bind."));
                if (!draft.UsesAnalogStick)
                    StartCapture(idx, CaptureTarget.Gamepad);
                else
                    NotifyChanged();
            }
            else
            {
                // Mouse click etc. — jump straight to gamepad bind (most common)
                StartCapture(idx, CaptureTarget.Gamepad);
            }
        }

        private void EditSelectedCustom()
        {
            CancelCapture();
            int index = SelectedRowIndex();
            if (!TryGetRow(index, out var row) || !row.IsCustom || row.Custom == null)
            {
                SetStatus("Select a custom (★) row to change its action type.", bad: true);
                return;
            }

            var draft = row.Custom.Clone();
            if (!ShowCustomTypePicker(draft, isNew: false))
                return;

            bool wasKeyTap = row.Custom.Action == CustomActionKind.KeyTap;
            row.Custom.Action = draft.Action;
            row.Custom.Label = "";
            if (draft.Action != CustomActionKind.KeyTap)
            {
                // Keep Arg as speed if mouse-move, else clear non-chord junk
                if (CustomActionCatalog.IsContinuous(draft.Action) &&
                    draft.Action is not (CustomActionKind.ScrollUp or CustomActionKind.ScrollDown))
                    row.Custom.Arg = draft.Arg;
                else
                    row.Custom.Arg = "";
            }
            // KeyTap keeps existing Arg (send chord) unless cleared by capture
            if (draft.UsesAnalogStick)
                row.Custom.Gamepad = default;

            AppSettings.Current.PersistCustomHotkeys();
            RebuildList();
            if (index < _grid.Rows.Count)
            {
                _grid.ClearSelection();
                _grid.Rows[index].Selected = true;
            }

            if (draft.Action == CustomActionKind.KeyTap &&
                (string.IsNullOrWhiteSpace(row.Custom.Arg) || !wasKeyTap))
            {
                StartCapture(index, CaptureTarget.SendChord);
            }
            else
            {
                SetStatus($"Action → “{row.Custom.DisplayLabel}”. Click Gamepad / Keyboard to bind input.");
                NotifyChanged();
            }
        }

        private void RemoveSelectedCustom()
        {
            CancelCapture();
            int index = SelectedRowIndex();
            if (!TryGetRow(index, out var row) || !row.IsCustom)
            {
                SetStatus("Select a custom (★) row to remove.", bad: true);
                return;
            }

            if (MessageBox.Show(this,
                    $"Remove custom binding “{row.Label}”?",
                    "Remove custom", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                != DialogResult.Yes)
                return;

            AppSettings.Current.RemoveCustomHotkey(row.Id);
            AppSettings.Current.PersistCustomHotkeys();
            RebuildList();
            SetStatus($"Removed “{row.Label}”.");
            NotifyChanged();
        }

        /// <summary>
        /// Pick action type only (mouse / send-hotkey). Key &amp; gamepad capture
        /// always happens on the main Key Map grid — never in this dialog.
        /// </summary>
        private bool ShowCustomTypePicker(CustomHotkeyBinding draft, bool isNew)
        {
            using var dlg = new Form
            {
                Text = isNew ? "Add custom" : "Change action type",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(ScaleUi(440), ScaleUi(180)),
                BackColor = UiTheme.Bg,
                ForeColor = UiTheme.Fg,
                Font = Font,
                TopMost = true,
            };

            var lbl = new Label
            {
                Text = "Action type",
                AutoSize = true,
                Location = new Point(ScaleUi(16), ScaleUi(16)),
                ForeColor = UiTheme.FgMuted,
            };
            var cmb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(ScaleUi(16), ScaleUi(38)),
                Width = ScaleUi(400),
                BackColor = UiTheme.BgInput,
                ForeColor = UiTheme.Fg,
                FlatStyle = FlatStyle.Flat,
            };
            foreach (var info in CustomActionCatalog.All)
                cmb.Items.Add(info);
            cmb.DisplayMember = nameof(CustomActionCatalog.ActionInfo.Label);
            int sel = Array.FindIndex(CustomActionCatalog.All, a => a.Kind == draft.Action);
            cmb.SelectedIndex = sel >= 0 ? sel : 0;

            string defaultSpdText = SystemInput.FormatSpeed(SystemInput.DefaultMouseSpeed);
            var lblSpeed = new Label
            {
                Text = $"Mouse speed (default {defaultSpdText}) — move/stick only",
                AutoSize = true,
                Location = new Point(ScaleUi(16), ScaleUi(74)),
                ForeColor = UiTheme.FgMuted,
            };
            // New customs always start at the current default; edits keep saved Arg.
            string initialSpeed;
            if (isNew || string.IsNullOrWhiteSpace(draft.Arg) ||
                !CustomActionCatalog.HasEditableSpeed(draft.Action))
            {
                initialSpeed = CustomActionCatalog.HasEditableSpeed(draft.Action) || isNew
                    ? defaultSpdText
                    : "";
            }
            else
            {
                initialSpeed = draft.Arg.Trim();
            }

            var tbSpeed = new TextBox
            {
                Text = initialSpeed,
                Location = new Point(ScaleUi(16), ScaleUi(96)),
                Width = ScaleUi(120),
                BackColor = UiTheme.BgInput,
                ForeColor = UiTheme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = defaultSpdText,
            };

            var hint = new Label
            {
                AutoSize = false,
                Location = new Point(ScaleUi(150), ScaleUi(96)),
                Size = new Size(ScaleUi(266), ScaleUi(36)),
                ForeColor = UiTheme.Ok,
                Text = $"Lower = slower (default {defaultSpdText}). Click Action on the grid to change later.",
            };

            void SyncSpeedEnabled()
            {
                bool speed = cmb.SelectedItem is CustomActionCatalog.ActionInfo i &&
                    CustomActionCatalog.HasEditableSpeed(i.Kind);
                tbSpeed.Enabled = speed;
                lblSpeed.ForeColor = speed ? UiTheme.FgMuted : UiTheme.FgDim;
                // When picking a mouse-speed action, always show the current default if empty
                // (or when adding new — force default so old leftover values don't stick).
                if (speed)
                {
                    if (isNew || string.IsNullOrWhiteSpace(tbSpeed.Text))
                        tbSpeed.Text = defaultSpdText;
                }
            }
            cmb.SelectedIndexChanged += (_, _) =>
            {
                if (isNew &&
                    cmb.SelectedItem is CustomActionCatalog.ActionInfo info &&
                    CustomActionCatalog.HasEditableSpeed(info.Kind))
                {
                    tbSpeed.Text = defaultSpdText;
                }
                SyncSpeedEnabled();
            };
            SyncSpeedEnabled();

            var btnOk = new Button
            {
                Text = "OK",
                Location = new Point(ScaleUi(240), ScaleUi(140)),
                Size = new Size(ScaleUi(80), ScaleUi(28)),
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.ButtonPrimary,
                ForeColor = UiTheme.Fg,
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ScaleUi(330), ScaleUi(140)),
                Size = new Size(ScaleUi(80), ScaleUi(28)),
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Button,
                ForeColor = UiTheme.Fg,
            };
            btnOk.Click += (_, _) =>
            {
                if (cmb.SelectedItem is not CustomActionCatalog.ActionInfo info)
                    return;
                draft.Action = info.Kind;
                draft.Label = "";
                if (info.NeedsKeyArg)
                    draft.Arg = draft.Arg ?? ""; // set on grid via SendChord capture
                else if (CustomActionCatalog.HasEditableSpeed(info.Kind))
                {
                    string raw = (tbSpeed.Text ?? "").Trim();
                    float spd = SystemInput.ParseSpeed(raw, SystemInput.DefaultMouseSpeed);
                    draft.Arg = SystemInput.FormatSpeed(spd);
                }
                else
                    draft.Arg = "";
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };

            dlg.Controls.AddRange(new Control[]
            {
                lbl, cmb, lblSpeed, tbSpeed, hint, btnOk, btnCancel
            });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;
            return dlg.ShowDialog(this) == DialogResult.OK;
        }

        private void ApplyCaptureRowStyle()
        {
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                var row = _grid.Rows[i];
                bool capturing = _captureTarget != CaptureTarget.None && i == _captureIndex;
                if (capturing)
                {
                    row.DefaultCellStyle.BackColor = UiTheme.AccentDim;
                    row.DefaultCellStyle.ForeColor = UiTheme.FgHeader;
                    row.DefaultCellStyle.SelectionBackColor = UiTheme.Accent;
                    row.DefaultCellStyle.SelectionForeColor = UiTheme.Fg;
                    if (_captureTarget == CaptureTarget.Keyboard)
                        row.Cells[ColKeyboard].Value = "Press keys…";
                    if (_captureTarget == CaptureTarget.Gamepad)
                        row.Cells[ColGamepad].Value = "Press / tilt…";
                    if (_captureTarget == CaptureTarget.SendChord)
                        row.Cells[ColAction].Value = "★ Press keys to send…";
                }
                else
                {
                    // Clear capture highlight; keep custom-row green ink + themed selection.
                    bool isCustom = TryGetRow(i, out var mapRow) && mapRow.IsCustom;
                    row.DefaultCellStyle.BackColor = Color.Empty;
                    row.DefaultCellStyle.ForeColor = isCustom ? UiTheme.Ok : Color.Empty;
                    row.DefaultCellStyle.SelectionBackColor = UiTheme.AccentDim;
                    row.DefaultCellStyle.SelectionForeColor = UiTheme.Fg;
                }
            }
        }

        private void Grid_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0)
                return;
            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
        }

        private void Grid_CellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.RowIndex < 0)
                return;

            if (!TryGetRow(e.RowIndex, out var row))
                return;

            // Custom KeyTap: Action column = capture the chord we SEND
            if (e.ColumnIndex == ColAction &&
                row.IsCustom &&
                row.Custom != null &&
                row.Custom.Action == CustomActionKind.KeyTap)
            {
                StartCapture(e.RowIndex, CaptureTarget.SendChord);
                return;
            }

            // Custom mouse move/stick: Action column = edit speed
            if (e.ColumnIndex == ColAction &&
                row.IsCustom &&
                row.Custom != null &&
                CustomActionCatalog.HasEditableSpeed(row.Custom.Action))
            {
                EditMouseSpeedAt(e.RowIndex);
                return;
            }

            // Stick-mouse: gamepad is the whole stick
            if (e.ColumnIndex == ColGamepad &&
                row.IsCustom && row.Custom != null && row.Custom.UsesAnalogStick)
            {
                SetStatus(
                    "Stick-mouse uses the whole stick. Click the Action column to change mouse speed.",
                    bad: false);
                _status.ForeColor = UiTheme.FgMuted;
                return;
            }

            // Same as built-in rows
            if (e.ColumnIndex == ColGamepad)
                StartCapture(e.RowIndex, CaptureTarget.Gamepad);
            else if (e.ColumnIndex is ColKeyboard or ColAction or ColScope)
                StartCapture(e.RowIndex, CaptureTarget.Keyboard);
        }

        private void EditSelectedMouseSpeed()
        {
            int index = SelectedRowIndex();
            if (!TryGetRow(index, out var row) ||
                !row.IsCustom ||
                row.Custom == null ||
                !CustomActionCatalog.HasEditableSpeed(row.Custom.Action))
            {
                SetStatus("Select a custom mouse-move or stick-mouse row to set speed.", bad: true);
                return;
            }
            EditMouseSpeedAt(index);
        }

        /// <summary>
        /// Edit pixels/tick for a move or stick-mouse custom. Shown in Action as “spd N”.
        /// </summary>
        private void EditMouseSpeedAt(int index)
        {
            CancelCapture();
            if (!TryGetRow(index, out var row) || row.Custom == null)
                return;
            if (!CustomActionCatalog.HasEditableSpeed(row.Custom.Action))
            {
                SetStatus("This action has no mouse speed setting.", bad: true);
                return;
            }

            string defaultSpdText = SystemInput.FormatSpeed(SystemInput.DefaultMouseSpeed);
            // Blank / missing Arg → show default (12). Saved values (including old "4") keep working.
            float current = string.IsNullOrWhiteSpace(row.Custom.Arg)
                ? SystemInput.DefaultMouseSpeed
                : SystemInput.ParseSpeed(row.Custom.Arg, SystemInput.DefaultMouseSpeed);

            using var dlg = new Form
            {
                Text = "Mouse speed",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(ScaleUi(400), ScaleUi(190)),
                BackColor = UiTheme.Bg,
                ForeColor = UiTheme.Fg,
                Font = Font,
                TopMost = true,
            };

            var lbl = new Label
            {
                Text = $"Speed (pixels/tick, ~60/sec) — default {defaultSpdText}",
                AutoSize = true,
                Location = new Point(ScaleUi(16), ScaleUi(16)),
                ForeColor = UiTheme.FgMuted,
            };
            var num = new NumericUpDown
            {
                Location = new Point(ScaleUi(16), ScaleUi(42)),
                Width = ScaleUi(120),
                DecimalPlaces = 2,
                Minimum = (decimal)SystemInput.MinMouseSpeed,
                Maximum = (decimal)SystemInput.MaxMouseSpeed,
                Increment = 0.25m,
                Value = (decimal)Math.Clamp(
                    current, SystemInput.MinMouseSpeed, SystemInput.MaxMouseSpeed),
                BackColor = UiTheme.BgInput,
                ForeColor = UiTheme.Fg,
            };
            var btnDefault = new Button
            {
                Text = $"Use default ({defaultSpdText})",
                Location = new Point(ScaleUi(150), ScaleUi(40)),
                Size = new Size(ScaleUi(160), ScaleUi(28)),
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Button,
                ForeColor = UiTheme.Fg,
                Cursor = Cursors.Hand,
            };
            btnDefault.FlatAppearance.BorderColor = UiTheme.ButtonBorder;
            btnDefault.Click += (_, _) =>
            {
                num.Value = (decimal)SystemInput.DefaultMouseSpeed;
            };
            var hint = new Label
            {
                AutoSize = false,
                Location = new Point(ScaleUi(16), ScaleUi(80)),
                Size = new Size(ScaleUi(360), ScaleUi(48)),
                ForeColor = UiTheme.Ok,
                Text = $"Default is {defaultSpdText}. Try 2–4 for precise aiming, " +
                     $"{defaultSpdText} normal, 14+ faster. " +
                     "If this still shows an old value (e.g. 4), that was saved on the row — " +
                     "press “Use default” or type the new speed.",
            };
            var btnOk = new Button
            {
                Text = "OK",
                Location = new Point(ScaleUi(210), ScaleUi(148)),
                Size = new Size(ScaleUi(80), ScaleUi(28)),
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.ButtonPrimary,
                ForeColor = UiTheme.Fg,
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ScaleUi(298), ScaleUi(148)),
                Size = new Size(ScaleUi(80), ScaleUi(28)),
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Button,
                ForeColor = UiTheme.Fg,
            };
            btnOk.Click += (_, _) =>
            {
                float v = (float)num.Value;
                // Resolve from AppSettings list (not only the MapRowRef copy) so Arg
                // is always the object that Save / profiles write out.
                var live = AppSettings.Current.FindCustomHotkey(row.Id) ?? row.Custom;
                live.Arg = SystemInput.FormatSpeed(v);
                live.Label = "";
                // SpeakRect.ini + active named profile (if any)
                AppSettings.Current.PersistCustomHotkeys();
                RebuildList();
                if (index < _grid.Rows.Count)
                {
                    _grid.ClearSelection();
                    _grid.Rows[index].Selected = true;
                }
                SetStatus($"Mouse speed → {SystemInput.FormatSpeed(v)} px/tick (saved)");
                NotifyChanged();
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };

            dlg.Controls.AddRange(new Control[] { lbl, num, btnDefault, hint, btnOk, btnCancel });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;
            dlg.ShowDialog(this);
        }

        private int SelectedRowIndex()
        {
            if (_grid.CurrentCell != null)
                return _grid.CurrentCell.RowIndex;
            if (_grid.SelectedRows.Count > 0)
                return _grid.SelectedRows[0].Index;
            return -1;
        }

        private void ClearSelectedBinding(bool hotkey, bool gamepad)
        {
            CancelCapture();
            int index = SelectedRowIndex();
            if (!TryGetRow(index, out var row))
            {
                SetStatus("Select a row first, then clear.", bad: true);
                return;
            }

            var settings = AppSettings.Current;
            bool changed = false;
            if (hotkey && !row.Getter(settings).IsEmpty)
            {
                row.Setter(settings, default);
                changed = true;
            }
            if (gamepad)
            {
                if (row.IsCustom && row.Custom != null && row.Custom.UsesAnalogStick)
                {
                    // nothing to clear on pad for stick-mouse
                }
                else if (!row.GamepadGetter(settings).IsEmpty)
                {
                    row.GamepadSetter(settings, default);
                    changed = true;
                }
            }
            if (!changed)
            {
                SetStatus($"“{row.Label}” is already unbound for that input.", bad: false);
                _status.ForeColor = UiTheme.FgMuted;
                return;
            }
            settings.PersistCustomHotkeys();
            RebuildList();
            if (index < _grid.Rows.Count)
            {
                _grid.ClearSelection();
                _grid.Rows[index].Selected = true;
                _grid.CurrentCell = _grid.Rows[index].Cells[ColAction];
            }
            string what = hotkey && gamepad ? "keyboard + gamepad"
                : hotkey ? "keyboard"
                : "gamepad";
            SetStatus($"Cleared {what} for “{row.Label}” (—).");
            NotifyChanged();
        }

        private void StartCapture(int index, CaptureTarget target)
        {
            if (!TryGetRow(index, out var row))
                return;

            if (target == CaptureTarget.Gamepad &&
                row.IsCustom && row.Custom != null && row.Custom.UsesAnalogStick)
            {
                SetStatus("Stick-mouse uses the whole stick automatically.", bad: false);
                _status.ForeColor = UiTheme.FgMuted;
                return;
            }

            if (target == CaptureTarget.SendChord &&
                !(row.IsCustom && row.Custom != null && row.Custom.Action == CustomActionKind.KeyTap))
            {
                return;
            }

            // End previous capture
            if (_captureTarget != CaptureTarget.None)
            {
                _captureTimer.Stop();
                UninstallKeyboardHook();
                _captureTarget = CaptureTarget.None;
                _captureIndex = -1;
                RebuildList();
            }

            _captureTarget = target;
            _captureIndex = index;

            if (target is CaptureTarget.Keyboard or CaptureTarget.SendChord)
            {
                InstallKeyboardHook();
                if (target == CaptureTarget.SendChord)
                {
                    _status.Text =
                        "Listening for keys to SEND (Win+=, Ctrl+C, …)… Esc cancel, Del clear. " +
                        "Then click Gamepad to bind the trigger.";
                }
                else
                {
                    _status.Text =
                        $"Listening for “{row.Label}” (input trigger key)… Esc cancel, Del unbind";
                }
            }
            else
            {
                UninstallKeyboardHook();
                _capPrev.Clear();
                int cidx = Math.Clamp(AppSettings.Current.GamepadControllerIndex, 0, 3);
                // Drain current state so a held button doesn't immediately bind
                XInputPoller.TryGetRisingEdge(cidx, ref _capPrev, out _);
                _status.Text =
                    $"Listening for “{row.Label}” (gamepad)… press a button / D-pad / stick. Esc cancel, Del unbind";
                _captureTimer.Start();
            }

            _status.ForeColor = Color.Gold;
            ApplyCaptureRowStyle();
            _grid.ClearSelection();
            if (index < _grid.Rows.Count)
            {
                _grid.Rows[index].Selected = true;
                int col = target switch
                {
                    CaptureTarget.Gamepad => ColGamepad,
                    CaptureTarget.SendChord => ColAction,
                    _ => ColKeyboard
                };
                _grid.CurrentCell = _grid.Rows[index].Cells[col];
            }
            Focus();
            Activate();
            RaiseCaptureStateChanged();
        }

        private void CancelCapture()
        {
            if (_captureTarget == CaptureTarget.None) return;
            _captureTarget = CaptureTarget.None;
            _captureIndex = -1;
            _captureTimer.Stop();
            UninstallKeyboardHook();
            RebuildList();
            UpdatePadStatusReady();
            RaiseCaptureStateChanged();
        }

        private void RaiseCaptureStateChanged()
        {
            try { CaptureStateChanged?.Invoke(this, EventArgs.Empty); }
            catch { /* ignore subscriber errors */ }
        }

        private void CaptureTimer_Tick(object? sender, EventArgs e)
        {
            if (_captureTarget != CaptureTarget.Gamepad || _captureIndex < 0)
                return;
            int cidx = Math.Clamp(AppSettings.Current.GamepadControllerIndex, 0, 3);
            if (!XInputPoller.TryGetRisingEdge(cidx, ref _capPrev, out var button))
                return;
            ApplyGamepadCapture(button);
        }

        private void Frm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (_captureTarget == CaptureTarget.None)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    if (_onRequestClose != null)
                        _onRequestClose();
                    else if (!_embedded)
                        Close();
                }
                else if (e.KeyCode is Keys.Delete or Keys.Back)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    ClearSelectedBinding(hotkey: true, gamepad: false);
                }
                return;
            }

            e.Handled = true;
            e.SuppressKeyPress = true;

            if (e.KeyCode == Keys.Escape)
            {
                CancelCapture();
                return;
            }

            if (_captureTarget == CaptureTarget.Gamepad)
            {
                if (e.KeyCode is Keys.Delete or Keys.Back)
                    ApplyGamepadCapture(default);
                return;
            }

            // Keyboard / SendChord — Del clears; chords mainly arrive via LL hook
            // (also accept WinForms path as fallback when hook misses)
            if (e.KeyCode is Keys.Delete or Keys.Back)
            {
                ApplyCapturedChord(default);
                return;
            }

            var chord = HotkeyChord.FromKeyEvent(e);
            if (chord.IsEmpty)
                return;
            ApplyCapturedChord(chord);
        }

        /// <summary>
        /// Apply a captured chord to Keyboard (input) or SendChord (custom output).
        /// Claims the capture immediately so hook + KeyDown cannot double-apply.
        /// </summary>
        private void ApplyCapturedChord(HotkeyChord chord)
        {
            var target = _captureTarget;
            if (target is not (CaptureTarget.Keyboard or CaptureTarget.SendChord))
                return;

            if (target == CaptureTarget.SendChord)
                ApplySendChordCapture(chord);
            else
                ApplyKeyboardCapture(chord);
        }

        private void ApplySendChordCapture(HotkeyChord chord)
        {
            if (!TryGetRow(_captureIndex, out var row) ||
                !row.IsCustom ||
                row.Custom == null)
            {
                CancelCapture();
                return;
            }

            var live = AppSettings.Current.FindCustomHotkey(row.Id) ?? row.Custom;
            live.Action = CustomActionKind.KeyTap;
            live.Arg = chord.IsEmpty ? "" : chord.ToIniString();
            live.Label = "";
            AppSettings.Current.PersistCustomHotkeys();

            int done = _captureIndex;
            _captureTarget = CaptureTarget.None;
            _captureIndex = -1;
            _captureTimer.Stop();
            UninstallKeyboardHook();
            RebuildList();
            RaiseCaptureStateChanged();

            if (done >= 0 && done < _grid.Rows.Count)
            {
                _grid.ClearSelection();
                _grid.Rows[done].Selected = true;
                _grid.CurrentCell = _grid.Rows[done].Cells[ColAction];
            }

            if (chord.IsEmpty)
            {
                SetStatus("Cleared send keys for custom row.");
                NotifyChanged();
                return;
            }

            SetStatus(
                $"Send {chord.ToIniString()} — now press a gamepad button (or click Keyboard for a key trigger)…");
            // Immediately capture gamepad trigger — one continuous mapping flow
            if (done >= 0)
                StartCapture(done, CaptureTarget.Gamepad);
            else
                NotifyChanged();
        }

        private void ApplyKeyboardCapture(HotkeyChord chord)
        {
            if (!TryGetRow(_captureIndex, out var row))
            {
                CancelCapture();
                return;
            }

            var settings = AppSettings.Current;
            if (!chord.IsEmpty)
            {
                string? conflict = settings.FindHotkeyConflict(row.Id, chord);
                if (conflict != null)
                {
                    SetStatus($"Conflict with “{conflict}”. Try another combo.", bad: true);
                    return;
                }
            }

            row.Setter(settings, chord);
            // Bindings + custom Arg/chords → SpeakRect.ini and active profile
            settings.PersistCustomHotkeys();
            int done = _captureIndex;
            _captureTarget = CaptureTarget.None;
            _captureIndex = -1;
            _captureTimer.Stop();
            UninstallKeyboardHook();
            RebuildList();
            RaiseCaptureStateChanged();
            if (done >= 0 && done < _grid.Rows.Count)
            {
                _grid.ClearSelection();
                _grid.Rows[done].Selected = true;
                _grid.CurrentCell = _grid.Rows[done].Cells[ColKeyboard];
            }
            SetStatus(chord.IsEmpty
                ? $"Cleared keyboard trigger for “{row.Label}”"
                : $"Keyboard trigger → {chord.ToIniString()}");
            NotifyChanged();
        }

        private void ApplyGamepadCapture(GamepadButton button)
        {
            if (!TryGetRow(_captureIndex, out var row))
            {
                CancelCapture();
                return;
            }

            var settings = AppSettings.Current;
            if (!button.IsEmpty)
            {
                string? conflict = settings.FindGamepadConflict(row.Id, button);
                if (conflict != null)
                {
                    SetStatus($"Gamepad conflict with “{conflict}”. Try another button.", bad: true);
                    int cidx = Math.Clamp(settings.GamepadControllerIndex, 0, 3);
                    XInputPoller.TryGetRisingEdge(cidx, ref _capPrev, out _);
                    return;
                }
            }

            // Always write through the live CustomHotkeyBinding in settings
            if (row.IsCustom)
            {
                var live = settings.FindCustomHotkey(row.Id) ?? row.Custom;
                if (live != null)
                    live.Gamepad = button;
            }
            else
                row.GamepadSetter(settings, button);

            settings.PersistCustomHotkeys();
            int done = _captureIndex;
            _captureTarget = CaptureTarget.None;
            _captureIndex = -1;
            _captureTimer.Stop();
            UninstallKeyboardHook();
            RebuildList();
            RaiseCaptureStateChanged();
            if (done >= 0 && done < _grid.Rows.Count)
            {
                _grid.ClearSelection();
                _grid.Rows[done].Selected = true;
                _grid.CurrentCell = _grid.Rows[done].Cells[ColGamepad];
            }
            SetStatus(button.IsEmpty
                ? $"Cleared gamepad for “{row.Label}”"
                : $"Gamepad → {button.ToIniString()}" +
                  (row.IsCustom && row.Custom?.Action == CustomActionKind.KeyTap &&
                   !string.IsNullOrWhiteSpace(row.Custom.Arg)
                      ? $"  (sends {row.Custom.Arg})"
                      : ""));
            NotifyChanged();
        }

        private void NotifyChanged()
        {
            try { _onHotkeysChanged?.Invoke(); }
            catch (Exception ex)
            {
                SetStatus($"Saved, but re-register failed: {ex.Message}", bad: true);
            }
        }

        private void BtnReset_Click(object? sender, EventArgs e)
        {
            CancelCapture();
            if (MessageBox.Show(this,
                    "Reset keyboard hotkeys to built-in defaults, clear all gamepad bindings, " +
                    "and remove all custom system actions?",
                    "Reset bindings", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                != DialogResult.Yes)
                return;

            AppSettings.Current.ResetHotkeysToDefaults();
            AppSettings.Current.ResetGamepadToDefaults();
            AppSettings.Current.ClearCustomHotkeys();
            AppSettings.Current.PersistCustomHotkeys();
            RebuildList();
            SetStatus("Defaults restored (keyboard defaults, gamepad + custom cleared).");
            try { _onHotkeysChanged?.Invoke(); }
            catch { /* ignore */ }
        }
    }
}
