using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Help tab (Settings): structured Getting Started, features, and hotkeys.
    /// Also hosts factory <b>Restore all defaults</b> (confirm first).
    /// </summary>
    public sealed class frm_Help : Form
    {
        private readonly Action? _onRequestClose;
        private readonly Action? _onAfterRestoreAllDefaults;
        private readonly bool _embedded;
        private readonly Button? _btnClose;
        private readonly Button _btnRestoreDefaults;
        private readonly Label _lblVersion;
        private readonly RichTextBox _rtb;

        public frm_Help(
            Action<frm_Settings.SettingsTab>? goToTab = null,
            bool embedded = false,
            Action? onRequestClose = null,
            Action? onAfterRestoreAllDefaults = null)
        {
            // goToTab kept for call-site compat; the tab strip is navigation.
            _ = goToTab;
            _embedded = embedded;
            _onRequestClose = onRequestClose;
            _onAfterRestoreAllDefaults = onAfterRestoreAllDefaults;

            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);

            Text = "SpeakRect — Help";
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
                MinimumSize = new Size(520, 560);
                ClientSize = new Size(560, 640);
                TopMost = true;
                ShowInTaskbar = false;
                MinimizeBox = false;
                MaximizeBox = false;
            }
            KeyPreview = true;
            BackColor = UiTheme.Bg;
            ForeColor = UiTheme.Fg;
            Font = new Font("Segoe UI", 9.5f);

            // ---- Bottom: README + factory restore (tabs already navigate Settings) ----
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = UiTheme.BgBar,
            };
            var btnReadme = MakePrimaryButton("Open full README…");
            btnReadme.Click += (_, _) => OpenReadme();
            bottom.Controls.Add(btnReadme);

            _btnRestoreDefaults = MakeButton("Restore all defaults…");
            _btnRestoreDefaults.Click += (_, _) => RestoreAllDefaults_Click();
            bottom.Controls.Add(_btnRestoreDefaults);

            if (!_embedded)
            {
                _btnClose = MakePrimaryButton("Close");
                UiTheme.StyleButton(_btnClose);
                _btnClose.Click += (_, _) =>
                {
                    if (_onRequestClose != null)
                        _onRequestClose();
                    else
                        Close();
                };
                bottom.Controls.Add(_btnClose);
            }
            void LayoutBottom()
            {
                int y = Math.Max(8, (bottom.ClientSize.Height - btnReadme.Height) / 2);
                btnReadme.Location = new Point(14, y);
                _btnRestoreDefaults.Location = new Point(
                    btnReadme.Right + 10, y);
                if (_btnClose != null)
                {
                    _btnClose.Location = new Point(
                        Math.Max(14, bottom.ClientSize.Width - _btnClose.Width - 14), y);
                }
            }
            bottom.Resize += (_, _) => LayoutBottom();
            LayoutBottom();

            _lblVersion = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                ForeColor = UiTheme.FgHeader,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Padding = new Padding(16, 0, 12, 0),
                BackColor = UiTheme.BgStatus,
                Text = AppInfo.VersionLine,
            };

            // Intro strip above the body
            var intro = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                BackColor = UiTheme.BgRaised,
                Padding = new Padding(16, 10, 16, 10),
            };
            var introTitle = new Label
            {
                Text = "Draw regions on your screen. Hear the text read aloud.",
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = UiTheme.Fg,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            var introSub = new Label
            {
                Text = "Reads text on your screen · Windows speech (default)",
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = UiTheme.FgMuted,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            intro.Controls.Add(introSub);
            intro.Controls.Add(introTitle);

            // Structured, scrollable rich text (one control = no layout overflow)
            var bodyHost = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 10, 8, 8),
                BackColor = UiTheme.Bg,
            };
            _rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = UiTheme.Bg,
                ForeColor = UiTheme.Fg,
                Font = new Font("Segoe UI", 9.5f),
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                TabStop = true,
                HideSelection = true,
            };
            bodyHost.Controls.Add(_rtb);

            // Dock order: Fill first, then Top, then bottoms
            Controls.Add(bodyHost);
            Controls.Add(intro);
            Controls.Add(_lblVersion);
            Controls.Add(bottom);

            Load += (_, _) =>
            {
                FillHelpRtf();
                LayoutBottom();
                // Drop caret so the pane looks like a document, not an editor
                _rtb.Select(0, 0);
                _rtb.ScrollToCaret();
                ActiveControl = btnReadme;
            };

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
        }

        public void ReloadFromSettings()
        {
            _lblVersion.Text = AppInfo.VersionLine;
        }

        private void FillHelpRtf()
        {
            // Build RTF with clear hierarchy: section headers, steps, key/action rows.
            var sb = new StringBuilder();
            sb.Append(@"{\rtf1\ansi\deff0");
            sb.Append(@"{\fonttbl{\f0\fswiss Segoe UI;}{\f1\fmodern Consolas;}}");
            sb.Append(@"{\colortbl;");
            sb.Append(@"\red236\green236\blue240;");   // 1 body (ink fg)
            sb.Append(@"\red255\green152\blue48;");    // 2 section (orange)
            sb.Append(@"\red255\green160\blue48;");    // 3 step num
            sb.Append(@"\red130\green190\blue110;");   // 4 key (ok green)
            sb.Append(@"\red255\green168\blue72;");    // 5 feature title
            sb.Append(@"\red150\green150\blue158;");   // 6 muted
            sb.Append(@"}");
            sb.Append(@"\fs19\cf1\f0 ");

            // Quick start
            Section(sb, "QUICK START");
            Step(sb, "1", "Show the overlay ", "Shift+Tab", " or tray \u2192 Show Overlay.");
            Step(sb, "2", "Draw a box around the text (starts on region 1).");
            Step(sb, "3", "Press ", "Enter", " to speak that region.");
            Step(sb, "4", "Still on the overlay: ", "Shift+F2", ", draw region 2, Enter to test.");
            Step(sb, "5", "Escape hides the overlay. Later ", "Shift+F1 / F2 / \u2026", " speak those spots instantly.");
            sb.Append(@"\par ");

            // Features
            Section(sb, "WHAT YOU CAN DO");
            Feature(sb, "8 regions", "Fixed slots (default Shift+F1\u2013F8) for dialogue, choices, menus\u2026");
            Feature(sb, "Follow", "Slot 9 \u2014 floating box at the mouse. Speak with Shift+F9.");
            Feature(sb, "Shapes", "Rectangle, oval, or freehand lasso (R / O / L on the overlay).");
            Feature(sb, "Modes", "Default for games/UI \u00b7 Comic Book for panels and balloons.");
            Feature(sb, "Key Map", "Remap keyboard and gamepad; add custom actions.");
            Feature(sb, "Profiles", "Save regions, hotkeys, modes, voice, speech rules, and Follow per game.");
            Feature(sb, "Voice", "Windows TTS by default. Optional SAPI 5 for adapters (see README).");
            Feature(sb, "Speech", "Settings \u2192 Speech: name rules, text cleanup, and the reading prompt. Saved with your profile.");
            Feature(sb, "Balloons", "Settings \u2192 Balloons: find and edit speech-balloon boxes for Comic Book. Saved with your profile.");
            Feature(sb, "Image", "Settings \u2192 Image: clean up the capture before reading, with live preview. Saved with your profile.");
            Feature(sb, "Regions map", "Settings \u2192 Regions shows where every slot sits on screen.");
            Feature(sb, "Analytics", "Settings \u2192 Analytics shows the last spoken text, pictures from that run, and timings. Export saves a zip.");
            Feature(sb, "Restore all defaults", "This Help tab \u2014 reset mode, image, voice, speech, hotkeys, regions, and follow (asks first). Keeps the profile name.");
            sb.Append(@"\par ");

            // Hotkeys
            Section(sb, "DEFAULT HOTKEYS");
            Hotkey(sb, "Shift+Tab", "Show / hide overlay");
            Hotkey(sb, "Ctrl+D", "Default mode (Ctrl, not Shift — typing-safe)");
            Hotkey(sb, "Ctrl+B", "Comic Book mode");
            Hotkey(sb, "Ctrl+Shift+S", "Stop speech (abort TTS in progress)");
            Hotkey(sb, "Shift+F1\u2013F8", "Speak region 1\u20138 \u00b7 switch slot if overlay is open");
            Hotkey(sb, "Shift+F9", "Speak Follow at mouse");
            Hotkey(sb, "Enter", "Speak current region (overlay)");
            Hotkey(sb, "Escape", "Hide overlay");
            Hotkey(sb, "R / O / L", "Rectangle / Oval / Lasso");
            Hotkey(sb, "Delete", "Clear active region slot");
            Hotkey(sb, "\u2190 / \u2192", "Overlay more transparent / opaque");
            sb.Append(@"\par\cf6\i Remap any of these in the Key Map tab.\i0\cf1\par\par ");

            // Tips
            Section(sb, "TIPS");
            Tip(sb, "Prefer borderless windowed or windowed mode for games \u2014 exclusive fullscreen often cannot be captured.");
            Tip(sb, "Use one profile per game so regions and hotkeys stay out of the way of controls.");
            Tip(sb, "Open the Regions tab to see a map of every slot you have set.");
            Tip(sb, "Ctrl+click FOLLOW on the overlay to open Follow size and offset settings.");
            Tip(sb, "Sidebar REGIONS buttons 1–8 switch slots for drawing (same as region hotkeys).");
            Tip(sb, "Leave Voice engine on Windows unless you installed a SAPI adapter; full steps are in README.md.");
            Tip(sb, "Speech \u2192 Names: e.g. X-Men \u2192 Ex-Men (any case). Click \u25b6 / Preview / Space to sample the Say as voice. Packs\u2026 lists NamePacks\\*.txt — pick one to import (rules start ON, A\u2013Z). Nothing auto-loads at startup.");
            Tip(sb, "Balloons: open a page or last capture — settings update the green boxes live. Speak (F6) reads them.");
            Tip(sb, "Image: cleanup preview matches what is used when speaking. Soften-for-find boxes is on Balloons only.");
            sb.Append(@"\par ");

            sb.Append(@"\cf6 Full documentation ships as README.md next to SpeakRect.exe (includes optional SAPI 5 setup).\cf1\par ");
            sb.Append(@"}");

            _rtb.Rtf = sb.ToString();
        }

        private static void Section(StringBuilder sb, string title)
        {
            sb.Append(@"\par\sb80\sa60\b\fs20\cf2 ");
            sb.Append(RtfEscape(title));
            sb.Append(@"\b0\fs19\cf1\par\sa40 ");
        }

        private static void Step(StringBuilder sb, string n, string a, string? key = null, string? b = null)
        {
            sb.Append(@"\li200\fi-200\sb60\sa40 ");
            sb.Append(@"\b\cf3 ");
            sb.Append(RtfEscape(n));
            sb.Append(@".\b0\cf1\~");
            // RTF collapses trailing spaces — use non-breaking spaces around keys.
            sb.Append(RtfEscape(a.TrimEnd()));
            if (key != null)
            {
                sb.Append(@"\~\f1\b\cf4 ");
                sb.Append(RtfEscape(key));
                sb.Append(@"\f0\b0\cf1\~");
            }
            if (b != null)
                sb.Append(RtfEscape(b.TrimStart()));
            sb.Append(@"\par\li0\fi0 ");
        }

        private static void Feature(StringBuilder sb, string title, string body)
        {
            sb.Append(@"\sb50\sa40 ");
            sb.Append(@"\b\cf5 ");
            sb.Append(RtfEscape(title));
            sb.Append(@"\b0\cf1\~\emdash\~");
            sb.Append(RtfEscape(body));
            sb.Append(@"\par ");
        }

        private static void Hotkey(StringBuilder sb, string key, string action)
        {
            sb.Append(@"\sb30\sa20\tx1400\f1\b\cf4 ");
            sb.Append(RtfEscape(key));
            sb.Append(@"\f0\b0\cf1\tab ");
            sb.Append(RtfEscape(action));
            sb.Append(@"\par ");
        }

        private static void Tip(StringBuilder sb, string text)
        {
            sb.Append(@"\li120\fi-120\sb40\sa40 \cf1 \u8226?  ");
            sb.Append(RtfEscape(text));
            sb.Append(@"\par\li0\fi0 ");
        }

        private static string RtfEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '{': sb.Append(@"\{"); break;
                    case '}': sb.Append(@"\}"); break;
                    case '\n': sb.Append(@"\par "); break;
                    default:
                        if (ch > 127)
                            sb.Append(@"\u").Append((int)ch).Append('?');
                        else
                            sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private void RestoreAllDefaults_Click()
        {
            var dr = UiMessageBox.Show(this,
                "Restore ALL SpeakRect settings to built-in defaults?\n\n" +
                "This resets mode, Image prep, Balloons, Voice, Speech (names + text rules + prompts), " +
                "Key Map (keyboard defaults; gamepad + custom actions cleared), Follow, and all region slots.\n\n" +
                "Your active profile name is kept, and the reset is written to disk.\n\n" +
                "This cannot be undone from here.",
                "Restore all defaults",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes)
                return;

            try
            {
                AppSettings.Current.RestoreAllBuiltInDefaults();
            }
            catch (Exception ex)
            {
                UiMessageBox.Show(this,
                    "Could not restore defaults:\n" + ex.Message,
                    "Restore all defaults",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            try { _onAfterRestoreAllDefaults?.Invoke(); }
            catch { /* host apply — settings already restored */ }

            UiMessageBox.Show(this,
                "All settings restored to built-in defaults.",
                "Restore all defaults",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void OpenReadme()
        {
            string? path = FindReadme();
            if (path == null)
            {
                UiMessageBox.Show(this,
                    "README.md was not found next to the app.\n\n" +
                    "If you installed from a release zip, re-extract so README.md sits beside SpeakRect.exe.",
                    "Open README",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                UiMessageBox.Show(this, "Could not open README:\n" + ex.Message,
                    "Open README", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string? FindReadme()
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "README.md"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "README.md")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "README.md")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "README.md")),
            };
            foreach (string c in candidates)
            {
                try
                {
                    if (File.Exists(c))
                        return c;
                }
                catch { /* ignore */ }
            }
            return null;
        }

        private static Button MakePrimaryButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(120, 32),
                Padding = new Padding(14, 4, 14, 4),
                Font = new Font("Segoe UI", 9f),
            };
            UiTheme.StylePrimaryButton(btn);
            return btn;
        }

        private static Button MakeButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(120, 32),
                Padding = new Padding(14, 4, 14, 4),
                Font = new Font("Segoe UI", 9f),
            };
            UiTheme.StyleButton(btn);
            return btn;
        }
    }
}
