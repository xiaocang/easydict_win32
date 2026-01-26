namespace Easydict.WinUI.Services;

/// <summary>
/// Result of parsing a hotkey string.
/// </summary>
public sealed record HotkeyParseResult(bool IsValid, uint Modifiers, uint VirtualKey, string? ErrorMessage = null);

/// <summary>
/// Parses hotkey strings like "Ctrl+Alt+M" into modifier flags and virtual key codes.
/// </summary>
public static class HotkeyParser
{
    // Modifier flags (matching Win32 RegisterHotKey)
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    /// <summary>
    /// Parse a hotkey string into modifiers and virtual key code.
    /// </summary>
    /// <param name="hotkeyString">Hotkey string like "Ctrl+Alt+M"</param>
    /// <returns>Parse result with modifiers and virtual key code</returns>
    public static HotkeyParseResult Parse(string? hotkeyString)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString))
        {
            return new HotkeyParseResult(false, 0, 0, "Hotkey string is empty");
        }

        var parts = hotkeyString.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return new HotkeyParseResult(false, 0, 0, "No hotkey parts found");
        }

        uint modifiers = 0;
        uint virtualKey = 0;
        bool foundKey = false;

        foreach (var part in parts)
        {
            var upperPart = part.ToUpperInvariant();

            // Check for modifiers
            if (upperPart is "CTRL" or "CONTROL")
            {
                modifiers |= MOD_CONTROL;
            }
            else if (upperPart is "ALT")
            {
                modifiers |= MOD_ALT;
            }
            else if (upperPart is "SHIFT")
            {
                modifiers |= MOD_SHIFT;
            }
            else if (upperPart is "WIN" or "WINDOWS")
            {
                modifiers |= MOD_WIN;
            }
            else
            {
                // This should be the key
                if (foundKey)
                {
                    return new HotkeyParseResult(false, 0, 0, $"Multiple keys found: already had a key, then found '{part}'");
                }

                var vk = MapKeyToVirtualKey(upperPart);
                if (vk == 0)
                {
                    return new HotkeyParseResult(false, 0, 0, $"Unknown key: '{part}'");
                }

                virtualKey = vk;
                foundKey = true;
            }
        }

        if (!foundKey)
        {
            return new HotkeyParseResult(false, 0, 0, "No key found in hotkey string");
        }

        return new HotkeyParseResult(true, modifiers, virtualKey);
    }

    /// <summary>
    /// Add Shift modifier to an existing hotkey result.
    /// Used for deriving toggle hotkeys from base hotkeys.
    /// </summary>
    public static HotkeyParseResult AddShiftModifier(HotkeyParseResult baseResult)
    {
        if (!baseResult.IsValid)
        {
            return baseResult;
        }

        return new HotkeyParseResult(true, baseResult.Modifiers | MOD_SHIFT, baseResult.VirtualKey);
    }

    /// <summary>
    /// Map a key name to its virtual key code.
    /// </summary>
    private static uint MapKeyToVirtualKey(string keyName)
    {
        // Single letter keys A-Z
        if (keyName.Length == 1 && keyName[0] >= 'A' && keyName[0] <= 'Z')
        {
            return (uint)keyName[0]; // VK_A (0x41) through VK_Z (0x5A)
        }

        // Number keys 0-9
        if (keyName.Length == 1 && keyName[0] >= '0' && keyName[0] <= '9')
        {
            return (uint)keyName[0]; // VK_0 (0x30) through VK_9 (0x39)
        }

        // Function keys F1-F24
        if (keyName.StartsWith('F') && keyName.Length >= 2)
        {
            if (int.TryParse(keyName[1..], out int fNum) && fNum >= 1 && fNum <= 24)
            {
                return (uint)(0x70 + fNum - 1); // VK_F1 (0x70) through VK_F24 (0x87)
            }
        }

        // Special keys
        return keyName switch
        {
            "SPACE" => 0x20,      // VK_SPACE
            "ENTER" or "RETURN" => 0x0D, // VK_RETURN
            "TAB" => 0x09,        // VK_TAB
            "ESC" or "ESCAPE" => 0x1B, // VK_ESCAPE
            "BACKSPACE" or "BACK" => 0x08, // VK_BACK
            "DELETE" or "DEL" => 0x2E, // VK_DELETE
            "INSERT" or "INS" => 0x2D, // VK_INSERT
            "HOME" => 0x24,       // VK_HOME
            "END" => 0x23,        // VK_END
            "PAGEUP" or "PGUP" => 0x21, // VK_PRIOR
            "PAGEDOWN" or "PGDN" => 0x22, // VK_NEXT
            "UP" => 0x26,         // VK_UP
            "DOWN" => 0x28,       // VK_DOWN
            "LEFT" => 0x25,       // VK_LEFT
            "RIGHT" => 0x27,      // VK_RIGHT
            "NUMPAD0" => 0x60,    // VK_NUMPAD0
            "NUMPAD1" => 0x61,
            "NUMPAD2" => 0x62,
            "NUMPAD3" => 0x63,
            "NUMPAD4" => 0x64,
            "NUMPAD5" => 0x65,
            "NUMPAD6" => 0x66,
            "NUMPAD7" => 0x67,
            "NUMPAD8" => 0x68,
            "NUMPAD9" => 0x69,    // VK_NUMPAD9
            "MULTIPLY" => 0x6A,   // VK_MULTIPLY
            "ADD" => 0x6B,        // VK_ADD
            "SUBTRACT" => 0x6D,   // VK_SUBTRACT
            "DECIMAL" => 0x6E,    // VK_DECIMAL
            "DIVIDE" => 0x6F,     // VK_DIVIDE
            "PRINTSCREEN" or "PRTSC" => 0x2C, // VK_SNAPSHOT
            "SCROLLLOCK" => 0x91, // VK_SCROLL
            "PAUSE" => 0x13,      // VK_PAUSE
            "CAPSLOCK" => 0x14,   // VK_CAPITAL
            "NUMLOCK" => 0x90,    // VK_NUMLOCK
            _ => 0 // Unknown key
        };
    }
}
