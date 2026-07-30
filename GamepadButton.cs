using System;

namespace SpeakRect
{
/// <summary>
/// One gamepad control (face/shoulder/thumb-click, D-pad, trigger, stick tilt).
/// Empty = unbound. Serialized as e.g. <c>A</c>, <c>DPadUp</c>, <c>LT</c>, <c>LSUp</c>.
/// Stick tilts use a deadzone; D-pad is digital (XInput wButtons 0x000F).
/// </summary>
public readonly struct GamepadButton : IEquatable<GamepadButton>
{
    // XINPUT_GAMEPAD_* bit masks
    public const ushort DPAD_MASK = 0x000F;
    public const ushort DPAD_UP = 0x0001;
    public const ushort DPAD_DOWN = 0x0002;
    public const ushort DPAD_LEFT = 0x0004;
    public const ushort DPAD_RIGHT = 0x0008;
    public const ushort START = 0x0010;
    public const ushort BACK = 0x0020;
    public const ushort LEFT_THUMB = 0x0040;
    public const ushort RIGHT_THUMB = 0x0080;
    public const ushort LEFT_SHOULDER = 0x0100;
    public const ushort RIGHT_SHOULDER = 0x0200;
    public const ushort A = 0x1000;
    public const ushort B = 0x2000;
    public const ushort X = 0x4000;
    public const ushort Y = 0x8000;

    /// <summary>Bits for analog stick directions (not XInput wButtons).</summary>
    public const byte STICK_L_UP = 1 << 0;
    public const byte STICK_L_DOWN = 1 << 1;
    public const byte STICK_L_LEFT = 1 << 2;
    public const byte STICK_L_RIGHT = 1 << 3;
    public const byte STICK_R_UP = 1 << 4;
    public const byte STICK_R_DOWN = 1 << 5;
    public const byte STICK_R_LEFT = 1 << 6;
    public const byte STICK_R_RIGHT = 1 << 7;

    public enum Kind : byte
    {
        None = 0,
        /// <summary>Face / shoulder / Start / Back / thumb-click (not D-pad).</summary>
        Digital = 1,
        LeftTrigger = 2,
        RightTrigger = 3,
        /// <summary>Analog stick direction; <see cref="Mask"/> holds one STICK_* bit.</summary>
        Stick = 4,
        /// <summary>D-pad direction; <see cref="Mask"/> holds one DPAD_* bit.</summary>
        DPad = 5,
    }

    public Kind ButtonKind { get; }
    /// <summary>
    /// XInput wButtons bit for <see cref="Kind.Digital"/> / <see cref="Kind.DPad"/>;
    /// one <c>STICK_*</c> bit for <see cref="Kind.Stick"/>; unused for triggers.
    /// </summary>
    public ushort Mask { get; }

    public GamepadButton(Kind kind, ushort mask = 0)
    {
        ButtonKind = kind;
        Mask = mask;
    }

    /// <summary>
    /// Face/shoulder/etc. D-pad bits are routed to <see cref="Kind.DPad"/> so capture
    /// and matching treat the hat as its own control family.
    /// </summary>
    public static GamepadButton FromDigital(ushort mask)
    {
        if (mask == 0) return default;
        // Pure single D-pad bit → Kind.DPad
        if ((mask & ~DPAD_MASK) == 0 && IsSingleBit(mask))
            return new GamepadButton(Kind.DPad, mask);
        return new GamepadButton(Kind.Digital, mask);
    }

    public static GamepadButton FromDPad(ushort dpadBit) =>
        dpadBit == 0 ? default : new GamepadButton(Kind.DPad, dpadBit);

    public static GamepadButton FromStick(byte stickBit) =>
        stickBit == 0 ? default : new GamepadButton(Kind.Stick, stickBit);

    public static GamepadButton LeftTrigger => new(Kind.LeftTrigger);
    public static GamepadButton RightTrigger => new(Kind.RightTrigger);

    public static GamepadButton DPadUp => FromDPad(DPAD_UP);
    public static GamepadButton DPadDown => FromDPad(DPAD_DOWN);
    public static GamepadButton DPadLeft => FromDPad(DPAD_LEFT);
    public static GamepadButton DPadRight => FromDPad(DPAD_RIGHT);

    public static GamepadButton LSUp => FromStick(STICK_L_UP);
    public static GamepadButton LSDown => FromStick(STICK_L_DOWN);
    public static GamepadButton LSLeft => FromStick(STICK_L_LEFT);
    public static GamepadButton LSRight => FromStick(STICK_L_RIGHT);
    public static GamepadButton RSUp => FromStick(STICK_R_UP);
    public static GamepadButton RSDown => FromStick(STICK_R_DOWN);
    public static GamepadButton RSLeft => FromStick(STICK_R_LEFT);
    public static GamepadButton RSRight => FromStick(STICK_R_RIGHT);

    public bool IsEmpty => ButtonKind == Kind.None;

    public string ToIniString()
    {
        if (IsEmpty) return "";
        return ButtonKind switch
        {
            Kind.LeftTrigger => "LT",
            Kind.RightTrigger => "RT",
            Kind.Digital => FormatDigital(Mask),
            Kind.DPad => FormatDPad(Mask),
            Kind.Stick => FormatStick((byte)Mask),
            _ => ""
        };
    }

    public override string ToString() => ToIniString();

    public static bool TryParse(string? raw, out GamepadButton button)
    {
        button = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string t = raw.Trim();
        if (t.Equals("LT", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("LeftTrigger", StringComparison.OrdinalIgnoreCase))
        {
            button = LeftTrigger;
            return true;
        }
        if (t.Equals("RT", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("RightTrigger", StringComparison.OrdinalIgnoreCase))
        {
            button = RightTrigger;
            return true;
        }

        // D-pad before sticks/digital so "Up" etc. bind as D-pad, not something else.
        if (TryParseDPad(t, out ushort dpadBit))
        {
            button = FromDPad(dpadBit);
            return true;
        }

        if (TryParseStick(t, out byte stickBit))
        {
            button = FromStick(stickBit);
            return true;
        }

        if (TryParseDigital(t, out ushort mask))
        {
            button = FromDigital(mask);
            return true;
        }
        return false;
    }

    /// <summary>Parse or empty (never falls back to a default binding).</summary>
    public static GamepadButton ParseOrEmpty(string? raw) =>
        TryParse(raw, out var b) ? b : default;

    /// <summary>
    /// D-pad directions compare equal whether stored as <see cref="Kind.DPad"/> or
    /// legacy <see cref="Kind.Digital"/> with the same DPAD_* mask.
    /// </summary>
    public bool Equals(GamepadButton other)
    {
        if (IsDPadFamily(this) && IsDPadFamily(other))
            return Mask == other.Mask;
        return ButtonKind == other.ButtonKind && Mask == other.Mask;
    }

    public override bool Equals(object? obj) =>
        obj is GamepadButton other && Equals(other);

    public override int GetHashCode() =>
        IsDPadFamily(this)
            ? HashCode.Combine((int)Kind.DPad, Mask)
            : HashCode.Combine((int)ButtonKind, Mask);

    public static bool operator ==(GamepadButton a, GamepadButton b) => a.Equals(b);
    public static bool operator !=(GamepadButton a, GamepadButton b) => !a.Equals(b);

    private static bool IsDPadFamily(GamepadButton b) =>
        b.ButtonKind == Kind.DPad ||
        (b.ButtonKind == Kind.Digital && (b.Mask & ~DPAD_MASK) == 0 && b.Mask != 0);

    private static bool IsSingleBit(ushort v) => v != 0 && (v & (v - 1)) == 0;

    private static string FormatDigital(ushort mask) => mask switch
    {
        A => "A",
        B => "B",
        X => "X",
        Y => "Y",
        LEFT_SHOULDER => "LB",
        RIGHT_SHOULDER => "RB",
        START => "Start",
        BACK => "Back",
        LEFT_THUMB => "LeftThumb",
        RIGHT_THUMB => "RightThumb",
        // Legacy digital-encoded D-pad (should be Kind.DPad now)
        DPAD_UP => "DPadUp",
        DPAD_DOWN => "DPadDown",
        DPAD_LEFT => "DPadLeft",
        DPAD_RIGHT => "DPadRight",
        _ => $"0x{mask:X4}"
    };

    private static string FormatDPad(ushort mask) => mask switch
    {
        DPAD_UP => "DPadUp",
        DPAD_DOWN => "DPadDown",
        DPAD_LEFT => "DPadLeft",
        DPAD_RIGHT => "DPadRight",
        _ => $"DPad0x{mask:X}"
    };

    private static string FormatStick(byte bit) => bit switch
    {
        STICK_L_UP => "LSUp",
        STICK_L_DOWN => "LSDown",
        STICK_L_LEFT => "LSLeft",
        STICK_L_RIGHT => "LSRight",
        STICK_R_UP => "RSUp",
        STICK_R_DOWN => "RSDown",
        STICK_R_LEFT => "RSLeft",
        STICK_R_RIGHT => "RSRight",
        _ => $"Stick0x{bit:X2}"
    };

    private static bool TryParseDPad(string t, out ushort bit)
    {
        bit = 0;
        if (Is(t, "DPadUp", "DPUp", "DP-Up", "PadUp", "HatUp", "POVUp", "Up"))
        { bit = DPAD_UP; return true; }
        if (Is(t, "DPadDown", "DPDown", "DP-Down", "PadDown", "HatDown", "POVDown", "Down"))
        { bit = DPAD_DOWN; return true; }
        if (Is(t, "DPadLeft", "DPLeft", "DP-Left", "PadLeft", "HatLeft", "POVLeft", "Left"))
        { bit = DPAD_LEFT; return true; }
        if (Is(t, "DPadRight", "DPRight", "DP-Right", "PadRight", "HatRight", "POVRight", "Right"))
        { bit = DPAD_RIGHT; return true; }
        return false;

        static bool Is(string value, params string[] names)
        {
            foreach (var n in names)
            {
                if (value.Equals(n, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    private static bool TryParseStick(string t, out byte bit)
    {
        bit = 0;
        // Left stick
        if (Is(t, "LSUp", "LeftStickUp", "LStickUp", "LeftStick_Up")) { bit = STICK_L_UP; return true; }
        if (Is(t, "LSDown", "LeftStickDown", "LStickDown", "LeftStick_Down")) { bit = STICK_L_DOWN; return true; }
        if (Is(t, "LSLeft", "LeftStickLeft", "LStickLeft", "LeftStick_Left")) { bit = STICK_L_LEFT; return true; }
        if (Is(t, "LSRight", "LeftStickRight", "LStickRight", "LeftStick_Right")) { bit = STICK_L_RIGHT; return true; }
        // Right stick
        if (Is(t, "RSUp", "RightStickUp", "RStickUp", "RightStick_Up")) { bit = STICK_R_UP; return true; }
        if (Is(t, "RSDown", "RightStickDown", "RStickDown", "RightStick_Down")) { bit = STICK_R_DOWN; return true; }
        if (Is(t, "RSLeft", "RightStickLeft", "RStickLeft", "RightStick_Left")) { bit = STICK_R_LEFT; return true; }
        if (Is(t, "RSRight", "RightStickRight", "RStickRight", "RightStick_Right")) { bit = STICK_R_RIGHT; return true; }
        return false;

        static bool Is(string value, params string[] names)
        {
            foreach (var n in names)
            {
                if (value.Equals(n, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    private static bool TryParseDigital(string t, out ushort mask)
    {
        mask = 0;
        // Common aliases (D-pad is handled by TryParseDPad)
        if (t.Equals("A", StringComparison.OrdinalIgnoreCase)) { mask = A; return true; }
        if (t.Equals("B", StringComparison.OrdinalIgnoreCase)) { mask = B; return true; }
        if (t.Equals("X", StringComparison.OrdinalIgnoreCase)) { mask = X; return true; }
        if (t.Equals("Y", StringComparison.OrdinalIgnoreCase)) { mask = Y; return true; }
        if (t.Equals("LB", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("LeftShoulder", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("L1", StringComparison.OrdinalIgnoreCase))
        { mask = LEFT_SHOULDER; return true; }
        if (t.Equals("RB", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("RightShoulder", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("R1", StringComparison.OrdinalIgnoreCase))
        { mask = RIGHT_SHOULDER; return true; }
        if (t.Equals("Start", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Menu", StringComparison.OrdinalIgnoreCase))
        { mask = START; return true; }
        if (t.Equals("Back", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Select", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("View", StringComparison.OrdinalIgnoreCase))
        { mask = BACK; return true; }
        if (t.Equals("LeftThumb", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("LS", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("L3", StringComparison.OrdinalIgnoreCase))
        { mask = LEFT_THUMB; return true; }
        if (t.Equals("RightThumb", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("RS", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("R3", StringComparison.OrdinalIgnoreCase))
        { mask = RIGHT_THUMB; return true; }
        return false;
    }
}

}
