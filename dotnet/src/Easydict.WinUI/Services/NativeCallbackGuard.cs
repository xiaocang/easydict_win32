namespace Easydict.WinUI.Services;

internal static class NativeCallbackGuard
{
    internal static void Invoke(string source, Action? callback)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            callback();
        }
        catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
        {
            CrashDiagnostics.LogException(source, ex, isTerminating: false, isHandled: true);
        }
    }

    internal static void Invoke<T>(string source, Action<T>? callback, T argument)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            callback(argument);
        }
        catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
        {
            CrashDiagnostics.LogException(source, ex, isTerminating: false, isHandled: true);
        }
    }

    internal static TResult Invoke<TResult>(string source, Func<TResult>? callback, TResult fallback)
    {
        if (callback is null)
        {
            return fallback;
        }

        try
        {
            return callback();
        }
        catch (Exception ex) when (!CrashDiagnostics.IsProcessFatal(ex))
        {
            CrashDiagnostics.LogException(source, ex, isTerminating: false, isHandled: true);
            return fallback;
        }
    }
}
