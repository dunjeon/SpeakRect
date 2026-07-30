using System;
using System.Collections.Generic;
using System.Linq;

namespace SpeakRect
{
    /// <summary>
    /// System-level action a custom global binding can fire.
    /// Independent of SpeakRect OCR/overlay features.
    /// </summary>
    public enum CustomActionKind
    {
        None = 0,

        // Mouse — one-shot
        MouseLeftClick,
        MouseRightClick,
        MouseMiddleClick,
        MouseDoubleClick,
        MouseLeftDown,
        MouseLeftUp,
        MouseRightDown,
        MouseRightUp,

        // Mouse — hold / continuous (digital d-pad, stick tilt, or keyboard auto-repeat)
        MouseMoveUp,
        MouseMoveDown,
        MouseMoveLeft,
        MouseMoveRight,
        ScrollUp,
        ScrollDown,

        // Mouse — full analog stick (gamepad binding is implicit; pad column shows stick)
        MouseLeftStick,
        MouseRightStick,

        /// <summary>
        /// Send an arbitrary key chord (Arg = e.g. <c>Win+=</c>, <c>Ctrl+C</c>).
        /// Primary non-mouse custom action — map any hotkey onto gamepad/keyboard input.
        /// </summary>
        KeyTap,

        // Legacy kinds: still execute if present in old ini (SystemInput.ExecuteOnce);
        // not offered in the Key Map UI. Keep until a deliberate migration removes them.
        WinMinimize,
        WinMaximize,
        WinRestore,
        WinClose,
        ShowDesktop,
        AltTab,
        VolumeUp,
        VolumeDown,
        VolumeMute,
        MediaPlayPause,
        MediaNext,
        MediaPrev,
    }

    /// <summary>
    /// One user-defined global binding: keyboard and/or gamepad → system action.
    /// </summary>
    public sealed class CustomHotkeyBinding
    {
        public const int MaxBindings = 32;

        /// <summary>Stable id, e.g. <c>Custom0</c>. Used for conflict maps and hotkey ids.</summary>
        public string Id { get; set; } = "";

        /// <summary>Display name in the Key Map (defaults from action if blank).</summary>
        public string Label { get; set; } = "";

        public HotkeyChord Keyboard { get; set; }

        public GamepadButton Gamepad { get; set; }

        public CustomActionKind Action { get; set; }

        /// <summary>
        /// Optional arg: key chord for <see cref="CustomActionKind.KeyTap"/>,
        /// or mouse speed (pixels/tick) for move/stick actions.
        /// </summary>
        public string Arg { get; set; } = "";

        public string DisplayLabel =>
            !string.IsNullOrWhiteSpace(Label)
                ? Label.Trim()
                : CustomActionCatalog.DefaultLabel(Action, Arg);

        public bool IsEmpty =>
            Action == CustomActionKind.None && Keyboard.IsEmpty && Gamepad.IsEmpty;

        /// <summary>True if this action should re-fire while the pad control is held.</summary>
        public bool IsContinuous => CustomActionCatalog.IsContinuous(Action);

        /// <summary>True if the action uses a whole analog stick (implicit gamepad source).</summary>
        public bool UsesAnalogStick =>
            Action is CustomActionKind.MouseLeftStick or CustomActionKind.MouseRightStick;

        public CustomHotkeyBinding Clone() => new()
        {
            Id = Id,
            Label = Label,
            Keyboard = Keyboard,
            Gamepad = Gamepad,
            Action = Action,
            Arg = Arg ?? ""
        };
    }

    /// <summary>UI labels and helpers for <see cref="CustomActionKind"/>.</summary>
    public static class CustomActionCatalog
    {
        public readonly record struct ActionInfo(
            CustomActionKind Kind,
            string Label,
            string Group,
            string Hint,
            bool NeedsKeyArg,
            bool Continuous);

        /// <summary>Actions shown in the Add/Edit custom combo (mouse + freeform hotkey).</summary>
        public static readonly ActionInfo[] All =
        {
            // Freeform: user types/captures any chord, then binds gamepad/keyboard input on the grid
            new(CustomActionKind.KeyTap, "Send hotkey…", "Hotkey",
                "Click the gold box and press the chord (same as Key Map). " +
                "Then bind gamepad/keyboard on the grid to fire it.",
                true, false),

            new(CustomActionKind.MouseLeftClick, "Mouse: Left click", "Mouse",
                "Single left button click", false, false),
            new(CustomActionKind.MouseRightClick, "Mouse: Right click", "Mouse",
                "Single right button click", false, false),
            new(CustomActionKind.MouseMiddleClick, "Mouse: Middle click", "Mouse",
                "Middle button click", false, false),
            new(CustomActionKind.MouseDoubleClick, "Mouse: Double click", "Mouse",
                "Two left clicks", false, false),
            new(CustomActionKind.MouseLeftDown, "Mouse: Left down", "Mouse",
                "Press left button (hold until Left up)", false, false),
            new(CustomActionKind.MouseLeftUp, "Mouse: Left up", "Mouse",
                "Release left button", false, false),
            new(CustomActionKind.MouseRightDown, "Mouse: Right down", "Mouse",
                "Press right button", false, false),
            new(CustomActionKind.MouseRightUp, "Mouse: Right up", "Mouse",
                "Release right button", false, false),

            new(CustomActionKind.MouseMoveUp, "Mouse: Move up", "Mouse move",
                "Cursor up while held. Click Action column to set speed.", false, true),
            new(CustomActionKind.MouseMoveDown, "Mouse: Move down", "Mouse move",
                "Cursor down while held. Click Action column to set speed.", false, true),
            new(CustomActionKind.MouseMoveLeft, "Mouse: Move left", "Mouse move",
                "Cursor left while held. Click Action column to set speed.", false, true),
            new(CustomActionKind.MouseMoveRight, "Mouse: Move right", "Mouse move",
                "Cursor right while held. Click Action column to set speed.", false, true),
            new(CustomActionKind.ScrollUp, "Mouse: Scroll up", "Mouse move",
                "Wheel up while held", false, true),
            new(CustomActionKind.ScrollDown, "Mouse: Scroll down", "Mouse move",
                "Wheel down while held", false, true),

            new(CustomActionKind.MouseLeftStick, "Mouse: Left stick", "Stick mouse",
                "Left stick → cursor. Click Action column to set speed.",
                false, true),
            new(CustomActionKind.MouseRightStick, "Mouse: Right stick", "Stick mouse",
                "Right stick → cursor. Click Action column to set speed.",
                false, true),
        };

        public static string DefaultLabel(CustomActionKind kind, string? arg)
        {
            if (kind == CustomActionKind.KeyTap && !string.IsNullOrWhiteSpace(arg))
                return $"Send {arg.Trim()}";
            var info = All.FirstOrDefault(a => a.Kind == kind);
            string baseLabel = info.Kind == kind ? info.Label : kind.ToString();
            if (HasEditableSpeed(kind))
            {
                float spd = SystemInput.ParseSpeed(arg, SystemInput.DefaultMouseSpeed);
                return $"{baseLabel} · spd {SystemInput.FormatSpeed(spd)}";
            }
            return baseLabel;
        }

        /// <summary>True for move/stick actions whose Arg is mouse speed.</summary>
        public static bool HasEditableSpeed(CustomActionKind kind) =>
            kind is CustomActionKind.MouseMoveUp or CustomActionKind.MouseMoveDown
                or CustomActionKind.MouseMoveLeft or CustomActionKind.MouseMoveRight
                or CustomActionKind.MouseLeftStick or CustomActionKind.MouseRightStick;

        public static bool IsContinuous(CustomActionKind kind) =>
            kind is CustomActionKind.MouseMoveUp or CustomActionKind.MouseMoveDown
                or CustomActionKind.MouseMoveLeft or CustomActionKind.MouseMoveRight
                or CustomActionKind.ScrollUp or CustomActionKind.ScrollDown
                or CustomActionKind.MouseLeftStick or CustomActionKind.MouseRightStick;

        public static bool TryParse(string? raw, out CustomActionKind kind)
        {
            kind = CustomActionKind.None;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            return Enum.TryParse(raw.Trim(), ignoreCase: true, out kind)
                   && kind != CustomActionKind.None;
        }

        public static string ToIniString(CustomActionKind kind) =>
            kind == CustomActionKind.None ? "" : kind.ToString();

        /// <summary>
        /// Virtual gamepad display for stick-mouse rows (not a real button bit).
        /// </summary>
        public static string StickSourceDisplay(CustomActionKind kind) =>
            kind switch
            {
                CustomActionKind.MouseLeftStick => "LStick",
                CustomActionKind.MouseRightStick => "RStick",
                _ => ""
            };
    }

    /// <summary>
    /// Flattened view used by the Key Map grid for both built-in and custom rows.
    /// </summary>
    public readonly record struct MapRowRef(
        string Id,
        string Label,
        string Group,
        bool IsGlobal,
        bool IsCustom,
        Func<AppSettings, HotkeyChord> Getter,
        Action<AppSettings, HotkeyChord> Setter,
        Func<AppSettings, GamepadButton> GamepadGetter,
        Action<AppSettings, GamepadButton> GamepadSetter,
        CustomHotkeyBinding? Custom);

    public static class HotkeyMapModel
    {
        /// <summary>
        /// Built-in SpeakRect rows + user custom rows for the Key Map UI.
        /// </summary>
        public static List<MapRowRef> BuildRows(AppSettings s)
        {
            var list = new List<MapRowRef>(AppSettings.HotkeyMapRows.Length + s.CustomHotkeys.Count);
            foreach (var row in AppSettings.HotkeyMapRows)
            {
                list.Add(new MapRowRef(
                    row.Id, row.Label, row.Group, row.IsGlobal, false,
                    row.Getter, row.Setter, row.GamepadGetter, row.GamepadSetter,
                    null));
            }

            foreach (var c in s.CustomHotkeys)
            {
                var binding = c;
                list.Add(new MapRowRef(
                    binding.Id,
                    binding.DisplayLabel,
                    "Custom",
                    true,
                    true,
                    _ => binding.Keyboard,
                    (_, chord) =>
                    {
                        binding.Keyboard = chord;
                    },
                    _ => binding.Gamepad,
                    (_, btn) =>
                    {
                        binding.Gamepad = btn;
                    },
                    binding));
            }
            return list;
        }
    }
}
