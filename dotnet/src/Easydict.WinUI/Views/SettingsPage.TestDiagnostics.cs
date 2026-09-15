#if WINUI_TEST
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace Easydict.WinUI.Views;

public sealed partial class SettingsPage
{
    private SettingsTabId _pendingRenderedTab;

    partial void QueueSettingsTabRenderProbe(SettingsTabId tabId)
    {
        CancelSettingsTabRenderProbe();
        _pendingRenderedTab = tabId;
        AutomationProperties.SetItemStatus(MainScrollViewer, string.Empty);
        CompositionTarget.Rendered += OnSettingsTabRendered;
    }

    private void OnSettingsTabRendered(object? sender, RenderedEventArgs args)
    {
        var timestamp = Stopwatch.GetTimestamp();
        CancelSettingsTabRenderProbe();
        if (_isUnloaded || _isTornDown || !_isInitialized) return;

        // Windows' performance counter is shared across processes. The UI test
        // compares this frame timestamp with its pre-click timestamp, excluding
        // the potentially slow UIA round trip used to observe the result.
        AutomationProperties.SetItemStatus(MainScrollViewer,
            $"RenderedSettingsTab:{_pendingRenderedTab}:{timestamp.ToString(CultureInfo.InvariantCulture)}");
    }

    partial void CancelSettingsTabRenderProbe()
    {
        CompositionTarget.Rendered -= OnSettingsTabRendered;
    }
}
#endif
