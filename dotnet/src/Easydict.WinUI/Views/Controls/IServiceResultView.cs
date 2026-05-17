using Easydict.TranslationService.Models;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Views.Controls;

public interface IServiceResultView
{
    ServiceQueryResult? ServiceResult { get; set; }

    FrameworkElement Element { get; }

    /// <summary>
    /// Root whose ActualTheme/RequestedTheme drives code-created resource resolution.
    /// </summary>
    FrameworkElement? ThemeRoot { get; set; }

    FrameworkElement HeaderPanel { get; }

    FrameworkElement? ActionButtonsPanel { get; }

    bool IsMinimalRenderer { get; }

    HashSet<string>? AlreadyShownPhonetics { get; set; }

    event EventHandler<ServiceQueryResult>? CollapseToggled;

    event EventHandler<ServiceQueryResult>? QueryRequested;

    event EventHandler<ServiceQueryResult>? FoundryLocalStartRequested;

    void RefreshDemotionState();

    void RefreshThemeChrome()
    {
    }

    IEnumerable<string> GetDisplayedPhoneticKeys();

    void Cleanup();
}
