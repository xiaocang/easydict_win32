using Easydict.TranslationService.Models;
using Microsoft.UI.Xaml;

namespace Easydict.WinUI.Views.Controls;

public interface IServiceResultView
{
    ServiceQueryResult? ServiceResult { get; set; }

    FrameworkElement Element { get; }

    FrameworkElement HeaderPanel { get; }

    FrameworkElement? ActionButtonsPanel { get; }

    bool IsMinimalRenderer { get; }

    HashSet<string>? AlreadyShownPhonetics { get; set; }

    event EventHandler<ServiceQueryResult>? CollapseToggled;

    event EventHandler<ServiceQueryResult>? QueryRequested;

    void RefreshDemotionState();

    void RefreshThemeChrome()
    {
    }

    IEnumerable<string> GetDisplayedPhoneticKeys();

    void Cleanup();
}
