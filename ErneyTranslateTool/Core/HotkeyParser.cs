using System;
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
}
