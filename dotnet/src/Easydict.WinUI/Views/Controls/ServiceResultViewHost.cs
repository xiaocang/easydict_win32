using System.Numerics;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Views.Controls;

internal static class ServiceResultViewHost
{
    public static IServiceResultView Create(
        ServiceQueryResult result,
        EventHandler<ServiceQueryResult> collapseToggled,
        EventHandler<ServiceQueryResult> queryRequested,
        FrameworkElement? themeRoot = null,
        EventHandler<ServiceQueryResult>? foundryLocalStartRequested = null)
    {
        IServiceResultView control = MinimalThemeService.IsActive
            ? new MinimalServiceResultItem()
            : new ServiceResultItem();

        control.ThemeRoot = themeRoot;
        control.CollapseToggled += collapseToggled;
        control.QueryRequested += queryRequested;
        if (foundryLocalStartRequested is not null)
        {
            control.FoundryLocalStartRequested += foundryLocalStartRequested;
        }
        control.ServiceResult = result;
        ApplyAutomationProperties(control, result);
        control.RefreshThemeChrome();
        control.ApplyAppearance(AppearanceService.CurrentSnapshot());
        return control;
    }

    public static IServiceResultView Add(
        ServiceQueryResult result,
        IList<IServiceResultView> controls,
        ItemsControl resultsPanel,
        EventHandler<ServiceQueryResult> collapseToggled,
        EventHandler<ServiceQueryResult> queryRequested,
        FrameworkElement? themeRoot = null,
        EventHandler<ServiceQueryResult>? foundryLocalStartRequested = null)
    {
        var control = Create(
            result,
            collapseToggled,
            queryRequested,
            themeRoot ?? resultsPanel,
            foundryLocalStartRequested);
        controls.Add(control);
        resultsPanel.Items.Add(control.Element);
        control.RefreshThemeChrome();
        return control;
    }

    private static void ApplyAutomationProperties(IServiceResultView control, ServiceQueryResult result)
    {
        var serviceId = string.IsNullOrWhiteSpace(result.ServiceId)
            ? "unknown"
            : result.ServiceId.Trim();
        var automationIdSuffix = string.Concat(
            serviceId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

        AutomationProperties.SetAutomationId(control.Element, $"ServiceResultItem_{automationIdSuffix}");
        AutomationProperties.SetName(control.Element, result.ServiceDisplayName);
        AutomationProperties.SetAutomationId(control.HeaderPanel, $"ServiceResultHeader_{automationIdSuffix}");
        AutomationProperties.SetName(control.HeaderPanel, result.ServiceDisplayName);
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
        FrameworkElement? themeRoot = null,
        EventHandler<ServiceQueryResult>? foundryLocalStartRequested = null)
    {
        using var hotspot = UiThreadHotspotDiagnostics.Measure("ServiceResultViewHost.RebuildForCurrentTheme");

        Release(controls, resultsPanel, collapseToggled, queryRequested, foundryLocalStartRequested);

        foreach (var result in results)
        {
            Add(
                result,
                controls,
                resultsPanel,
                collapseToggled,
                queryRequested,
                themeRoot,
                foundryLocalStartRequested);
        }
    }

    public static void Release(
        IList<IServiceResultView> controls,
        ItemsControl resultsPanel,
        EventHandler<ServiceQueryResult> collapseToggled,
        EventHandler<ServiceQueryResult> queryRequested,
        EventHandler<ServiceQueryResult>? foundryLocalStartRequested = null)
    {
        using var hotspot = UiThreadHotspotDiagnostics.Measure("ServiceResultViewHost.Release");

        foreach (var control in controls)
        {
            control.CollapseToggled -= collapseToggled;
            control.QueryRequested -= queryRequested;
            if (foundryLocalStartRequested is not null)
            {
                control.FoundryLocalStartRequested -= foundryLocalStartRequested;
            }
            control.Cleanup();
        }

        controls.Clear();
        resultsPanel.Items.Clear();
    }

    public static void Reorder(
        IReadOnlyList<ServiceQueryResult> results,
        IReadOnlyList<IServiceResultView> controls,
        ItemsControl resultsPanel,
        bool hideEmptySetting,
        bool pinGrammarCapable = false)
    {
        using var hotspot = UiThreadHotspotDiagnostics.Measure("ServiceResultViewHost.Reorder");

        if (controls.Count == 0)
        {
            return;
        }

        var order = ServiceResultDemotionHelper.StablePartitionIndices(
            results, hideEmptySetting, pinGrammarCapable);

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
        using var hotspot = UiThreadHotspotDiagnostics.Measure("ServiceResultViewHost.UpdateStickyHeaders");

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
        using var hotspot = UiThreadHotspotDiagnostics.Measure("ServiceResultViewHost.UpdatePhoneticDeduplication");

        var shownPhonetics = new HashSet<string>();
        var controlCount = 0;

        foreach (var control in controls)
        {
            controlCount++;
            control.AlreadyShownPhonetics = shownPhonetics.Count > 0
                ? new HashSet<string>(shownPhonetics)
                : null;

            foreach (var key in control.GetDisplayedPhoneticKeys())
            {
                shownPhonetics.Add(key);
            }
        }

        UiThreadHotspotDiagnostics.LogCounter(
            "ServiceResultViewHost.UpdatePhoneticDeduplication.Controls",
            controlCount);
    }

    public static void RefreshThemeChrome(
        IEnumerable<IServiceResultView> controls,
        FrameworkElement? themeRoot = null)
    {
        using var hotspot = UiThreadHotspotDiagnostics.Measure("ServiceResultViewHost.RefreshThemeChrome");

        foreach (var control in controls)
        {
            if (themeRoot is not null)
            {
                control.ThemeRoot = themeRoot;
            }

            control.RefreshThemeChrome();
        }
    }

    /// <summary>
    /// Re-apply the current appearance snapshot (result font size) to existing items.
    /// Mirrors <see cref="RefreshThemeChrome"/>; call after a font-size setting change.
    /// </summary>
    public static void RefreshAppearance(IEnumerable<IServiceResultView> controls)
    {
        using var hotspot = UiThreadHotspotDiagnostics.Measure("ServiceResultViewHost.RefreshAppearance");

        var snapshot = AppearanceService.CurrentSnapshot();
        foreach (var control in controls)
        {
            control.ApplyAppearance(snapshot);
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
