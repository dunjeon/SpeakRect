using System;
using System.Drawing;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Always-opaque left tool strip. Separate HWND so overlay Opacity (and capture
    /// dimming) never wash out SHAPE / MODE / SETTINGS — same clarity as Settings.
    /// Keyboard focus always stays on the host overlay (Enter/Esc/arrows).
    /// </summary>
    internal sealed class OverlaySidebarChromeForm : Form
    {
        private readonly Action<Graphics, int> _paintSidebar;

        public OverlaySidebarChromeForm(Action<Graphics, int> paintSidebar, int sidebarWidth)
        {
            _paintSidebar = paintSidebar ?? throw new ArgumentNullException(nameof(paintSidebar));
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            // Independent of host.Opacity — always full strength.
            Opacity = 1.0;
            BackColor = UiTheme.Bg;
            MinimumSize = new Size(sidebarWidth, 100);
            Size = new Size(sidebarWidth, 800);
            // Never become the active control/form — host handles all keys.
            // Clicks still reach the low-level mouse hook for tool hit-tests.
            TabStop = false;
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.Selectable,
                true);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_NOACTIVATE = 0x08000000;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x21;
            const int MA_NOACTIVATE = 3;
            // Clicks must not steal focus from the overlay (Enter / shape keys / arrows).
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            _paintSidebar(e.Graphics, ClientSize.Height);
        }
    }
}
