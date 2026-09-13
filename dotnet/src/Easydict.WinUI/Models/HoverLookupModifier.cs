namespace Easydict.WinUI.Models;

/// <summary>
/// Trigger key for hover word lookup: the key that has to be held while the pointer rests on a word.
/// <see cref="None"/> means plain hovering triggers a lookup.
/// </summary>
public enum HoverLookupModifier
{
    None,
    Ctrl,
    Shift,
    Alt,
}

/// <summary>
/// Parsing and virtual-key helpers for <see cref="HoverLookupModifier"/>.
/// Pure logic (no Win32), so it is unit-testable and safe to reference from the hook thread.
/// </summary>
public static class HoverLookupModifierExtensions
{
    public const HoverLookupModifier Default = HoverLookupModifier.Ctrl;

    private const uint VK_SHIFT = 0x10;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_MENU = 0x12;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_LMENU = 0xA4;
    private const uint VK_RMENU = 0xA5;

    /// <summary>
    /// Parse the persisted enum name (case-insensitive). Unknown or empty values map to <see cref="Default"/>.
    /// </summary>
    public static HoverLookupModifier Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        return Enum.TryParse<HoverLookupModifier>(value.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : Default;
    }

    /// <summary>
    /// Whether the given virtual-key code (generic, left or right variant) is this modifier.
    /// <see cref="HoverLookupModifier.None"/> never matches.
    /// </summary>
    public static bool MatchesVirtualKey(this HoverLookupModifier modifier, uint vkCode)
    {
        return modifier switch
        {
            HoverLookupModifier.Ctrl => vkCode is VK_CONTROL or VK_LCONTROL or VK_RCONTROL,
            HoverLookupModifier.Shift => vkCode is VK_SHIFT or VK_LSHIFT or VK_RSHIFT,
            HoverLookupModifier.Alt => vkCode is VK_MENU or VK_LMENU or VK_RMENU,
            _ => false,
        };
    }

    /// <summary>
    /// Left/right virtual keys to poll (e.g. with GetAsyncKeyState) to check whether this
    /// modifier is physically held. Empty for <see cref="HoverLookupModifier.None"/>.
    /// </summary>
    public static IReadOnlyList<int> GetPollVirtualKeys(this HoverLookupModifier modifier)
    {
        return modifier switch
        {
            HoverLookupModifier.Ctrl => [(int)VK_LCONTROL, (int)VK_RCONTROL],
            HoverLookupModifier.Shift => [(int)VK_LSHIFT, (int)VK_RSHIFT],
            HoverLookupModifier.Alt => [(int)VK_LMENU, (int)VK_RMENU],
            _ => [],
        };
    }

    /// <summary>
    /// Whether the virtual-key code is any modifier key (Shift/Ctrl/Alt/Win, generic or sided).
    /// Modifier presses never dismiss the hover popup; other keys do.
    /// </summary>
    public static bool IsAnyModifierVirtualKey(uint vkCode)
    {
        return vkCode is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN
            or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU;
    }
}
