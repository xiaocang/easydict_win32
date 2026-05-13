using System.Numerics;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Views.Controls;

internal static class ServiceResultViewHost
{
    public static IServiceResultView Create(
        ServiceQueryResult result,
        EventHandler<ServiceQueryResult> collapseToggled,
        EventHandler<ServiceQueryResult> queryRequested,
        FrameworkElement? themeRoot = null)
    {
        IServiceResultView control = MinimalThemeService.IsActive
            ? new MinimalServiceResultItem()
            : new ServiceResultItem();

        control.ThemeRoot = themeRoot;
        control.ServiceResult = result;
        control.CollapseToggled += collapseToggled;
        control.QueryRequested += queryRequested;
        return control;
    }

    public static IServiceResultView Add(
        ServiceQueryResult result,
        IList<IServiceResultView> controls,
        ItemsControl resultsPanel,
        EventHandler<ServiceQueryResult> collapseToggled,
        EventHandler<ServiceQueryResult> queryRequested,
        FrameworkElement? themeRoot = null)
    {
        var control = Create(result, collapseToggled, queryRequested, themeRoot ?? resultsPanel);
        controls.Add(control);
        resultsPanel.Items.Add(control.Element);
        return control;
    }

    public static bool NeedsThemeRebuild(IReadOnlyList<IServiceResultView> controls, bool minimal)
    {
        return controls.Count > 0 && controls.Any(control => control.IsMinimalRenderer != minimal);
    }

    public static void RebuildForCurrentTheme(
        IReadOnlyList<ServiceQueryResult> results,
        IList<IServiceResultView> controls,
        ItemsControl resultsPanel,
        EventHandler<ServiceQueryResult> collapseToggled,
        EventHandler<ServiceQueryResult> queryRequested,
        FrameworkElement? themeRoot = null)
    {
        Release(controls, resultsPanel, collapseToggled, queryRequested);

        foreach (var result in results)
        {
            Add(result, controls, resultsPanel, collapseToggled, queryRequested, themeRoot);
        }
    }

    public static void Release(
        IList<IServiceResultView> controls,
        ItemsControl resultsPanel,
        EventHandler<ServiceQueryResult> collapseToggled,
        EventHandler<ServiceQueryResult> queryRequested)
    {
        foreach (var control in controls)
        {
            control.CollapseToggled -= collapseToggled;
            control.QueryRequested -= queryRequested;
            control.Cleanup();
        }

        controls.Clear();
        resultsPanel.Items.Clear();
    }

    public static void Reorder(
        IReadOnlyList<ServiceQueryResult> results,
        IReadOnlyList<IServiceResultView> controls,
        ItemsControl resultsPanel,
        bool hideEmptySetting)
    {
        if (controls.Count == 0)
        {
            return;
        }

        var order = ServiceResultDemotionHelper.StablePartitionIndices(results, hideEmptySetting);

        var orderMatches = resultsPanel.Items.Count == controls.Count;
        for (int i = 0; orderMatches && i < order.Count; i++)
        {
            if (!ReferenceEquals(resultsPanel.Items[i], controls[order[i]].Element))
            {
                orderMatches = false;
            }
        }

        if (orderMatches)
        {
            return;
        }

        for (int i = 0; i < order.Count; i++)
        {
            var target = controls[order[i]].Element;
            var currentIndex = resultsPanel.Items.IndexOf(target);
            if (currentIndex == i)
            {
                continue;
            }

            if (currentIndex >= 0)
            {
                resultsPanel.Items.RemoveAt(currentIndex);
            }

            resultsPanel.Items.Insert(i, target);
        }
    }

    public static void UpdateStickyHeaders(
        IReadOnlyList<IServiceResultView> controls,
        FrameworkElement viewport)
    {
        if (MinimalThemeService.IsActive || controls.Count == 0)
        {
            return;
        }

        const double margin = 4.0;

        foreach (var control in controls)
        {
            var element = control.Element;
            var actionButtons = control.ActionButtonsPanel;
            if (element.Visibility != Visibility.Visible || actionButtons is null)
            {
                continue;
            }

            try
            {
                var transform = element.TransformToVisual(viewport);
                var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                var offsetY = point.Y < 0 ? Math.Abs(point.Y) : 0;
                var maxOffset = element.ActualHeight - control.HeaderPanel.ActualHeight - margin;
                offsetY = Math.Clamp(offsetY, 0, Math.Max(0, maxOffset));

                var translation = new Vector3(0, (float)offsetY, 0);
                control.HeaderPanel.Translation = translation;
                actionButtons.Translation = translation;
            }
            catch (Exception)
            {
                // TransformToVisual throws if element is mid-detach from the visual tree.
            }
        }
    }

    public static void UpdatePhoneticDeduplication(IEnumerable<IServiceResultView> controls)
    {
        var shownPhonetics = new HashSet<string>();

        foreach (var control in controls)
        {
            control.AlreadyShownPhonetics = shownPhonetics.Count > 0
                ? new HashSet<string>(shownPhonetics)
                : null;

            foreach (var key in control.GetDisplayedPhoneticKeys())
            {
                shownPhonetics.Add(key);
            }
        }
    }

    public static void RefreshThemeChrome(
        IEnumerable<IServiceResultView> controls,
        FrameworkElement? themeRoot = null)
    {
        foreach (var control in controls)
        {
            if (themeRoot is not null)
            {
                control.ThemeRoot = themeRoot;
            }

            control.RefreshThemeChrome();
        }
    }

    public static void Refresh(
        IReadOnlyList<IServiceResultView> controls,
        ServiceQueryResult result)
    {
        foreach (var control in controls)
        {
            if (ReferenceEquals(control.ServiceResult, result))
            {
                control.RefreshDemotionState();
                return;
            }
        }
    }
}
