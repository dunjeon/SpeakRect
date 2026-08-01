using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Linq;

namespace SpeakRect
{
    public class frm_SpeakRect : Form
    {
        private enum CaptureMode { Rectangle, Ellipse, Lasso }

        private class SavedRegion
        {
            public CaptureMode Mode { get; set; } = CaptureMode.Rectangle;
            public Rectangle Rect { get; set; } = Rectangle.Empty;
            public List<Point> LassoPoints { get; set; } = new List<Point>();
        }

        private readonly Dictionary<Keys, SavedRegion> _savedRegions = new();
        private Keys _activeRectKey = Keys.F1;
        private CaptureMode _currentMode = CaptureMode.Rectangle;

        private Rectangle _currentRect = Rectangle.Empty;
        private Rectangle _currentEllipse = Rectangle.Empty;
        private List<Point> _currentLasso = new();

        private bool _isDrawing = false;
        /// <summary>
        /// When true, the left tool sidebar is not painted/hit-tested so the user
        /// can see and draw over the left edge of the screen (text can live there).
        /// Set when a shape tool is picked or a stroke starts; cleared on mouse-up,
        /// cancel, or Escape. Next mousedown hides again if needed.
        /// </summary>
        private bool _sidebarHiddenForDraw = false;
        private Point _drawStart;
        private Point _drawEnd;
        private List<Point> _drawLassoPoints = new();

        private OcrProcessor? _current;
        private NotifyIcon? _trayIcon;
        private ContextMenuStrip? _trayMenu;
        private Cursor? _defaultCursor;

        // RegisterHotKey ids (must stay unique and stable for WndProc dispatch)
        private const int HOTKEY_TOGGLE_OVERLAY = 9000;
        private const int HOTKEY_REGION_BASE = 9010; // +0..7 → slots 1..8
        private const int HOTKEY_FOLLOW = 9018;      // region 9 — mouse-float speak
        private const int HOTKEY_DEFAULT_MODE = 9019;
        private const int HOTKEY_COMIC = 9020;
        private const int HOTKEY_STOP_TTS = 9021;    // abort in-progress speech
        /// <summary>Custom system actions: 9100 + index into CustomHotkeys.</summary>
        private const int HOTKEY_CUSTOM_BASE = 9100;
        private const int HOTKEY_CUSTOM_MAX = CustomHotkeyBinding.MaxBindings;


        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        // Sidebar layout (client coords) — keep hit-tests in sync with OnPaint.
        // Derived bottom-up from shape stack so REGIONS / FOLLOW never collide with LASSO.
        private const int SidebarWidth = 108;
        private const int ShapeBtnX = 8;
        private const int ShapeBtnW = 92;
        private const int ShapeBtnH = 52;
        private const int ShapeGap = 8;
        private const int RectBtnY = 48;
        private const int OvalBtnY = RectBtnY + ShapeBtnH + ShapeGap;     // 108
        private const int LassoBtnY = OvalBtnY + ShapeBtnH + ShapeGap;    // 168
        // REGIONS (slots 1–8): section title + 4×2 numbered grid, then FOLLOW
        private const int AfterShapesGap = 14;
        private const int RegionSectionTitleH = 14;
        private const int RegionSectionY = LassoBtnY + ShapeBtnH + AfterShapesGap; // 234
        private const int RegionGridY = RegionSectionY + RegionSectionTitleH;      // 248
        private const int RegionCols = 4;
        private const int RegionRows = 2;
        private const int RegionBtnH = 22;
        private const int RegionBtnGap = 3;
        private const int RegionGridH =
            RegionRows * RegionBtnH + (RegionRows - 1) * RegionBtnGap; // 47
        private const int AfterRegionsGap = 12;
        private const int FollowBtnH = 36;
        private const int FollowBtnY =
            RegionGridY + RegionGridH + AfterRegionsGap; // 307
        // Mode flags start below FOLLOW with room for their own section titles
        private const int AfterFollowGap = 18;
        private const int FlagStackStartY = FollowBtnY + FollowBtnH + AfterFollowGap; // 361
        private const int FlagBtnH = 24;
        private const int FlagBtnGap = 3;
        private const int FlagSectionGap = 14;
        private const int SettingsBtnH = 28;
        // Bottom actions (pinned to strip bottom — Hide = tray, Exit = quit + Local-LLM host).
        private const int BottomActionBtnH = 30;
        private const int BottomActionGap = 6;
        private const int BottomActionMargin = 12;
        /// <summary>Build version line painted under EXIT (client px).</summary>
        private const int VersionLabelH = 16;
        private const int OpacityBlockH = 40; // title + track hit area above Hide

        // Overlay opacity slider (same range as Left/Right arrow keys).
        private const double OpacityMin = 0.1;
        private const double OpacityMax = 1.0;
        private const double OpacityStep = 0.1;
        private const int OpacityTrackH = 6;
        private const int OpacityThumbW = 10;
        private const int OpacityThumbH = 14;
        /// <summary>True while the user is dragging the sidebar opacity thumb.</summary>
        private bool _draggingOpacitySlider;

        private frm_Settings? _settingsForm;
        /// <summary>
        /// Fully opaque tool strip (own HWND). Overlay <see cref="Form.Opacity"/> dims
        /// only the capture veil — tools stay bold/clear like Settings.
        /// </summary>
        private OverlaySidebarChromeForm? _sidebarChrome;
        /// <summary>Set on UI thread; read from mouse-hook thread (no Form.Visible cross-thread).</summary>
        private volatile bool _settingsOpen;
        private XInputPoller? _gamepadPoller;

        private static IntPtr _mouseHookID = IntPtr.Zero;
        private static LowLevelInputHooks.LowLevelMouseProc? _mouseProc;

        private static IntPtr _keyboardHookID = IntPtr.Zero;
        private static LowLevelInputHooks.LowLevelKeyboardProc? _keyboardProc;

        /// <summary>Legacy alias for external/Interop consumers of POINT.</summary>
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
            public POINT(Point pt) { X = pt.X; Y = pt.Y; }
        }

        /// <summary>Follow session active (floating or locked). Overlay paints the box.</summary>
        private bool _followActive;

        /// <summary>
        /// When true, the follow box tracks the mouse (↑).
        /// When false with <see cref="_followActive"/>, the box is locked at its last position (Enter).
        /// </summary>
        public bool dynamic_rect = false;

        /// <summary>
        /// Region 9 follow box only — never written into F1–F8 slots or live draw fields.
        /// </summary>
        private Rectangle _followBox = Rectangle.Empty;

        /// <summary>Follow is armed (float or locked pin).</summary>
        private bool FollowActive => _followActive || dynamic_rect;

        public frm_SpeakRect()
        {
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 3840, 2160);
            TopMost = true;
            Opacity = 0.4;
            DoubleBuffered = true;

            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);

            KeyPreview = true;

            try
            {
                _defaultCursor = Cursor;
                Cursor = new Cursor(@"C:\Windows\Cursors\aero_arrow.cur");
            }
            catch { Cursor = Cursors.Hand; }

            Load += frm_SpeakRect_Load;
            KeyDown += frm_SpeakRect_KeyDown;
            // Keep the opaque tool chrome in sync when the overlay repaints (modes, follow, etc.).
            Invalidated += (_, _) => InvalidateSidebarChrome();
            // Settings already loaded in Program.Main before Application.Run
        }

        private void frm_SpeakRect_KeyDown(object? sender, KeyEventArgs e)
        {
            if (!Visible) return;

            var chord = HotkeyChord.FromKeyEvent(e);
            if (chord.IsEmpty) return;

            var s = AppSettings.Current;
            if (chord == s.HotkeyShapeRect)
            {
                SetMode(CaptureMode.Rectangle);
                e.Handled = true;
            }
            else if (chord == s.HotkeyShapeOval)
            {
                SetMode(CaptureMode.Ellipse);
                e.Handled = true;
            }
            else if (chord == s.HotkeyShapeLasso)
            {
                SetMode(CaptureMode.Lasso);
                e.Handled = true;
            }
        }

        private void frm_SpeakRect_Load(object? sender, EventArgs e)
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Show Overlay", null, (s, ev) => ShowOverlay());
            _trayMenu.Items.Add("Settings…", null, (s, ev) => OpenSettings());

            var profilesMenu = new ToolStripMenuItem("Profiles");
            profilesMenu.DropDownOpening += ProfilesMenu_DropDownOpening;
            _trayMenu.Items.Add(profilesMenu);

            _trayMenu.Items.Add("Exit", null, (s, ev) => ExitApplication());

            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                ContextMenuStrip = _trayMenu,
                Visible = true,
                Text = "SpeakRect"
            };
            _trayIcon.DoubleClick += (s, ev) => ShowOverlay();

            RegisterAllHotkeys();
            // Restore any region geometries saved in SpeakRect.ini / last profile snapshot.
            ApplyRegionsFromSettings();

            _gamepadPoller = new XInputPoller(OnGamepadAction, OnGamepadContinuous);
            _gamepadPoller.SyncFromSettings();

            _mouseProc = HookCallback;
            _mouseHookID = LowLevelInputHooks.SetMouseHook(_mouseProc);
            // Arrow keys while overlay is up (Follow / opacity) even if focus
            // slipped off the form after sidebar chrome clicks (WS_EX_NOACTIVATE).
            _keyboardProc = KeyboardHookCallback;
            _keyboardHookID = LowLevelInputHooks.SetKeyboardHook(_keyboardProc);
        }

        private void ProfilesMenu_DropDownOpening(object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem menu)
                return;

            menu.DropDownItems.Clear();

            string active = AppSettings.Current.ActiveProfileName;
            var names = AppSettings.ListProfiles();

            if (names.Length == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("(no saved profiles)") { Enabled = false });
            }
            else
            {
                foreach (string name in names)
                {
                    string captured = name;
                    var item = new ToolStripMenuItem(name)
                    {
                        Checked = name.Equals(active, StringComparison.OrdinalIgnoreCase),
                    };
                    item.Click += (_, _) => LoadProfileFromTray(captured);
                    menu.DropDownItems.Add(item);
                }
            }

            menu.DropDownItems.Add(new ToolStripSeparator());
            menu.DropDownItems.Add("Save current…", null, (_, _) => SaveProfileFromTray(saveAs: false));
            menu.DropDownItems.Add("Save as…", null, (_, _) => SaveProfileFromTray(saveAs: true));
            menu.DropDownItems.Add("Manage in Settings…", null, (_, _) => OpenSettings());
        }

        private void LoadProfileFromTray(string name)
        {
            if (!AppSettings.Current.LoadProfile(name, out string? error))
            {
                UiMessageBox.Show(
                    error ?? "Failed to load profile.",
                    "SpeakRect — Profile",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            ApplyFullProfileFromSettings();
            if (_settingsForm is { IsDisposed: false })
                _settingsForm.ReloadFromSettings();

            OcrProcessor.SpeakAnnouncement($"Profile {AppSettings.Current.ActiveProfileName}");
        }

        private void SaveProfileFromTray(bool saveAs)
        {
            string current = AppSettings.Current.ActiveProfileName;
            string? name = current;

            if (saveAs || string.IsNullOrWhiteSpace(name) || !AppSettings.ProfileExists(name))
            {
                name = PromptProfileNameTray(
                    saveAs ? "Save profile as…" : "Save profile…",
                    string.IsNullOrWhiteSpace(current) ? "Default" : current);
                if (name == null)
                    return;
            }

            // Push live overlay state (regions, shape tool) first. Mode flags already
            // live on AppSettings (and auto-sync to the active profile on toggle).
            SyncRegionsToSettings();
            AppSettings.Current.NormalizeModeFlags();
            // Follow/voice are already on AppSettings.Current when their panels save;
            // normalize so the profile file always has a clean [FOLLOW] block.
            AppSettings.Current.NormalizeFollowSettings();
            AppSettings.Current.NormalizeVoiceSettings();

            if (!AppSettings.Current.SaveProfile(name, out string? error))
            {
                UiMessageBox.Show(
                    error ?? "Failed to save profile.",
                    "SpeakRect — Profile",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (_settingsForm is { IsDisposed: false })
                _settingsForm.ReloadFromSettings();

            UiMessageBox.Show(
                $"Saved profile “{AppSettings.Current.ActiveProfileName}”.",
                "SpeakRect — Profile",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private string? PromptProfileNameTray(string title, string defaultName)
        {
            using var dlg = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                ClientSize = new Size(340, 110),
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                TopMost = true,
                Font = new Font("Segoe UI", 9f),
            };
            UiTheme.ApplyForm(dlg);
            var lbl = new Label
            {
                Text = "Profile name:",
                AutoSize = true,
                Location = new Point(12, 14),
                ForeColor = UiTheme.FgMuted,
            };
            var tb = new TextBox
            {
                Text = defaultName,
                Location = new Point(12, 36),
                Width = 316,
            };
            UiTheme.StyleTextBox(tb);
            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(162, 70),
                Width = 80,
            };
            UiTheme.StylePrimaryButton(ok);
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(248, 70),
                Width = 80,
            };
            UiTheme.StyleButton(cancel);
            dlg.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;

            if (dlg.ShowDialog() != DialogResult.OK)
                return null;

            if (!AppSettings.TryNormalizeProfileName(tb.Text, out string clean, out string? error))
            {
                UiMessageBox.Show(error ?? "Invalid name.", "SpeakRect — Profile",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            return clean;
        }

        /// <summary>Re-register hotkeys / gamepad after settings or profile change.</summary>
        private void ApplyBindingsFromSettings()
        {
            if (IsHandleCreated && !IsDisposed)
            {
                // Keep keyboard free for Key Map capture while Settings is open.
                if (!_settingsOpen)
                    RegisterAllHotkeys();
                _gamepadPoller?.SyncFromSettings();
                SyncGamepadSuppressionForSettings();
            }
            if (Visible)
                Invalidate();
        }

        /// <summary>
        /// After loading a profile: re-register hotkeys/gamepad, restore region
        /// geometries + shape tool, follow size/shape, voice UI, refresh overlay.
        /// </summary>
        private void ApplyFullProfileFromSettings()
        {
            ApplyBindingsFromSettings();
            ApplyRegionsFromSettings();

            // Follow box size/shape/offset come from [FOLLOW] in the profile.
            AppSettings.Current.NormalizeFollowSettings();
            if (FollowActive)
            {
                if (dynamic_rect)
                    UpdateDynamicRect(Cursor.Position);
                else if (!_followBox.IsEmpty)
                {
                    // Locked: keep top-left, apply new W/H from profile.
                    var s = AppSettings.Current;
                    _followBox = new Rectangle(
                        _followBox.X, _followBox.Y, s.FollowWidth, s.FollowHeight);
                }
            }

            if (_settingsForm is { IsDisposed: false })
                _settingsForm.ReloadFromSettings();

            if (Visible)
                Invalidate();
        }

        /// <summary>
        /// Copy live overlay regions / active slot / shape tool into
        /// <see cref="AppSettings"/> so profile + SpeakRect.ini snapshots include them.
        /// </summary>
        private void SyncRegionsToSettings()
        {
            // Commit the in-progress F1–F8 selection into the slot dictionary first.
            // Avoid recursion: SaveCurrentSelection also calls this — use internal helper.
            // Follow (R9) uses _followBox and is never part of this commit.
            CommitCurrentSelectionToDict(_activeRectKey);

            var s = AppSettings.Current;
            s.ActiveRegionSlot = Math.Clamp((int)_activeRectKey - (int)Keys.F1, 0, 7);
            s.ShapeMode = _currentMode switch
            {
                CaptureMode.Ellipse => "Ellipse",
                CaptureMode.Lasso => "Lasso",
                _ => "Rectangle",
            };

            for (int i = 0; i < 8; i++)
            {
                var slot = s.RegionSlots[i];
                Keys key = Keys.F1 + i;
                if (_savedRegions.TryGetValue(key, out var reg) && reg != null)
                {
                    if (reg.Mode == CaptureMode.Lasso && reg.LassoPoints.Count > 2)
                        slot.SetLasso(reg.LassoPoints);
                    else if (reg.Mode == CaptureMode.Ellipse && !reg.Rect.IsEmpty)
                        slot.SetBox("Oval", reg.Rect);
                    else if (reg.Mode == CaptureMode.Rectangle && !reg.Rect.IsEmpty)
                        slot.SetBox("Rect", reg.Rect);
                    else
                        slot.Clear();
                }
                else
                {
                    slot.Clear();
                }
            }
        }

        /// <summary>
        /// Rebuild overlay region dictionary + active selection from settings.
        /// Does not start OCR; only restores UI geometry.
        /// </summary>
        private void ApplyRegionsFromSettings()
        {
            var s = AppSettings.Current;
            _savedRegions.Clear();

            for (int i = 0; i < 8; i++)
            {
                var slot = s.RegionSlots[i];
                if (slot.IsEmpty) continue;

                var reg = new SavedRegion();
                if (slot.IsLassoMode)
                {
                    var pts = slot.GetLassoPoints();
                    if (pts.Count < 3) continue;
                    reg.Mode = CaptureMode.Lasso;
                    reg.LassoPoints = pts;
                    reg.Rect = Rectangle.Empty;
                }
                else if (slot.IsOvalMode)
                {
                    var r = slot.ToRectangle();
                    if (r.IsEmpty) continue;
                    reg.Mode = CaptureMode.Ellipse;
                    reg.Rect = r;
                }
                else
                {
                    var r = slot.ToRectangle();
                    if (r.IsEmpty) continue;
                    reg.Mode = CaptureMode.Rectangle;
                    reg.Rect = r;
                }

                _savedRegions[Keys.F1 + i] = reg;
            }

            int slotIdx = Math.Clamp(s.ActiveRegionSlot, 0, 7);
            _activeRectKey = Keys.F1 + slotIdx;

            // Prefer the active slot's saved geometry; otherwise fall back to ShapeMode.
            if (_savedRegions.TryGetValue(_activeRectKey, out var active) && active != null)
            {
                _currentMode = active.Mode;
                if (active.Mode == CaptureMode.Rectangle)
                {
                    _currentRect = active.Rect;
                    _currentEllipse = Rectangle.Empty;
                    _currentLasso.Clear();
                }
                else if (active.Mode == CaptureMode.Ellipse)
                {
                    _currentEllipse = active.Rect;
                    _currentRect = Rectangle.Empty;
                    _currentLasso.Clear();
                }
                else
                {
                    _currentLasso = new List<Point>(active.LassoPoints);
                    _currentRect = Rectangle.Empty;
                    _currentEllipse = Rectangle.Empty;
                }
            }
            else
            {
                _currentMode = s.ShapeMode switch
                {
                    "Ellipse" => CaptureMode.Ellipse,
                    "Lasso" => CaptureMode.Lasso,
                    _ => CaptureMode.Rectangle,
                };
                _currentRect = Rectangle.Empty;
                _currentEllipse = Rectangle.Empty;
                _currentLasso.Clear();
            }

            _isDrawing = false;
            _drawLassoPoints.Clear();
        }

        /// <summary>Unregister then register every global hotkey from AppSettings.</summary>
        private void RegisterAllHotkeys()
        {
            UnregisterAllHotkeys();

            var s = AppSettings.Current;
            TryRegister(HOTKEY_TOGGLE_OVERLAY, s.HotkeyToggleOverlay, "ToggleOverlay");
            for (int i = 0; i < 8; i++)
                TryRegister(HOTKEY_REGION_BASE + i, s.HotkeyRegions[i], $"Region{i + 1}");
            TryRegister(HOTKEY_FOLLOW, s.HotkeyFollowRegion, "Region9");
            TryRegister(HOTKEY_DEFAULT_MODE, s.HotkeyToggleDefaultMode, "ToggleDefaultMode");
            TryRegister(HOTKEY_COMIC, s.HotkeyToggleComicBook, "ToggleComicBook");
            TryRegister(HOTKEY_STOP_TTS, s.HotkeyStopTts, "StopTts");

            // User custom system actions (mouse, keys, window, media…)
            int n = Math.Min(s.CustomHotkeys.Count, HOTKEY_CUSTOM_MAX);
            for (int i = 0; i < n; i++)
            {
                var c = s.CustomHotkeys[i];
                if (c.Action == CustomActionKind.None || c.Keyboard.IsEmpty)
                    continue;
                TryRegister(HOTKEY_CUSTOM_BASE + i, c.Keyboard, c.Id);
            }
        }

        private void UnregisterAllHotkeys()
        {
            LowLevelInputHooks.UnregisterHotKey(this.Handle, HOTKEY_TOGGLE_OVERLAY);
            for (int i = 0; i < 8; i++)
                LowLevelInputHooks.UnregisterHotKey(this.Handle, HOTKEY_REGION_BASE + i);
            LowLevelInputHooks.UnregisterHotKey(this.Handle, HOTKEY_FOLLOW);
            LowLevelInputHooks.UnregisterHotKey(this.Handle, HOTKEY_DEFAULT_MODE);
            LowLevelInputHooks.UnregisterHotKey(this.Handle, HOTKEY_COMIC);
            LowLevelInputHooks.UnregisterHotKey(this.Handle, HOTKEY_STOP_TTS);
            for (int i = 0; i < HOTKEY_CUSTOM_MAX; i++)
                LowLevelInputHooks.UnregisterHotKey(this.Handle, HOTKEY_CUSTOM_BASE + i);
        }

        private void TryRegister(int id, HotkeyChord chord, string name)
        {
            if (chord.IsEmpty || !chord.IsGlobalCandidate)
            {
                System.Diagnostics.Debug.WriteLine($"[Hotkey] skip empty {name}");
                return;
            }

            bool ok = LowLevelInputHooks.RegisterHotKey(this.Handle, id, chord.Modifiers, (uint)chord.Key);
            if (!ok)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Hotkey] RegisterHotKey failed for {name}={chord.ToIniString()} " +
                    "(key may be in use by another app)");
            }
        }

        /// <summary>
        /// Toggle a mode flag from a global hotkey. Overlay visible → just refresh
        /// the UI. Overlay hidden → announce what changed via TTS (on and off).
        /// Default mode hotkey always <b>selects</b> Default (does not bounce to Comic
        /// if Default is already on). Other flags still toggle.
        /// </summary>
        private void ToggleModeFromHotkey(int flagIndex)
        {
            var s = AppSettings.Current;
            bool wasComic = s.ComicBook;

            if (flagIndex == AppSettings.FlagIndexDefault)
                s.SetFlag(flagIndex, true); // always enter Default mode
            else
                s.ToggleFlag(flagIndex);

            // POI suspended/restored with mode — refresh Balloons only (not whole Settings).
            try { _settingsForm?.ReloadBalloonsFromModeChange(); } catch { /* ignore */ }

            if (Visible)
            {
                Invalidate();
                return;
            }

            string phrase = OcrProcessor.DescribeModeChange(wasComic, s.ComicBook);
            OcrProcessor.SpeakAnnouncement(phrase);
        }

        /// <summary>True while Settings is open (keyboard hotkeys unregistered for Key Map capture).</summary>
        private bool IsSettingsOpen => _settingsOpen;

        /// <summary>True while the Settings tool window is open (blocks overlay drawing).</summary>
        private bool IsToolWindowOpen => _settingsOpen;

        /// <summary>True while Key Map is listening for a bind (gamepad actions suppressed).</summary>
        private bool IsHotkeyCaptureActive =>
            _settingsForm is { IsDisposed: false, IsCapturingHotkey: true };

        /// <summary>Abort any in-progress region draw without committing.</summary>
        private void CancelDrawing()
        {
            if (!_isDrawing && _drawLassoPoints.Count == 0)
            {
                // Still restore the sidebar if we were waiting for a draw after shape pick.
                ShowSidebar();
                return;
            }
            _isDrawing = false;
            _drawLassoPoints.Clear();
            ShowSidebar();
            if (Visible)
                Invalidate();
        }

        /// <summary>Open Settings on the user's last tab (or Key Map if none saved).</summary>
        private void OpenSettings() =>
            OpenSettings(frm_Settings.ParseSettingsTab(AppSettings.Current.LastSettingsTab));

        /// <summary>Open Settings on a specific tab (e.g. Follow / Analytics deep-links).</summary>
        private void OpenSettings(frm_Settings.SettingsTab tab)
        {
            // Drawing while settings is open fights with title-bar drags / clicks.
            CancelDrawing();
            _settingsOpen = true;

            // Push live overlay geometry into settings so Regions map matches what is drawn.
            SyncRegionsToSettings();

            // Drop keyboard RegisterHotKey so Key Map can capture chords freely.
            // Gamepad stays live for mouse-like navigation while Settings is open;
            // only suppressed while Key Map is actively listening for a bind.
            UnregisterAllHotkeys();
            SyncGamepadSuppressionForSettings();

            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                _settingsForm = new frm_Settings(
                    onHotkeysChanged: () => ApplyBindingsFromSettings(),
                    onBeforeProfileSave: SyncRegionsToSettings,
                    onAfterProfileLoad: ApplyFullProfileFromSettings,
                    onFollowChanged: OnFollowSettingsChanged,
                    onRegionsChanged: OnRegionsSettingsChanged,
                    onModeChanged: OnModeSettingsChanged,
                    captureActiveRegion: CaptureActiveRegionForPreviewAsync,
                    initialTab: tab);
                _settingsForm.HotkeyCaptureStateChanged += (_, _) =>
                    SyncGamepadSuppressionForSettings();
                _settingsForm.FormClosed += (_, _) =>
                {
                    _settingsOpen = false;
                    CancelDrawing();
                    if (_gamepadPoller != null)
                    {
                        _gamepadPoller.SuppressActions = false;
                        _gamepadPoller.ForcePoll = false;
                        _gamepadPoller.SyncFromSettings();
                    }
                    if (IsHandleCreated && !IsDisposed)
                        RegisterAllHotkeys();
                    _settingsForm = null;
                    if (Visible)
                        Invalidate();
                };
            }
            else
            {
                _settingsForm.SelectTab(tab);
                _settingsForm.ReloadFromSettings();
            }

            if (!_settingsForm.Visible)
                _settingsForm.Show(this);
            else
            {
                _settingsForm.Activate();
                _settingsForm.Focus();
            }
            _settingsOpen = true;
            SyncGamepadSuppressionForSettings();
        }

        /// <summary>
        /// Suppress gamepad-driven actions only while Key Map is capturing a bind.
        /// Keeps stick-mouse / continuous navigation working with Settings open.
        /// </summary>
        private void SyncGamepadSuppressionForSettings()
        {
            if (_gamepadPoller == null)
                return;
            bool capturing = IsHotkeyCaptureActive;
            _gamepadPoller.SuppressActions = capturing;
            // Capture UI reads pad via static TryGetRisingEdge; ForcePoll keeps the
            // poller timer warm so edge state stays coherent when bindings are empty.
            _gamepadPoller.ForcePoll = capturing || _settingsOpen;
            if (!capturing)
                _gamepadPoller.SyncFromSettings();
        }

        private void OpenFollowSettings() => OpenSettings(frm_Settings.SettingsTab.Follow);

        /// <summary>Apply region clear / external region edits from Settings → Regions.</summary>
        private void OnRegionsSettingsChanged()
        {
            ApplyRegionsFromSettings();
            if (Visible)
                Invalidate();
        }

        /// <summary>
        /// Balloons → POI (or other settings) changed mode — refresh MODE stack + Balloons UI.
        /// </summary>
        private void OnModeSettingsChanged()
        {
            AppSettings.Current.NormalizeModeFlags();
            try { _settingsForm?.ReloadBalloonsFromModeChange(); } catch { /* ignore */ }
            if (Visible)
                Invalidate();
        }

        /// <summary>Live-refresh mouse-follow rect when FOLLOW panel saves.</summary>
        private void OnFollowSettingsChanged()
        {
            if (FollowActive)
            {
                // Size/shape change: re-place at cursor when floating, or keep locked
                // top-left and apply new W/H when locked.
                if (dynamic_rect)
                    UpdateDynamicRect(Cursor.Position);
                else
                {
                    var s = AppSettings.Current;
                    s.NormalizeFollowSettings();
                    var cur = GetDynamicBounds();
                    if (!cur.IsEmpty)
                        _followBox = new Rectangle(cur.X, cur.Y, s.FollowWidth, s.FollowHeight);
                }
                if (Visible)
                    Invalidate();
            }
        }

        /// <summary>Dispatch a gamepad binding (row id from HotkeyMapRows or CustomN).</summary>
        private void OnGamepadAction(string rowId)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            // Custom system actions
            if (rowId.StartsWith("Custom", StringComparison.OrdinalIgnoreCase))
            {
                ExecuteCustomAction(rowId);
                return;
            }

            // Ensure UI-thread safety (timer already fires on UI thread via WinForms Timer).
            switch (rowId)
            {
                case "ToggleOverlay":
                    ToggleOverlayFromInput();
                    break;
                case "ToggleDefaultMode":
                    ToggleModeFromHotkey(AppSettings.FlagIndexDefault);
                    break;
                case "ToggleComicBook":
                    ToggleModeFromHotkey(AppSettings.FlagIndexComicBook);
                    break;
                case "StopTts":
                    AbortTtsInProgress();
                    break;
                case "ShapeRect":
                    if (Visible) SetMode(CaptureMode.Rectangle);
                    break;
                case "ShapeOval":
                    if (Visible) SetMode(CaptureMode.Ellipse);
                    break;
                case "ShapeLasso":
                    if (Visible) SetMode(CaptureMode.Lasso);
                    break;
                default:
                    if (rowId.Equals("Region9", StringComparison.OrdinalIgnoreCase))
                    {
                        SpeakFollowRegion();
                    }
                    else if (rowId.StartsWith("Region", StringComparison.Ordinal) &&
                        int.TryParse(rowId.AsSpan("Region".Length), out int n) &&
                        n is >= 1 and <= 8)
                    {
                        ActivateRegionSlot(n - 1);
                    }
                    break;
            }
        }

        /// <summary>Hold / continuous custom gamepad actions (mouse nudge, scroll).</summary>
        private void OnGamepadContinuous(CustomHotkeyBinding binding, float magnitude)
        {
            // Allow while Settings is open (mouse-like nav); only block during Key Map capture.
            if (IsDisposed || IsHotkeyCaptureActive)
                return;
            if (binding.Action == CustomActionKind.None)
                return;
            // Stick mouse is applied inside the poller; this handles discrete continuous kinds.
            if (binding.UsesAnalogStick)
                return;
            SystemInput.ExecuteContinuous(binding.Action, magnitude, binding.Arg);
        }

        private void ExecuteCustomAction(string rowId)
        {
            // Gamepad customs must work with Settings open; keyboard RegisterHotKey is
            // already unregistered while Settings is open so this is mainly gamepad.
            if (IsHotkeyCaptureActive)
                return;
            var c = AppSettings.Current.FindCustomHotkey(rowId);
            if (c == null || c.Action == CustomActionKind.None)
                return;
            // Continuous kinds still get an initial ExecuteOnce on edge (keyboard) /
            // but gamepad continuous path handles holds — for keyboard RegisterHotKey
            // auto-repeat will re-fire ExecuteOnce which is fine for move/scroll.
            SystemInput.ExecuteOnce(c.Action, c.Arg);
        }

        private void ExecuteCustomActionAt(int index)
        {
            if (IsHotkeyCaptureActive)
                return;
            var list = AppSettings.Current.CustomHotkeys;
            if (index < 0 || index >= list.Count)
                return;
            var c = list[index];
            if (c.Action == CustomActionKind.None)
                return;
            SystemInput.ExecuteOnce(c.Action, c.Arg);
        }

        private void ToggleOverlayFromInput()
        {
            if (Visible)
            {
                SaveCurrentSelection(_activeRectKey);
                HideToTray();
                _current?.Stop();
            }
            else
            {
                ShowOverlay();
            }
        }

        /// <param name="slotIndex">0..7 → logical F1..F8 region slots.</param>
        private void ActivateRegionSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex > 7)
                return;

            Keys newKey = Keys.F1 + slotIndex;
            if (newKey == _activeRectKey && Visible)
            {
                Invalidate();
                return;
            }

            // F1–F8 only — follow uses _followBox and is not part of this save.
            SaveCurrentSelection(_activeRectKey);

            _activeRectKey = newKey;
            // Switching to a fixed slot ends the follow session (R9 off).
            ClearFollowSession();
            LoadRegionIntoCurrent(_activeRectKey);

            if (!Visible)
            {
                _current?.Stop();
                bool hasSelection = false;
                Rectangle bounds = Rectangle.Empty;
                List<Point>? lasso = null;
                bool ellipse = false;

                if (_currentMode == CaptureMode.Rectangle && !_currentRect.IsEmpty)
                {
                    hasSelection = true;
                    bounds = _currentRect;
                }
                else if (_currentMode == CaptureMode.Ellipse && !_currentEllipse.IsEmpty)
                {
                    hasSelection = true;
                    bounds = _currentEllipse;
                    ellipse = true;
                }
                else if (_currentMode == CaptureMode.Lasso && _currentLasso.Count > 2)
                {
                    hasSelection = true;
                    bounds = GetBoundingRect(_currentLasso);
                    lasso = _currentLasso;
                }

                if (hasSelection)
                {
                    try { OcrProcessor.CancelBackgroundComicSpeak(); } catch { /* ignore */ }
                    try { _current?.Stop(); } catch { /* ignore */ }
                    _current = new OcrProcessor(bounds, lasso, ellipse);
                    Task.Run(() => _current.Start());
                }
            }
            else
            {
                Invalidate();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Don't steal arrows while Settings has the keyboard.
            if (IsToolWindowOpen)
                return base.ProcessCmdKey(ref msg, keyData);

            // Also handled by WH_KEYBOARD_LL so arrows work without form focus.
            // Keep ProcessCmdKey so focus-on-overlay path still works when the
            // hook is unavailable.
            Keys key = keyData & Keys.KeyCode;
            if ((keyData & Keys.Modifiers) == Keys.None)
            {
                if (key == Keys.Right)
                {
                    SetOverlayOpacity(Opacity + OpacityStep);
                    return true;
                }
                if (key == Keys.Left)
                {
                    SetOverlayOpacity(Opacity - OpacityStep);
                    return true;
                }
                if (key == Keys.Up)
                {
                    BeginFollowFloating();
                    return true;
                }
                if (key == Keys.Down)
                {
                    ClearFollowSession();
                    return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            // First launch uses Application.Run (not ShowOverlay) — still need the
            // opaque tool strip. Also covers hide-to-tray / re-show.
            SyncSidebarChrome();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Cursor = _defaultCursor ?? Cursors.Default;
            UnregisterAllHotkeys();
            try { _gamepadPoller?.Dispose(); } catch { /* ignore */ }
            _gamepadPoller = null;
            try { _settingsForm?.Close(); } catch { /* ignore */ }
            DisposeSidebarChrome();
            _trayIcon?.Dispose();
            LowLevelInputHooks.UnhookWindowsHookEx(_mouseHookID);
            if (_keyboardHookID != IntPtr.Zero)
            {
                LowLevelInputHooks.UnhookWindowsHookEx(_keyboardHookID);
                _keyboardHookID = IntPtr.Zero;
            }
            // Real exit (tray Exit / Application.Exit) — tear down Local-LLM host with us.
            // Hide-to-tray does not close the form, so Local-LLM keeps running there.
            LocalLlmHost.Stop();
            base.OnFormClosed(e);
        }

        /// <summary>
        /// Overlay arrow shortcuts without requiring form focus (sidebar chrome
        /// is NOACTIVATE and used to steal focus away from ProcessCmdKey).
        /// </summary>
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Visible && !IsToolWindowOpen &&
                (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var info = Marshal.PtrToStructure<LowLevelInputHooks.KBDLLHOOKSTRUCT>(lParam);
                // Ignore key-repeat with injected? No — handle normal and repeat.
                const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
                // Skip when a modifier is held (Ctrl/Alt/Shift/Win) so we don't
                // fight global hotkeys or text fields in other apps... wait — we
                // only want this when overlay is the interaction surface. Other
                // apps still get arrows unless we only fire when our form is
                // top-level interaction. Use: overlay visible AND (we are
                // foreground OR no other SpeakRect tool). Still steals arrows
                // from games under the veil — that is intended while overlay is up.
                bool mod =
                    (LowLevelInputHooks.GetAsyncKeyState(0x10) & 0x8000) != 0 || // Shift
                    (LowLevelInputHooks.GetAsyncKeyState(0x11) & 0x8000) != 0 || // Ctrl
                    (LowLevelInputHooks.GetAsyncKeyState(0x12) & 0x8000) != 0 || // Alt
                    (LowLevelInputHooks.GetAsyncKeyState(0x5B) & 0x8000) != 0 || // LWin
                    (LowLevelInputHooks.GetAsyncKeyState(0x5C) & 0x8000) != 0;   // RWin
                if (!mod)
                {
                    int vk = (int)info.vkCode;
                    if (vk is VK_UP or VK_DOWN or VK_LEFT or VK_RIGHT)
                    {
                        SafeBeginInvoke(() => HandleOverlayArrowKey(vk));
                        return (IntPtr)1; // swallow so game under veil doesn't also get it
                    }
                }
            }
            return LowLevelInputHooks.CallNextHookEx(_keyboardHookID, nCode, wParam, lParam);
        }

        private void HandleOverlayArrowKey(int vk)
        {
            if (!Visible || IsToolWindowOpen || IsDisposed)
                return;
            const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
            switch (vk)
            {
                case VK_UP:
                    BeginFollowFloating();
                    break;
                case VK_DOWN:
                    ClearFollowSession();
                    break;
                case VK_RIGHT:
                    SetOverlayOpacity(Opacity + OpacityStep);
                    break;
                case VK_LEFT:
                    SetOverlayOpacity(Opacity - OpacityStep);
                    break;
            }
        }

        /// <summary>Start / resume floating Follow preview (↑ on overlay). Does not speak.</summary>
        private void BeginFollowFloating()
        {
            _followActive = true;
            dynamic_rect = true;
            // Does not touch F1–F8 live draw fields — follow uses _followBox only.
            UpdateDynamicRect(Cursor.Position);
            if (Visible)
            {
                Invalidate();
                InvalidateSidebarChrome();
            }
        }

        /// <summary>
        /// Sidebar FOLLOW button: arm floating follow, or turn follow off.
        /// Ctrl+click opens size/shape settings instead.
        /// </summary>
        private void ToggleFollowFromSidebar()
        {
            if (FollowActive)
                ClearFollowSession();
            else
                BeginFollowFloating();
        }

        /// <summary>Pin the follow box at its current position (Enter). Does not speak.</summary>
        private void LockFollowAtCurrent()
        {
            if (!FollowActive)
                return;
            // Snap once more so lock matches the cursor, then stop tracking.
            if (dynamic_rect)
                UpdateDynamicRect(Cursor.Position);
            dynamic_rect = false;
            _followActive = true;
            if (Visible)
                Invalidate();
        }

        /// <summary>
        /// End follow session (float or locked). Overlay stays open.
        /// F1–F8 geometry is untouched — follow has its own box (R9 / Shift+F9).
        /// </summary>
        private void ClearFollowSession()
        {
            if (!_followActive && !dynamic_rect)
                return;
            _followActive = false;
            dynamic_rect = false;
            _followBox = Rectangle.Empty;
            if (Visible)
                Invalidate();
        }

        /// <summary>
        /// Load a saved F1–F8 region into the live draw fields (or clear if empty).
        /// Does not change follow session flags.
        /// </summary>
        private void LoadRegionIntoCurrent(Keys key)
        {
            if (_savedRegions.TryGetValue(key, out var saved) && saved != null)
            {
                _currentMode = saved.Mode;
                if (saved.Mode == CaptureMode.Rectangle)
                {
                    _currentRect = saved.Rect;
                    _currentEllipse = Rectangle.Empty;
                    _currentLasso.Clear();
                }
                else if (saved.Mode == CaptureMode.Ellipse)
                {
                    _currentEllipse = saved.Rect;
                    _currentRect = Rectangle.Empty;
                    _currentLasso.Clear();
                }
                else
                {
                    _currentLasso = new List<Point>(saved.LassoPoints);
                    _currentRect = Rectangle.Empty;
                    _currentEllipse = Rectangle.Empty;
                }
                return;
            }

            // Empty slot: restore drawing tool from settings, not follow's last shape.
            _currentMode = AppSettings.Current.ShapeMode switch
            {
                "Ellipse" => CaptureMode.Ellipse,
                "Lasso" => CaptureMode.Lasso,
                _ => CaptureMode.Rectangle,
            };
            _currentLasso.Clear();
            _currentRect = Rectangle.Empty;
            _currentEllipse = Rectangle.Empty;
        }

        /// <summary>
        /// Place the follow box from cursor + [FOLLOW] size/offset/shape.
        /// Used while floating and when Region 9 speaks. Never touches F1–F8 fields.
        /// </summary>
        private void UpdateDynamicRect(Point cursorPos)
        {
            var s = AppSettings.Current;
            s.NormalizeFollowSettings();

            var newRect = new Rectangle(
                cursorPos.X + s.FollowOffsetX,
                cursorPos.Y + s.FollowOffsetY,
                s.FollowWidth,
                s.FollowHeight);

            // Prefer virtual desktop so multi-monitor follow works
            var vs = SystemInformation.VirtualScreen;
            newRect = Rectangle.Intersect(newRect, vs);
            if (newRect.IsEmpty)
            {
                // Fallback: clamp top-left so size is still usable
                int x = Math.Clamp(cursorPos.X + s.FollowOffsetX, vs.Left, vs.Right - 1);
                int y = Math.Clamp(cursorPos.Y + s.FollowOffsetY, vs.Top, vs.Bottom - 1);
                newRect = new Rectangle(x, y,
                    Math.Min(s.FollowWidth, vs.Right - x),
                    Math.Min(s.FollowHeight, vs.Bottom - y));
            }

            if (newRect != _followBox)
            {
                _followBox = newRect;
                if (Visible)
                    Invalidate();
            }
        }

        /// <summary>Bounds currently used by mouse-follow (region 9 only).</summary>
        private Rectangle GetDynamicBounds() => _followBox;

        /// <summary>
        /// Region 9 speak: always refresh the box from the mouse using [FOLLOW]
        /// settings, then OCR. Works from tray or with overlay open.
        /// </summary>
        private void SpeakFollowRegion()
        {
            if (IsDisposed)
                return;

            void run()
            {
                // Speak command always re-samples at the cursor (dynamic region 9).
                // Does not touch F1–F8 slots — R9 is independent.
                _followActive = true;
                UpdateDynamicRect(Cursor.Position);
                // Stay floating after speak so the next aim still tracks.
                dynamic_rect = true;

                var bounds = GetDynamicBounds();
                if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8)
                    return;

                try { OcrProcessor.CancelBackgroundComicSpeak(); } catch { /* ignore */ }
                _current?.Stop();
                bool ellipse = AppSettings.Current.FollowIsEllipse;
                var next = new OcrProcessor(bounds, null, ellipse);

                if (Visible)
                {
                    StartSpeakKeepingOverlay(next);
                }
                else
                {
                    _current = next;
                    Task.Run(() => _current.Start());
                }

                if (Visible)
                    Invalidate();
            }

            if (InvokeRequired)
                BeginInvoke(run);
            else
                run();
        }

        /// <summary>Sidebar is interactive and painted (not in draw-focus mode).</summary>
        private bool IsSidebarVisible => !_sidebarHiddenForDraw;

        /// <summary>
        /// Hide the left sidebar so the full screen (including the left strip) can
        /// be drawn. Called when the user picks RECT / OVAL / LASSO, or starts a stroke
        /// with the already-selected tool (default is rect — no re-click needed).
        /// </summary>
        private void HideSidebarForDraw()
        {
            if (_sidebarHiddenForDraw)
                return;
            _sidebarHiddenForDraw = true;
            _draggingOpacitySlider = false;
            SyncSidebarChrome();
            Invalidate(); // draw-hint on the veil
        }

        /// <summary>Restore the sidebar after a finished draw or cancel.</summary>
        private void ShowSidebar()
        {
            if (!_sidebarHiddenForDraw)
                return;
            _sidebarHiddenForDraw = false;
            SyncSidebarChrome();
            Invalidate(); // clear draw-hint on the veil
        }

        private void EnsureSidebarChrome()
        {
            if (_sidebarChrome is { IsDisposed: false })
                return;
            _sidebarChrome = new OverlaySidebarChromeForm(PaintSidebarChrome, SidebarWidth);
        }

        /// <summary>
        /// Show/hide/position the opaque tool strip with the overlay. Hidden while
        /// drawing (left-edge capture) and whenever the overlay is closed.
        /// </summary>
        private void SyncSidebarChrome()
        {
            if (IsDisposed)
                return;

            bool want = Visible && IsSidebarVisible;
            if (!want)
            {
                if (_sidebarChrome is { IsDisposed: false, Visible: true })
                    _sidebarChrome.Hide();
                return;
            }

            EnsureSidebarChrome();
            var chrome = _sidebarChrome!;
            var bounds = new Rectangle(Left, Top, SidebarWidth, Height);
            if (chrome.Bounds != bounds)
                chrome.Bounds = bounds;

            bool wasHidden = !chrome.Visible;
            if (wasHidden)
            {
                // Owned + no-activate: stays above the veil without stealing keyboard focus.
                chrome.Show(this);
                // First show / after draw: put Enter/Esc back on the overlay.
                // Skip when already visible — avoid yanking focus from Settings.
                EnsureOverlayKeyboardFocus(defer: true);
            }
            else
            {
                chrome.Invalidate();
            }
        }

        /// <summary>
        /// Keep dialog keys (Enter speak, Esc hide) on the overlay, not the tool chrome.
        /// </summary>
        private void EnsureOverlayKeyboardFocus(bool defer = false)
        {
            if (!Visible || IsDisposed || IsToolWindowOpen)
                return;

            void focus()
            {
                if (!Visible || IsDisposed || IsToolWindowOpen)
                    return;
                ActiveControl = null;
                if (Form.ActiveForm != this)
                    Activate();
                Focus();
            }

            if (defer && IsHandleCreated)
            {
                try { BeginInvoke(focus); }
                catch (InvalidOperationException) { focus(); }
            }
            else
            {
                focus();
            }
        }

        private void InvalidateSidebarChrome()
        {
            if (_sidebarChrome is { IsDisposed: false, Visible: true })
                _sidebarChrome.Invalidate();
        }

        private void DisposeSidebarChrome()
        {
            if (_sidebarChrome == null)
                return;
            try
            {
                if (!_sidebarChrome.IsDisposed)
                    _sidebarChrome.Close();
            }
            catch { /* ignore */ }
            try
            {
                if (!_sidebarChrome.IsDisposed)
                    _sidebarChrome.Dispose();
            }
            catch { /* ignore */ }
            _sidebarChrome = null;
        }

        /// <summary>True if click is over the left sidebar (absorb so drawing doesn't start).</summary>
        private bool IsInSidebar(Point screenPt)
        {
            if (!IsSidebarVisible)
                return false;
            return screenPt.X >= this.Left &&
                   screenPt.X < this.Left + SidebarWidth &&
                   screenPt.Y >= this.Top &&
                   screenPt.Y < this.Top + this.Height;
        }

        private bool IsShapeButtonHit(Point screenPt, out CaptureMode hitMode)
        {
            hitMode = _currentMode;
            if (!IsInSidebar(screenPt)) return false;

            int localY = screenPt.Y - this.Top;
            int localX = screenPt.X - this.Left;
            if (localX < ShapeBtnX || localX >= ShapeBtnX + ShapeBtnW)
                return false;

            if (localY >= RectBtnY && localY < RectBtnY + ShapeBtnH)
            {
                hitMode = CaptureMode.Rectangle;
                return true;
            }
            if (localY >= OvalBtnY && localY < OvalBtnY + ShapeBtnH)
            {
                hitMode = CaptureMode.Ellipse;
                return true;
            }
            if (localY >= LassoBtnY && localY < LassoBtnY + ShapeBtnH)
            {
                hitMode = CaptureMode.Lasso;
                return true;
            }
            return false;
        }

        private Rectangle GetFollowButtonRect() =>
            new Rectangle(ShapeBtnX, FollowBtnY, ShapeBtnW, FollowBtnH);

        private bool IsFollowButtonHit(Point screenPt)
        {
            if (!IsInSidebar(screenPt)) return false;
            int localY = screenPt.Y - this.Top;
            int localX = screenPt.X - this.Left;
            return GetFollowButtonRect().Contains(localX, localY);
        }

        /// <summary>
        /// Client rect for region slot button <paramref name="slotIndex"/> (0..7 → labels 1..8).
        /// Four columns × two rows under the REGIONS section title.
        /// </summary>
        private static Rectangle GetRegionSlotButtonRect(int slotIndex)
        {
            int i = Math.Clamp(slotIndex, 0, 7);
            int col = i % RegionCols;
            int row = i / RegionCols;
            int cellW = (ShapeBtnW - (RegionCols - 1) * RegionBtnGap) / RegionCols;
            int x = ShapeBtnX + col * (cellW + RegionBtnGap);
            int y = RegionGridY + row * (RegionBtnH + RegionBtnGap);
            return new Rectangle(x, y, cellW, RegionBtnH);
        }

        /// <summary>
        /// Hit-test sidebar region grid. Returns slot index 0..7 when a button is hit.
        /// </summary>
        private bool IsRegionSlotButtonHit(Point screenPt, out int slotIndex)
        {
            slotIndex = -1;
            if (!IsInSidebar(screenPt)) return false;

            int localY = screenPt.Y - this.Top;
            int localX = screenPt.X - this.Left;
            for (int i = 0; i < 8; i++)
            {
                if (GetRegionSlotButtonRect(i).Contains(localX, localY))
                {
                    slotIndex = i;
                    return true;
                }
            }
            return false;
        }

        /// <summary>True if slot <paramref name="slotIndex"/> (0..7) has a saved capture geometry.</summary>
        private bool RegionSlotHasSavedGeometry(int slotIndex)
        {
            int i = Math.Clamp(slotIndex, 0, 7);
            Keys key = Keys.F1 + i;
            if (_savedRegions.TryGetValue(key, out var reg) && reg != null)
            {
                if (reg.Mode == CaptureMode.Lasso)
                    return reg.LassoPoints != null && reg.LassoPoints.Count > 2;
                return !reg.Rect.IsEmpty;
            }
            return AppSettings.Current.RegionSlots[i] is { IsEmpty: false };
        }

        /// <summary>Y of each flag button (client coords), accounting for section headers.</summary>
        private static int[] BuildFlagButtonYs()
        {
            var ys = new int[AppSettings.Flags.Length];
            int y = FlagStackStartY;
            string? lastSection = null;
            for (int i = 0; i < AppSettings.Flags.Length; i++)
            {
                string section = AppSettings.Flags[i].Section;
                if (section != lastSection)
                {
                    if (lastSection != null)
                        y += FlagSectionGap;
                    y += 16; // section title height
                    lastSection = section;
                }
                ys[i] = y;
                y += FlagBtnH + FlagBtnGap;
            }
            return ys;
        }

        private bool IsFlagButtonHit(Point screenPt, out int flagIndex)
        {
            flagIndex = -1;
            if (!IsInSidebar(screenPt)) return false;

            int localY = screenPt.Y - this.Top;
            int localX = screenPt.X - this.Left;
            if (localX < ShapeBtnX || localX >= ShapeBtnX + ShapeBtnW)
                return false;

            int[] ys = BuildFlagButtonYs();
            for (int i = 0; i < ys.Length; i++)
            {
                if (localY >= ys[i] && localY < ys[i] + FlagBtnH)
                {
                    flagIndex = i;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Client rect for the SETTINGS sidebar button (under mode flags).</summary>
        private Rectangle GetSettingsButtonRect()
        {
            int[] ys = BuildFlagButtonYs();
            int y = FlagStackStartY;
            if (ys.Length > 0)
                y = ys[^1] + FlagBtnH + FlagSectionGap + 16; // gap + section title
            return new Rectangle(ShapeBtnX, y, ShapeBtnW, SettingsBtnH);
        }

        private bool IsSettingsButtonHit(Point screenPt)
        {
            if (!IsInSidebar(screenPt)) return false;
            int localY = screenPt.Y - this.Top;
            int localX = screenPt.X - this.Left;
            return GetSettingsButtonRect().Contains(localX, localY);
        }

        /// <summary>
        /// EXIT near the bottom of the strip, with room for the version label under it.
        /// </summary>
        private Rectangle GetExitButtonRect() =>
            new Rectangle(
                ShapeBtnX,
                Math.Max(0, this.Height - BottomActionMargin - VersionLabelH - BottomActionBtnH),
                ShapeBtnW,
                BottomActionBtnH);

        /// <summary>Build version drawn under EXIT (not a hit target).</summary>
        private Rectangle GetVersionLabelRect()
        {
            Rectangle exit = GetExitButtonRect();
            return new Rectangle(
                ShapeBtnX,
                exit.Bottom + 2,
                ShapeBtnW,
                VersionLabelH);
        }

        /// <summary>HIDE stacked just above EXIT.</summary>
        private Rectangle GetHideButtonRect()
        {
            Rectangle exit = GetExitButtonRect();
            return new Rectangle(
                ShapeBtnX,
                Math.Max(0, exit.Y - BottomActionGap - BottomActionBtnH),
                ShapeBtnW,
                BottomActionBtnH);
        }

        private bool IsHideButtonHit(Point screenPt)
        {
            if (!IsInSidebar(screenPt)) return false;
            int localY = screenPt.Y - this.Top;
            int localX = screenPt.X - this.Left;
            return GetHideButtonRect().Contains(localX, localY);
        }

        private bool IsExitButtonHit(Point screenPt)
        {
            if (!IsInSidebar(screenPt)) return false;
            int localY = screenPt.Y - this.Top;
            int localX = screenPt.X - this.Left;
            return GetExitButtonRect().Contains(localX, localY);
        }

        /// <summary>
        /// Client Y for the OPACITY section: under SETTINGS, but always above Hide/Exit.
        /// </summary>
        private int OpacitySectionY
        {
            get
            {
                int underSettings = GetSettingsButtonRect().Bottom + 16;
                int aboveHide = GetHideButtonRect().Y - OpacityBlockH;
                // Prefer under Settings; clamp so we never cover the bottom actions.
                return Math.Min(underSettings, Math.Max(0, aboveHide));
            }
        }

        /// <summary>Track bar for the opacity slider (client coords).</summary>
        private Rectangle GetOpacityTrackRect() =>
            new Rectangle(ShapeBtnX + 4, OpacitySectionY + 18, ShapeBtnW - 8, OpacityTrackH);

        /// <summary>Larger hit area so the thumb is easy to grab.</summary>
        private Rectangle GetOpacitySliderHitRect() =>
            new Rectangle(ShapeBtnX, OpacitySectionY, ShapeBtnW, OpacityBlockH);

        private bool IsOpacitySliderHit(Point screenPt)
        {
            if (!IsInSidebar(screenPt)) return false;
            int localY = screenPt.Y - this.Top;
            int localX = screenPt.X - this.Left;
            return GetOpacitySliderHitRect().Contains(localX, localY);
        }

        /// <summary>Tray Exit path: close UI and tear down Local-LLM / related hosts.</summary>
        private void ExitApplication()
        {
            try { _settingsForm?.Close(); } catch { /* ignore */ }
            try { _current?.Stop(); } catch { /* ignore */ }
            // ApplicationExit + OnFormClosed both call LocalLlmHost.Stop().
            Application.Exit();
        }

        /// <summary>
        /// Clamp and apply overlay opacity (Left/Right keys + sidebar slider).
        /// Invalidates so the painted thumb stays in sync.
        /// </summary>
        private void SetOverlayOpacity(double opacity)
        {
            double clamped = Math.Clamp(opacity, OpacityMin, OpacityMax);
            // Avoid thrashing Invalidate on tiny floating noise while dragging.
            if (Math.Abs(Opacity - clamped) < 0.001)
                return;
            Opacity = clamped;
            // Slider lives on the opaque chrome (not the translucent veil).
            if (IsSidebarVisible && _sidebarChrome is { IsDisposed: false, Visible: true })
                _sidebarChrome.Invalidate(GetOpacitySliderHitRect());
        }

        /// <summary>Map a screen X to opacity from the slider track and apply it.</summary>
        private void SetOpacityFromScreenX(int screenX)
        {
            Rectangle track = GetOpacityTrackRect();
            int localX = screenX - this.Left;
            double t = (localX - track.X) / (double)Math.Max(1, track.Width);
            t = Math.Clamp(t, 0.0, 1.0);
            SetOverlayOpacity(OpacityMin + t * (OpacityMax - OpacityMin));
        }

        private void SetMode(CaptureMode mode)
        {
            // Re-picking the same shape still hides the sidebar for a left-side draw.
            if (_currentMode != mode)
            {
                // Keep whatever is already committed for this slot; only clear the live stroke.
                // (Do not wipe _savedRegions — other slots and this slot's last shape stay.)
                _currentMode = mode;
                _currentRect = Rectangle.Empty;
                _currentEllipse = Rectangle.Empty;
                _currentLasso.Clear();
                _isDrawing = false;
                _drawLassoPoints.Clear();
                _drawStart = Point.Empty;
                _drawEnd = Point.Empty;

                // Remember drawing tool for profiles (Rectangle / Ellipse / Lasso).
                AppSettings.Current.ShapeMode = mode switch
                {
                    CaptureMode.Ellipse => "Ellipse",
                    CaptureMode.Lasso => "Lasso",
                    _ => "Rectangle",
                };
            }

            // Pick shape → hide sidebar until the user finishes drawing (or Esc).
            HideSidebarForDraw();
            Invalidate();
        }

        /// <summary>Write current F1–F8 draw into <see cref="_savedRegions"/> only (no settings I/O).</summary>
        private void CommitCurrentSelectionToDict(Keys key)
        {
            if (key == Keys.None) return;

            var region = new SavedRegion { Mode = _currentMode };

            if (_currentMode == CaptureMode.Rectangle && !_currentRect.IsEmpty)
            {
                region.Rect = _currentRect;
            }
            else if (_currentMode == CaptureMode.Ellipse && !_currentEllipse.IsEmpty)
            {
                region.Rect = _currentEllipse;
            }
            else if (_currentMode == CaptureMode.Lasso && _currentLasso.Count > 2)
            {
                region.LassoPoints = new List<Point>(_currentLasso);
            }
            else
            {
                return;
            }

            _savedRegions[key] = region;
        }

        private void SaveCurrentSelection(Keys key)
        {
            CommitCurrentSelectionToDict(key);
            // Keep AppSettings + SpeakRect.ini in sync so profiles and restart restore geometry.
            if (key != Keys.None)
            {
                SyncRegionsToSettings();
                AppSettings.Current.Save();
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && lParam != IntPtr.Zero)
                {
                    var hookStruct = Marshal.PtrToStructure<LowLevelInputHooks.MSLLHOOKSTRUCT>(lParam);
                    Point currentPos = hookStruct.pt;

                    // Tool windows open: do not start/continue overlay drawing (dragging
                    // a panel would otherwise paint a region under the cursor).
                    bool toolOpen = IsToolWindowOpen;

                    if (wParam == (IntPtr)WM_MOUSEMOVE)
                    {
                        // Opacity slider drag (sidebar) — UI thread only.
                        if (_draggingOpacitySlider && Visible)
                        {
                            SafeBeginInvoke(() =>
                            {
                                if (!_draggingOpacitySlider) return;
                                SetOpacityFromScreenX(currentPos.X);
                            });
                        }

                        // Mouse-follow: update region under cursor while overlay is up.
                        if (dynamic_rect && Visible && !toolOpen)
                            SafeBeginInvoke(() => UpdateDynamicRect(currentPos));

                        // All draw-state mutation on UI thread only — hook thread
                        // mutating List/fields while OnPaint runs was a crash source.
                        if (_isDrawing && Visible)
                        {
                            if (toolOpen)
                            {
                                SafeBeginInvoke(CancelDrawing);
                            }
                            else
                            {
                                SafeBeginInvoke(() =>
                                {
                                    if (!_isDrawing || IsToolWindowOpen) return;

                                    if (_currentMode == CaptureMode.Lasso)
                                    {
                                        if (_drawLassoPoints.Count == 0 ||
                                            Distance(_drawLassoPoints[_drawLassoPoints.Count - 1], currentPos) > 4)
                                        {
                                            _drawLassoPoints.Add(currentPos);
                                        }
                                    }
                                    else
                                    {
                                        _drawEnd = currentPos;
                                    }
                                    Invalidate();
                                });
                            }
                        }
                    }
                    else if (wParam == (IntPtr)WM_LBUTTONDOWN && Visible)
                    {
                        SafeBeginInvoke(() =>
                        {
                            // Sidebar always works — including while Follow is floating.
                            // (Previously !dynamic_rect blocked every sidebar click once R9 was on.)
                            // Hide / Exit first so they work even with Settings open.
                            if (IsHideButtonHit(currentPos))
                            {
                                // Close tool windows first — HideToTray alone leaves a
                                // zombie Settings HWND and _settingsOpen stuck true.
                                try { _settingsForm?.Close(); } catch { /* ignore */ }
                                HideToTray();
                                return;
                            }
                            if (IsExitButtonHit(currentPos))
                            {
                                ExitApplication();
                                return;
                            }
                            if (IsSettingsButtonHit(currentPos))
                            {
                                OpenSettings();
                                return;
                            }
                            if (IsFollowButtonHit(currentPos))
                            {
                                // Plain click: toggle Follow. Ctrl+click: Settings → Follow tab.
                                if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                                    OpenFollowSettings();
                                else
                                    ToggleFollowFromSidebar();
                                return;
                            }

                            // Region slots 1–8: same as region hotkeys (switch canvas for draw/speak).
                            if (IsRegionSlotButtonHit(currentPos, out int regionSlot))
                            {
                                ActivateRegionSlot(regionSlot);
                                return;
                            }

                            // Opacity slider: click/drag like Left/Right arrow keys.
                            if (IsOpacitySliderHit(currentPos))
                            {
                                _draggingOpacitySlider = true;
                                SetOpacityFromScreenX(currentPos.X);
                                return;
                            }

                            // While a tool window is up: no drawing, no shape/flag hits
                            // (avoids ghost selections when moving the tool window).
                            if (IsToolWindowOpen)
                                return;

                            // Flag toggles (under shape buttons)
                            if (IsFlagButtonHit(currentPos, out int flagIndex))
                            {
                                AppSettings.Current.ToggleFlag(flagIndex);
                                // POI etc. suspended/restored with MODE — Balloons only.
                                try { _settingsForm?.ReloadBalloonsFromModeChange(); } catch { /* ignore */ }
                                Invalidate();
                                return;
                            }

                            if (IsShapeButtonHit(currentPos, out var hitMode))
                            {
                                // Always SetMode: same tool re-click still hides sidebar for draw.
                                SetMode(hitMode);
                                return;
                            }

                            // Clicks on empty sidebar chrome — don't start a selection
                            if (IsInSidebar(currentPos))
                                return;

                            // Floating Follow owns the cursor for R9 — don't start F1–F8 draws.
                            if (dynamic_rect)
                                return;

                            // Hide tools as soon as a stroke starts (not only when re-picking
                            // a shape button). Default mode is already RECT, so users often
                            // draw without clicking the sidebar first — without this the
                            // left strip would stay painted over the selection.
                            HideSidebarForDraw();

                            _isDrawing = true;
                            _drawStart = currentPos;
                            if (_currentMode == CaptureMode.Lasso)
                            {
                                _drawLassoPoints.Clear();
                                _drawLassoPoints.Add(currentPos);
                            }
                            else
                            {
                                _drawEnd = currentPos;
                            }
                            Invalidate();
                        });
                    }
                    else if (wParam == (IntPtr)WM_LBUTTONUP && Visible && (_isDrawing || _draggingOpacitySlider))
                    {
                        SafeBeginInvoke(() =>
                        {
                            if (_draggingOpacitySlider)
                            {
                                _draggingOpacitySlider = false;
                                return;
                            }

                            if (!_isDrawing) return;

                            // Discard the stroke if a tool window opened mid-drag.
                            if (IsToolWindowOpen)
                            {
                                CancelDrawing();
                                return;
                            }

                            _isDrawing = false;
                            Point endPoint = currentPos;

                            if (_currentMode == CaptureMode.Lasso)
                            {
                                if (_drawLassoPoints.Count > 2)
                                {
                                    double d = Distance(_drawLassoPoints.Last(), _drawStart);
                                    if (d < 30)
                                    {
                                        if (d > 0.5)
                                            _drawLassoPoints[_drawLassoPoints.Count - 1] = _drawStart;
                                    }
                                    else
                                    {
                                        _drawLassoPoints.Add(endPoint);
                                    }

                                    if (_drawLassoPoints.Count > 2)
                                    {
                                        _currentLasso = new List<Point>(_drawLassoPoints);
                                        // Persist lasso into the active region slot (1–8), not only rects.
                                        SaveCurrentSelection(_activeRectKey);
                                    }
                                }
                                _drawLassoPoints.Clear();
                            }
                            else if (_currentMode == CaptureMode.Rectangle)
                            {
                                var r = NormalizeScreenRect(_drawStart, endPoint);
                                if (r.Width > 8 && r.Height > 8)
                                {
                                    _currentRect = r;
                                    SaveCurrentSelection(_activeRectKey);
                                }
                            }
                            else if (_currentMode == CaptureMode.Ellipse)
                            {
                                var r = NormalizeScreenRect(_drawStart, endPoint);
                                if (r.Width > 8 && r.Height > 8)
                                {
                                    _currentEllipse = r;
                                    // Oval/ellipse uses the same slot storage as rect (Oval:x,y,w,h).
                                    SaveCurrentSelection(_activeRectKey);
                                }
                            }

                            // Stroke finished (commit or discard) — restore tools immediately.
                            // Left-edge redraws still work: the next LBUTTONDOWN hides again.
                            ShowSidebar();

                            Invalidate();
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MouseHook] {ex.Message}");
            }

            return LowLevelInputHooks.CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
        }

        /// <summary>UI-thread marshal that never throws if the form is gone.</summary>
        private void SafeBeginInvoke(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>Axis-aligned rect from two screen points, clamped to the virtual desktop.</summary>
        private static Rectangle NormalizeScreenRect(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X);
            int y = Math.Min(a.Y, b.Y);
            int w = Math.Abs(b.X - a.X);
            int h = Math.Abs(b.Y - a.Y);
            var r = new Rectangle(x, y, w, h);
            var vs = SystemInformation.VirtualScreen;
            r = Rectangle.Intersect(r, vs);
            return r;
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();

                if (id == HOTKEY_TOGGLE_OVERLAY)
                {
                    ToggleOverlayFromInput();
                }
                else if (id == HOTKEY_DEFAULT_MODE)
                {
                    ToggleModeFromHotkey(AppSettings.FlagIndexDefault);
                }
                else if (id == HOTKEY_COMIC)
                {
                    ToggleModeFromHotkey(AppSettings.FlagIndexComicBook);
                }
                else if (id == HOTKEY_STOP_TTS)
                {
                    AbortTtsInProgress();
                }
                else if (id >= HOTKEY_REGION_BASE && id < HOTKEY_REGION_BASE + 8)
                {
                    // Logical slot stays F1..F8 even if the chord was remapped.
                    ActivateRegionSlot(id - HOTKEY_REGION_BASE);
                }
                else if (id == HOTKEY_FOLLOW)
                {
                    SpeakFollowRegion();
                }
                else if (id >= HOTKEY_CUSTOM_BASE && id < HOTKEY_CUSTOM_BASE + HOTKEY_CUSTOM_MAX)
                {
                    ExecuteCustomActionAt(id - HOTKEY_CUSTOM_BASE);
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// Stop every active speak path: current region/Follow OCR TTS, Balloons
        /// refine speak after overlay hide, and short UI announcements.
        /// </summary>
        private void AbortTtsInProgress()
        {
            try { OcrProcessor.CancelAnnouncement(); } catch { /* ignore */ }
            try { OcrProcessor.CancelBackgroundComicSpeak(); } catch { /* ignore */ }
            try { _current?.Stop(); } catch { /* ignore */ }
        }

        private void ShowOverlay()
        {
            try { Cursor = new Cursor(@"C:\Windows\Cursors\aero_arrow.cur"); }
            catch { Cursor = Cursors.Hand; }

            _current?.Stop();

            // Active F1–F8 slot (independent of region 9 follow box).
            LoadRegionIntoCurrent(_activeRectKey);

            Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            WindowState = FormWindowState.Normal;
            Show();

            // Resume follow preview if a session was still armed (_followBox survives hide).
            if (FollowActive && dynamic_rect)
                UpdateDynamicRect(Cursor.Position);

            _sidebarHiddenForDraw = false; // always show tools when reopening the overlay
            _draggingOpacitySlider = false;
            SyncSidebarChrome(); // also restores keyboard focus for Enter/Esc
            Invalidate();
        }

        private void HideToTray()
        {
            this.ActiveControl = null;

            SaveCurrentSelection(_activeRectKey);
            _sidebarHiddenForDraw = false; // next show starts with tools visible
            _draggingOpacitySlider = false;

            Cursor = _defaultCursor ?? Cursors.Default;
            _current?.Stop();
            Hide();
            SyncSidebarChrome(); // hide opaque tools with the overlay

            // Balloons refine one-shot: only if the user edited boxes (pending arm).
            // Flush session from open Settings so latest geometry is available first.
            try
            {
                if (_settingsForm is { IsDisposed: false } sf)
                {
                    try { sf.FlushBalloonsRefineSession(); } catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
            try { frm_ComicRegions.TrySpeakOverrideOnOverlayHide(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Overlay] refine speak on hide: {ex.Message}");
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Tool chrome is a separate fully-opaque window (see OverlaySidebarChromeForm).
            // Only the compact draw hint stays on this translucent veil.
            if (!IsSidebarVisible)
            {
                using var drawHintFont = new Font("Segoe UI", 8f, FontStyle.Bold);
                using var drawHintBg = new SolidBrush(Color.FromArgb(160, 20, 20, 20));
                string tool = _currentMode switch
                {
                    CaptureMode.Ellipse => "OVAL",
                    CaptureMode.Lasso => "LASSO",
                    _ => "RECT",
                };
                string msg = $"Draw {tool} · Esc = tools";
                var size = e.Graphics.MeasureString(msg, drawHintFont);
                var box = new RectangleF(8, 8, size.Width + 12, size.Height + 8);
                e.Graphics.FillRectangle(drawHintBg, box);
                e.Graphics.DrawString(msg, drawHintFont, Brushes.LightGray, box.X + 6, box.Y + 4);
            }

            // ====================== SAVED REGIONS ======================
            // Stable color per slot index (F1=0 … F8=7), shared with Settings → Regions.
            foreach (var kvp in _savedRegions)
            {
                var region = kvp.Value;
                int slotIdx = Math.Clamp((int)kvp.Key - (int)Keys.F1, 0, 7);
                var col = RegionSlotColors.GetFill(slotIdx);
                using var fill = new SolidBrush(col);
                using var pen = new Pen(Color.White, 2);
                using var font = new Font("Segoe UI", 9, FontStyle.Bold);
                using var textBrush = new SolidBrush(Color.White);

                string label = kvp.Key.ToString();
                if (region.Mode == CaptureMode.Rectangle)
                {
                    if (!region.Rect.IsEmpty)
                    {
                        e.Graphics.FillRectangle(fill, region.Rect);
                        e.Graphics.DrawRectangle(pen, region.Rect);
                        e.Graphics.DrawString(label + " [R]", font, textBrush, region.Rect.Location);
                    }
                }
                else if (region.Mode == CaptureMode.Ellipse)
                {
                    if (!region.Rect.IsEmpty)
                    {
                        e.Graphics.FillEllipse(fill, region.Rect);
                        e.Graphics.DrawEllipse(pen, region.Rect);
                        e.Graphics.DrawString(label + " [O]", font, textBrush, region.Rect.Location);
                    }
                }
                else if (region.Mode == CaptureMode.Lasso && region.LassoPoints.Count > 2)
                {
                    var pts = region.LassoPoints.ToArray();
                    e.Graphics.FillPolygon(fill, pts);
                    e.Graphics.DrawPolygon(pen, pts);
                    e.Graphics.DrawString(label + " [L]", font, textBrush, pts[0]);
                }
            }

            // ====================== CURRENT SELECTION (active F1–F8 editing) ======================
            // Drawn even while Follow (R9) is armed — they are independent regions.
            {
                using var currentFill = new SolidBrush(Color.FromArgb(70, 240, 128, 24));
                using var currentPen = new Pen(UiTheme.AccentHot, 3);
                using var currentFont = new Font("Segoe UI", 10, FontStyle.Bold);
                using var currentTextBrush = new SolidBrush(Color.White);

                string modeLabel = _activeRectKey.ToString();
                if (_currentMode == CaptureMode.Rectangle)
                    modeLabel += " [RECT]";
                else if (_currentMode == CaptureMode.Ellipse)
                    modeLabel += " [OVAL]";
                else
                    modeLabel += " [LASSO]";

                if (_currentMode == CaptureMode.Rectangle && !_currentRect.IsEmpty)
                {
                    e.Graphics.FillRectangle(currentFill, _currentRect);
                    e.Graphics.DrawRectangle(currentPen, _currentRect);
                    e.Graphics.DrawString(modeLabel, currentFont, currentTextBrush, _currentRect.Location);
                }
                else if (_currentMode == CaptureMode.Ellipse && !_currentEllipse.IsEmpty)
                {
                    e.Graphics.FillEllipse(currentFill, _currentEllipse);
                    e.Graphics.DrawEllipse(currentPen, _currentEllipse);
                    e.Graphics.DrawString(modeLabel, currentFont, currentTextBrush, _currentEllipse.Location);
                }
                else if (_currentMode == CaptureMode.Lasso && _currentLasso.Count > 2)
                {
                    var pts = _currentLasso.ToArray();
                    e.Graphics.FillPolygon(currentFill, pts);
                    e.Graphics.DrawPolygon(currentPen, pts);
                    e.Graphics.DrawString(modeLabel, currentFont, currentTextBrush, pts[0]);
                }
            }

            // ====================== IN-PROGRESS DRAWING ======================
            if (_isDrawing)
            {
                using var drawFill = new SolidBrush(Color.FromArgb(70, 240, 128, 24));
                using var drawPen = new Pen(UiTheme.AccentHot, 3);

                if (_currentMode == CaptureMode.Lasso && _drawLassoPoints.Count >= 2)
                {
                    e.Graphics.DrawLines(drawPen, _drawLassoPoints.ToArray());

                    if (_drawLassoPoints.Count > 4)
                    {
                        var last = _drawLassoPoints.Last();
                        double d = Distance(last, _drawStart);
                        if (d < 40)
                        {
                            e.Graphics.DrawLine(drawPen, last, _drawStart);
                            using var snapBrush = new SolidBrush(Color.LimeGreen);
                            e.Graphics.FillEllipse(snapBrush, _drawStart.X - 6, _drawStart.Y - 6, 12, 12);
                        }
                    }
                }
                else if (_currentMode == CaptureMode.Rectangle)
                {
                    var r = new Rectangle(
                        Math.Min(_drawStart.X, _drawEnd.X),
                        Math.Min(_drawStart.Y, _drawEnd.Y),
                        Math.Abs(_drawEnd.X - _drawStart.X),
                        Math.Abs(_drawEnd.Y - _drawStart.Y));
                    if (r.Width > 3 && r.Height > 3)
                    {
                        e.Graphics.FillRectangle(drawFill, r);
                        e.Graphics.DrawRectangle(drawPen, r);
                    }
                }
                else if (_currentMode == CaptureMode.Ellipse)
                {
                    var r = new Rectangle(
                        Math.Min(_drawStart.X, _drawEnd.X),
                        Math.Min(_drawStart.Y, _drawEnd.Y),
                        Math.Abs(_drawEnd.X - _drawStart.X),
                        Math.Abs(_drawEnd.Y - _drawStart.Y));
                    if (r.Width > 3 && r.Height > 3)
                    {
                        e.Graphics.FillEllipse(drawFill, r);
                        e.Graphics.DrawEllipse(drawPen, r);
                    }
                }
            }

            // Follow region 9 box (floating or locked) — size/shape from FOLLOW settings
            if (FollowActive)
            {
                var r = GetDynamicBounds();
                if (!r.IsEmpty)
                {
                    // Floating = orange; locked = deeper amber so lock state is obvious
                    Color fillC = dynamic_rect
                        ? Color.FromArgb(90, 240, 128, 24)
                        : Color.FromArgb(100, 200, 100, 20);
                    Color penC = dynamic_rect ? UiTheme.AccentHot : UiTheme.Accent;
                    using var fill = new SolidBrush(fillC);
                    using var pen = new Pen(penC, 3);
                    if (AppSettings.Current.FollowIsEllipse)
                    {
                        e.Graphics.FillEllipse(fill, r);
                        e.Graphics.DrawEllipse(pen, r);
                    }
                    else
                    {
                        e.Graphics.FillRectangle(fill, r);
                        e.Graphics.DrawRectangle(pen, r);
                    }

                    using var tagFont = new Font("Segoe UI", 8f, FontStyle.Bold);
                    string tag = dynamic_rect ? "FOLLOW" : "LOCKED";
                    e.Graphics.DrawString(tag, tagFont, Brushes.White, r.X + 4, r.Y + 4);
                }
            }

            base.OnPaint(e);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            // Settings / tool windows own Enter (commit field), Esc (close panel), etc.
            // Never speak, lock Follow, or hide the overlay while a tool window is up.
            if (IsToolWindowOpen)
                return true;

            // Enter while Follow is active: lock / unlock only — never speak.
            // Speak Follow via Region 9 hotkey (default Shift+F9).
            if (keyData == Keys.Enter)
            {
                if (!Visible || _isDrawing)
                    return true;

                if (FollowActive)
                {
                    if (dynamic_rect)
                        LockFollowAtCurrent();
                    else
                        BeginFollowFloating(); // unlock → float again
                    return true;
                }

                // Regions 1–8: speak the committed selection (overlay stays open).
                _current?.Stop();

                SaveCurrentSelection(_activeRectKey);

                OcrProcessor? next = null;
                if (_currentMode == CaptureMode.Rectangle && !_currentRect.IsEmpty)
                {
                    next = new OcrProcessor(_currentRect);
                }
                else if (_currentMode == CaptureMode.Ellipse && !_currentEllipse.IsEmpty)
                {
                    next = new OcrProcessor(_currentEllipse, null, true);
                }
                else if (_currentMode == CaptureMode.Lasso && _currentLasso.Count > 2)
                {
                    var bounds = GetBoundingRect(_currentLasso);
                    next = new OcrProcessor(bounds, _currentLasso);
                }

                if (next != null)
                    StartSpeakKeepingOverlay(next);

                return true;
            }

            _current?.Stop();

            if (keyData == Keys.Escape)
            {
                // While the sidebar is hidden for drawing: restore tools instead of leaving.
                if (_sidebarHiddenForDraw || _isDrawing)
                {
                    CancelDrawing();
                    ShowSidebar();
                    Invalidate();
                    return true;
                }

                ClearFollowSession();
                SaveCurrentSelection(_activeRectKey);
                _currentLasso.Clear();
                _currentRect = Rectangle.Empty;
                _currentEllipse = Rectangle.Empty;
                HideToTray();
                return true;
            }

            if (keyData == Keys.Delete)
            {
                _savedRegions.Remove(_activeRectKey);
                _currentLasso.Clear();
                _currentRect = Rectangle.Empty;
                _currentEllipse = Rectangle.Empty;
                Invalidate();
                return true;
            }

            return base.ProcessDialogKey(keyData);
        }

        /// <summary>
        /// Snap the active F1–F8 region (same geometry as Enter speak) with no OCR/TTS.
        /// Hides Settings + overlay chrome, settles, snaps, restores. Caller owns the bitmap.
        /// </summary>
        internal async Task<(Bitmap? Bitmap, string Error)> CaptureActiveRegionForPreviewAsync()
        {
            // Same geometry commit as Enter before speak.
            SaveCurrentSelection(_activeRectKey);
            // Ensure live fields match the active slot (overlay may be hidden under Settings).
            LoadRegionIntoCurrent(_activeRectKey);

            Rectangle bounds = Rectangle.Empty;
            List<Point>? lasso = null;
            bool ellipse = false;
            bool hasSelection = false;

            if (_currentMode == CaptureMode.Rectangle && !_currentRect.IsEmpty)
            {
                hasSelection = true;
                bounds = _currentRect;
            }
            else if (_currentMode == CaptureMode.Ellipse && !_currentEllipse.IsEmpty)
            {
                hasSelection = true;
                bounds = _currentEllipse;
                ellipse = true;
            }
            else if (_currentMode == CaptureMode.Lasso && _currentLasso.Count > 2)
            {
                hasSelection = true;
                bounds = GetBoundingRect(_currentLasso);
                lasso = new List<Point>(_currentLasso);
            }

            if (!hasSelection)
            {
                return (null,
                    "No active region — draw or select a region (1–8) on the overlay first.");
            }

            var settings = _settingsForm;
            bool settingsWasVisible = settings is { IsDisposed: false, Visible: true };
            double restoreOpacity = Opacity;
            if (restoreOpacity < 0.05)
                restoreOpacity = 0.4;
            bool overlayWasVisible = Visible;

            try
            {
                // Hide Settings so it is not in the snap (Enter path only dims overlay).
                if (settingsWasVisible)
                {
                    try { settings!.Hide(); } catch { /* ignore */ }
                }

                // Same chrome hide as PrepareForCapture on Enter speak.
                void hideChrome()
                {
                    if (IsDisposed) return;
                    Opacity = 0;
                    if (_sidebarChrome is { IsDisposed: false, Visible: true })
                        _sidebarChrome.Hide();
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    if (InvokeRequired)
                        Invoke(hideChrome);
                    else
                        hideChrome();
                }

                // Compositor settle — same delay as CaptureAndRecognizeAsync.
                await Task.Delay(80).ConfigureAwait(true);

                Bitmap? snapped = null;
                try
                {
                    snapped = OcrProcessor.SnapRegionOnly(bounds, lasso, ellipse);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Overlay] SnapRegionOnly: {ex.Message}");
                    return (null, $"Snap failed: {ex.Message}");
                }

                if (snapped == null || snapped.Width < 2 || snapped.Height < 2)
                {
                    try { snapped?.Dispose(); } catch { /* ignore */ }
                    return (null, "Snap failed or empty region.");
                }

                return (snapped, "");
            }
            finally
            {
                // Restore overlay look (do not force-show if it was already hidden).
                void restoreChrome()
                {
                    if (IsDisposed) return;
                    Opacity = restoreOpacity;
                    if (overlayWasVisible)
                    {
                        if (!Visible)
                        {
                            try { Show(); } catch { /* ignore */ }
                        }
                        SyncSidebarChrome();
                        if (dynamic_rect)
                            UpdateDynamicRect(Cursor.Position);
                        Invalidate();
                    }
                    else if (_sidebarChrome is { IsDisposed: false, Visible: true })
                    {
                        try { _sidebarChrome.Hide(); } catch { /* ignore */ }
                    }
                }

                try
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        if (InvokeRequired)
                            Invoke(restoreChrome);
                        else
                            restoreChrome();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Overlay] restore after preview snap: {ex.Message}");
                }

                if (settingsWasVisible && settings is { IsDisposed: false })
                {
                    try
                    {
                        if (!settings.Visible)
                            settings.Show(this);
                        settings.Activate();
                        settings.Focus();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Overlay] restore Settings after snap: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// OCR + speak without closing the overlay. Dims briefly so the snap
        /// does not include selection chrome, then restores opacity.
        /// </summary>
        private void StartSpeakKeepingOverlay(OcrProcessor next)
        {
            // Stop overlay-hide Balloons refine TTS if still playing.
            try { OcrProcessor.CancelBackgroundComicSpeak(); } catch { /* ignore */ }
            try { _current?.Stop(); } catch { /* ignore */ }
            _current = next;

            // Capture current opacity so Left/Right dimming is preserved after snap.
            double restoreOpacity = Opacity;
            if (restoreOpacity < 0.05)
                restoreOpacity = 0.4;

            next.PrepareForCapture = () =>
            {
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    void hideChrome()
                    {
                        if (IsDisposed) return;
                        // Veil + opaque tools must both vanish or they land in the snap.
                        Opacity = 0;
                        if (_sidebarChrome is { IsDisposed: false, Visible: true })
                            _sidebarChrome.Hide();
                    }

                    if (InvokeRequired)
                    {
                        Invoke(hideChrome);
                        return;
                    }
                    hideChrome();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Overlay] PrepareForCapture: {ex.Message}");
                }
            };

            next.RestoreAfterCapture = () =>
            {
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    void restore()
                    {
                        if (IsDisposed) return;
                        // Overlay stays open — only restore look.
                        Opacity = restoreOpacity;
                        if (!Visible)
                        {
                            Show();
                            Activate();
                        }
                        SyncSidebarChrome();
                        if (dynamic_rect)
                            UpdateDynamicRect(Cursor.Position);
                        Invalidate();
                        Focus();
                    }

                    if (InvokeRequired)
                        BeginInvoke(restore);
                    else
                        restore();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Overlay] RestoreAfterCapture: {ex.Message}");
                }
            };

            next.Start();
        }

        /// <summary>
        /// Paint SHAPE / REGIONS / FOLLOW / MODE / SETTINGS / OPACITY / HIDE / EXIT onto the
        /// opaque chrome form. Client coords match the left strip (hit-tests stay valid).
        /// </summary>
        private void PaintSidebarChrome(Graphics g, int clientHeight)
        {
            // Solid fill — chrome form is already fully opaque (not Form.Opacity on the veil).
            using (var sideBrush = new SolidBrush(UiTheme.Bg))
                g.FillRectangle(sideBrush, 0, 0, SidebarWidth, clientHeight);
            using (var sidePen = new Pen(UiTheme.Border, 1))
                g.DrawLine(sidePen, SidebarWidth - 1, 0, SidebarWidth - 1, clientHeight);

            using var titleFont = new Font("Segoe UI", 11, FontStyle.Bold);
            using var titleBrush = new SolidBrush(UiTheme.FgHeader);
            g.DrawString("SHAPE", titleFont, titleBrush, 12, 18);

            Color activeColor = UiTheme.Accent;
            Color inactiveBg = UiTheme.Button;
            Color flagOnBg = UiTheme.AccentDim;
            Color flagOffBg = UiTheme.BgRaised;
            using var inkPen = new Pen(UiTheme.Fg, 1);
            using var accentPen = new Pen(UiTheme.AccentHot, 1);
            using var iconPen = new Pen(UiTheme.Accent, 2);
            using var fgBrush = new SolidBrush(UiTheme.Fg);
            using var mutedBrush = new SolidBrush(UiTheme.FgMuted);
            using var dimBrush = new SolidBrush(UiTheme.FgDim);
            using var headerBrush = new SolidBrush(UiTheme.FgHeader);

            // RECT
            Rectangle rectBtn = new Rectangle(ShapeBtnX, RectBtnY, ShapeBtnW, ShapeBtnH);
            bool rectActive = _currentMode == CaptureMode.Rectangle;
            using (var bg = new SolidBrush(rectActive ? Color.FromArgb(90, activeColor) : inactiveBg))
                g.FillRectangle(bg, rectBtn);
            g.DrawRectangle(rectActive ? accentPen : inkPen, rectBtn);
            Rectangle rectPreview = new Rectangle(rectBtn.X + 8, rectBtn.Y + 8, 22, 18);
            g.DrawRectangle(iconPen, rectPreview);
            using var btnFont = new Font("Segoe UI", 9, FontStyle.Bold);
            g.DrawString("RECT", btnFont, fgBrush, rectBtn.X + 35, rectBtn.Y + 18);

            // OVAL
            Rectangle ovalBtn = new Rectangle(ShapeBtnX, OvalBtnY, ShapeBtnW, ShapeBtnH);
            bool ovalActive = _currentMode == CaptureMode.Ellipse;
            using (var bg = new SolidBrush(ovalActive ? Color.FromArgb(90, activeColor) : inactiveBg))
                g.FillRectangle(bg, ovalBtn);
            g.DrawRectangle(ovalActive ? accentPen : inkPen, ovalBtn);
            Rectangle ovalPreview = new Rectangle(ovalBtn.X + 8, ovalBtn.Y + 10, 22, 18);
            g.DrawEllipse(iconPen, ovalPreview);
            g.DrawString("OVAL", btnFont, fgBrush, ovalBtn.X + 35, ovalBtn.Y + 18);

            // LASSO
            Rectangle lassoBtn = new Rectangle(ShapeBtnX, LassoBtnY, ShapeBtnW, ShapeBtnH);
            bool lassoActive = _currentMode == CaptureMode.Lasso;
            using (var bg = new SolidBrush(lassoActive ? Color.FromArgb(90, activeColor) : inactiveBg))
                g.FillRectangle(bg, lassoBtn);
            g.DrawRectangle(lassoActive ? accentPen : inkPen, lassoBtn);
            Point[] lassoIcon = {
                new Point(lassoBtn.X + 10, lassoBtn.Y + 15),
                new Point(lassoBtn.X + 18, lassoBtn.Y + 28),
                new Point(lassoBtn.X + 28, lassoBtn.Y + 22),
                new Point(lassoBtn.X + 22, lassoBtn.Y + 38),
                new Point(lassoBtn.X + 32, lassoBtn.Y + 30)
            };
            g.DrawLines(iconPen, lassoIcon);
            g.DrawString("LASSO", btnFont, fgBrush, lassoBtn.X + 38, lassoBtn.Y + 18);

            // REGIONS (slots 1–8) — same action as region hotkeys; no hotkey labels
            using var sectionFont = new Font("Segoe UI", 8, FontStyle.Bold);
            using var flagFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            using var regionNumFont = new Font("Segoe UI", 8f, FontStyle.Bold);
            using var regionCenter = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("REGIONS", sectionFont, dimBrush, 10, RegionSectionY);
            int activeSlot = Math.Clamp((int)_activeRectKey - (int)Keys.F1, 0, 7);
            // Follow (R9) is not an F1–F8 slot — dim highlight while Follow owns the overlay.
            for (int i = 0; i < 8; i++)
            {
                Rectangle rb = GetRegionSlotButtonRect(i);
                bool isActive = !FollowActive && i == activeSlot;
                bool hasGeom = RegionSlotHasSavedGeometry(i);
                Color solid = RegionSlotColors.GetSolid(i);
                Color fill = isActive
                    ? Color.FromArgb(110, solid)
                    : hasGeom
                        ? Color.FromArgb(55, solid)
                        : UiTheme.BgRaised;
                using (var bg = new SolidBrush(fill))
                    g.FillRectangle(bg, rb);
                using (var border = new Pen(isActive ? UiTheme.AccentHot : (hasGeom ? solid : UiTheme.Border), 1))
                    g.DrawRectangle(border, rb);
                g.DrawString(
                    (i + 1).ToString(),
                    regionNumFont,
                    isActive ? fgBrush : (hasGeom ? headerBrush : mutedBrush),
                    rb,
                    regionCenter);
            }

            // FOLLOW (region 9)
            Rectangle followBtn = GetFollowButtonRect();
            bool followOn = FollowActive;
            bool followFloat = dynamic_rect;
            using (var bg = new SolidBrush(followOn ? UiTheme.AccentDim : UiTheme.BgRaised))
                g.FillRectangle(bg, followBtn);
            using (var followPen = new Pen(followOn ? UiTheme.AccentHot : UiTheme.Border, 1))
                g.DrawRectangle(followPen, followBtn);
            using var followTitleFont = new Font("Segoe UI", 8f, FontStyle.Bold);
            using var followHintFont = new Font("Segoe UI", 6.5f);
            g.DrawString(
                followOn ? "● FOLLOW" : "FOLLOW",
                followTitleFont,
                fgBrush,
                followBtn.X + 6,
                followBtn.Y + 4);
            string followHint = !followOn
                ? "click on · Ctrl setup"
                : followFloat ? "float · click off" : "LOCKED · click off";
            g.DrawString(
                followHint,
                followHintFont,
                followOn ? headerBrush : mutedBrush,
                followBtn.X + 6,
                followBtn.Y + 20);

            // MODE FLAGS
            using var flagOnPen = new Pen(UiTheme.AccentHot, 1);
            using var flagOffPen = new Pen(UiTheme.Border, 1);
            var settings = AppSettings.Current;
            int[] flagYs = BuildFlagButtonYs();
            string? lastFlagSection = null;
            for (int i = 0; i < AppSettings.Flags.Length; i++)
            {
                var item = AppSettings.Flags[i];
                if (item.Section != lastFlagSection)
                {
                    int titleY = flagYs[i] - 15;
                    g.DrawString(item.Section, sectionFont, dimBrush, 10, titleY);
                    lastFlagSection = item.Section;
                }

                bool on = item.Getter(settings);
                Rectangle fb = new Rectangle(ShapeBtnX, flagYs[i], ShapeBtnW, FlagBtnH);
                using (var bg = new SolidBrush(on ? flagOnBg : flagOffBg))
                    g.FillRectangle(bg, fb);
                g.DrawRectangle(on ? flagOnPen : flagOffPen, fb);

                g.DrawString(
                    (on ? "● " : "○ ") + item.Label,
                    flagFont,
                    on ? fgBrush : mutedBrush,
                    fb.X + 3,
                    fb.Y + 4);
            }

            // SETTINGS
            Rectangle settingsBtn = GetSettingsButtonRect();
            g.DrawString("SETUP", sectionFont, dimBrush, 10, settingsBtn.Y - 15);
            using (var bg = new SolidBrush(UiTheme.Button))
                g.FillRectangle(bg, settingsBtn);
            using (var settingsPen = new Pen(UiTheme.Accent, 1))
                g.DrawRectangle(settingsPen, settingsBtn);
            g.DrawString("SETTINGS", flagFont, fgBrush, settingsBtn.X + 6, settingsBtn.Y + 6);

            // OPACITY slider
            int opacityY = OpacitySectionY;
            Rectangle opacityTrack = GetOpacityTrackRect();
            double opacityT = Math.Clamp(
                (Opacity - OpacityMin) / (OpacityMax - OpacityMin), 0.0, 1.0);
            int thumbCenterX = opacityTrack.X + (int)Math.Round(opacityT * opacityTrack.Width);
            var opacityThumb = new Rectangle(
                thumbCenterX - OpacityThumbW / 2,
                opacityTrack.Y + (opacityTrack.Height - OpacityThumbH) / 2,
                OpacityThumbW,
                OpacityThumbH);

            g.DrawString("OPACITY", sectionFont, dimBrush, 10, opacityY);
            string opacityPct = $"{(int)Math.Round(Opacity * 100)}%";
            var pctSize = g.MeasureString(opacityPct, flagFont);
            g.DrawString(
                opacityPct,
                flagFont,
                mutedBrush,
                ShapeBtnX + ShapeBtnW - pctSize.Width,
                opacityY);

            using (var trackBg = new SolidBrush(UiTheme.BgInput))
                g.FillRectangle(trackBg, opacityTrack);
            int fillW = Math.Max(0, thumbCenterX - opacityTrack.X);
            if (fillW > 0)
            {
                using var fillBrush = new SolidBrush(UiTheme.Accent);
                g.FillRectangle(
                    fillBrush,
                    opacityTrack.X,
                    opacityTrack.Y,
                    fillW,
                    opacityTrack.Height);
            }
            using (var trackPen = new Pen(UiTheme.Border, 1))
                g.DrawRectangle(trackPen, opacityTrack);
            using (var thumbBrush = new SolidBrush(UiTheme.AccentHot))
                g.FillRectangle(thumbBrush, opacityThumb);
            using (var thumbPen = new Pen(UiTheme.Fg, 1))
                g.DrawRectangle(thumbPen, opacityThumb);

            // HIDE / EXIT — pinned to bottom of the strip (any resolution height).
            Rectangle hideBtn = GetHideButtonRect();
            Rectangle exitBtn = GetExitButtonRect();
            using (var bg = new SolidBrush(UiTheme.Button))
                g.FillRectangle(bg, hideBtn);
            using (var hidePen = new Pen(UiTheme.ButtonBorder, 1))
                g.DrawRectangle(hidePen, hideBtn);
            g.DrawString("HIDE", flagFont, fgBrush, hideBtn.X + 28, hideBtn.Y + 7);

            using (var bg = new SolidBrush(Color.FromArgb(72, 28, 24)))
                g.FillRectangle(bg, exitBtn);
            using (var exitPen = new Pen(UiTheme.Bad, 1))
                g.DrawRectangle(exitPen, exitBtn);
            g.DrawString("EXIT", flagFont, fgBrush, exitBtn.X + 30, exitBtn.Y + 7);

            // Build version under EXIT (muted; not clickable)
            Rectangle verRect = GetVersionLabelRect();
            using var verFont = new Font("Segoe UI", 7f, FontStyle.Regular);
            using var verBrush = new SolidBrush(UiTheme.FgDim);
            using var verFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString(AppInfo.VersionTag, verFont, verBrush, verRect, verFormat);
        }

        // ====================== Helper methods ======================
        private double Distance(Point a, Point b)
        {
            int dx = a.X - b.X;
            int dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private Rectangle GetBoundingRect(List<Point> points)
        {
            if (points == null || points.Count == 0)
                return Rectangle.Empty;

            int minX = points.Min(p => p.X);
            int minY = points.Min(p => p.Y);
            int maxX = points.Max(p => p.X);
            int maxY = points.Max(p => p.Y);

            int w = Math.Max(1, maxX - minX + 1);
            int h = Math.Max(1, maxY - minY + 1);

            return new Rectangle(minX, minY, w, h);
        }
    }
}
