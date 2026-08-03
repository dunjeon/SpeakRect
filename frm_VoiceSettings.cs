using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Voice tab (Settings): pick TTS engine (Windows UWP or SAPI 5), voice, and options.
    /// Writes to AppSettings / SpeakRect.ini.
    /// </summary>
    public sealed class frm_VoiceSettings : Form
    {
        private readonly ComboBox _cmbEngine;
        private readonly ComboBox _cmbVoice;
        private readonly Label _lblVoiceHint;
        private readonly Label _lblOptionsHint;
        private readonly Label _lblSilenceHint;
        private readonly Label _lblPauseHint;
        private readonly CheckBox _chkCustomPauseEncodings;
        private readonly TrackBar _trkRate;
        private readonly TrackBar _trkPitch;
        private readonly TrackBar _trkVolume;
        private readonly TrackBar _trkCommaPause;
        private readonly TrackBar _trkSentencePause;
        private readonly TrackBar _trkOtherPause;
        private readonly TrackBar _trkBubblePause;
        private readonly Label _lblRateVal;
        private readonly Label _lblPitchVal;
        private readonly Label _lblVolumeVal;
        private readonly Label _lblCommaPauseVal;
        private readonly Label _lblSentencePauseVal;
        private readonly Label _lblOtherPauseVal;
        private readonly Label _lblBubblePauseVal;
        private readonly Label _lblCommaPause;
        private readonly Label _lblSentencePause;
        private readonly Label _lblOtherPause;
        private readonly Label _lblBubblePause;
        private readonly ComboBox _cmbAppendedSilence;
        private readonly ComboBox _cmbPunctuationSilence;
        private readonly Label _lblStatus;
        private readonly Button _btnPreview;
        private readonly Button _btnReset;
        private readonly Button? _btnClose;
        private readonly Action? _onRequestClose;
        private readonly bool _embedded;

        private bool _loading;
        private bool _dirty;
        private readonly System.Windows.Forms.Timer _diskSaveTimer;
        private bool _diskSavePending;

        // TrackBar integer scales → double ranges
        // Rate: 50..600 → 0.50..6.00
        // Pitch: 0..200 → 0.00..2.00
        // Volume: 0..100 → 0.00..1.00
        // Speak pauses: 0..MaxSpeakPauseMs → 0.00..3.00 s (value labels in seconds)
        private const int RateMin = 50;
        private const int RateMax = 600;
        private const int PitchMin = 0;
        private const int PitchMax = 200;
        private const int VolMin = 0;
        private const int VolMax = 100;

        public frm_VoiceSettings(bool embedded = false, Action? onRequestClose = null)
        {
            _embedded = embedded;
            _onRequestClose = onRequestClose;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Voice";
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
                MinimumSize = new Size(440, 520);
                ClientSize = new Size(480, 580);
                TopMost = true;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
            }
            KeyPreview = true;
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Fg;
            Font = new Font("Segoe UI", 9f);

            // Memory knobs update every tick; disk write is debounced so drag stays snappy.
            _diskSaveTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _diskSaveTimer.Tick += (_, _) =>
            {
                _diskSaveTimer.Stop();
                FlushDiskSave(force: false);
            };

            // Root: scrollable fields · always-visible status · buttons
            // (engine row + multi-line hints overflowed the old fixed body).
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(14, 12, 14, 10),
                BackColor = UiTheme.Bg,
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));

            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = UiTheme.Bg,
                Padding = new Padding(0, 0, 4, 0),
            };

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = UiTheme.Bg,
                Padding = new Padding(0, 0, 8, 4),
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int row = 0;
            void AddRow(Control label, Control field, int height = 32)
            {
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                body.Controls.Add(label, 0, row);
                body.Controls.Add(field, 1, row);
                row++;
            }

            void AddFull(Control c, int height)
            {
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                body.SetColumnSpan(c, 2);
                body.Controls.Add(c, 0, row);
                row++;
            }

            // ---- Header ----
            AddFull(MakeSection("VOICE"), 26);

            _cmbEngine = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.BgInput,
                ForeColor = UiTheme.Fg,
            };
            _cmbEngine.Items.Add(new EngineItem("Windows", "Windows (default)"));
            _cmbEngine.Items.Add(new EngineItem("Sapi", "SAPI 5 (optional)"));
            AddRow(MakeLabel("Engine"), _cmbEngine, 34);

            _cmbVoice = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.BgInput,
                ForeColor = UiTheme.Fg,
                IntegralHeight = false,
                MaxDropDownItems = 16,
            };
            AddRow(MakeLabel("Voice"), _cmbVoice, 34);

            _lblVoiceHint = MakeHint(
                "Windows works out of the box. SAPI 5 needs a separate install (see README).");
            AddFull(_lblVoiceHint, 40);

            // ---- Rate / Pitch / Volume ----
            AddFull(MakeSection("OPTIONS"), 28);

            _trkRate = MakeTrack(RateMin, RateMax, 100);
            _lblRateVal = MakeValueLabel();
            AddRow(MakeLabel("Rate"), WrapTrack(_trkRate, _lblRateVal), 48);

            _trkPitch = MakeTrack(PitchMin, PitchMax, 100);
            _lblPitchVal = MakeValueLabel();
            AddRow(MakeLabel("Pitch"), WrapTrack(_trkPitch, _lblPitchVal), 48);

            _trkVolume = MakeTrack(VolMin, VolMax, 100);
            _lblVolumeVal = MakeValueLabel();
            AddRow(MakeLabel("Volume"), WrapTrack(_trkVolume, _lblVolumeVal), 48);

            _lblOptionsHint = MakeHint(
                "How fast, high, and loud the voice is.");
            AddFull(_lblOptionsHint, 28);

            // ---- Silence ----
            AddFull(MakeSection("SILENCE"), 28);

            _cmbAppendedSilence = MakeSilenceCombo();
            AddRow(MakeLabel("End silence"), _cmbAppendedSilence, 34);

            _cmbPunctuationSilence = MakeSilenceCombo();
            AddRow(MakeLabel("Punctuation"), _cmbPunctuationSilence, 34);

            _lblSilenceHint = MakeHint(
                "How much quiet space after speech and around punctuation (Windows voice only).");
            AddFull(_lblSilenceHint, 40);

            // ---- Speak-unit pauses (engine-agnostic Task.Delay between units) ----
            AddFull(MakeSection("SPEAK PAUSES"), 28);

            _chkCustomPauseEncodings = new CheckBox
            {
                Text = "Use custom pauses",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.FgMuted,
                BackColor = UiTheme.Bg,
                Checked = true,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 2, 0, 0),
                Cursor = Cursors.Hand,
            };
            AddRow(MakeLabel("Pauses"), _chkCustomPauseEncodings, 32);

            _trkCommaPause = MakeTrack(
                AppSettings.MinSpeakPauseMs, AppSettings.MaxSpeakPauseMs,
                AppSettings.DefaultCommaPauseMs);
            _lblCommaPauseVal = MakeValueLabel();
            _lblCommaPause = MakeLabel("Comma");
            AddRow(_lblCommaPause, WrapTrack(_trkCommaPause, _lblCommaPauseVal), 48);

            _trkSentencePause = MakeTrack(
                AppSettings.MinSpeakPauseMs, AppSettings.MaxSpeakPauseMs,
                AppSettings.DefaultSentencePauseMs);
            _lblSentencePauseVal = MakeValueLabel();
            _lblSentencePause = MakeLabel("Sentence .!?");
            AddRow(_lblSentencePause, WrapTrack(_trkSentencePause, _lblSentencePauseVal), 48);

            _trkOtherPause = MakeTrack(
                AppSettings.MinSpeakPauseMs, AppSettings.MaxSpeakPauseMs,
                AppSettings.DefaultOtherPauseMs);
            _lblOtherPauseVal = MakeValueLabel();
            _lblOtherPause = MakeLabel("Other");
            AddRow(_lblOtherPause, WrapTrack(_trkOtherPause, _lblOtherPauseVal), 48);

            _trkBubblePause = MakeTrack(
                AppSettings.MinSpeakPauseMs, AppSettings.MaxSpeakPauseMs,
                AppSettings.DefaultBubblePauseMs);
            _lblBubblePauseVal = MakeValueLabel();
            _lblBubblePause = MakeLabel("Balloon");
            AddRow(_lblBubblePause, WrapTrack(_trkBubblePause, _lblBubblePauseVal), 48);

            _lblPauseHint = MakeHint(
                "On: wait after commas, sentences, and balloons. Off: ignore the sliders.");
            AddFull(_lblPauseHint, 36);

            // Keep body as wide as the viewport (minus scrollbar) so Dock=Fill hints wrap fully.
            void SyncBodyWidth()
            {
                int avail = scroll.ClientSize.Width;
                int sb = SystemInformation.VerticalScrollBarWidth;
                int w = avail > sb + 220 ? avail - sb - 2 : Math.Max(220, avail - 4);
                body.MinimumSize = new Size(w, 0);
                body.Width = w;
            }
            scroll.Resize += (_, _) => SyncBodyWidth();
            scroll.Controls.Add(body);

            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.FgMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(2, 0, 2, 0),
                BackColor = UiTheme.BgStatus,
            };

            // ---- Bottom buttons ----
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 6, 0, 0),
                BackColor = UiTheme.BgBar,
            };

            _btnPreview = MakeButton("Preview");
            _btnReset = MakeButton("Reset");
            _btnPreview.Click += (_, _) => Preview_Click();
            _btnReset.Click += (_, _) => Reset_Click();

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
            bottom.Controls.Add(_btnPreview);
            bottom.Controls.Add(_btnReset);

            root.Controls.Add(scroll, 0, 0);
            root.Controls.Add(_lblStatus, 0, 1);
            root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);

            Load += (_, _) =>
            {
                SyncBodyWidth();
                LoadFromSettings();
            };

            _trkRate.ValueChanged += (_, _) => OnSliderChanged();
            _trkPitch.ValueChanged += (_, _) => OnSliderChanged();
            _trkVolume.ValueChanged += (_, _) => OnSliderChanged();
            _trkCommaPause.ValueChanged += (_, _) => OnSliderChanged();
            _trkSentencePause.ValueChanged += (_, _) => OnSliderChanged();
            _trkOtherPause.ValueChanged += (_, _) => OnSliderChanged();
            _trkBubblePause.ValueChanged += (_, _) => OnSliderChanged();
            _chkCustomPauseEncodings.CheckedChanged += (_, _) => OnCustomPauseEncodingChanged();
            _cmbEngine.SelectedIndexChanged += (_, _) => OnEngineChanged();
            _cmbVoice.SelectedIndexChanged += (_, _) => OnFieldChanged();
            _cmbAppendedSilence.SelectedIndexChanged += (_, _) => OnFieldChanged();
            _cmbPunctuationSilence.SelectedIndexChanged += (_, _) => OnFieldChanged();

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

            // Load handler registered above (SyncBodyWidth + LoadFromSettings).
            FormClosing += (_, _) =>
            {
                if (_dirty || _diskSavePending)
                    Persist(writeDiskNow: true);
                try { _diskSaveTimer.Stop(); } catch { /* ignore */ }
                try { _diskSaveTimer.Dispose(); } catch { /* ignore */ }
            };
        }

        private static Label MakeLabel(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 0, 8, 0),
        };

        private static Label MakeSection(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgHeader,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 0, 0, 2),
        };

        private static Label MakeHint(string text) => new()
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.FgDim,
            Font = new Font("Segoe UI", 7.5f),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 2, 4, 2),
        };

        private static Label MakeValueLabel() => new()
        {
            AutoSize = false,
            Width = 60,
            Height = 22,
            ForeColor = UiTheme.Fg,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(6, 0, 0, 0),
        };

        private static TrackBar MakeTrack(int min, int max, int value) => new()
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickStyle = TickStyle.None,
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Bg,
            AutoSize = false,
            Height = 36,
        };

        private static Control WrapTrack(TrackBar track, Label value)
        {
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64f));
            p.Controls.Add(track, 0, 0);
            p.Controls.Add(value, 1, 0);
            return p;
        }

        private static ComboBox MakeSilenceCombo()
        {
            var c = new ComboBox
            {
                Dock = DockStyle.Left,
                Width = 140,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.BgInput,
                ForeColor = UiTheme.Fg,
            };
            c.Items.Add("Default");
            c.Items.Add("Min");
            c.SelectedIndex = 0;
            return c;
        }

        private static Button MakeButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(88, 30),
                Margin = new Padding(6, 2, 0, 2),
            };
            UiTheme.StyleButton(btn);
            return btn;
        }

        /// <summary>Refresh controls from <see cref="AppSettings"/> (e.g. after profile load).</summary>
        public void ReloadFromSettings() => LoadFromSettings();

        /// <summary>
        /// Ensure the Voice tab’s current controls are written to <see cref="AppSettings"/>
        /// (used before profile save so engine / voice / options are never stale).
        /// </summary>
        public void FlushToSettings()
        {
            if (_loading) return;
            Persist(writeDiskNow: true);
        }

        private void LoadFromSettings()
        {
            _loading = true;
            try
            {
                var s = AppSettings.Current;
                s.NormalizeVoiceSettings();

                _cmbEngine.SelectedIndex = s.IsSapiTtsEngine ? 1 : 0;
                ReloadVoiceList(s);
                ApplyEngineUiState(s.IsSapiTtsEngine);

                _trkRate.Value = RateToTick(s.VoiceSpeakingRate);
                _trkPitch.Value = PitchToTick(s.VoicePitch);
                _trkVolume.Value = VolToTick(s.VoiceVolume);
                _chkCustomPauseEncodings.Checked = s.VoiceUseCustomPauseEncodings;
                _trkCommaPause.Value = PauseToTick(s.VoiceCommaPauseMs);
                _trkSentencePause.Value = PauseToTick(s.VoiceSentencePauseMs);
                _trkOtherPause.Value = PauseToTick(s.VoiceOtherPauseMs);
                _trkBubblePause.Value = PauseToTick(s.VoiceBubblePauseMs);
                SelectSilence(_cmbAppendedSilence, s.VoiceAppendedSilence);
                SelectSilence(_cmbPunctuationSilence, s.VoicePunctuationSilence);
                ApplyCustomPauseEncodingUiState(_chkCustomPauseEncodings.Checked);
                RefreshValueLabels();
                _dirty = false;
                _lblStatus.Text = $"Active: {OcrProcessor.DescribeCurrentVoice()}";
            }
            finally
            {
                _loading = false;
            }
        }

        private void OnCustomPauseEncodingChanged()
        {
            ApplyCustomPauseEncodingUiState(_chkCustomPauseEncodings.Checked);
            OnFieldChanged();
        }

        private void ApplyCustomPauseEncodingUiState(bool enabled)
        {
            _trkCommaPause.Enabled = enabled;
            _trkSentencePause.Enabled = enabled;
            _trkOtherPause.Enabled = enabled;
            _trkBubblePause.Enabled = enabled;
            _lblCommaPause.Enabled = enabled;
            _lblSentencePause.Enabled = enabled;
            _lblOtherPause.Enabled = enabled;
            _lblBubblePause.Enabled = enabled;
            _lblCommaPauseVal.Enabled = enabled;
            _lblSentencePauseVal.Enabled = enabled;
            _lblOtherPauseVal.Enabled = enabled;
            _lblBubblePauseVal.Enabled = enabled;
            _lblPauseHint.Text = enabled
                ? "On: typed pause marks + gaps after each speak unit (0–3.00 s). Comma · sentence (. ! ?) · other · balloon. Both engines."
                : "Off: punctuation stays for TTS prosody; no typed pause marks or Task.Delay gaps. Slider values are kept for later.";
        }

        private void OnEngineChanged()
        {
            if (_loading) return;
            bool sapi = SelectedEngineIsSapi();
            ApplyEngineUiState(sapi);
            // Rebuild voice list for the new engine; keep prior selection for that engine.
            _loading = true;
            try
            {
                ReloadVoiceList(AppSettings.Current);
            }
            finally
            {
                _loading = false;
            }
            OnFieldChanged();
        }

        private bool SelectedEngineIsSapi() =>
            _cmbEngine.SelectedItem is EngineItem ei &&
            string.Equals(ei.Id, "Sapi", StringComparison.OrdinalIgnoreCase);

        private void ApplyEngineUiState(bool sapi)
        {
            _cmbAppendedSilence.Enabled = !sapi;
            _cmbPunctuationSilence.Enabled = !sapi;
            _lblVoiceHint.Text = sapi
                ? "SAPI 5: Control Panel + registered engines (e.g. NaturalVoiceSAPIAdapter). " +
                  "(System default) = engine default voice. Setup steps are in README."
                : "Windows (default): OneCore / UWP voices. (System default) = Windows " +
                  "SpeechSynthesizer.DefaultVoice (OS setting), not “first English in the list.” " +
                  "Add voices under Windows Speech settings.";
            _lblSilenceHint.Text = sapi
                ? "Silence options apply only to the Windows (UWP) engine."
                : "Default ≈ normal pauses. Min reduces trailing / punctuation silence between phrases.";
        }

        private void ReloadVoiceList(AppSettings s)
        {
            bool sapi = SelectedEngineIsSapi();
            _cmbVoice.Items.Clear();
            _cmbVoice.Items.Add(new VoiceItem("", "(System default)"));
            int select = 0;

            if (sapi)
            {
                var voices = OcrProcessor.ListInstalledSapiVoices();
                for (int i = 0; i < voices.Count; i++)
                {
                    var (name, label) = voices[i];
                    _cmbVoice.Items.Add(new VoiceItem(name, label));
                    if (!string.IsNullOrEmpty(s.SapiVoiceName) &&
                        string.Equals(name, s.SapiVoiceName, StringComparison.OrdinalIgnoreCase))
                        select = i + 1;
                }
            }
            else
            {
                var voices = OcrProcessor.ListInstalledVoices();
                for (int i = 0; i < voices.Count; i++)
                {
                    var (id, label) = voices[i];
                    _cmbVoice.Items.Add(new VoiceItem(id, label));
                    if (!string.IsNullOrEmpty(s.VoiceId) &&
                        string.Equals(id, s.VoiceId, StringComparison.OrdinalIgnoreCase))
                        select = i + 1;
                }
            }

            if (_cmbVoice.Items.Count > 0)
                _cmbVoice.SelectedIndex = Math.Clamp(select, 0, _cmbVoice.Items.Count - 1);
        }

        private static void SelectSilence(ComboBox cmb, string name)
        {
            int idx = name.Equals("Min", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (cmb.Items.Count > idx)
                cmb.SelectedIndex = idx;
        }

        private void OnSliderChanged()
        {
            RefreshValueLabels();
            OnFieldChanged();
        }

        private void OnFieldChanged()
        {
            if (_loading) return;
            _dirty = true;
            Persist(writeDiskNow: false);
            _lblStatus.Text = "Saved · " + OcrProcessor.DescribeCurrentVoice();
        }

        private void RefreshValueLabels()
        {
            var inv = CultureInfo.InvariantCulture;
            _lblRateVal.Text = TickToRate(_trkRate.Value).ToString("0.00", inv);
            _lblPitchVal.Text = TickToPitch(_trkPitch.Value).ToString("0.00", inv);
            _lblVolumeVal.Text = TickToVol(_trkVolume.Value).ToString("0.00", inv);
            _lblCommaPauseVal.Text = FormatPauseSeconds(_trkCommaPause.Value, inv);
            _lblSentencePauseVal.Text = FormatPauseSeconds(_trkSentencePause.Value, inv);
            _lblOtherPauseVal.Text = FormatPauseSeconds(_trkOtherPause.Value, inv);
            _lblBubblePauseVal.Text = FormatPauseSeconds(_trkBubblePause.Value, inv);
        }

        /// <summary>
        /// Write voice knobs into <see cref="AppSettings.Current"/> immediately.
        /// Disk write is debounced unless <paramref name="writeDiskNow"/>.
        /// </summary>
        private void Persist(bool writeDiskNow = false)
        {
            var s = AppSettings.Current;
            bool sapi = SelectedEngineIsSapi();
            s.TtsEngine = sapi ? "Sapi" : "Windows";

            if (_cmbVoice.SelectedItem is VoiceItem vi)
            {
                if (sapi)
                    s.SapiVoiceName = vi.Id ?? "";
                else
                    s.VoiceId = vi.Id ?? "";
            }
            else if (sapi)
                s.SapiVoiceName = "";
            else
                s.VoiceId = "";

            s.VoiceSpeakingRate = TickToRate(_trkRate.Value);
            s.VoicePitch = TickToPitch(_trkPitch.Value);
            s.VoiceVolume = TickToVol(_trkVolume.Value);
            s.VoiceAppendedSilence = _cmbAppendedSilence.SelectedItem as string ?? "Default";
            s.VoicePunctuationSilence = _cmbPunctuationSilence.SelectedItem as string ?? "Default";
            s.VoiceUseCustomPauseEncodings = _chkCustomPauseEncodings.Checked;
            s.VoiceCommaPauseMs = TickToPauseMs(_trkCommaPause.Value);
            s.VoiceSentencePauseMs = TickToPauseMs(_trkSentencePause.Value);
            s.VoiceOtherPauseMs = TickToPauseMs(_trkOtherPause.Value);
            s.VoiceBubblePauseMs = TickToPauseMs(_trkBubblePause.Value);
            s.NormalizeVoiceSettings();
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

        private void Preview_Click()
        {
            Persist(writeDiskNow: true);

            string name = OcrProcessor.DescribeCurrentVoice();
            double rate = TickToRate(_trkRate.Value);
            double pitch = TickToPitch(_trkPitch.Value);
            // name matches the voice ApplyVoiceSettings will use (system default or pick).
            OcrProcessor.SpeakAnnouncement(
                $"This is the SpeakRect voice preview. Rate {rate:0.0}, pitch {pitch:0.0}. Using {name}.");
            _lblStatus.Text = "Playing preview… · " + name;
        }

        private void Reset_Click()
        {
            _loading = true;
            try
            {
                _cmbEngine.SelectedIndex = 0;
                ApplyEngineUiState(sapi: false);
                // Clear both voice ids so default is unambiguous.
                AppSettings.Current.VoiceId = "";
                AppSettings.Current.SapiVoiceName = "";
                ReloadVoiceList(AppSettings.Current);
                if (_cmbVoice.Items.Count > 0)
                    _cmbVoice.SelectedIndex = 0;
                _trkRate.Value = RateToTick(1.0);
                _trkPitch.Value = PitchToTick(1.0);
                _trkVolume.Value = VolToTick(1.0);
                _chkCustomPauseEncodings.Checked = true;
                _trkCommaPause.Value = PauseToTick(AppSettings.DefaultCommaPauseMs);
                _trkSentencePause.Value = PauseToTick(AppSettings.DefaultSentencePauseMs);
                _trkOtherPause.Value = PauseToTick(AppSettings.DefaultOtherPauseMs);
                _trkBubblePause.Value = PauseToTick(AppSettings.DefaultBubblePauseMs);
                _cmbAppendedSilence.SelectedIndex = 0;
                _cmbPunctuationSilence.SelectedIndex = 0;
                ApplyCustomPauseEncodingUiState(true);
                RefreshValueLabels();
            }
            finally
            {
                _loading = false;
            }

            _dirty = true;
            Persist(writeDiskNow: true);
            _lblStatus.Text = "Reset to defaults · " + OcrProcessor.DescribeCurrentVoice();
        }

        private static int RateToTick(double rate) =>
            Math.Clamp((int)Math.Round(rate * 100.0), RateMin, RateMax);

        private static double TickToRate(int tick) => tick / 100.0;

        private static int PitchToTick(double pitch) =>
            Math.Clamp((int)Math.Round(pitch * 100.0), PitchMin, PitchMax);

        private static double TickToPitch(int tick) => tick / 100.0;

        private static int VolToTick(double vol) =>
            Math.Clamp((int)Math.Round(vol * 100.0), VolMin, VolMax);

        private static double TickToVol(int tick) => tick / 100.0;

        private static int PauseToTick(int ms) =>
            Math.Clamp(ms, AppSettings.MinSpeakPauseMs, AppSettings.MaxSpeakPauseMs);

        private static int TickToPauseMs(int tick) =>
            Math.Clamp(tick, AppSettings.MinSpeakPauseMs, AppSettings.MaxSpeakPauseMs);

        private static string FormatPauseSeconds(int ms, CultureInfo inv) =>
            (TickToPauseMs(ms) / 1000.0).ToString("0.00", inv) + " s";

        private sealed class EngineItem
        {
            public string Id { get; }
            public string Label { get; }

            public EngineItem(string id, string label)
            {
                Id = id ?? "";
                Label = label ?? "";
            }

            public override string ToString() => Label;
        }

        private sealed class VoiceItem
        {
            public string Id { get; }
            public string Label { get; }

            public VoiceItem(string id, string label)
            {
                Id = id ?? "";
                Label = label ?? "";
            }

            public override string ToString() => Label;
        }
    }
}
