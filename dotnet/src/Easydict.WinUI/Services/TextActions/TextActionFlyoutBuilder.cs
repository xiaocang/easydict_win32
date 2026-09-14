using Easydict.TranslationService.Models;
using Easydict.TranslationService.TextActions;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Easydict.WinUI.Services.TextActions;

/// <summary>
/// Fills a window's Actions menu. Actions are grouped by origin so third-party plugins are
/// never mixed with Easydict's own search and service entries.
/// </summary>
internal static class TextActionFlyoutBuilder
{
    /// <summary>
    /// Rebuild <paramref name="flyout"/>'s items. <paramref name="contextProvider"/> is invoked when
    /// the menu opens and again when an item is clicked, so the latest text is used.
    /// </summary>
    public static void Populate(MenuFlyout flyout, Func<TextActionContext?> contextProvider)
    {
        ArgumentNullException.ThrowIfNull(flyout);
        ArgumentNullException.ThrowIfNull(contextProvider);

        flyout.Items.Clear();
        var loc = LocalizationService.Instance;
        var context = contextProvider();
        var actions = TextActionRegistry.GetActions();

        if (context is null || string.IsNullOrWhiteSpace(context.Text))
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = loc.GetStringOrDefault("TextActionsSelectTextFirst", "Enter or select some text first"),
                IsEnabled = false
            });
            return;
        }

        if (actions.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = loc.GetStringOrDefault("TextActionsNoActions", "No actions configured (Settings → Advanced → Text Actions)"),
                IsEnabled = false
            });
            return;
        }

        AddGroup(flyout, loc.GetStringOrDefault("TextActionsGroupSearch", "Search"),
            actions.Where(a => a.Action.Type == TextActionType.OpenUrl), contextProvider);
        AddGroup(flyout, loc.GetStringOrDefault("TextActionsGroupServices", "Services"),
            actions.Where(a => a.Action.Type == TextActionType.RunService && !a.IsPluginAction), contextProvider);
        AddGroup(flyout, loc.GetStringOrDefault("TextActionsGroupPlugins", "Bob plugins"),
            actions.Where(a => a.IsPluginAction), contextProvider);
    }

    private static void AddGroup(
        MenuFlyout flyout,
        string header,
        IEnumerable<PresentedTextAction> entries,
        Func<TextActionContext?> contextProvider)
    {
        var list = entries.ToList();
        if (list.Count == 0)
        {
            return;
        }

        if (flyout.Items.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        flyout.Items.Add(new MenuFlyoutItem { Text = header, IsEnabled = false });

        foreach (var entry in list)
        {
            var action = entry.Action;
            var text = entry.IsPluginAction && ServiceOriginHelper.BadgeText(entry.Origin) is { } badge
                ? $"{action.Title} · {badge}"
                : action.Title;
            var item = new MenuFlyoutItem
            {
                Text = text,
                Icon = CreateIcon(entry),
                Tag = action.Id
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, $"TextAction_{action.Id}");
            item.Click += async (_, _) =>
            {
                var context = contextProvider();
                if (context is not null)
                {
                    await TextActionExecutor.ExecuteAsync(action, context);
                }
            };
            flyout.Items.Add(item);
        }
    }

    private static IconElement? CreateIcon(PresentedTextAction entry)
    {
        if (entry.Action.Type == TextActionType.RunService && !string.IsNullOrEmpty(entry.Action.ServiceId))
        {
            try
            {
                var theme = MinimalThemeService.ToElementTheme(SettingsService.Instance.AppTheme);
                return new BitmapIcon
                {
                    UriSource = ServiceIconAssetResolver.GetIconUri(entry.Action.ServiceId, theme),
                    ShowAsMonochrome = false
                };
            }
            catch
            {
                return new FontIcon { Glyph = "\uE8C1" };
            }
        }

        return new FontIcon { Glyph = string.IsNullOrEmpty(entry.Action.IconGlyph) ? "\uE721" : entry.Action.IconGlyph };
    }
}
