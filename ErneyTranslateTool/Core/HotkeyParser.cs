using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ErneyTranslateTool.Core;

/// <summary>
/// Parses hotkey strings like "Ctrl+Shift+T" into Win32 modifier flags and virtual key codes.
/// </summary>
public static class HotkeyParser
{
    public const int MOD_ALT = 0x0001;
    public const int MOD_CONTROL = 0x0002;
    public const int MOD_SHIFT = 0x0004;
    public const int MOD_WIN = 0x0008;

    /// <summary>
    /// Try to parse a string like "Ctrl+Shift+T" into modifiers and virtual key code.
    /// </summary>
    public static bool TryParse(string? hotkey, out int modifiers, out int virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(hotkey))
            return false;

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        foreach (var raw in parts.Take(parts.Length - 1))
        {
            var token = raw.ToUpperInvariant();
            switch (token)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= MOD_CONTROL;
                    break;
                case "SHIFT":
                    modifiers |= MOD_SHIFT;
                    break;
                case "ALT":
                    modifiers |= MOD_ALT;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    return false;
            }
        }

        var keyName = parts[^1];
        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out var key))
            return false;

        virtualKey = KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    /// <summary>
    /// Render a WPF <see cref="ModifierKeys"/> + <see cref="Key"/> pair back
    /// into the canonical "Ctrl+Shift+T" string format used by
    /// <see cref="TryParse"/>. The HotkeyCaptureBox UserControl uses this
    /// to convert the user's key-press into a stored, parse-friendly value.
    /// Returns empty string if there's no real (non-modifier) key — there's
    /// nothing useful to serialise without one.
    /// </summary>
    public static string Format(ModifierKeys modifiers, Key key)
    {
        if (key == Key.None) return string.Empty;
        // Ignore the standalone modifier keys themselves — those land in the
        // modifiers bitmask, not the key slot.
        if (key is Key.LeftCtrl or Key.RightCtrl
                 or Key.LeftShift or Key.RightShift
                 or Key.LeftAlt or Key.RightAlt
                 or Key.LWin or Key.RWin)
            return string.Empty;

        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
