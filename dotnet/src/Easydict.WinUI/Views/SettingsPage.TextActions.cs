using Easydict.TranslationService.Models;
using Easydict.TranslationService.TextActions;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.TextActions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Views;

/// <summary>
/// Settings → Plugins → Text Actions: the editor for the declarative actions that appear on the
/// selection pop-up strip and in each window's Actions menu.
/// </summary>
public sealed partial class SettingsPage
{
    private void BuildTextActionsUI()
    {
        if (TextActionsPanel is null)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        TextActionsHeaderText.Text = loc.GetStringOrDefault("TextActionsHeader", "Text Actions");
        TextActionsDescriptionText.Text = loc.GetStringOrDefault(
            "TextActionsDescription",
            "Actions run on the selected or entered text: open a search site, or run one specific service (for example a Bob plugin). They appear in the Actions menu of every window and, optionally, on the selection pop-up.");
        TextActionsPopButtonHintText.Text = loc.GetStringOrDefault(
            "TextActionsPopButtonHint",
            "Actions marked 'Show on selection button' appear next to the translate icon after you select text (requires Mouse Selection Translate).");
        AddTextActionButton.Content = loc.GetStringOrDefault("TextActionAdd", "Add action");
        ResetTextActionsButton.Content = loc.GetStringOrDefault("TextActionResetDefaults", "Reset to defaults");

        TextActionsPanel.Children.Clear();
        var actions = _settings.TextActions;
        for (var i = 0; i < actions.Count; i++)
        {
            TextActionsPanel.Children.Add(CreateTextActionRow(actions[i], i, actions.Count));
        }
    }

    private FrameworkElement CreateTextActionRow(TextAction action, int index, int count)
    {
        var loc = LocalizationService.Instance;
        var presented = TextActionRegistry.Present(action);

        var grid = new Grid
        {
            MinHeight = 36,
            ColumnSpacing = 8
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabledBox = new CheckBox
        {
            IsChecked = action.IsEnabled,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(enabledBox, loc.GetStringOrDefault("TextActionEnabled", "Enabled"));
        enabledBox.Checked += (_, _) => UpdateTextAction(action.Id, a => a with { IsEnabled = true });
        enabledBox.Unchecked += (_, _) => UpdateTextAction(action.Id, a => a with { IsEnabled = false });
        Grid.SetColumn(enabledBox, 0);
        grid.Children.Add(enabledBox);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock
        {
            Text = action.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (presented.IsPluginAction && ServiceOriginHelper.BadgeText(presented.Origin) is { } badgeText)
        {
            titleRow.Children.Add(CreateOriginBadge(badgeText, ServiceOriginHelper.Tooltip(presented.Origin)));
        }

        var subtitle = action.Type == TextActionType.OpenUrl
            ? action.UrlTemplate ?? string.Empty
            : string.Format(
                loc.GetStringOrDefault("TextActionServiceSubtitle", "Run service: {0}"),
                presented.ServiceDisplayName ?? action.ServiceId ?? string.Empty);
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        textStack.Children.Add(titleRow);
        textStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = ThemeResourceService.GetBrush("TextFillColorSecondaryBrush", this),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        var popToggle = new ToggleSwitch
        {
            IsOn = action.ShowOnPopButton,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(popToggle, loc.GetStringOrDefault("TextActionShowOnPopButton", "Show on selection button"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(popToggle, loc.GetStringOrDefault("TextActionShowOnPopButton", "Show on selection button"));
        popToggle.Toggled += (s, _) =>
        {
            var isOn = ((ToggleSwitch)s).IsOn;
            UpdateTextAction(action.Id, a => a with { ShowOnPopButton = isOn });
        };
        Grid.SetColumn(popToggle, 2);
        grid.Children.Add(popToggle);

        var upButton = CreateTextActionIconButton("\uE70E", loc.GetStringOrDefault("TextActionMoveUp", "Move up"));
        upButton.IsEnabled = index > 0;
        upButton.Click += (_, _) => MoveTextAction(action.Id, -1);
        Grid.SetColumn(upButton, 3);
        grid.Children.Add(upButton);

        var downButton = CreateTextActionIconButton("\uE70D", loc.GetStringOrDefault("TextActionMoveDown", "Move down"));
        downButton.IsEnabled = index < count - 1;
        downButton.Click += (_, _) => MoveTextAction(action.Id, 1);
        Grid.SetColumn(downButton, 4);
        grid.Children.Add(downButton);

        var editButton = CreateTextActionIconButton("\uE70F", loc.GetStringOrDefault("TextActionEdit", "Edit"));
        editButton.Click += async (_, _) => await ShowTextActionDialogAsync(action);
        Grid.SetColumn(editButton, 5);
        grid.Children.Add(editButton);

        var deleteButton = CreateTextActionIconButton("\uE74D", loc.GetStringOrDefault("TextActionDelete", "Delete"));
        deleteButton.Click += (_, _) => RemoveTextAction(action.Id);
        Grid.SetColumn(deleteButton, 6);
        grid.Children.Add(deleteButton);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(grid, $"TextActionRow_{action.Id}");
        return grid;
    }

    private Border CreateOriginBadge(string text, string? tooltip)
    {
        var badge = new Border
        {
            Background = ThemeResourceService.GetBrush("PluginBadgeBackgroundBrush", this),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = ThemeResourceService.GetBrush("PluginBadgeForegroundBrush", this)
            }
        };
        if (!string.IsNullOrEmpty(tooltip))
        {
            ToolTipService.SetToolTip(badge, tooltip);
        }

        return badge;
    }

    private static Button CreateTextActionIconButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 12 }
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private void UpdateTextAction(string actionId, Func<TextAction, TextAction> update)
    {
        var actions = _settings.TextActions;
        var index = actions.FindIndex(a => a.Id == actionId);
        if (index < 0)
        {
            return;
        }

        actions[index] = update(actions[index]);
        SaveTextActions(rebuild: false);
    }

    private void MoveTextAction(string actionId, int delta)
    {
        var actions = _settings.TextActions;
        var index = actions.FindIndex(a => a.Id == actionId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= actions.Count)
        {
            return;
        }

        (actions[index], actions[target]) = (actions[target], actions[index]);
        SaveTextActions(rebuild: true);
    }

    private void RemoveTextAction(string actionId)
    {
        _settings.TextActions.RemoveAll(a => a.Id == actionId);
        SaveTextActions(rebuild: true);
    }

    private void SaveTextActions(bool rebuild)
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsPage] Failed to save text actions: {ex.Message}");
        }

        if (rebuild)
        {
            BuildTextActionsUI();
        }
    }

    private async void OnAddTextActionClicked(object sender, RoutedEventArgs e)
    {
        await ShowTextActionDialogAsync(null);
    }

    private void OnResetTextActionsClicked(object sender, RoutedEventArgs e)
    {
        // Keep actions generated by other subsystems (e.g. installed plugins); restore the built-ins.
        var generated = _settings.TextActions.Where(a => !string.IsNullOrEmpty(a.Source)).ToList();
        _settings.TextActions = TextActionCatalog.Merge(generated);
        SaveTextActions(rebuild: true);
    }

    /// <summary>
    /// Add (<paramref name="existing"/> is null) or edit a text action through a small dialog.
    /// </summary>
    private async Task ShowTextActionDialogAsync(TextAction? existing)
    {
        var loc = LocalizationService.Instance;
        var isService = existing?.Type == TextActionType.RunService;

        var typeUrl = new RadioButton { Content = loc.GetStringOrDefault("TextActionTypeUrl", "Open a URL"), IsChecked = !isService, GroupName = "TextActionType" };
        var typeService = new RadioButton { Content = loc.GetStringOrDefault("TextActionTypeService", "Run a service"), IsChecked = isService, GroupName = "TextActionType" };
        var typePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        typePanel.Children.Add(typeUrl);
        typePanel.Children.Add(typeService);

        var titleBox = new TextBox
        {
            Header = loc.GetStringOrDefault("TextActionTitle", "Title"),
            Text = existing?.Title ?? string.Empty
        };
        var urlBox = new TextBox
        {
            Header = loc.GetStringOrDefault("TextActionUrlTemplate", "URL template"),
            Text = existing?.UrlTemplate ?? "https://",
            PlaceholderText = "https://example.com/search?q={encodedText}",
            Visibility = isService ? Visibility.Collapsed : Visibility.Visible
        };
        var urlHint = new TextBlock
        {
            Text = loc.GetStringOrDefault("TextActionUrlHint", "Placeholders: {encodedText}, {encodedTranslation}, {from}, {to}. Only http/https URLs."),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeResourceService.GetBrush("TextFillColorSecondaryBrush", this),
            Visibility = isService ? Visibility.Collapsed : Visibility.Visible
        };

        var serviceBox = new ComboBox
        {
            Header = loc.GetStringOrDefault("TextActionServiceLabel", "Service"),
            MinWidth = 260,
            Visibility = isService ? Visibility.Visible : Visibility.Collapsed
        };
        using (var handle = TranslationManagerService.Instance.AcquireHandle())
        {
            foreach (var (serviceId, service) in handle.Manager.Services)
            {
                var origin = ServiceOriginHelper.Resolve(service, serviceId);
                serviceBox.Items.Add(new ComboBoxItem
                {
                    Content = ServiceOriginHelper.FormatName(service.DisplayName, origin),
                    Tag = serviceId
                });
            }
        }
        if (existing?.ServiceId is { } existingServiceId)
        {
            serviceBox.SelectedItem = serviceBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, existingServiceId, StringComparison.Ordinal));
        }

        var showOnPop = new CheckBox
        {
            Content = loc.GetStringOrDefault("TextActionShowOnPopButton", "Show on selection button"),
            IsChecked = existing?.ShowOnPopButton ?? false
        };
        var errorText = new TextBlock
        {
            Foreground = ThemeResourceService.GetBrush("SystemFillColorCriticalBrush", this),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        typeUrl.Checked += (_, _) =>
        {
            urlBox.Visibility = urlHint.Visibility = Visibility.Visible;
            serviceBox.Visibility = Visibility.Collapsed;
        };
        typeService.Checked += (_, _) =>
        {
            urlBox.Visibility = urlHint.Visibility = Visibility.Collapsed;
            serviceBox.Visibility = Visibility.Visible;
        };

        var content = new StackPanel { Spacing = 10, MinWidth = 360 };
        content.Children.Add(typePanel);
        content.Children.Add(titleBox);
        content.Children.Add(urlBox);
        content.Children.Add(urlHint);
        content.Children.Add(serviceBox);
        content.Children.Add(showOnPop);
        content.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = existing is null
                ? loc.GetStringOrDefault("TextActionDialogAddTitle", "Add text action")
                : loc.GetStringOrDefault("TextActionDialogEditTitle", "Edit text action"),
            Content = new ScrollViewer { Content = content },
            PrimaryButtonText = loc.GetStringOrDefault("Save", "Save"),
            CloseButtonText = loc.GetStringOrDefault("Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        TextAction? candidate = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var useService = typeService.IsChecked == true;
            var title = titleBox.Text?.Trim() ?? string.Empty;
            var selectedServiceId = (serviceBox.SelectedItem as ComboBoxItem)?.Tag as string;

            var id = existing?.Id ?? (useService && !string.IsNullOrEmpty(selectedServiceId)
                ? TextActionValidator.ServiceActionIdPrefix + selectedServiceId
                : "custom-" + Guid.NewGuid().ToString("N")[..8]);

            candidate = new TextAction
            {
                Id = id,
                Title = title,
                Type = useService ? TextActionType.RunService : TextActionType.OpenUrl,
                UrlTemplate = useService ? null : urlBox.Text?.Trim(),
                ServiceId = useService ? selectedServiceId : null,
                IconGlyph = existing?.IconGlyph,
                IsEnabled = existing?.IsEnabled ?? true,
                ShowOnPopButton = showOnPop.IsChecked == true,
                IsBuiltIn = existing?.IsBuiltIn ?? false,
                Source = existing?.Source
            };

            var error = TextActionValidator.Validate(candidate);
            if (error is null && existing is null && _settings.TextActions.Any(a => a.Id == candidate.Id))
            {
                error = loc.GetStringOrDefault("TextActionDuplicate", "An action for this service already exists.");
            }

            if (error is not null)
            {
                errorText.Text = error.StartsWith("URL", StringComparison.Ordinal)
                    ? loc.GetStringOrDefault("TextActionInvalidUrl", "Enter a valid http(s) URL template containing {encodedText}.")
                    : error;
                errorText.Visibility = Visibility.Visible;
                candidate = null;
                args.Cancel = true;
            }
        };

        var result = await ShowDialogAsync(dialog);
        if (result != ContentDialogResult.Primary || candidate is null)
        {
            return;
        }

        var actions = _settings.TextActions;
        var index = existing is null ? -1 : actions.FindIndex(a => a.Id == existing.Id);
        if (index >= 0)
        {
            actions[index] = candidate;
        }
        else
        {
            actions.Add(candidate);
        }

        SaveTextActions(rebuild: true);
    }
}
