using System.Runtime.InteropServices;

namespace Easydict.WinUI.Services;

internal static class CrashDiagnostics
{
    private static int _globalHandlersRegistered;

    internal static void RegisterGlobalHandlers()
    {
        if (Interlocked.Exchange(ref _globalHandlersRegistered, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    internal static bool IsProcessFatal(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is StackOverflowException
                or OutOfMemoryException
                or AccessViolationException
                or TypeInitializationException)
            {
                return true;
            }
        }

        return false;
    }

    internal static void Log(string message)
    {
        try
        {
            var entry = $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}";
            AppendToLog("debug.log", entry, "Easydict-debug.log");
        }
        catch
        {
            // Diagnostic logging must never throw.
        }
    }

    internal static void LogException(
        string source,
        Exception? exception,
        bool isTerminating,
        bool isHandled)
    {
        try
        {
            var entry = $"""
[{DateTime.UtcNow:O}] source={source}
pid={Environment.ProcessId}
os={RuntimeInformation.OSDescription}
runtime={RuntimeInformation.FrameworkDescription}
processArchitecture={RuntimeInformation.ProcessArchitecture}
isTerminating={isTerminating}
isHandled={isHandled}
exception={exception?.ToString() ?? "<null>"}

""";

            AppendToLog("debug.log", entry, "Easydict-debug.log");
            AppendToLog("crash.log", entry, "Easydict-crash.log");
        }
        catch
        {
            // Diagnostic logging must never throw.
        }
    }

    private static void OnUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        try
        {
            if (e.ExceptionObject is Exception exception)
            {
                LogException(
                    "AppDomain.CurrentDomain.UnhandledException",
                    exception,
                    e.IsTerminating,
                    isHandled: false);
                return;
            }

            Log($"[AppDomain.CurrentDomain.UnhandledException] Non-Exception object: {e.ExceptionObject}");
            LogException(
                "AppDomain.CurrentDomain.UnhandledException (non-Exception object)",
                exception: null,
                e.IsTerminating,
                isHandled: false);
        }
        catch
        {
            // AppDomain exception handling must never throw.
        }
    }

    private static void AppendToLog(string fileName, string entry, string fallbackFileName)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Easydict");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, fileName), entry);
            return;
        }
        catch
        {
            // Diagnostic logging must never throw.
        }

        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), fallbackFileName), entry);
        }
        catch
        {
            // Diagnostic logging must never throw.
        }
    }
}
