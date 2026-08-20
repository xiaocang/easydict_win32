namespace Easydict.WinUI.Services;

internal static class HotkeyShowRequestTracker
{
    internal static int Begin(ref int generation)
        => Interlocked.Increment(ref generation);

    internal static bool IsCurrent(ref int generation, int requestGeneration)
        => Volatile.Read(ref generation) == requestGeneration;
}
