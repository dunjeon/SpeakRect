using System;
using System.Drawing;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Overlay tab: freeze the program underneath and/or draw on a screenshot.
    /// Both default off.
    /// </summary>
    public sealed class frm_OverlaySettings : Form
    {
        private readonly CheckBox _chkPause;
        private readonly CheckBox _chkScreenshot;
        private readonly Label _lblStatus;
        private readonly Button _btnReset;
        private readonly Button? _btnClose;
        private readonly Action? _onRequestClose;
        private readonly Action? _onChanged;
        private readonly bool _embedded;

        private bool _loading;
        private bool _dirty;
        private readonly System.Windows.Forms.Timer _diskSaveTimer;
        private bool _diskSavePending;

        public frm_OverlaySettings(
            Action? onChanged = null,
            bool embedded = false,
            Action? onRequestClose = null)
        {
            _onChanged = onChanged;
            _embedded = embedded;
            _onRequestClose = onRequestClose;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Overlay";
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
                MinimumSize = new Size(520, 420);
                ClientSize = new Size(560, 460);
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
                "When a game hides its own UI as soon as it loses focus, these options " +
                "keep the picture you wanted to draw on. Both default off."), 48);

            AddFull(MakeSection("PAUSE THE PROGRAM UNDERNEATH"), 28);
            _chkPause = new CheckBox
            {
                Text = "Freeze the program under the overlay while it is shown",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = false,
            };
            _chkPause.CheckedChanged += (_, _) => OnFieldChanged();
            AddFull(_chkPause, 32);
            AddFull(MakeHint(
                "Suspends that program's threads so it cannot open a pause menu or change " +
                "the screen. Resume is guaranteed when the overlay hides or SpeakRect exits. " +
                "Some games (especially those with anti-cheat) may not like this — if anything " +
                "odd happens, turn it off and try the screenshot option instead."), 72);

            AddFull(MakeSection("DRAW ON A SCREENSHOT"), 28);
            _chkScreenshot = new CheckBox
            {
                Text = "Photograph the desktop first, then draw on that still image",
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Fg,
                BackColor = UiTheme.Bg,
                AutoSize = false,
                Checked = false,
            };
            _chkScreenshot.CheckedChanged += (_, _) => OnFieldChanged();
            AddFull(_chkScreenshot, 32);
            AddFull(MakeHint(
                "Takes a full-desktop snapshot before the overlay appears. You draw on that " +
                "picture; speaking a region reads those pixels, not the live window. Safer for " +
                "games that must keep running. Takes effect the next time the overlay is shown."), 72);

            AddFull(MakeSection("HOW IT WORKS"), 28);
            AddFull(MakeHint(
                "1. Hide the overlay (Escape) so the game has focus.\n" +
                "2. Show the overlay (Shift+Tab). SpeakRect notes the program in front, " +
                "optionally freezes it, and optionally photographs the desktop.\n" +
                "3. Draw and Enter as usual. Hide the overlay to resume the program and drop the still image.\n" +
                "4. You can use one option, both, or neither. Watch still runs on the live window " +
                "while the overlay is hidden."), 108);

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
            };
            FormClosing += (_, _) =>
            {
                if (_dirty || _diskSavePending)
                    Persist(writeDiskNow: true);
                try { _diskSaveTimer.Stop(); } catch { /* ignore */ }
                try { _diskSaveTimer.Dispose(); } catch { /* ignore */ }
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
                _chkPause.Checked = s.OverlayPauseUnderlay;
                _chkScreenshot.Checked = s.OverlayDrawOnScreenshot;
                _lblStatus.Text = StatusLine(s);
            }
            finally
            {
                _loading = false;
            }
        }

        private void OnFieldChanged()
        {
            if (_loading) return;
            Persist(writeDiskNow: false);
            _lblStatus.Text = "Saved · " + StatusLine(AppSettings.Current);
            try { _onChanged?.Invoke(); } catch { /* ignore host */ }
        }

        private void Persist(bool writeDiskNow = false)
        {
            var s = AppSettings.Current;
            s.OverlayPauseUnderlay = _chkPause.Checked;
            s.OverlayDrawOnScreenshot = _chkScreenshot.Checked;
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
                _chkPause.Checked = false;
                _chkScreenshot.Checked = false;
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

        private static string StatusLine(AppSettings s)
        {
            string pause = s.OverlayPauseUnderlay ? "Pause on" : "Pause off";
            string snap = s.OverlayDrawOnScreenshot ? "Screenshot on" : "Screenshot off";
            return $"{pause}  ·  {snap}  (default both off)";
        }

        private void LayoutBottomButtons(Panel bottom)
        {
            int y = Math.Max(8, (bottom.ClientSize.Height - _btnReset.Height) / 2);
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
