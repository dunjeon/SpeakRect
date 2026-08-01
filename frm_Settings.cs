using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Unified settings window: profile save/load at the top, tabs for
    /// Key Map, Regions, Follow, Voice, Speech, Image, Balloons, Analytics, and Help.
    /// Hosted as a tool window over the overlay.
    /// </summary>
    public sealed class frm_Settings : Form
    {
        public enum SettingsTab
        {
            KeyMap = 0,
            Regions = 1,
            Follow = 2,
            Voice = 3,
            Speech = 4,
            Image = 5,
            Balloons = 6,
            Analytics = 7,
            Help = 8,
        }

        private readonly Action _onHotkeysChanged;
        private readonly Action? _onBeforeProfileSave;
        private readonly Action? _onAfterProfileLoad;
        private readonly Action? _onFollowChanged;
        private readonly Action? _onRegionsChanged;
        private readonly Action? _onModeChanged;
        /// <summary>
        /// Host snaps the active F1–F8 region (no OCR/TTS). Caller owns the bitmap.
        /// </summary>
        private readonly Func<System.Threading.Tasks.Task<(Bitmap? Bitmap, string Error)>>? _captureActiveRegion;

        private readonly Panel _profileBar;
        private readonly FlowLayoutPanel _profileFlow;
        private readonly ComboBox _cmbProfile;
        private readonly Button _btnProfileLoad;
        private readonly Button _btnProfileSave;
        private readonly Button _btnProfileSaveAs;
        private readonly Button _btnProfileDelete;
        private readonly ThemedTabControl _tabs;
        private readonly Panel _bottom;
        private readonly Button _btnClose;
        private readonly Label _status;

        private readonly frm_HotkeyMap _keyMap;
        private readonly frm_RegionMap _regions;
        private readonly frm_VoiceSettings _voice;
        private readonly frm_SpeechRules _speech;
        private readonly frm_ComicRegions _balloons;
        private readonly frm_ImagePrep _imagePrep;
        private readonly frm_FollowSettings _follow;
        private readonly frm_Analytics _analytics;
        private readonly frm_Help _help;

        private bool _suppressProfileComboEvents;
        private bool _profileLoadInProgress;

        /// <summary>True while Key Map is listening for a keyboard/gamepad bind.</summary>
        public bool IsCapturingHotkey => _keyMap.IsCapturing;

        /// <summary>Raised when Key Map capture starts or ends.</summary>
        public event EventHandler? HotkeyCaptureStateChanged;

        public frm_Settings(
            Action onHotkeysChanged,
            Action? onBeforeProfileSave = null,
            Action? onAfterProfileLoad = null,
            Action? onFollowChanged = null,
            Action? onRegionsChanged = null,
            Action? onModeChanged = null,
            Func<System.Threading.Tasks.Task<(Bitmap? Bitmap, string Error)>>? captureActiveRegion = null,
            SettingsTab initialTab = SettingsTab.KeyMap)
        {
            _onHotkeysChanged = onHotkeysChanged;
            _onBeforeProfileSave = onBeforeProfileSave;
            _onAfterProfileLoad = onAfterProfileLoad;
            _onFollowChanged = onFollowChanged;
            _onRegionsChanged = onRegionsChanged;
            _onModeChanged = onModeChanged;
            _captureActiveRegion = captureActiveRegion;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Settings";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterScreen;
            // Room for Analytics thumbs + nine tab labels without clipping names.
            MinimumSize = new Size(1040, 700);
            ClientSize = new Size(1120, 800);
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            UiTheme.ApplyForm(this);
            Font = new Font("Segoe UI", 9f);
            MinimizeBox = false;
            MaximizeBox = true;

            // ---- Profile bar (top of whole settings window) ----
            _profileBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = UiTheme.BgBar,
            };
            _profileFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(10, 8, 10, 6),
                BackColor = UiTheme.BgBar,
            };
            var lblProfile = new Label
            {
                Text = "Profile",
                AutoSize = true,
                ForeColor = UiTheme.FgMuted,
                Margin = new Padding(0, 6, 6, 0),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _cmbProfile = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 168,
                Margin = new Padding(0, 2, 8, 0),
            };
            UiTheme.StyleCombo(_cmbProfile);
            _cmbProfile.SelectedIndexChanged += (_, _) => ProfileCombo_SelectedIndexChanged();
            _btnProfileLoad = MakeProfileButton("Load");
            _btnProfileSave = MakeProfileButton("Save");
            _btnProfileSaveAs = MakeProfileButton("Save As…");
            _btnProfileDelete = MakeProfileButton("Delete");
            _btnProfileLoad.Click += (_, _) => ProfileLoad_Click(forceReload: true);
            _btnProfileSave.Click += (_, _) => ProfileSave_Click(saveAs: false);
            _btnProfileSaveAs.Click += (_, _) => ProfileSave_Click(saveAs: true);
            _btnProfileDelete.Click += (_, _) => ProfileDelete_Click();
            _profileFlow.Controls.Add(lblProfile);
            _profileFlow.Controls.Add(_cmbProfile);
            _profileFlow.Controls.Add(_btnProfileLoad);
            _profileFlow.Controls.Add(_btnProfileSave);
            _profileFlow.Controls.Add(_btnProfileSaveAs);
            _profileFlow.Controls.Add(_btnProfileDelete);
            _profileBar.Controls.Add(_profileFlow);

            // ---- Tabs (owner-drawn black/ink + orange; no white system tabs) ----
            _tabs = new ThemedTabControl
            {
                Dock = DockStyle.Fill,
                // Wide enough for "Key Map" / "Balloons" / "Analytics" without clipping.
                ItemSize = new Size(100, 32),
            };

            TabPage MakeTab(string title) => new(title)
            {
                BackColor = UiTheme.Bg,
                Padding = new Padding(0),
                UseVisualStyleBackColor = false,
                ForeColor = UiTheme.Fg,
            };
            var tabKeyMap = MakeTab("Key Map");
            var tabRegions = MakeTab("Regions");
            var tabFollow = MakeTab("Follow");
            var tabVoice = MakeTab("Voice");
            var tabSpeech = MakeTab("Speech");
            var tabImage = MakeTab("Image");
            var tabBalloons = MakeTab("Balloons");
            var tabAnalytics = MakeTab("Analytics");
            var tabHelp = MakeTab("Help");

            _keyMap = new frm_HotkeyMap(
                onHotkeysChanged: () => _onHotkeysChanged(),
                onBeforeProfileSave: null,
                onAfterProfileLoad: null,
                embedded: true,
                onRequestClose: () => Close());
            _keyMap.CaptureStateChanged += (_, e) =>
            {
                try { HotkeyCaptureStateChanged?.Invoke(this, e); }
                catch { /* ignore */ }
            };
            EmbedChild(_keyMap, tabKeyMap);

            _regions = new frm_RegionMap(
                onRegionsChanged: () => _onRegionsChanged?.Invoke(),
                embedded: true,
                onRequestClose: () => Close());
            EmbedChild(_regions, tabRegions);

            _follow = new frm_FollowSettings(
                onChanged: () => _onFollowChanged?.Invoke(),
                embedded: true,
                onRequestClose: () => Close());
            EmbedChild(_follow, tabFollow);

            _voice = new frm_VoiceSettings(
                embedded: true,
                onRequestClose: () => Close());
            EmbedChild(_voice, tabVoice);

            _speech = new frm_SpeechRules(
                embedded: true,
                onRequestClose: () => Close());
            EmbedChild(_speech, tabSpeech);

            _imagePrep = new frm_ImagePrep(
                embedded: true,
                onRequestClose: () => Close(),
                onCaptureActiveRegion: _captureActiveRegion);
            EmbedChild(_imagePrep, tabImage);

            _balloons = new frm_ComicRegions(
                embedded: true,
                onRequestClose: () => Close(),
                onCaptureActiveRegion: _captureActiveRegion,
                onModeChanged: () => _onModeChanged?.Invoke());
            EmbedChild(_balloons, tabBalloons);

            _analytics = new frm_Analytics(
                embedded: true,
                onRequestClose: () => Close());
            EmbedChild(_analytics, tabAnalytics);

            _help = new frm_Help(
                goToTab: tab => SelectTab(tab),
                embedded: true,
                onRequestClose: () => Close());
            EmbedChild(_help, tabHelp);

            _tabs.TabPages.Add(tabKeyMap);
            _tabs.TabPages.Add(tabRegions);
            _tabs.TabPages.Add(tabFollow);
            _tabs.TabPages.Add(tabVoice);
            _tabs.TabPages.Add(tabSpeech);
            _tabs.TabPages.Add(tabImage);
            _tabs.TabPages.Add(tabBalloons);
            _tabs.TabPages.Add(tabAnalytics);
            _tabs.TabPages.Add(tabHelp);

            // Refresh region map / analytics when user switches to those tabs;
            // remember last tab for next Settings open.
            _tabs.SelectedIndexChanged += (_, _) =>
            {
                RememberCurrentTab();
                // Flush tool tabs so AppSettings matches the last tab the user edited.
                try { _imagePrep.FlushToSettings(); } catch { /* ignore */ }
                try { _balloons.FlushToSettings(); } catch { /* ignore */ }
                try { _follow.FlushToSettings(); } catch { /* ignore */ }
                try { _voice.FlushToSettings(); } catch { /* ignore */ }
                try { _speech.FlushToSettings(); } catch { /* ignore */ }

                if (_tabs.SelectedIndex == (int)SettingsTab.Regions)
                    _regions.OnTabSelected();
                else if (_tabs.SelectedIndex == (int)SettingsTab.Analytics)
                    _analytics.ReloadFromSettings();
                else if (_tabs.SelectedIndex == (int)SettingsTab.Image)
                    _imagePrep.ReloadFromSettings();
                else if (_tabs.SelectedIndex == (int)SettingsTab.Balloons)
                    _balloons.ReloadFromSettings();
                else if (_tabs.SelectedIndex == (int)SettingsTab.Follow)
                    _follow.ReloadFromSettings();
                else if (_tabs.SelectedIndex == (int)SettingsTab.Voice)
                    _voice.ReloadFromSettings();
                else if (_tabs.SelectedIndex == (int)SettingsTab.Speech)
                    _speech.ReloadFromSettings();
            };

            // ---- Status (profile ops) + bottom close ----
            // Keep a single outer status strip; tabs keep their own action bars.
            _status = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Bottom,
                Height = 26,
                Padding = new Padding(12, 3, 12, 2),
                Text = "Ready.",
                ForeColor = UiTheme.Ok,
                BackColor = UiTheme.BgStatus,
                AutoEllipsis = true,
            };
            _bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                BackColor = UiTheme.BgBar,
            };
            _btnClose = new Button
            {
                Text = "Close",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(100, 30),
                Padding = new Padding(14, 4, 14, 4),
            };
            UiTheme.StylePrimaryButton(_btnClose);
            _btnClose.Click += (_, _) => Close();
            _bottom.Controls.Add(_btnClose);
            _bottom.Resize += (_, _) => LayoutBottom();

            // Dock order: Fill first, then Bottom (status above close), then Top profile
            SuspendLayout();
            Controls.Add(_tabs);
            Controls.Add(_status);
            Controls.Add(_bottom);
            Controls.Add(_profileBar);
            ResumeLayout(true);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    Close();
                }
            };

            Load += (_, _) =>
            {
                SizeProfileControls();
                LayoutBottom();
                RefreshProfileCombo();
                SetStatusReady();
                SelectTab(initialTab);
            };
            Shown += (_, _) =>
            {
                SizeProfileControls();
                LayoutBottom();
                // Ensure embedded children finish layout after parent has a real size
                _keyMap.PerformLayout();
                _regions.PerformLayout();
                _follow.PerformLayout();
                _voice.PerformLayout();
                _speech.PerformLayout();
                _imagePrep.PerformLayout();
                _balloons.PerformLayout();
                _analytics.PerformLayout();
                _help.PerformLayout();
                _regions.ReloadFromSettings();
                _follow.ReloadFromSettings();
                _speech.ReloadFromSettings();
                _imagePrep.ReloadFromSettings();
                _balloons.ReloadFromSettings();
                _analytics.ReloadFromSettings();
                // Keyboard Tab order + dark scrollbars across Settings and all tabs.
                ApplySettingsAccessibilityChrome();
            };
            FormClosing += (_, _) =>
            {
                RememberCurrentTab();
                _keyMap.CancelActiveCapture();
                // Embedded tabs may not get FormClosing reliably — flush all knobs on close.
                try { _voice.FlushToSettings(); } catch { /* ignore */ }
                try { _speech.FlushToSettings(); } catch { /* ignore */ }
                try { _follow.FlushToSettings(); } catch { /* ignore */ }
                try { _imagePrep.FlushToSettings(); } catch { /* ignore */ }
                try { _balloons.FlushToSettings(); } catch { /* ignore */ }
                // Refined Balloons speak runs on overlay hide (not Settings close).
            };
        }

        /// <summary>Persist the active tab so the next Settings open returns here.</summary>
        private void RememberCurrentTab()
        {
            try
            {
                int idx = _tabs.SelectedIndex;
                if (idx < 0 || idx >= Enum.GetValues(typeof(SettingsTab)).Length)
                    return;
                var tab = (SettingsTab)idx;
                AppSettings.Current.RememberSettingsTab(tab.ToString());
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Push Balloons refined regions into the session so overlay-hide speak can use them.
        /// </summary>
        public void FlushBalloonsRefineSession()
        {
            try { _balloons.FlushToSettings(); } catch { /* ignore */ }
            try { _balloons.FlushRefineSessionForOverlay(); } catch { /* ignore */ }
        }

        /// <summary>Resolve a stored tab name to <see cref="SettingsTab"/> (fallback KeyMap).</summary>
        public static SettingsTab ParseSettingsTab(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return SettingsTab.KeyMap;
            if (Enum.TryParse(name.Trim(), ignoreCase: true, out SettingsTab tab))
                return tab;
            return SettingsTab.KeyMap;
        }

        /// <summary>
        /// Follow: Enter commits coords + refreshes the diagram.
        /// Other tabs: do not swallow Enter (Key Map capture / normal WinForms).
        /// Overlay speak is blocked separately while Settings is open.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((keyData == Keys.Enter || keyData == Keys.Return) &&
                (_tabs.SelectedIndex == (int)SettingsTab.Follow || _follow.ContainsFocus))
            {
                _follow.ApplyEditorsAndRefreshPreview();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Enter &&
                (_tabs.SelectedIndex == (int)SettingsTab.Follow || _follow.ContainsFocus))
            {
                _follow.ApplyEditorsAndRefreshPreview();
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        private static void EmbedChild(Form child, TabPage host)
        {
            child.TopLevel = false;
            child.FormBorderStyle = FormBorderStyle.None;
            child.Dock = DockStyle.Fill;
            child.Visible = true;
            child.ShowInTaskbar = false;
            // Embedded children still need dark scrollbars + title-bar path if undocked later.
            UiTheme.ApplyDarkTitleBar(child);
            host.Controls.Add(child);
            child.Show();
        }

        /// <summary>
        /// Profile bar → each tab page → close button: sequential Tab indices so
        /// users can keyboard-navigate every interactive control.
        /// </summary>
        private void ApplySettingsAccessibilityChrome()
        {
            UiTheme.ApplyDarkChromeTree(this);

            int idx = 0;
            idx = UiTheme.ApplyTabOrder(_profileBar, idx);
            _tabs.TabStop = true;
            _tabs.TabIndex = idx++;
            foreach (TabPage page in _tabs.TabPages)
                idx = UiTheme.ApplyTabOrder(page, idx);
            idx = UiTheme.ApplyTabOrder(_status, idx);
            _ = UiTheme.ApplyTabOrder(_bottom, idx);
            // Balloons / Image use fixed left→buttons→detail order (screen Y sort is wrong).
            _balloons.ApplyKeyboardTabOrder();
            _imagePrep.ApplyKeyboardTabOrder();
        }

        public void SelectTab(SettingsTab tab)
        {
            int idx = Math.Clamp((int)tab, 0, _tabs.TabPages.Count - 1);
            _tabs.SelectedIndex = idx;
            switch (tab)
            {
                case SettingsTab.KeyMap:
                    UiTheme.ApplyDarkChromeTree(_keyMap);
                    UiTheme.ApplyTabOrder(_keyMap);
                    _keyMap.Focus();
                    break;
                case SettingsTab.Regions:
                    // OnTabSelected reloads + focuses the slots list so owner-draw paints all 8.
                    _regions.OnTabSelected();
                    UiTheme.ApplyDarkChromeTree(_regions);
                    UiTheme.ApplyTabOrder(_regions);
                    break;
                case SettingsTab.Follow:
                    _follow.ReloadFromSettings();
                    UiTheme.ApplyDarkChromeTree(_follow);
                    UiTheme.ApplyTabOrder(_follow);
                    _follow.Focus();
                    break;
                case SettingsTab.Voice:
                    // Voice persists on every change; reload in case a profile load left UI behind.
                    _voice.ReloadFromSettings();
                    UiTheme.ApplyDarkChromeTree(_voice);
                    UiTheme.ApplyTabOrder(_voice);
                    _voice.Focus();
                    break;
                case SettingsTab.Speech:
                    _speech.ReloadFromSettings();
                    UiTheme.ApplyDarkChromeTree(_speech);
                    UiTheme.ApplyTabOrder(_speech);
                    _speech.Focus();
                    break;
                case SettingsTab.Image:
                    try { _balloons.FlushToSettings(); } catch { /* ignore */ }
                    try { _imagePrep.FlushToSettings(); } catch { /* ignore */ }
                    _imagePrep.ReloadFromSettings();
                    UiTheme.ApplyDarkChromeTree(_imagePrep);
                    _imagePrep.ApplyKeyboardTabOrder();
                    _imagePrep.Focus();
                    break;
                case SettingsTab.Balloons:
                    // Image prep must be flushed before Balloons builds detect preview.
                    try { _imagePrep.FlushToSettings(); } catch { /* ignore */ }
                    try { _balloons.FlushToSettings(); } catch { /* ignore */ }
                    _balloons.ReloadFromSettings();
                    UiTheme.ApplyDarkChromeTree(_balloons);
                    // Explicit left-column → buttons → detail order (not screen Y sort).
                    _balloons.ApplyKeyboardTabOrder();
                    _balloons.Focus();
                    break;
                case SettingsTab.Analytics:
                    _analytics.ReloadFromSettings();
                    UiTheme.ApplyDarkChromeTree(_analytics);
                    UiTheme.ApplyTabOrder(_analytics);
                    _analytics.Focus();
                    break;
                case SettingsTab.Help:
                    _help.ReloadFromSettings();
                    UiTheme.ApplyDarkChromeTree(_help);
                    UiTheme.ApplyTabOrder(_help);
                    _help.Focus();
                    break;
            }
        }

        /// <summary>Refresh all panels after external profile / settings change.</summary>
        public void ReloadFromSettings()
        {
            _keyMap.ReloadFromSettings();
            _regions.ReloadFromSettings();
            _voice.ReloadFromSettings();
            _speech.ReloadFromSettings();
            _balloons.ReloadFromSettings();
            _imagePrep.ReloadFromSettings();
            _follow.ReloadFromSettings();
            _analytics.ReloadFromSettings();
            _help.ReloadFromSettings();
            RefreshProfileCombo();
            SetStatusReady();
        }

        /// <summary>
        /// MODE toggle only touches Comic Book / POI coherence — refresh Balloons
        /// checkboxes without wiping in-progress edits on Voice / Key Map / etc.
        /// </summary>
        public void ReloadBalloonsFromModeChange()
        {
            try { _balloons.ReloadFromSettings(); }
            catch { /* ignore */ }
        }

        private void LayoutBottom()
        {
            int y = Math.Max(8, (_bottom.ClientSize.Height - _btnClose.Height) / 2);
            _btnClose.Location = new Point(
                Math.Max(12, _bottom.ClientSize.Width - _btnClose.Width - 12), y);
        }

        private Button MakeProfileButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(56, 28),
                Padding = new Padding(10, 3, 10, 3),
                Margin = new Padding(0, 0, 6, 0),
                Font = Font,
            };
            UiTheme.StyleButton(btn);
            return btn;
        }

        private void SizeProfileControls()
        {
            float dpi = DeviceDpi > 0 ? DeviceDpi : 96f;
            int Scale(int px96) => (int)Math.Round(px96 * (dpi / 96f));
            _cmbProfile.Width = Scale(180);
            _cmbProfile.Height = Math.Max(_cmbProfile.PreferredHeight, Scale(24));
            int rowH = 0;
            foreach (Control c in _profileFlow.Controls)
                rowH = Math.Max(rowH, c.Height + c.Margin.Vertical);
            _profileBar.Height = Math.Max(Scale(40), rowH + _profileFlow.Padding.Vertical + 4);
            PerformLayout();
        }

        private void RefreshProfileCombo()
        {
            string active = AppSettings.Current.ActiveProfileName ?? "";
            var names = AppSettings.ListProfiles().ToList();
            if (!string.IsNullOrWhiteSpace(active) &&
                !names.Exists(n => n.Equals(active, StringComparison.OrdinalIgnoreCase)))
            {
                names.Insert(0, active);
            }

            _suppressProfileComboEvents = true;
            try
            {
                _cmbProfile.BeginUpdate();
                _cmbProfile.Items.Clear();
                foreach (var n in names)
                    _cmbProfile.Items.Add(n);

                int idx = names.FindIndex(n =>
                    n.Equals(active, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    _cmbProfile.SelectedIndex = idx;
                else if (_cmbProfile.Items.Count > 0)
                    _cmbProfile.SelectedIndex = 0;
                else
                    _cmbProfile.SelectedIndex = -1;
                _cmbProfile.EndUpdate();
            }
            finally
            {
                _suppressProfileComboEvents = false;
            }
        }

        private string SelectedProfileName()
        {
            if (_cmbProfile.SelectedItem is string s && !string.IsNullOrWhiteSpace(s))
                return s.Trim();
            if (_cmbProfile.SelectedIndex >= 0 &&
                _cmbProfile.SelectedIndex < _cmbProfile.Items.Count)
            {
                return (_cmbProfile.Items[_cmbProfile.SelectedIndex]?.ToString() ?? "").Trim();
            }
            return (_cmbProfile.Text ?? "").Trim();
        }

        private void ProfileCombo_SelectedIndexChanged()
        {
            if (_suppressProfileComboEvents || _profileLoadInProgress)
                return;
            if (_cmbProfile.SelectedIndex < 0)
                return;

            string name = SelectedProfileName();
            if (string.IsNullOrEmpty(name))
                return;

            if (name.Equals(AppSettings.Current.ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                return;

            if (!AppSettings.ProfileExists(name))
            {
                SetStatus($"No saved file for “{name}”.", bad: true);
                return;
            }

            ProfileLoad_Click(forceReload: false);
        }

        private void ProfileLoad_Click(bool forceReload = true)
        {
            if (_profileLoadInProgress)
                return;

            _keyMap.CancelActiveCapture();
            string name = SelectedProfileName();
            if (string.IsNullOrEmpty(name))
            {
                SetStatus("Pick a profile name to load.", bad: true);
                return;
            }

            if (!forceReload &&
                name.Equals(AppSettings.Current.ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                return;

            _profileLoadInProgress = true;
            try
            {
                if (!AppSettings.Current.LoadProfile(name, out string? error))
                {
                    SetStatus(error ?? "Load failed.", bad: true);
                    return;
                }

                _keyMap.ReloadFromSettings();
                _regions.ReloadFromSettings();
                _voice.ReloadFromSettings();
                _speech.ReloadFromSettings();
                _imagePrep.ReloadFromSettings();
                _balloons.ReloadFromSettings();
                _follow.ReloadFromSettings();
                _help.ReloadFromSettings();
                RefreshProfileCombo();
                SetStatus($"Loaded profile “{AppSettings.Current.ActiveProfileName}”.");
                try { _onAfterProfileLoad?.Invoke(); }
                catch { /* host apply failed — settings already loaded */ }
                if (_onAfterProfileLoad == null)
                    _onHotkeysChanged();
            }
            finally
            {
                _profileLoadInProgress = false;
            }
        }

        private void ProfileSave_Click(bool saveAs)
        {
            _keyMap.CancelActiveCapture();
            string name = SelectedProfileName();
            if (saveAs || string.IsNullOrWhiteSpace(name))
            {
                string? typed = PromptProfileName(
                    saveAs ? "Save profile as…" : "Save profile…",
                    string.IsNullOrWhiteSpace(name) ? AppSettings.Current.ActiveProfileName : name);
                if (typed == null) return;
                name = typed;
            }

            try { _onBeforeProfileSave?.Invoke(); }
            catch { /* still try to save what we have */ }
            // Voice / Speech / Follow / Balloons / Image may have pending edits — force AppSettings before snapshot.
            try { _voice.FlushToSettings(); } catch { /* still save what we have */ }
            try { _speech.FlushToSettings(); } catch { /* still save what we have */ }
            try { _follow.FlushToSettings(); } catch { /* still save what we have */ }
            try { _balloons.FlushToSettings(); } catch { /* still save what we have */ }
            try { _imagePrep.FlushToSettings(); } catch { /* still save what we have */ }
            AppSettings.Current.NormalizeModeFlags();
            AppSettings.Current.NormalizeFollowSettings();
            AppSettings.Current.NormalizeVoiceSettings();
            AppSettings.Current.NormalizeComicRegionSettings();
            AppSettings.Current.NormalizeImagePrepSettings();
            if (!AppSettings.Current.SaveProfile(name, out string? error))
            {
                SetStatus(error ?? "Save failed.", bad: true);
                return;
            }
            RefreshProfileCombo();
            string eng = AppSettings.Current.IsSapiTtsEngine ? "SAPI" : "Windows";
            int rules = AppSettings.Current.SpeechRules.Count;
            SetStatus(
                $"Saved profile “{AppSettings.Current.ActiveProfileName}” " +
                $"(modes, regions, balloons, image, follow, voice/{eng}, speech×{rules}, hotkeys).");
        }

        private void ProfileDelete_Click()
        {
            _keyMap.CancelActiveCapture();
            string name = SelectedProfileName();
            if (string.IsNullOrEmpty(name))
            {
                SetStatus("Pick a profile to delete.", bad: true);
                return;
            }
            if (!AppSettings.ProfileExists(name))
            {
                SetStatus($"No saved file for “{name}”.", bad: true);
                return;
            }
            if (UiMessageBox.Show(this,
                    $"Delete profile “{name}” from disk?\n\nCurrent settings stay as they are.",
                    "Delete profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                != DialogResult.Yes)
                return;
            if (!AppSettings.Current.DeleteProfile(name, out string? error))
            {
                SetStatus(error ?? "Delete failed.", bad: true);
                return;
            }
            RefreshProfileCombo();
            SetStatus($"Deleted profile “{name}”.");
        }

        private string? PromptProfileName(string title, string defaultName)
        {
            using var dlg = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(340, 110),
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                TopMost = true,
                Font = Font,
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
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return null;
            if (!AppSettings.TryNormalizeProfileName(tb.Text, out string clean, out string? error))
            {
                UiMessageBox.Show(this, error ?? "Invalid name.", "Profile",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            return clean;
        }

        private void SetStatus(string text, bool bad = false)
        {
            _status.Text = text;
            _status.ForeColor = bad ? UiTheme.Bad : UiTheme.Ok;
        }

        private void SetStatusReady()
        {
            string profile = AppSettings.Current.ActiveProfileName;
            int idx = Math.Clamp(AppSettings.Current.GamepadControllerIndex, 0, 3);
            bool connected = XInputPoller.IsControllerConnected(idx);
            int rules = AppSettings.Current.SpeechRules.Count;
            string pad = connected
                ? $"Controller {idx} connected"
                : $"No controller on slot {idx}";
            string speech = rules == 0
                ? "no speech rules"
                : $"{rules} speech rule{(rules == 1 ? "" : "s")}";
            _status.Text = $"Profile “{profile}”. {speech}. {pad}.";
            _status.ForeColor = connected ? UiTheme.Ok : UiTheme.FgMuted;
        }
    }
}
