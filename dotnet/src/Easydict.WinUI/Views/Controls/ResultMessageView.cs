using Easydict.WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Views.Controls;

/// <summary>Native, accessible feedback shared by every result host.</summary>
public sealed class ResultMessageView : InfoBar
{
    private DispatcherQueueTimer? _timer;
    public ResultMessageView() => AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);

    public void Show(string resourceKey, bool error, bool transient = false)
    {
        _timer?.Stop();
        Message = LocalizationService.Instance.GetString(resourceKey);
        Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        IsClosable = true;
        IsOpen = true;
        FrameworkElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(
            AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent,
            Message, "ResultFeedback");
        if (!transient) return;
        _timer ??= DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick -= OnElapsed;
        _timer.Tick += OnElapsed;
        _timer.Start();
    }

    private void OnElapsed(DispatcherQueueTimer sender, object args) => IsOpen = false;

    public void Cleanup()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnElapsed;
            _timer = null;
        }
        IsOpen = false;
    }
}

public sealed class ResultRenderingEventArgs(bool isFallback) : EventArgs
{
    public bool IsFallback { get; } = isFallback;
}
