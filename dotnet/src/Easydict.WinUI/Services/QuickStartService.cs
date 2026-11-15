using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>Single entry shared by first launch, About, and the tray menu.</summary>
public static class QuickStartService
{
    public static void Open(QuickStartEntryPoint entryPoint = QuickStartEntryPoint.Help)
        => App.OpenQuickStart(entryPoint);
}
