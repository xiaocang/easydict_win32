using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
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

    /// <summary>
    /// Apply user-configurable appearance (result font size) to this item.
    /// Default no-op keeps the interface non-breaking for any other implementers.
    /// </summary>
    void ApplyAppearance(AppearanceSettings settings)
    {
    }

    IEnumerable<string> GetDisplayedPhoneticKeys();

    void Cleanup();
}
