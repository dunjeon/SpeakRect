using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpeakRect
{
    /// <summary>
    /// Edge-tracking snapshot for face buttons, D-pad, triggers, and stick directions.
    /// Used by the poller and Key Map capture UI.
    /// </summary>
    public struct PadEdgeState
    {
        /// <summary>wButtons without D-pad bits (face, shoulders, Start/Back, thumb clicks).</summary>
        public ushort Buttons;
        /// <summary>D-pad only (XInput 0x0001..0x0008).</summary>
        public ushort DPad;
        public bool Lt;
        public bool Rt;
        /// <summary>OR of <see cref="GamepadButton.STICK_*"/> bits past deadzone.</summary>
        public byte Sticks;

        public void Clear()
        {
            Buttons = 0;
            DPad = 0;
            Lt = false;
            Rt = false;
            Sticks = 0;
        }
    }

    /// <summary>
    /// Polls XInput for rising-edge face button / D-pad / trigger / stick-tilt and maps
    /// them to action ids. Also drives continuous custom actions (mouse move / stick mouse)
    /// while controls stay held. Timer only runs when at least one binding needs it
    /// (or while the Key Map is capturing).
    /// </summary>
    public sealed class XInputPoller : IDisposable
    {
        private const byte TriggerThreshold = 64; // ~25% of 0..255
        /// <summary>
        /// Stick axis deadzone (XInput short −32768..32767). ~49% so assignment needs
        /// a deliberate tilt, not rest noise. Continuous stick-mouse uses a softer zone.
        /// </summary>
        private const short StickThreshold = 16000;
        private const short StickMouseDeadzone = 6000;

        /// <summary>One-shot action id (built-in row id or CustomN).</summary>
        private readonly Action<string> _onAction;

        /// <summary>
        /// Continuous custom binding tick (called every poll while held / stick active).
        /// </summary>
        private readonly Action<CustomHotkeyBinding, float /*magnitude*/>? _onContinuous;

        private readonly System.Windows.Forms.Timer _timer;
        private readonly List<(GamepadButton Button, string RowId, bool Continuous)> _bindings = new();
        private readonly List<CustomHotkeyBinding> _stickMouse = new();
        private readonly List<CustomHotkeyBinding> _holdCustom = new();

        private PadEdgeState _prev;
        private bool _suppressActions;
        private bool _forcePoll; // capture mode keeps timer alive with no bindings
        private bool _disposed;
        private int _scrollTick; // throttle wheel while held

        public XInputPoller(
            Action<string> onAction,
            Action<CustomHotkeyBinding, float>? onContinuous = null)
        {
            _onAction = onAction ?? throw new ArgumentNullException(nameof(onAction));
            _onContinuous = onContinuous;
            _timer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60 Hz for smooth mouse
            _timer.Tick += Timer_Tick;
        }

        /// <summary>When true, rising edges are still tracked but no actions fire.</summary>
        public bool SuppressActions
        {
            get => _suppressActions;
            set => _suppressActions = value;
        }

        /// <summary>
        /// Keep polling even with no bindings (Key Map capture). Does not fire actions
        /// unless <see cref="SuppressActions"/> is false and bindings exist.
        /// </summary>
        public bool ForcePoll
        {
            get => _forcePoll;
            set
            {
                _forcePoll = value;
                UpdateTimerRunning();
            }
        }

        public void SyncFromSettings()
        {
            _bindings.Clear();
            _stickMouse.Clear();
            _holdCustom.Clear();
            var s = AppSettings.Current;

            foreach (var row in AppSettings.HotkeyMapRows)
            {
                var btn = row.GamepadGetter(s);
                if (!btn.IsEmpty)
                    _bindings.Add((btn, row.Id, Continuous: false));
            }

            foreach (var c in s.CustomHotkeys)
            {
                if (c.Action == CustomActionKind.None)
                    continue;

                if (c.UsesAnalogStick)
                {
                    _stickMouse.Add(c);
                    continue;
                }

                if (c.Gamepad.IsEmpty)
                    continue;

                if (c.IsContinuous)
                {
                    _holdCustom.Add(c);
                    // still track edge for first-tick feel; continuous path does the work
                    _bindings.Add((c.Gamepad, c.Id, Continuous: true));
                }
                else
                {
                    _bindings.Add((c.Gamepad, c.Id, Continuous: false));
                }
            }

            // Reset edge baseline so holding a control across remaps doesn't re-fire.
            if (TryReadPad(s.GamepadControllerIndex, out var cur))
                _prev = cur;
            else
                _prev.Clear();

            UpdateTimerRunning();
            Debug.WriteLine(
                $"[Gamepad] sync: {_bindings.Count} edge, {_holdCustom.Count} hold, " +
                $"{_stickMouse.Count} stick-mouse, index={s.GamepadControllerIndex}, " +
                $"timer={_timer.Enabled}");
        }

        /// <summary>
        /// Snapshot first newly activated control on the configured controller.
        /// Used by Key Map capture. Returns false if nothing new is active.
        /// Priority: D-pad → face/shoulder → triggers → stick directions.
        /// </summary>
        public static bool TryGetRisingEdge(int controllerIndex, ref PadEdgeState prev,
            out GamepadButton button)
        {
            button = default;
            if (!TryReadPad(controllerIndex, out var cur))
            {
                prev.Clear();
                return false;
            }

            ushort risenDPad = (ushort)(cur.DPad & ~prev.DPad);
            ushort risenButtons = (ushort)(cur.Buttons & ~prev.Buttons);
            bool ltEdge = cur.Lt && !prev.Lt;
            bool rtEdge = cur.Rt && !prev.Rt;
            byte risenSticks = (byte)(cur.Sticks & ~prev.Sticks);

            prev = cur;

            // D-pad first so hat presses are never stolen by stick noise on the same tick.
            if (risenDPad != 0)
            {
                int r = risenDPad;
                ushort bit = (ushort)(r & -r);
                button = GamepadButton.FromDPad(bit);
                return !button.IsEmpty;
            }
            if (risenButtons != 0)
            {
                int r = risenButtons;
                ushort bit = (ushort)(r & -r);
                button = GamepadButton.FromDigital(bit);
                return !button.IsEmpty;
            }
            if (ltEdge)
            {
                button = GamepadButton.LeftTrigger;
                return true;
            }
            if (rtEdge)
            {
                button = GamepadButton.RightTrigger;
                return true;
            }
            if (risenSticks != 0)
            {
                int r = risenSticks;
                byte bit = (byte)(r & -r);
                button = GamepadButton.FromStick(bit);
                return !button.IsEmpty;
            }
            return false;
        }

        public static bool IsControllerConnected(int controllerIndex) =>
            TryGetState(controllerIndex, out _);

        /// <summary>
        /// Read normalized stick axes (−1..1). Y is +up. Returns false if disconnected.
        /// </summary>
        public static bool TryGetStickAxes(int controllerIndex,
            out float lx, out float ly, out float rx, out float ry)
        {
            lx = ly = rx = ry = 0f;
            if (!TryGetState(controllerIndex, out var state))
                return false;

            lx = NormalizeAxis(state.Gamepad.sThumbLX, StickMouseDeadzone);
            ly = NormalizeAxis(state.Gamepad.sThumbLY, StickMouseDeadzone);
            rx = NormalizeAxis(state.Gamepad.sThumbRX, StickMouseDeadzone);
            ry = NormalizeAxis(state.Gamepad.sThumbRY, StickMouseDeadzone);
            return true;
        }

        private void UpdateTimerRunning()
        {
            bool want = !_disposed && (
                _forcePoll ||
                _bindings.Count > 0 ||
                _holdCustom.Count > 0 ||
                _stickMouse.Count > 0);
            if (want && !_timer.Enabled)
                _timer.Start();
            else if (!want && _timer.Enabled)
                _timer.Stop();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_disposed) return;

            int index = Math.Clamp(AppSettings.Current.GamepadControllerIndex, 0, 3);
            if (!TryReadPad(index, out var cur))
            {
                _prev.Clear();
                return;
            }

            ushort risenDPad = (ushort)(cur.DPad & ~_prev.DPad);
            ushort risenButtons = (ushort)(cur.Buttons & ~_prev.Buttons);
            bool ltEdge = cur.Lt && !_prev.Lt;
            bool rtEdge = cur.Rt && !_prev.Rt;
            byte risenSticks = (byte)(cur.Sticks & ~_prev.Sticks);

            _prev = cur;

            if (_suppressActions)
                return;

            // ---- One-shot rising edges (built-in + non-continuous custom) ----
            if (_bindings.Count > 0 &&
                (risenDPad != 0 || risenButtons != 0 || ltEdge || rtEdge || risenSticks != 0))
            {
                foreach (var (btn, rowId, continuous) in _bindings)
                {
                    if (continuous) continue; // hold path owns these
                    bool hit = MatchesRising(btn, risenDPad, risenButtons, ltEdge, rtEdge, risenSticks);
                    if (!hit) continue;

                    try { _onAction(rowId); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Gamepad] action {rowId} failed: {ex.Message}");
                    }
                }
            }

            // ---- Continuous discrete holds (mouse nudge / scroll) ----
            if (_holdCustom.Count > 0 && _onContinuous != null)
            {
                _scrollTick++;
                foreach (var c in _holdCustom)
                {
                    if (!IsHeld(c.Gamepad, cur))
                        continue;

                    float mag = 1f;
                    // Triggers can scale with analog value if we had it; digital hold = 1
                    if (c.Action is CustomActionKind.ScrollUp or CustomActionKind.ScrollDown)
                    {
                        // ~10 Hz scroll while held (~60Hz timer → every 6 ticks)
                        if (_scrollTick % 6 != 0)
                            continue;
                    }

                    try { _onContinuous(c, mag); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Gamepad] continuous {c.Id} failed: {ex.Message}");
                    }
                }
            }

            // ---- Analog stick mouse ----
            if (_stickMouse.Count > 0 && _onContinuous != null &&
                TryGetStickAxes(index, out float lx, out float ly, out float rx, out float ry))
            {
                foreach (var c in _stickMouse)
                {
                    float nx, ny;
                    if (c.Action == CustomActionKind.MouseLeftStick)
                    {
                        nx = lx; ny = ly;
                    }
                    else
                    {
                        nx = rx; ny = ry;
                    }

                    float mag = MathF.Sqrt(nx * nx + ny * ny);
                    if (mag < 0.02f)
                        continue;

                    try
                    {
                        float speed = SystemInput.ParseSpeed(c.Arg, SystemInput.DefaultMouseSpeed);
                        SystemInput.MouseFromStick(nx, ny, speed);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Gamepad] stick-mouse {c.Id} failed: {ex.Message}");
                    }
                }
            }
        }

        private static bool IsHeld(GamepadButton btn, PadEdgeState cur)
        {
            return btn.ButtonKind switch
            {
                GamepadButton.Kind.DPad =>
                    btn.Mask != 0 && (cur.DPad & btn.Mask) == btn.Mask,
                GamepadButton.Kind.Digital when (btn.Mask & GamepadButton.DPAD_MASK) != 0
                    && (btn.Mask & ~GamepadButton.DPAD_MASK) == 0 =>
                    (cur.DPad & btn.Mask) == btn.Mask,
                GamepadButton.Kind.Digital =>
                    btn.Mask != 0 && (cur.Buttons & btn.Mask) == btn.Mask,
                GamepadButton.Kind.LeftTrigger => cur.Lt,
                GamepadButton.Kind.RightTrigger => cur.Rt,
                GamepadButton.Kind.Stick =>
                    btn.Mask != 0 && (cur.Sticks & (byte)btn.Mask) == (byte)btn.Mask,
                _ => false
            };
        }

        private static bool MatchesRising(
            GamepadButton btn,
            ushort risenDPad,
            ushort risenButtons,
            bool ltEdge,
            bool rtEdge,
            byte risenSticks)
        {
            return btn.ButtonKind switch
            {
                GamepadButton.Kind.DPad =>
                    btn.Mask != 0 && (risenDPad & btn.Mask) == btn.Mask,

                // Legacy saves may store D-pad as Digital with DPAD_* mask
                GamepadButton.Kind.Digital when (btn.Mask & GamepadButton.DPAD_MASK) != 0
                    && (btn.Mask & ~GamepadButton.DPAD_MASK) == 0 =>
                    (risenDPad & btn.Mask) == btn.Mask,

                GamepadButton.Kind.Digital =>
                    btn.Mask != 0 && (risenButtons & btn.Mask) == btn.Mask,

                GamepadButton.Kind.LeftTrigger => ltEdge,
                GamepadButton.Kind.RightTrigger => rtEdge,
                GamepadButton.Kind.Stick =>
                    btn.Mask != 0 && (risenSticks & (byte)btn.Mask) == (byte)btn.Mask,
                _ => false
            };
        }

        private static bool TryReadPad(int userIndex, out PadEdgeState pad)
        {
            pad = default;
            if (!TryGetState(userIndex, out var state))
                return false;

            ushort w = state.Gamepad.wButtons;
            pad.DPad = (ushort)(w & GamepadButton.DPAD_MASK);
            pad.Buttons = (ushort)(w & ~GamepadButton.DPAD_MASK);
            pad.Lt = state.Gamepad.bLeftTrigger >= TriggerThreshold;
            pad.Rt = state.Gamepad.bRightTrigger >= TriggerThreshold;
            pad.Sticks = EncodeSticks(
                state.Gamepad.sThumbLX, state.Gamepad.sThumbLY,
                state.Gamepad.sThumbRX, state.Gamepad.sThumbRY);
            return true;
        }

        private static byte EncodeSticks(short lx, short ly, short rx, short ry)
        {
            byte s = 0;
            // XInput: +Y is up, +X is right
            if (ly >= StickThreshold) s |= GamepadButton.STICK_L_UP;
            if (ly <= -StickThreshold) s |= GamepadButton.STICK_L_DOWN;
            if (lx <= -StickThreshold) s |= GamepadButton.STICK_L_LEFT;
            if (lx >= StickThreshold) s |= GamepadButton.STICK_L_RIGHT;
            if (ry >= StickThreshold) s |= GamepadButton.STICK_R_UP;
            if (ry <= -StickThreshold) s |= GamepadButton.STICK_R_DOWN;
            if (rx <= -StickThreshold) s |= GamepadButton.STICK_R_LEFT;
            if (rx >= StickThreshold) s |= GamepadButton.STICK_R_RIGHT;
            return s;
        }

        private static float NormalizeAxis(short value, short deadzone)
        {
            int v = value;
            int dz = deadzone;
            if (v > -dz && v < dz)
                return 0f;
            // scale remaining range to 0..1 with sign
            float sign = v < 0 ? -1f : 1f;
            float mag = Math.Abs(v);
            float max = 32767f;
            float t = (mag - dz) / (max - dz);
            return sign * Math.Clamp(t, 0f, 1f);
        }

        private static bool TryGetState(int userIndex, out XINPUT_STATE state)
        {
            state = default;
            if (userIndex < 0 || userIndex > 3)
                return false;

            int result = XInputGetState((uint)userIndex, out state);
            return result == 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _timer.Dispose();
        }

        #region XInput P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

        #endregion
    }
}
