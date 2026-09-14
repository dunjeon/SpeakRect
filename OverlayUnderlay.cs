using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Optional overlay underlay: freeze the foreground program (GamePauser-style
    /// thread suspend) and/or draw on a desktop screenshot taken before the overlay
    /// appears. Both default off. Always released when the overlay hides or SpeakRect exits.
    /// </summary>
    public static class OverlayUnderlay
    {
        private const uint Th32CsSnapThread = 0x00000004;
        private const uint ThreadSuspendResume = 0x0002;

        private static readonly object Gate = new();
        private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "dwm", "csrss", "winlogon", "services", "lsass", "smss",
            "wininit", "fontdrvhost", "conhost", "sihost", "taskmgr", "searchhost",
            "searchapp", "searchui", "startmenuexperiencehost", "shellexperiencehost",
            "textinputhost", "applicationframehost", "runtimebroker", "systemsettings",
            "lockapp", "logonui", "shellhost", "ctfmon", "widgets", "widgetservice",
            "gamebar", "gamebarftw", "speakrect", "koboldcpp",
        };

        private static int _candidatePid;
        private static IntPtr _candidateHwnd;
        private static int _heldPid;
        private static readonly List<uint> SuspendedTids = new();
        private static Bitmap? _snapshot;
        private static Rectangle _snapshotBounds;

        /// <summary>True while a still desktop image is the overlay / OCR source.</summary>
        public static bool HasSnapshot
        {
            get { lock (Gate) return _snapshot != null; }
        }

        /// <summary>True while a foreign process is thread-suspended by us.</summary>
        public static bool IsHoldingProcess
        {
            get { lock (Gate) return _heldPid != 0; }
        }

        /// <summary>
        /// Remember the current foreground window if it is safe to freeze.
        /// Call while the overlay is still hidden (game still focused).
        /// </summary>
        public static void NoteForegroundCandidate()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    return;
                _ = GetWindowThreadProcessId(hwnd, out uint pid);
                if (!CanHoldPid((int)pid))
                    return;
                lock (Gate)
                {
                    _candidateHwnd = hwnd;
                    _candidatePid = (int)pid;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Overlay] NoteForeground: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply current Overlay settings. Snapshot only when the overlay is not
        /// already covering the desktop (otherwise we would photograph ourselves).
        /// </summary>
        public static void BeginForOverlay(bool overlayAlreadyVisible)
        {
            var s = AppSettings.Current;
            bool wantPause = s.OverlayPauseUnderlay;
            bool wantSnap = s.OverlayDrawOnScreenshot;

            if (!overlayAlreadyVisible)
                NoteForegroundCandidate();

            if (wantPause)
                TryHoldCandidate();
            else
                ReleaseHold();

            if (wantSnap)
            {
                if (!overlayAlreadyVisible)
                    CaptureDesktopSnapshot();
            }
            else
            {
                DropSnapshot();
            }
        }

        /// <summary>Resume any held process and drop the still image. Safe to call often.</summary>
        public static void EndForOverlay()
        {
            ReleaseHold();
            DropSnapshot();
        }

        /// <summary>
        /// Drop the still image only (keep a frozen process). Used when the
        /// virtual desktop size changes so crops would miss.
        /// </summary>
        public static void DiscardSnapshot() => DropSnapshot();

        /// <summary>
        /// Crop the still image in screen coordinates. Null when there is no snapshot
        /// (caller should CopyFromScreen). Caller owns the bitmap.
        /// </summary>
        public static Bitmap? TryCloneRegion(Rectangle screenRect)
        {
            lock (Gate)
            {
                if (_snapshot == null)
                    return null;
                var src = MapScreenRectToSnapshot(screenRect, _snapshotBounds);
                if (src.Width < 1 || src.Height < 1)
                    return null;
                src = Rectangle.Intersect(src, new Rectangle(0, 0, _snapshot.Width, _snapshot.Height));
                if (src.Width < 1 || src.Height < 1)
                    return null;
                try
                {
                    var crop = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                    using var g = Graphics.FromImage(crop);
                    g.DrawImage(
                        _snapshot,
                        new Rectangle(0, 0, src.Width, src.Height),
                        src,
                        GraphicsUnit.Pixel);
                    return crop;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Overlay] snapshot crop: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>Paint the still image into the overlay client (1:1 with virtual desktop).</summary>
        public static bool TryDrawSnapshot(Graphics g, Rectangle client)
        {
            if (g == null || client.Width < 1 || client.Height < 1)
                return false;
            lock (Gate)
            {
                if (_snapshot == null)
                    return false;
                try
                {
                    g.DrawImage(_snapshot, client);
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Overlay] snapshot paint: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>Screen rect → pixel rect inside a snapshot taken at <paramref name="snapBounds"/>.</summary>
        public static Rectangle MapScreenRectToSnapshot(Rectangle screen, Rectangle snapBounds)
        {
            if (snapBounds.Width < 1 || snapBounds.Height < 1)
                return Rectangle.Empty;
            var hit = Rectangle.Intersect(screen, snapBounds);
            if (hit.Width < 1 || hit.Height < 1)
                return Rectangle.Empty;
            return new Rectangle(
                hit.X - snapBounds.X,
                hit.Y - snapBounds.Y,
                hit.Width,
                hit.Height);
        }

        /// <summary>True when this process name must never be frozen (shell / self / host).</summary>
        public static bool IsProtectedProcessName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;
            string n = name.Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                n = n[..^4];
            return ProtectedNames.Contains(n);
        }

        public static bool CanHoldPid(int pid)
        {
            if (pid <= 4)
                return false;
            try
            {
                if (pid == Environment.ProcessId)
                    return false;
            }
            catch { /* ignore */ }

            try
            {
                int? host = LocalLlmHost.HostProcessId;
                if (host is int h && h == pid)
                    return false;
            }
            catch { /* ignore */ }

            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                    return false;
                if (IsProtectedProcessName(p.ProcessName))
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static void CaptureDesktopSnapshot()
        {
            var vs = SystemInformation.VirtualScreen;
            if (vs.Width < 8 || vs.Height < 8)
                return;
            Bitmap? bmp = null;
            try
            {
                bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(vs.Location, Point.Empty, vs.Size);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Overlay] desktop snapshot: {ex.Message}");
                try { bmp?.Dispose(); } catch { /* ignore */ }
                return;
            }

            lock (Gate)
            {
                try { _snapshot?.Dispose(); } catch { /* ignore */ }
                _snapshot = bmp;
                _snapshotBounds = vs;
            }
            Debug.WriteLine($"[Overlay] snapshot {vs.Width}x{vs.Height} at {vs.Location}");
        }

        private static void DropSnapshot()
        {
            lock (Gate)
            {
                try { _snapshot?.Dispose(); } catch { /* ignore */ }
                _snapshot = null;
                _snapshotBounds = Rectangle.Empty;
            }
        }

        private static void TryHoldCandidate()
        {
            int pid;
            lock (Gate)
                pid = _candidatePid;
            if (pid == 0 || !CanHoldPid(pid))
                return;

            lock (Gate)
            {
                if (_heldPid == pid)
                    return;
                if (_heldPid != 0)
                    ResumeHeldThreads_NoLock();
            }

            ReleaseHeldKeys();
            SuspendPid(pid);
        }

        private static void ReleaseHold()
        {
            lock (Gate)
                ResumeHeldThreads_NoLock();
        }

        private static void SuspendPid(int pid)
        {
            var tids = new List<uint>();
            IntPtr snap = CreateToolhelp32Snapshot(Th32CsSnapThread, 0);
            if (snap == IntPtr.Zero || snap == (IntPtr)(-1))
            {
                Debug.WriteLine($"[Overlay] thread snapshot failed for pid={pid}");
                return;
            }

            try
            {
                var te = new ThreadEntry32 { DwSize = (uint)Marshal.SizeOf<ThreadEntry32>() };
                if (!Thread32First(snap, ref te))
                    return;
                do
                {
                    if (te.Th32OwnerProcessId != (uint)pid)
                        continue;
                    IntPtr ht = OpenThread(ThreadSuspendResume, false, te.Th32ThreadId);
                    if (ht == IntPtr.Zero)
                        continue;
                    try
                    {
                        uint prev = SuspendThread(ht);
                        if (prev != 0xFFFFFFFFu)
                            tids.Add(te.Th32ThreadId);
                    }
                    finally
                    {
                        CloseHandle(ht);
                    }
                } while (Thread32Next(snap, ref te));
            }
            finally
            {
                CloseHandle(snap);
            }

            lock (Gate)
            {
                SuspendedTids.Clear();
                SuspendedTids.AddRange(tids);
                _heldPid = tids.Count > 0 ? pid : 0;
            }
            Debug.WriteLine($"[Overlay] paused pid={pid} threads={tids.Count}");
        }

        private static void ResumeHeldThreads_NoLock()
        {
            int pid = _heldPid;
            var tids = SuspendedTids.ToArray();
            SuspendedTids.Clear();
            _heldPid = 0;
            if (pid == 0 || tids.Length == 0)
                return;

            int n = 0;
            foreach (uint tid in tids)
            {
                IntPtr ht = OpenThread(ThreadSuspendResume, false, tid);
                if (ht == IntPtr.Zero)
                    continue;
                try
                {
                    if (ResumeThread(ht) != 0xFFFFFFFFu)
                        n++;
                }
                finally
                {
                    CloseHandle(ht);
                }
            }
            Debug.WriteLine($"[Overlay] resumed pid={pid} threads={n}/{tids.Length}");
        }

        /// <summary>
        /// Lift keys that are physically down so a frozen game does not keep WASD
        /// after resume (same idea as GamePauser).
        /// </summary>
        private static void ReleaseHeldKeys()
        {
            try
            {
                for (int vk = 1; vk < 256; vk++)
                {
                    // Skip mouse buttons (VK_LBUTTON..XBUTTON2). Injecting those
                    // as keyboard-up events is nonsense and can confuse the game.
                    if (vk is 1 or 2 or 4 or 5 or 6)
                        continue;
                    if ((GetAsyncKeyState(vk) & 0x8000) == 0)
                        continue;
                    SystemInput.KeyUp(new HotkeyChord(0, (Keys)vk));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Overlay] release keys: {ex.Message}");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadEntry32
        {
            public uint DwSize;
            public uint CntUsage;
            public uint Th32ThreadId;
            public uint Th32OwnerProcessId;
            public int TpBasePri;
            public int TpDeltaPri;
            public uint DwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll")]
        private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry32 entry);

        [DllImport("kernel32.dll")]
        private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry32 entry);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint SuspendThread(IntPtr thread);

        [DllImport("kernel32.dll")]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
