using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpeakRect
{
/// <summary>
/// A key chord for global or local hotkeys: optional Ctrl/Alt/Shift/Win + a key.
/// Serialized as e.g. <c>Shift+Tab</c>, <c>Ctrl+Shift+B</c>, or bare <c>R</c>.
/// </summary>
public readonly struct HotkeyChord : IEquatable<HotkeyChord>
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public uint Modifiers { get; }
    public Keys Key { get; }

    public HotkeyChord(uint modifiers, Keys key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public bool IsEmpty => Key == Keys.None;

    public bool IsGlobalCandidate =>
        Key != Keys.None && Key is not (Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin);

    public string ToIniString()
    {
        if (Key == Keys.None)
            return "";

        var parts = new List<string>(4);
        if ((Modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(FormatKey(Key));
        return string.Join("+", parts);
    }

    public override string ToString() => ToIniString();

    public static bool TryParse(string? raw, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string[] tokens = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        uint mods = 0;
        Keys key = Keys.None;

        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i];
            bool isLast = i == tokens.Length - 1;

            if (!isLast || IsModifierToken(t))
            {
                if (IsCtrlToken(t)) { mods |= MOD_CONTROL; continue; }
                if (IsAltToken(t)) { mods |= MOD_ALT; continue; }
                if (IsShiftToken(t)) { mods |= MOD_SHIFT; continue; }
                if (IsWinToken(t)) { mods |= MOD_WIN; continue; }
                if (!isLast)
                    return false;
            }

            if (!TryParseKey(t, out key) || key == Keys.None)
                return false;
        }

        if (key == Keys.None)
            return false;

        chord = new HotkeyChord(mods, key);
        return true;
    }

    public static HotkeyChord ParseOrDefault(string? raw, HotkeyChord fallback) =>
        TryParse(raw, out var c) ? c : fallback;

    /// <summary>
    /// Ini load helper: missing key → <paramref name="fallbackIfMissing"/>;
    /// blank / None / Off / Unbound / - → empty (unbound);
    /// valid chord → that chord; invalid text → fallback.
    /// </summary>
    public static HotkeyChord ParseFromIni(string? raw, HotkeyChord fallbackIfMissing)
    {
        // Key not present in the file at all
        if (raw == null)
            return fallbackIfMissing;

        string t = raw.Trim();
        if (t.Length == 0 || IsUnboundToken(t))
            return default; // explicitly unbound

        return TryParse(t, out var c) ? c : fallbackIfMissing;
    }

    /// <summary>True for blank-ish tokens that mean “no hotkey”.</summary>
    public static bool IsUnboundToken(string t) =>
        t.Equals("None", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Off", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Unbound", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Clear", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("-", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("—", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("na", StringComparison.OrdinalIgnoreCase);

    public bool Equals(HotkeyChord other) =>
        Modifiers == other.Modifiers && Key == other.Key;

    public override bool Equals(object? obj) =>
        obj is HotkeyChord other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Modifiers, (int)Key);

    public static bool operator ==(HotkeyChord a, HotkeyChord b) => a.Equals(b);
    public static bool operator !=(HotkeyChord a, HotkeyChord b) => !a.Equals(b);

    private static bool IsModifierToken(string t) =>
        IsCtrlToken(t) || IsAltToken(t) || IsShiftToken(t) || IsWinToken(t);

    private static bool IsCtrlToken(string t) =>
        t.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Ctl", StringComparison.OrdinalIgnoreCase);

    private static bool IsAltToken(string t) =>
        t.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Menu", StringComparison.OrdinalIgnoreCase);

    private static bool IsShiftToken(string t) =>
        t.Equals("Shift", StringComparison.OrdinalIgnoreCase);

    private static bool IsWinToken(string t) =>
        t.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("Meta", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseKey(string token, out Keys key)
    {
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        string t = token.Trim();

        // Single digit / letter
        if (t.Length == 1)
        {
            char c = char.ToUpperInvariant(t[0]);
            if (c is >= '0' and <= '9')
            {
                key = Keys.D0 + (c - '0');
                return true;
            }
            if (c is >= 'A' and <= 'Z')
            {
                key = (Keys)c; // Keys.A == 65
                return true;
            }
        }

        // Common aliases
        if (t.Equals("Esc", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Escape", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Escape;
            return true;
        }
        if (t.Equals("Return", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Enter", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Enter;
            return true;
        }
        if (t.Equals("Space", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Spacebar", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Space;
            return true;
        }
        if (t.Equals("PgUp", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("PageUp", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.PageUp;
            return true;
        }
        if (t.Equals("PgDn", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("PageDown", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.PageDown;
            return true;
        }

        // OEM / punctuation (US layout names + symbols)
        if (t is "=" or "+" ||
            t.Equals("Equals", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Equal", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Plus", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("OemPlus", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Oemplus", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Oemplus;
            return true;
        }
        if (t is "-" or "_" ||
            t.Equals("Minus", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("OemMinus", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.OemMinus;
            return true;
        }
        if (t is "[" or "{" || t.Equals("OemOpenBrackets", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.OemOpenBrackets;
            return true;
        }
        if (t is "]" or "}" || t.Equals("OemCloseBrackets", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Oem6", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.OemCloseBrackets;
            return true;
        }
        if (t is ";" or ":" || t.Equals("Oem1", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("OemSemicolon", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Oem1;
            return true;
        }
        if (t is "'" or "\"" || t.Equals("Oem7", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("OemQuotes", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Oem7;
            return true;
        }
        if (t is "," or "<" || t.Equals("Oemcomma", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("OemComma", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Oemcomma;
            return true;
        }
        if (t is "." or ">" || t.Equals("OemPeriod", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.OemPeriod;
            return true;
        }
        if (t is "/" or "?" || t.Equals("Oem2", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("OemQuestion", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Oem2;
            return true;
        }
        if (t is "`" or "~" || t.Equals("Oem3", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Oemtilde", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Oem3;
            return true;
        }
        if (t is "\\" or "|" || t.Equals("Oem5", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("OemPipe", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("OemBackslash", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Oem5;
            return true;
        }

        if (Enum.TryParse(t, ignoreCase: true, out Keys parsed) &&
            parsed != Keys.None)
        {
            // Reject pure modifier key names used as the main key
            if (parsed is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or
                Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or
                Keys.RControlKey or Keys.LMenu or Keys.RMenu or
                Keys.LWin or Keys.RWin)
                return false;

            key = parsed;
            return true;
        }

        return false;
    }

    private static string FormatKey(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9)
            return ((char)('0' + (key - Keys.D0))).ToString();
        if (key >= Keys.A && key <= Keys.Z)
            return key.ToString(); // A..Z
        return key switch
        {
            Keys.Escape => "Esc",
            Keys.Enter => "Enter",
            Keys.PageUp => "PageUp",
            Keys.PageDown => "PageDown",
            Keys.Space => "Space",
            Keys.Oemplus => "=",
            Keys.OemMinus => "-",
            Keys.OemOpenBrackets => "[",
            Keys.OemCloseBrackets => "]",
            Keys.Oem1 => ";",
            Keys.Oem7 => "'",
            Keys.Oemcomma => ",",
            Keys.OemPeriod => ".",
            Keys.Oem2 => "/",
            Keys.Oem3 => "`",
            Keys.Oem5 => "\\",
            _ => key.ToString()
        };
    }

    /// <summary>Build a chord from a WinForms KeyEventArgs (capture UI).</summary>
    public static HotkeyChord FromKeyEvent(KeyEventArgs e)
    {
        return FromVirtualKey((int)e.KeyCode, e.Control, e.Alt, e.Shift, IsWinKeyDown());
    }

    /// <summary>
    /// Build a chord from a virtual-key code + live modifier state
    /// (low-level hook capture — required for Win combos).
    /// </summary>
    public static HotkeyChord FromVirtualKey(
        int vk,
        bool control = false,
        bool alt = false,
        bool shift = false,
        bool win = false)
    {
        // Ignore pure modifier key-downs; wait for the main key
        if (IsModifierVk(vk))
            return default;

        Keys key = (Keys)(vk & 0xFF);
        if (key == Keys.None)
            return default;

        uint mods = 0;
        if (control || IsVkDown(VK_CONTROL) || IsVkDown(VK_LCONTROL) || IsVkDown(VK_RCONTROL))
            mods |= MOD_CONTROL;
        if (alt || IsVkDown(VK_MENU) || IsVkDown(VK_LMENU) || IsVkDown(VK_RMENU))
            mods |= MOD_ALT;
        if (shift || IsVkDown(VK_SHIFT) || IsVkDown(VK_LSHIFT) || IsVkDown(VK_RSHIFT))
            mods |= MOD_SHIFT;
        if (win || IsWinKeyDown())
            mods |= MOD_WIN;

        return new HotkeyChord(mods, key);
    }

    /// <summary>Sample modifiers from the keyboard right now + this vk as the main key.</summary>
    public static HotkeyChord FromHookVk(int vk) =>
        FromVirtualKey(vk, control: false, alt: false, shift: false, win: false);

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    /// <summary>True if either Windows key is currently down.</summary>
    public static bool IsWinKeyDown() =>
        IsVkDown(VK_LWIN) || IsVkDown(VK_RWIN);

    private static bool IsVkDown(int vk) =>
        (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsModifierVk(int vk) =>
        vk is VK_SHIFT or VK_CONTROL or VK_MENU
            or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL
            or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN
            or 0x5D; // Apps/menu key

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}

}
