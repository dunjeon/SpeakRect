using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Watch tab (Settings): timer-read one saved region when its text changes.
    /// Does not overlap or stop in-progress speech.
    /// </summary>
    public sealed class frm_WatchSettings : Form
    {
        private readonly CheckBox _chkEnabled;
        private readonly ComboBox _cmbRegion;
        private readonly ComboBox _cmbPipeline;
        private readonly ComboBox _cmbTextSource;
        private readonly NumericUpDown _numInterval;
        private readonly CheckBox _chkClearOnNoText;
        private readonly NumericUpDown _numMinDiff;
        private readonly Label _lblStatus;
        private readonly Label _lblLive;
        private readonly Button _btnReset;
        private readonly Button? _btnClose;
        private readonly Action? _onRequestClose;
        private readonly Action? _onChanged;
        private readonly bool _embedded;

        private bool _loading;
        private bool _dirty;
        private readonly System.Windows.Forms.Timer _diskSaveTimer;
        private readonly System.Windows.Forms.Timer _statusTimer;
        private bool _diskSavePending;

        public frm_WatchSettings(
            Action? onChanged = null,
            bool embedded = false,
            Action? onRequestClose = null)
        {
            _onChanged = onChanged;
            _embedded = embedded;
            _onRequestClose = onRequestClose;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Watch";
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
                MinimumSize = new Size(520, 480);
                ClientSize = new Size(560, 520);
                TopMost = true;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
            }
            KeyPreview = true;
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Fg;
            Font = new Font("Segoe UI", 9.5f);

            _diskSaveTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _diskSaveTimer.Tick += (_, _) =>
            {
                _diskSaveTimer.Stop();
                FlushDiskSave(force: false);
            };

            _statusTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _statusTimer.Tick += (_, _) => RefreshLiveStatus();

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

            int row = 0;
            void AddFull(Control c, int height)
            {
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                body.Controls.Add(c, 0, row);
                row++;
            }

            AddFull(MakeIntro(
                "Timer-check one saved region. If the text changed and nothing else is " +
                "being read, SpeakRect reads it. Never overlaps or stops speech in progress."), 48);

            AddFull(MakeSection("ENABLE"), 28);
            _chkEnabled = new CheckBox
            {
                Text = "Watch this region for text changes",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = false,
            };
            _chkEnabled.CheckedChanged += (_, _) => OnFieldChanged();
            AddFull(_chkEnabled, 32);
            AddFull(MakeHint(
                "Watch runs on its own background thread. Toggle with Key Map (default Ctrl+Shift+W) " +
                "or the checkbox below. Opening the overlay stops it immediately. Hide the overlay " +
                "(Escape) to resume. It also waits while Settings is open or anything is already speaking."), 56);

            AddFull(MakeSection("REGION"), 28);
            _cmbRegion = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 520,
                DropDownWidth = 560,
                Font = new Font("Segoe UI", 10f),
            };
            UiTheme.StyleCombo(_cmbRegion);
            _cmbRegion.SelectedIndexChanged += (_, _) => OnFieldChanged();
            AddFull(WrapField(_cmbRegion), 40);
            AddFull(MakeHint(
                "Pick any slot 1–8 that you have drawn. Follow (region 9) cannot be watched — " +
                "it tracks the mouse, not a fixed box."), 40);

            AddFull(MakeSection("PIPELINE"), 28);
            _cmbPipeline = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 520,
                DropDownWidth = 560,
                Font = new Font("Segoe UI", 10f),
            };
            UiTheme.StyleCombo(_cmbPipeline);
            _cmbPipeline.Items.Add(new PipeItem(
                WatchPipeline.RawSnap, "Raw snap — region pixels only (fastest)"));
            _cmbPipeline.Items.Add(new PipeItem(
                WatchPipeline.Image, "Image — Image tab cleanup, then one full-frame read"));
            _cmbPipeline.Items.Add(new PipeItem(
                WatchPipeline.ImageBalloon,
                "Image + Balloon — Image tab + balloon boxes, then one read per balloon"));
            _cmbPipeline.SelectedIndexChanged += (_, _) => OnFieldChanged();
            AddFull(WrapField(_cmbPipeline), 40);
            AddFull(MakeHint(
                "Independent of MODE. Raw snap skips the Image tab. Image matches Default speak " +
                "(prep, then one full-frame read). Image + Balloon matches Comic Book speak " +
                "(prep + balloons). Every check still asks OCR yes/no first; no text → stop."), 56);

            AddFull(MakeSection("TEXT SOURCE"), 28);
            _cmbTextSource = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 520,
                DropDownWidth = 560,
                Font = new Font("Segoe UI", 10f),
            };
            UiTheme.StyleCombo(_cmbTextSource);
            _cmbTextSource.Items.Add(new TextSourceItem(
                WatchTextSource.LocalLlm, "Local-LLM (default) — more accurate"));
            _cmbTextSource.Items.Add(new TextSourceItem(
                WatchTextSource.Ocr, "OCR — faster, skips the local model"));
            _cmbTextSource.SelectedIndexChanged += (_, _) => OnFieldChanged();
            AddFull(WrapField(_cmbTextSource), 40);
            AddFull(MakeHint(
                "Watch override of Settings → Speech text source. Local-LLM is the Watch default. " +
                "OCR reads the same pipeline bitmap without calling the model. Image prep, balloons, " +
                "speech rules, pauses, and voice still apply either way."), 48);

            AddFull(MakeSection("CHECK INTERVAL"), 28);
            _numInterval = new NumericUpDown
            {
                Minimum = 0.5m,
                Maximum = 60m,
                Increment = 0.5m,
                DecimalPlaces = 1,
                Value = 2.0m,
                Width = 140,
                Height = 28,
                BackColor = UiTheme.BgInput,
                ForeColor = UiTheme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
                ThousandsSeparator = false,
                Font = new Font("Segoe UI", 10f),
            };
            AddFull(WrapField(_numInterval), 40);
            AddFull(MakeHint(
                "Seconds between checks (0.5–60). Default 2.0. Image + Balloon can take " +
                "longer than this — the next tick waits until the last one finishes."), 40);
            _numInterval.ValueChanged += (_, _) => OnFieldChanged();

            AddFull(MakeSection("WHEN TEXT DISAPPEARS"), 28);
            _chkClearOnNoText = new CheckBox
            {
                Text = "Forget last spoken words when no text is found",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = true,
            };
            _chkClearOnNoText.CheckedChanged += (_, _) => OnFieldChanged();
            AddFull(_chkClearOnNoText, 32);
            AddFull(MakeHint(
                "On (default): a cutscene or empty box clears the last line, so the same " +
                "dialogue can be read again when it comes back."), 40);

            AddFull(MakeSection("HOW DIFFERENT BEFORE SPEAKING"), 28);
            _numMinDiff = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100,
                Increment = 1,
                DecimalPlaces = 0,
                Value = 90,
                Width = 140,
                Height = 28,
                BackColor = UiTheme.BgInput,
                ForeColor = UiTheme.Fg,
                BorderStyle = BorderStyle.FixedSingle,
                ThousandsSeparator = false,
                Font = new Font("Segoe UI", 10f),
            };
            AddFull(WrapField(_numMinDiff), 40);
            AddFull(MakeHint(
                "Percent (0–100). Default 90. New words must differ by at least this much " +
                "from the last spoken line. 0 = any change. 100 = completely different."), 44);
            _numMinDiff.ValueChanged += (_, _) => OnFieldChanged();

            AddFull(MakeSection("HOW IT WORKS"), 28);
            AddFull(MakeHint(
                "1. Each check: OCR answers yes/no — is there text? It does not read the line. No → silent (no LLM).\n" +
                "2. Yes → Local-LLM or OCR (Text source) using the pipeline you picked. Those words are what Watch compares and speaks.\n" +
                "3. First successful read after enable/slot change is a silent baseline. After a no-text gap (if forget-last is on), returning dialogue is spoken.\n" +
                "4. Otherwise it speaks only if the new line differs enough (see percent above) and nothing else is being read.\n" +
                "5. Opening the overlay stops Watch immediately and cancels a check in flight.\n" +
                "6. A region hotkey, Follow speak, or Stop speech also cancels a Watch check so your action wins."), 156);

            AddFull(MakeSection("LIVE"), 28);
            _lblLive = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.FgMuted,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.TopLeft,
                AutoEllipsis = true,
            };
            AddFull(_lblLive, 36);

            scroll.Controls.Add(body);
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
                LoadFromSettings();
                _statusTimer.Start();
            };
            VisibleChanged += (_, _) =>
            {
                if (Visible)
                    _statusTimer.Start();
                else
                    _statusTimer.Stop();
            };
            FormClosing += (_, _) =>
            {
                if (_dirty || _diskSavePending)
                    Persist(writeDiskNow: true);
                try { _diskSaveTimer.Stop(); } catch { /* ignore */ }
                try { _diskSaveTimer.Dispose(); } catch { /* ignore */ }
                try { _statusTimer.Stop(); } catch { /* ignore */ }
                try { _statusTimer.Dispose(); } catch { /* ignore */ }
            };
        }

        public void ReloadFromSettings() => LoadFromSettings();

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
                s.NormalizeWatchSettings();
                RebuildRegionCombo(s);
                _chkEnabled.Checked = s.WatchEnabled;
                SelectSlotInCombo(s.WatchRegionSlot);
                SelectPipelineInCombo(s.WatchPipeline);
                SelectTextSourceInCombo(s.WatchTextSource);
                decimal sec = s.WatchIntervalMs / 1000m;
                if (sec < _numInterval.Minimum) sec = _numInterval.Minimum;
                if (sec > _numInterval.Maximum) sec = _numInterval.Maximum;
                _numInterval.Value = sec;
                _chkClearOnNoText.Checked = s.WatchClearLastOnNoText;
                decimal diff = s.WatchMinDifferencePercent;
                if (diff < _numMinDiff.Minimum) diff = _numMinDiff.Minimum;
                if (diff > _numMinDiff.Maximum) diff = _numMinDiff.Maximum;
                _numMinDiff.Value = diff;
                _dirty = false;
                _lblStatus.Text = StatusLine(s);
                RefreshLiveStatus();
            }
            finally
            {
                _loading = false;
            }
        }

        private void RebuildRegionCombo(AppSettings s)
        {
            int keep = s.WatchRegionSlot;
            _cmbRegion.Items.Clear();
            for (int i = 0; i < 8; i++)
            {
                var slot = s.RegionSlots[i];
                string hk = s.HotkeyRegions[i].IsEmpty
                    ? "unbound"
                    : s.HotkeyRegions[i].ToIniString();
                string geom;
                if (slot.IsEmpty)
                    geom = "empty";
                else if (slot.IsLassoMode)
                    geom = $"Lasso · {slot.GetLassoPoints().Count} pts";
                else
                    geom = $"{(slot.IsOvalMode ? "Oval" : "Rect")} {slot.W}×{slot.H}";
                _cmbRegion.Items.Add(new SlotItem(i, $"Region {i + 1} · {geom} · {hk}", !slot.IsEmpty));
            }
            SelectSlotInCombo(keep);
        }

        private void SelectSlotInCombo(int slotIndex)
        {
            int idx = Math.Clamp(slotIndex, 0, 7);
            if (_cmbRegion.Items.Count > idx)
                _cmbRegion.SelectedIndex = idx;
        }

        private int SelectedSlotIndex()
        {
            if (_cmbRegion.SelectedItem is SlotItem item)
                return item.Index;
            int i = _cmbRegion.SelectedIndex;
            return i < 0 ? 0 : Math.Clamp(i, 0, 7);
        }

        private void SelectPipelineInCombo(WatchPipeline pipeline)
        {
            var want = RegionWatch.NormalizePipeline(pipeline);
            for (int i = 0; i < _cmbPipeline.Items.Count; i++)
            {
                if (_cmbPipeline.Items[i] is PipeItem p && p.Pipeline == want)
                {
                    _cmbPipeline.SelectedIndex = i;
                    return;
                }
            }
            if (_cmbPipeline.Items.Count > 0)
                _cmbPipeline.SelectedIndex = 0;
        }

        private WatchPipeline SelectedPipeline()
        {
            if (_cmbPipeline.SelectedItem is PipeItem item)
                return item.Pipeline;
            return AppSettings.DefaultWatchPipeline;
        }

        private void SelectTextSourceInCombo(WatchTextSource source)
        {
            var want = RegionWatch.NormalizeTextSource(source);
            for (int i = 0; i < _cmbTextSource.Items.Count; i++)
            {
                if (_cmbTextSource.Items[i] is TextSourceItem t && t.Source == want)
                {
                    _cmbTextSource.SelectedIndex = i;
                    return;
                }
            }
            if (_cmbTextSource.Items.Count > 0)
                _cmbTextSource.SelectedIndex = 0;
        }

        private WatchTextSource SelectedTextSource()
        {
            if (_cmbTextSource.SelectedItem is TextSourceItem item)
                return item.Source;
            return AppSettings.DefaultWatchTextSource;
        }

        private void OnFieldChanged()
        {
            if (_loading) return;
            _dirty = true;
            Persist(writeDiskNow: false);
            _lblStatus.Text = "Saved · " + StatusLine(AppSettings.Current);
            try { _onChanged?.Invoke(); } catch { /* ignore host */ }
        }

        private void Persist(bool writeDiskNow = false)
        {
            var s = AppSettings.Current;
            s.WatchEnabled = _chkEnabled.Checked;
            s.WatchRegionSlot = SelectedSlotIndex();
            s.WatchPipeline = SelectedPipeline();
            s.WatchTextSource = SelectedTextSource();
            s.WatchIntervalMs = (int)Math.Round(_numInterval.Value * 1000m);
            s.WatchClearLastOnNoText = _chkClearOnNoText.Checked;
            s.WatchMinDifferencePercent = (int)_numMinDiff.Value;
            s.NormalizeWatchSettings();
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
            try
            {
                AppSettings.Current.Save();
                AppSettings.Current.SyncActiveProfileFile();
            }
            catch { /* keep in-memory */ }
        }

        private void Reset_Click()
        {
            _loading = true;
            try
            {
                _chkEnabled.Checked = false;
                SelectSlotInCombo(0);
                SelectPipelineInCombo(AppSettings.DefaultWatchPipeline);
                SelectTextSourceInCombo(AppSettings.DefaultWatchTextSource);
                _numInterval.Value = 2.0m;
                _chkClearOnNoText.Checked = true;
                _numMinDiff.Value = AppSettings.DefaultWatchMinDifferencePercent;
            }
            finally
            {
                _loading = false;
            }

            _dirty = true;
            Persist(writeDiskNow: true);
            _lblStatus.Text = "Reset to defaults · " + StatusLine(AppSettings.Current);
            try { _onChanged?.Invoke(); } catch { /* ignore */ }
        }

        private void RefreshLiveStatus()
        {
            if (_lblLive.IsDisposed)
                return;
            string live = RegionWatch.LastStatus;
            var s = AppSettings.Current;
            s.NormalizeWatchSettings();
            bool slotSet = !s.RegionSlots[Math.Clamp(s.WatchRegionSlot, 0, 7)].IsEmpty;
            string extra = "";
            if (s.WatchEnabled && !slotSet)
                extra = "  ·  selected slot is empty — draw it on the overlay";
            _lblLive.Text = live + extra;
        }

        private static string StatusLine(AppSettings s)
        {
            string on = s.WatchEnabled ? "On" : "Off";
            double sec = s.WatchIntervalMs / 1000.0;
            return $"{on}  ·  region {s.WatchRegionSlot + 1}  ·  {RegionWatch.PipelineDisplayName(s.WatchPipeline)}  ·  {RegionWatch.TextSourceDisplayName(s.WatchTextSource)}  ·  every {sec.ToString("0.0", CultureInfo.CurrentCulture)} s  ·  ≥{s.WatchMinDifferencePercent}% different";
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

        private sealed class SlotItem
        {
            public int Index { get; }
            public string Label { get; }
            public bool IsSet { get; }

            public SlotItem(int index, string label, bool isSet)
            {
                Index = index;
                Label = label;
                IsSet = isSet;
            }

            public override string ToString() => Label;
        }

        private sealed class PipeItem
        {
            public WatchPipeline Pipeline { get; }
            public string Label { get; }

            public PipeItem(WatchPipeline pipeline, string label)
            {
                Pipeline = pipeline;
                Label = label;
            }

            public override string ToString() => Label;
        }

        private sealed class TextSourceItem
        {
            public WatchTextSource Source { get; }
            public string Label { get; }

            public TextSourceItem(WatchTextSource source, string label)
            {
                Source = source;
                Label = label;
            }

            public override string ToString() => Label;
        }
    }
}
