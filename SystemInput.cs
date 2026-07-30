using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Synthetic mouse / keyboard / window control via Win32 SendInput and friends.
    /// Used by custom global hotkeys (gamepad → click, stick → cursor, etc.).
    /// </summary>
    public static class SystemInput
    {
        // ---- mouse flags ----
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_HWHEEL = 0x1000;

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        private const int WHEEL_DELTA = 120;

        private const int SW_MINIMIZE = 6;
        private const int SW_MAXIMIZE = 3;
        private const int SW_RESTORE = 9;

        /// <summary>Relative mouse move in pixels (positive X = right, positive Y = down).</summary>
        public static void MouseMove(int dx, int dy)
        {
            if (dx == 0 && dy == 0) return;
            SendMouse(MOUSEEVENTF_MOVE, dx, dy, 0);
        }

        public static void MouseLeftClick()
        {
            SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0);
            SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
        }

        public static void MouseRightClick()
        {
            SendMouse(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0);
            SendMouse(MOUSEEVENTF_RIGHTUP, 0, 0, 0);
        }

        public static void MouseMiddleClick()
        {
            SendMouse(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0);
            SendMouse(MOUSEEVENTF_MIDDLEUP, 0, 0, 0);
        }

        public static void MouseLeftDown() => SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0);
        public static void MouseLeftUp() => SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
        public static void MouseRightDown() => SendMouse(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0);
        public static void MouseRightUp() => SendMouse(MOUSEEVENTF_RIGHTUP, 0, 0, 0);

        public static void MouseDoubleClick()
        {
            MouseLeftClick();
            MouseLeftClick();
        }

        /// <summary>Vertical scroll; positive = up (away from user).</summary>
        public static void MouseScroll(int notches)
        {
            if (notches == 0) return;
            SendMouse(MOUSEEVENTF_WHEEL, 0, 0, (uint)(notches * WHEEL_DELTA));
        }

        /// <summary>Horizontal scroll; positive = right.</summary>
        public static void MouseHScroll(int notches)
        {
            if (notches == 0) return;
            SendMouse(MOUSEEVENTF_HWHEEL, 0, 0, (uint)(notches * WHEEL_DELTA));
        }

        /// <summary>
        /// Press then release a chord (modifiers + key), same tokens as hotkey ini.
        /// </summary>
        public static void KeyTap(HotkeyChord chord)
        {
            if (chord.IsEmpty) return;
            KeyDown(chord);
            KeyUp(chord);
        }

        public static void KeyDown(HotkeyChord chord)
        {
            if (chord.IsEmpty) return;
            if ((chord.Modifiers & HotkeyChord.MOD_CONTROL) != 0) SendVk(Keys.ControlKey, down: true);
            if ((chord.Modifiers & HotkeyChord.MOD_ALT) != 0) SendVk(Keys.Menu, down: true);
            if ((chord.Modifiers & HotkeyChord.MOD_SHIFT) != 0) SendVk(Keys.ShiftKey, down: true);
            if ((chord.Modifiers & HotkeyChord.MOD_WIN) != 0) SendVk(Keys.LWin, down: true);
            SendVk(chord.Key, down: true);
        }

        public static void KeyUp(HotkeyChord chord)
        {
            if (chord.IsEmpty) return;
            SendVk(chord.Key, down: false);
            if ((chord.Modifiers & HotkeyChord.MOD_WIN) != 0) SendVk(Keys.LWin, down: false);
            if ((chord.Modifiers & HotkeyChord.MOD_SHIFT) != 0) SendVk(Keys.ShiftKey, down: false);
            if ((chord.Modifiers & HotkeyChord.MOD_ALT) != 0) SendVk(Keys.Menu, down: false);
            if ((chord.Modifiers & HotkeyChord.MOD_CONTROL) != 0) SendVk(Keys.ControlKey, down: false);
        }

        public static void KeyTapVk(Keys key) => KeyTap(new HotkeyChord(0, key));

        public static void MinimizeForeground()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SW_MINIMIZE);
        }

        public static void MaximizeForeground()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SW_MAXIMIZE);
        }

        public static void RestoreForeground()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SW_RESTORE);
        }

        public static void CloseForeground()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            // WM_CLOSE — polite close (apps can prompt to save)
            PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }

        public static void ShowDesktop() =>
            KeyTap(new HotkeyChord(HotkeyChord.MOD_WIN, Keys.D));

        public static void AltTab() =>
            KeyTap(new HotkeyChord(HotkeyChord.MOD_ALT, Keys.Tab));

        public static void VolumeUp() => KeyTapVk(Keys.VolumeUp);
        public static void VolumeDown() => KeyTapVk(Keys.VolumeDown);
        public static void VolumeMute() => KeyTapVk(Keys.VolumeMute);
        public static void MediaPlayPause() => KeyTapVk(Keys.MediaPlayPause);
        public static void MediaNext() => KeyTapVk(Keys.MediaNextTrack);
        public static void MediaPrev() => KeyTapVk(Keys.MediaPreviousTrack);

        /// <summary>
        /// Run a one-shot custom action (not continuous mouse/scroll).
        /// </summary>
        public static void ExecuteOnce(CustomActionKind kind, string? arg)
        {
            try
            {
                switch (kind)
                {
                    case CustomActionKind.MouseLeftClick: MouseLeftClick(); break;
                    case CustomActionKind.MouseRightClick: MouseRightClick(); break;
                    case CustomActionKind.MouseMiddleClick: MouseMiddleClick(); break;
                    case CustomActionKind.MouseDoubleClick: MouseDoubleClick(); break;
                    case CustomActionKind.MouseLeftDown: MouseLeftDown(); break;
                    case CustomActionKind.MouseLeftUp: MouseLeftUp(); break;
                    case CustomActionKind.MouseRightDown: MouseRightDown(); break;
                    case CustomActionKind.MouseRightUp: MouseRightUp(); break;
                    case CustomActionKind.KeyTap:
                        if (HotkeyChord.TryParse(arg, out var chord) && !chord.IsEmpty)
                            KeyTap(chord);
                        break;
                    case CustomActionKind.WinMinimize: MinimizeForeground(); break;
                    case CustomActionKind.WinMaximize: MaximizeForeground(); break;
                    case CustomActionKind.WinRestore: RestoreForeground(); break;
                    case CustomActionKind.WinClose: CloseForeground(); break;
                    case CustomActionKind.ShowDesktop: ShowDesktop(); break;
                    case CustomActionKind.AltTab: AltTab(); break;
                    case CustomActionKind.VolumeUp: VolumeUp(); break;
                    case CustomActionKind.VolumeDown: VolumeDown(); break;
                    case CustomActionKind.VolumeMute: VolumeMute(); break;
                    case CustomActionKind.MediaPlayPause: MediaPlayPause(); break;
                    case CustomActionKind.MediaNext: MediaNext(); break;
                    case CustomActionKind.MediaPrev: MediaPrev(); break;
                    // Continuous kinds are handled by the poller / hold path
                    case CustomActionKind.MouseMoveUp:
                        MouseMove(0, -DefaultNudge());
                        break;
                    case CustomActionKind.MouseMoveDown:
                        MouseMove(0, DefaultNudge());
                        break;
                    case CustomActionKind.MouseMoveLeft:
                        MouseMove(-DefaultNudge(), 0);
                        break;
                    case CustomActionKind.MouseMoveRight:
                        MouseMove(DefaultNudge(), 0);
                        break;
                    case CustomActionKind.ScrollUp:
                        MouseScroll(1);
                        break;
                    case CustomActionKind.ScrollDown:
                        MouseScroll(-1);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemInput] ExecuteOnce({kind}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Default mouse speed (pixels per poll tick ≈ 60 Hz).
        /// 12 is the current default; 14 felt like teleporting.
        /// </summary>
        public const float DefaultMouseSpeed = 12f;

        /// <summary>Allowed range for user-set mouse speed (pixels/tick).</summary>
        public const float MinMouseSpeed = 0.25f;
        public const float MaxMouseSpeed = 40f;

        // Sub-pixel accumulators so speeds like 0.5 / 2.5 move smoothly instead of snapping
        private static float _accX;
        private static float _accY;

        /// <summary>
        /// Continuous tick for hold/analog actions. <paramref name="magnitude"/> is 0..1
        /// (1 for digital hold; stick length for analog).
        /// </summary>
        public static void ExecuteContinuous(CustomActionKind kind, float magnitude, string? arg)
        {
            if (magnitude <= 0f) return;
            magnitude = Math.Clamp(magnitude, 0f, 1f);
            try
            {
                float speed = ParseSpeed(arg, DefaultMouseSpeed);
                float step = speed * magnitude;

                switch (kind)
                {
                    case CustomActionKind.MouseMoveUp:
                        ApplyAccumulated(0f, -step);
                        break;
                    case CustomActionKind.MouseMoveDown:
                        ApplyAccumulated(0f, step);
                        break;
                    case CustomActionKind.MouseMoveLeft:
                        ApplyAccumulated(-step, 0f);
                        break;
                    case CustomActionKind.MouseMoveRight:
                        ApplyAccumulated(step, 0f);
                        break;
                    case CustomActionKind.ScrollUp:
                        if (magnitude >= 0.35f)
                            MouseScroll(1);
                        break;
                    case CustomActionKind.ScrollDown:
                        if (magnitude >= 0.35f)
                            MouseScroll(-1);
                        break;
                    case CustomActionKind.MouseLeftStick:
                    case CustomActionKind.MouseRightStick:
                        // handled via MouseFromStick in the poller
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemInput] ExecuteContinuous({kind}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Analog stick → relative mouse. Axes are normalized −1..1 (X right, Y up from stick).
        /// Screen Y is inverted (positive down). Uses sub-pixel accumulation for fine speeds.
        /// </summary>
        public static void MouseFromStick(float nx, float ny, float speedPixelsPerTick)
        {
            if (Math.Abs(nx) < 0.001f && Math.Abs(ny) < 0.001f) return;
            float s = Math.Max(MinMouseSpeed, speedPixelsPerTick);
            // stick +Y up → screen -Y
            ApplyAccumulated(nx * s, -ny * s);
        }

        /// <summary>One-shot nudge size (keyboard auto-repeat path).</summary>
        public static int DefaultNudge() =>
            Math.Max(1, (int)Math.Round(DefaultMouseSpeed));

        public static float ParseSpeed(string? arg, float defaultSpeed)
        {
            if (string.IsNullOrWhiteSpace(arg)) return defaultSpeed;
            if (float.TryParse(arg.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0f)
                return Math.Clamp(v, MinMouseSpeed, MaxMouseSpeed);
            return defaultSpeed;
        }

        /// <summary>Format speed for display / ini (invariant).</summary>
        public static string FormatSpeed(float speed) =>
            speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        private static void ApplyAccumulated(float dx, float dy)
        {
            _accX += dx;
            _accY += dy;
            int ix = (int)Math.Truncate(_accX);
            int iy = (int)Math.Truncate(_accY);
            _accX -= ix;
            _accY -= iy;
            if (ix != 0 || iy != 0)
                MouseMove(ix, iy);
        }

        private static void SendMouse(uint flags, int dx, int dy, uint mouseData)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = dx,
                        dy = dy,
                        mouseData = mouseData,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static void SendVk(Keys key, bool down)
        {
            ushort vk = (ushort)((int)key & 0xFF);
            if (vk == 0) return;
            uint flags = down ? 0u : KEYEVENTF_KEYUP;
            if (IsExtended(key))
                flags |= KEYEVENTF_EXTENDEDKEY;

            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static bool IsExtended(Keys key) =>
            key is Keys.Up or Keys.Down or Keys.Left or Keys.Right
                or Keys.Insert or Keys.Delete or Keys.Home or Keys.End
                or Keys.PageUp or Keys.PageDown or Keys.NumLock
                or Keys.RControlKey or Keys.RMenu or Keys.LWin or Keys.RWin
                or Keys.Apps or Keys.Divide
                or Keys.MediaNextTrack or Keys.MediaPreviousTrack
                or Keys.MediaPlayPause or Keys.MediaStop
                or Keys.VolumeUp or Keys.VolumeDown or Keys.VolumeMute;

        #region P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        #endregion
    }
}
