using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// App mark for the tray, window captions, and (via ApplicationIcon) the exe.
    /// Each caller gets its own <see cref="Icon"/> instance — WinForms disposes
    /// <see cref="Form.Icon"/> / <see cref="NotifyIcon.Icon"/> on teardown.
    /// </summary>
    internal static class AppIcons
    {
        private const string ResourceName = "SpeakRect.app.ico";
        private static readonly object Gate = new();
        private static Icon? _source;
        private static MemoryStream? _keepAlive;

        public static Icon ForTray()
        {
            Size sz = SystemInformation.SmallIconSize;
            return Create(sz.Width, sz.Height);
        }

        public static Icon ForWindow()
        {
            Size sz = SystemInformation.IconSize;
            return Create(sz.Width, sz.Height);
        }

        public static Icon Create(int width, int height)
        {
            Icon src = Source();
            try
            {
                return new Icon(src, Math.Max(16, width), Math.Max(16, height));
            }
            catch
            {
                return (Icon)src.Clone();
            }
        }

        private static Icon Source()
        {
            if (_source != null)
                return _source;
            lock (Gate)
            {
                if (_source != null)
                    return _source;

                var asm = typeof(AppIcons).Assembly;
                using Stream? s = asm.GetManifestResourceStream(ResourceName);
                if (s != null)
                {
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    byte[] bytes = ms.ToArray();
                    _keepAlive = new MemoryStream(bytes, writable: false);
                    _source = new Icon(_keepAlive);
                    return _source;
                }

                _source = (Icon)SystemIcons.Application.Clone();
                return _source;
            }
        }
    }
}
