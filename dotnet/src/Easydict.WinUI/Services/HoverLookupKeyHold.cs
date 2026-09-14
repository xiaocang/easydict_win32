namespace Easydict.WinUI.Services;

/// <summary>Requires one continuous trigger-key press; repeated key-down events do not restart it.</summary>
internal sealed class HoverLookupKeyHold
{
    internal const int MinimumHoldMs = 50;

    private long _pressedAt;
    internal bool IsDown { get; private set; }

    internal void SetDown(bool isDown, long nowTicks)
    {
        if (isDown && !IsDown)
        {
            _pressedAt = nowTicks;
        }

        IsDown = isDown;
    }

    internal bool IsSatisfied(long nowTicks) => IsDown && nowTicks - _pressedAt >= MinimumHoldMs;
}
