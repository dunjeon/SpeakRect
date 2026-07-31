using System;
using System.Drawing;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Themed stand-in for <see cref="MessageBox"/> — dark ink surfaces and orange
    /// accents so confirmations (delete profile, reset rules, etc.) match Settings.
    /// Drop-in compatible overloads for the call patterns we use in-app.
    /// </summary>
    public static class UiMessageBox
    {
        public static DialogResult Show(string text) =>
            Show(null, text, "SpeakRect", MessageBoxButtons.OK, MessageBoxIcon.None);

        public static DialogResult Show(string text, string caption) =>
            Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);

        public static DialogResult Show(
            string text, string caption, MessageBoxButtons buttons) =>
            Show(null, text, caption, buttons, MessageBoxIcon.None);

        public static DialogResult Show(
            string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) =>
            Show(null, text, caption, buttons, icon);

        public static DialogResult Show(IWin32Window? owner, string text) =>
            Show(owner, text, "SpeakRect", MessageBoxButtons.OK, MessageBoxIcon.None);

        public static DialogResult Show(IWin32Window? owner, string text, string caption) =>
            Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);

        public static DialogResult Show(
            IWin32Window? owner, string text, string caption, MessageBoxButtons buttons) =>
            Show(owner, text, caption, buttons, MessageBoxIcon.None);

        public static DialogResult Show(
            IWin32Window? owner,
            string text,
            string caption,
            MessageBoxButtons buttons,
            MessageBoxIcon icon)
        {
            using var dlg = new ThemedMessageForm(
                text ?? "",
                string.IsNullOrWhiteSpace(caption) ? "SpeakRect" : caption,
                buttons,
                icon);

            // Prefer CenterParent when we have a live owner handle; otherwise screen.
            if (owner is Control c && c.IsHandleCreated && !c.IsDisposed)
                dlg.StartPosition = FormStartPosition.CenterParent;
            else if (owner != null)
                dlg.StartPosition = FormStartPosition.CenterParent;
            else
                dlg.StartPosition = FormStartPosition.CenterScreen;

            return owner != null
                ? dlg.ShowDialog(owner)
                : dlg.ShowDialog();
        }

        private sealed class ThemedMessageForm : Form
        {
            private readonly Label _lblIcon;
            private readonly Label _lblMessage;
            private readonly FlowLayoutPanel _btnRow;

            public ThemedMessageForm(
                string text,
                string caption,
                MessageBoxButtons buttons,
                MessageBoxIcon icon)
            {
                Text = caption;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                TopMost = true;
                KeyPreview = true;
                AutoScaleMode = AutoScaleMode.Font;
                Font = new Font("Segoe UI", 9.5f);
                MinimumSize = new Size(360, 140);
                // Sized after content measure
                ClientSize = new Size(440, 160);

                UiTheme.ApplyForm(this);

                var body = new Panel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(18, 16, 18, 8),
                    BackColor = UiTheme.Bg,
                };

                _lblIcon = new Label
                {
                    AutoSize = false,
                    Size = new Size(36, 36),
                    Location = new Point(0, 2),
                    Font = new Font("Segoe UI Symbol", 18f),
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = IconColor(icon),
                    Text = IconGlyph(icon),
                    Visible = icon != MessageBoxIcon.None,
                };

                _lblMessage = new Label
                {
                    AutoSize = false,
                    ForeColor = UiTheme.Fg,
                    Font = new Font("Segoe UI", 9.5f),
                    Text = text.Replace("\r\n", "\n").Replace('\r', '\n'),
                    // UseFlags for multi-line
                };
                // Owner-draw-ish: plain Label wraps when AutoSize=false + height set
                _lblMessage.UseCompatibleTextRendering = false;

                body.Controls.Add(_lblMessage);
                body.Controls.Add(_lblIcon);

                var footer = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 56,
                    BackColor = UiTheme.BgBar,
                    Padding = new Padding(12, 10, 12, 10),
                };

                _btnRow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    BackColor = UiTheme.BgBar,
                    Padding = new Padding(0),
                };
                footer.Controls.Add(_btnRow);

                AddButtons(buttons);

                Controls.Add(body);
                Controls.Add(footer);

                // Layout after handle so fonts measure correctly
                Load += (_, _) => LayoutContent(body);
                body.Resize += (_, _) => LayoutContent(body);

                KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Escape && CancelButton != null)
                    {
                        e.Handled = true;
                        DialogResult = CancelButton is Button cb
                            ? cb.DialogResult
                            : DialogResult.Cancel;
                        Close();
                    }
                };
            }

            private void LayoutContent(Panel body)
            {
                int padL = body.Padding.Left;
                int padT = body.Padding.Top;
                int padR = body.Padding.Right;

                int textLeft = padL;
                if (_lblIcon.Visible)
                {
                    _lblIcon.Location = new Point(padL, padT + 2);
                    textLeft = padL + _lblIcon.Width + 12;
                }

                // Comfortable reading width; remeasure after apply
                int preferTextW = 360;
                int textW = preferTextW;
                Size measured = TextRenderer.MeasureText(
                    _lblMessage.Text,
                    _lblMessage.Font,
                    new Size(textW, 0),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

                int textH = Math.Max(36, measured.Height + 4);
                _lblMessage.Location = new Point(textLeft, padT);
                _lblMessage.Size = new Size(textW, textH);

                int contentH = Math.Max(
                    _lblIcon.Visible ? _lblIcon.Bottom : 0,
                    _lblMessage.Bottom) + body.Padding.Bottom;

                const int footerH = 56;
                int wantClientW = Math.Clamp(textLeft + textW + padR, 400, 560);
                int wantClientH = Math.Clamp(contentH + footerH, 140, 520);
                ClientSize = new Size(wantClientW, wantClientH);

                // Re-measure against final client width
                textW = Math.Max(160, body.ClientSize.Width - textLeft - padR);
                measured = TextRenderer.MeasureText(
                    _lblMessage.Text,
                    _lblMessage.Font,
                    new Size(textW, 0),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                textH = Math.Max(36, measured.Height + 4);
                _lblMessage.Size = new Size(textW, textH);

                contentH = Math.Max(
                    _lblIcon.Visible ? _lblIcon.Bottom : 0,
                    _lblMessage.Bottom) + body.Padding.Bottom;
                wantClientH = Math.Clamp(contentH + footerH, 140, 520);
                if (ClientSize.Height != wantClientH)
                    ClientSize = new Size(ClientSize.Width, wantClientH);
            }

            private void AddButtons(MessageBoxButtons buttons)
            {
                // RightToLeft flow: add in reverse visual order (rightmost first).
                switch (buttons)
                {
                    case MessageBoxButtons.OK:
                        AcceptButton = AddButton("OK", DialogResult.OK, primary: true);
                        CancelButton = AcceptButton;
                        break;

                    case MessageBoxButtons.OKCancel:
                        CancelButton = AddButton("Cancel", DialogResult.Cancel, primary: false);
                        AcceptButton = AddButton("OK", DialogResult.OK, primary: true);
                        break;

                    case MessageBoxButtons.YesNo:
                        // RightToLeft: first added = rightmost. Standard layout: Yes | No
                        AddButton("No", DialogResult.No, primary: false);
                        AcceptButton = AddButton("Yes", DialogResult.Yes, primary: true);
                        CancelButton = FindButton(DialogResult.No); // Esc → No
                        break;

                    case MessageBoxButtons.YesNoCancel:
                        // Rightmost Cancel, then No, then Yes (left)
                        AddButton("Cancel", DialogResult.Cancel, primary: false);
                        AddButton("No", DialogResult.No, primary: false);
                        AcceptButton = AddButton("Yes", DialogResult.Yes, primary: true);
                        CancelButton = FindButton(DialogResult.Cancel);
                        break;

                    case MessageBoxButtons.RetryCancel:
                        CancelButton = AddButton("Cancel", DialogResult.Cancel, primary: false);
                        AcceptButton = AddButton("Retry", DialogResult.Retry, primary: true);
                        break;

                    case MessageBoxButtons.AbortRetryIgnore:
                        AddButton("Ignore", DialogResult.Ignore, primary: false);
                        AddButton("Retry", DialogResult.Retry, primary: false);
                        AcceptButton = AddButton("Abort", DialogResult.Abort, primary: true);
                        CancelButton = FindButton(DialogResult.Ignore);
                        break;

                    default:
                        AcceptButton = AddButton("OK", DialogResult.OK, primary: true);
                        CancelButton = AcceptButton;
                        break;
                }
            }

            private Button? FindButton(DialogResult dr)
            {
                foreach (Control c in _btnRow.Controls)
                {
                    if (c is Button b && b.DialogResult == dr)
                        return b;
                }
                return null;
            }

            private Button AddButton(string text, DialogResult result, bool primary)
            {
                var btn = new Button
                {
                    Text = text,
                    DialogResult = result,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    MinimumSize = new Size(88, 32),
                    Padding = new Padding(12, 4, 12, 4),
                    Margin = new Padding(8, 0, 0, 0),
                    Font = new Font("Segoe UI", 9f),
                };
                if (primary)
                    UiTheme.StylePrimaryButton(btn);
                else
                    UiTheme.StyleButton(btn);

                // Click closes with DialogResult (Form uses button DialogResult when modal)
                btn.Click += (_, _) =>
                {
                    DialogResult = result;
                    Close();
                };

                _btnRow.Controls.Add(btn);
                return btn;
            }

            private static string IconGlyph(MessageBoxIcon icon) => icon switch
            {
                MessageBoxIcon.Error => "\u26A0",       // ⚠ (also used for hard errors)
                MessageBoxIcon.Warning => "\u26A0",
                MessageBoxIcon.Question => "\u2753",    // ❓
                MessageBoxIcon.Information => "\u2139", // ℹ
                _ => "",
            };

            private static Color IconColor(MessageBoxIcon icon) => icon switch
            {
                MessageBoxIcon.Error => UiTheme.Bad,
                MessageBoxIcon.Warning => UiTheme.Warn,
                MessageBoxIcon.Question => UiTheme.AccentHot,
                MessageBoxIcon.Information => UiTheme.Ok,
                _ => UiTheme.FgMuted,
            };
        }
    }
}
