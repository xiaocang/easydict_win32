using System.Diagnostics;
using Easydict.BobPlugin;
using Easydict.BobPlugin.Manifest;
using Easydict.TranslationService.TextActions;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.BobPlugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Easydict.WinUI.Views;

/// <summary>
/// Settings → Plugins: importing, configuring and removing third-party Bob plugins.
///
/// Everything a plugin owns lives in its own tab with its own heading and badge, so a plugin's
/// options are never mistaken for an Easydict service's.
/// </summary>
public sealed partial class SettingsPage
{
    /// <summary>Source tag on text actions this page creates for a plugin.</summary>
    private const string BobTextActionSource = "bob";

    /// <summary>
    /// Bob's third-party plugin catalog, opened in the browser from the Plugins tab.
    /// </summary>
    private const string BobPluginStoreUrl = "https://bobplugin.ripperhe.com/";

    /// <summary>Editable option fields per plugin, read back by <see cref="SaveBobPluginOptions"/>.</summary>
    private readonly Dictionary<string, List<(BobPluginOption Option, FrameworkElement Field)>> _bobOptionFields = new(StringComparer.Ordinal);

    private async void OnImportBobPluginClicked(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        try
        {
            var mainWindow = App.MainWindow;
            if (mainWindow is null) return;

            var path = await Services.Storage.PickerFactory.PickSingleFileAsync(
                mainWindow,
                Services.Storage.PickerFactory.SettingsIdentifiers.BobPluginImport,
                new[] { ".bobplugin", ".zip" });
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var plugin = BobPluginInstaller.Install(path, _settings);

            if (!TranslationManagerService.Instance.TryRegisterBobPlugin(plugin, out var error))
            {
                BobPluginInstaller.DeleteFiles(plugin);
                await ShowSimpleDialogAsync(
                    loc.GetStringOrDefault("BobPluginImportFailedTitle", "Import failed"),
                    error ?? "The plugin could not be loaded.");
                return;
            }

            // Probing costs one engine start, which is why it happens here and not at every launch.
            plugin.SupportedLanguageCodes =
                (await TranslationManagerService.Instance.ProbeBobPluginLanguagesAsync(plugin.ServiceId)).ToList();
            if (plugin.SupportedLanguageCodes.Count > 0)
            {
                // Rebuild so the service reports the languages it just told us about.
                TranslationManagerService.Instance.TryRegisterBobPlugin(plugin, out _);
            }

            _settings.InstalledBobPlugins.RemoveAll(p => string.Equals(p.ServiceId, plugin.ServiceId, StringComparison.Ordinal));
            _settings.InstalledBobPlugins.Add(plugin);

            // Available in every window, but manual-query: a third-party plugin does not get to
            // run on every keystroke until the user asks for it.
            EnableBobPluginAsManualQuery(plugin.ServiceId);

            // The selection pop-up gets a button for it, configurable from the Plugins tab.
            _settings.AddOrReplaceTextAction(TextActionValidator.ForService(
                plugin.ServiceId,
                plugin.DisplayName,
                showOnPopButton: true,
                source: BobTextActionSource));

            _settings.Save();
            LoadSettings(MinimalThemeService.IsActive);

            await ShowSimpleDialogAsync(
                loc.GetStringOrDefault("BobPluginImportSuccessTitle", "Plugin installed"),
                string.Format(
                    loc.GetStringOrDefault(
                        "BobPluginImportSuccessMessage",
                        "'{0}' was installed as a manual-query service. Click its card to run it, or select text to get its button on the pop-up (requires Mouse Selection Translate)."),
                    plugin.DisplayName));
        }
        catch (BobPluginException ex)
        {
            await ShowSimpleDialogAsync(
                loc.GetStringOrDefault("BobPluginImportFailedTitle", "Import failed"),
                ex.Code == BobPluginError.UnsupportedCategory
                    ? loc.GetStringOrDefault("BobPluginUnsupportedCategory", ex.Message)
                    : ex.Message);
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync(
                loc.GetStringOrDefault("BobPluginImportFailedTitle", "Import failed"),
                ex.Message);
        }
    }

    private void EnableBobPluginAsManualQuery(string serviceId)
    {
        if (!_settings.MainWindowEnabledServices.Contains(serviceId)) _settings.MainWindowEnabledServices.Add(serviceId);
        if (!_settings.MiniWindowEnabledServices.Contains(serviceId)) _settings.MiniWindowEnabledServices.Add(serviceId);
        if (!_settings.FixedWindowEnabledServices.Contains(serviceId)) _settings.FixedWindowEnabledServices.Add(serviceId);

        _settings.MainWindowServiceEnabledQuery[serviceId] = false;
        _settings.MiniWindowServiceEnabledQuery[serviceId] = false;
        _settings.FixedWindowServiceEnabledQuery[serviceId] = false;
    }

    private void UpdateBobPluginsSummary()
    {
        if (InstalledBobPluginsSummaryText is null)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        var count = _settings.InstalledBobPlugins.Count;
        InstalledBobPluginsSummaryText.Text = count switch
        {
            0 => loc.GetStringOrDefault("BobPluginSummaryNone", "No Bob plugins installed"),
            1 => loc.GetStringOrDefault("BobPluginSummaryOne", "1 Bob plugin installed"),
            _ => string.Format(loc.GetStringOrDefault("BobPluginSummaryMany", "{0} Bob plugins installed"), count)
        };

        if (ImportBobPluginButton is not null)
        {
            ImportBobPluginButton.Content = loc.GetStringOrDefault("BobPluginImportButton", "Import Bob Plugin");
        }

        if (PluginsHeaderText is not null)
        {
            PluginsHeaderText.Text = loc.GetStringOrDefault("BobPluginsTabHeader", "Bob Plugins");
        }

        if (PluginsDescriptionText is not null)
        {
            PluginsDescriptionText.Text = loc.GetStringOrDefault(
                "BobPluginsTabDescription",
                "Install third-party Bob translate plugins. They run in an embedded JavaScript engine, separate from Easydict's built-in services.");
        }

        if (BobPluginStoreLink is not null)
        {
            BobPluginStoreLink.Content = loc.GetStringOrDefault("BobPluginGetMoreLink", "Get more plugins");
            BobPluginStoreLink.NavigateUri = new Uri(BobPluginStoreUrl);
        }
    }

    /// <summary>Rebuild the per-plugin configuration cards. Hidden entirely when nothing is installed.</summary>
    private void BuildInstalledBobPluginsConfigUI()
    {
        if (InstalledBobPluginsConfigPanel is null || BobPluginsSection is null)
        {
            return;
        }

        _bobOptionFields.Clear();
        InstalledBobPluginsConfigPanel.Children.Clear();

        var plugins = _settings.InstalledBobPlugins;
        BobPluginsSection.Visibility = plugins.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (plugins.Count == 0)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        BobPluginsSectionHeader.Text = loc.GetStringOrDefault("BobPluginsSectionHeader", "Bob plugins (third party)");
        BobPluginsSectionDescription.Text = loc.GetStringOrDefault(
            "BobPluginsSectionDescription",
            "Provided by third-party JavaScript plugins, not built into Easydict. Network requests and data handling are done by the plugin itself. Installed plugins are manual-query by default, with no caching and no retries.");

        foreach (var plugin in plugins.ToList())
        {
            InstalledBobPluginsConfigPanel.Children.Add(CreateBobPluginExpander(plugin, loc));
        }
    }

    private Expander CreateBobPluginExpander(SettingsService.InstalledBobPlugin plugin, LocalizationService loc)
    {
        var manifest = BobPluginInstaller.TryReadManifest(plugin);

        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Header = CreateBobPluginHeader(plugin, loc)
        };

        var content = new StackPanel { Spacing = 10 };

        if (!string.IsNullOrWhiteSpace(manifest?.Summary))
        {
            content.Children.Add(SecondaryText(manifest!.Summary!));
        }

        if (!string.IsNullOrWhiteSpace(manifest?.Author))
        {
            content.Children.Add(SecondaryText($"{loc.GetStringOrDefault("BobPluginAuthor", "Author")}: {manifest!.Author}"));
        }

        if (!string.IsNullOrWhiteSpace(manifest?.Homepage)
            && Uri.TryCreate(manifest!.Homepage, UriKind.Absolute, out var homepage)
            && (homepage.Scheme == Uri.UriSchemeHttp || homepage.Scheme == Uri.UriSchemeHttps))
        {
            content.Children.Add(new HyperlinkButton
            {
                Content = loc.GetStringOrDefault("BobPluginHomepage", "Plugin homepage"),
                NavigateUri = homepage,
                FontSize = 12,
                Padding = new Thickness(0)
            });
        }

        if (manifest is { Options.Count: > 0 })
        {
            content.Children.Add(new TextBlock
            {
                Text = loc.GetStringOrDefault("BobPluginOptionsHeader", "Plugin options"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            var fields = new List<(BobPluginOption, FrameworkElement)>();
            foreach (var option in manifest.Options)
            {
                var field = CreateBobOptionField(plugin, option);
                fields.Add((option, field));
                content.Children.Add(WrapOption(option, field));
            }

            _bobOptionFields[plugin.ServiceId] = fields;
        }

        content.Children.Add(CreateBobPolicyPanel(plugin, loc));
        content.Children.Add(CreateBobPluginButtons(plugin, loc));
        content.Children.Add(SecondaryText(plugin.InstallDirectory));

        expander.Content = content;
        return expander;
    }

    private static FrameworkElement CreateBobPluginHeader(SettingsService.InstalledBobPlugin plugin, LocalizationService loc)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        if (!string.IsNullOrWhiteSpace(plugin.IconPath) && File.Exists(plugin.IconPath))
        {
            try
            {
                header.Children.Add(new Image
                {
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(plugin.IconPath))
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsPage] Could not load plugin icon '{plugin.IconPath}': {ex.Message}");
            }
        }

        header.Children.Add(new TextBlock
        {
            Text = plugin.DisplayName,
            VerticalAlignment = VerticalAlignment.Center
        });

        header.Children.Add(new Border
        {
            Background = ThemeBrush("PluginBadgeBackgroundBrush"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = loc.GetStringOrDefault("ServiceOrigin_BobBadge", "Bob plugin"),
                FontSize = 10,
                Foreground = ThemeBrush("PluginBadgeForegroundBrush")
            }
        });

        header.Children.Add(new TextBlock
        {
            Text = $"v{plugin.Version}",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush")
        });

        return header;
    }

    private FrameworkElement CreateBobOptionField(SettingsService.InstalledBobPlugin plugin, BobPluginOption option)
    {
        if (option.Type == BobPluginOptionType.Menu && option.MenuValues.Count > 0)
        {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 220 };
            var current = plugin.OptionValues.GetValueOrDefault(option.Identifier) ?? option.DefaultValue;
            foreach (var value in option.MenuValues)
            {
                var item = new ComboBoxItem { Content = value.Title, Tag = value.Value };
                combo.Items.Add(item);
                if (string.Equals(value.Value, current, StringComparison.Ordinal))
                {
                    combo.SelectedItem = item;
                }
            }

            combo.SelectedItem ??= combo.Items.FirstOrDefault();
            return combo;
        }

        if (option.IsSecure)
        {
            return new PasswordBox
            {
                Password = _settings.GetBobPluginSecureOption(plugin.ServiceId, option.Identifier) ?? string.Empty,
                PlaceholderText = option.TextConfig?.PlaceholderText ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }

        // A plugin asks for a tall field by declaring a height; anything else is a one-liner.
        var multiline = option.TextConfig?.Height is > 40;
        return new TextBox
        {
            Text = plugin.OptionValues.GetValueOrDefault(option.Identifier) ?? option.DefaultValue ?? string.Empty,
            PlaceholderText = option.TextConfig?.PlaceholderText ?? string.Empty,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height = multiline ? option.TextConfig!.Height!.Value : double.NaN,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private static FrameworkElement WrapOption(BobPluginOption option, FrameworkElement field)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = option.Title, FontSize = 12 });
        panel.Children.Add(field);

        if (!string.IsNullOrWhiteSpace(option.Desc))
        {
            panel.Children.Add(SecondaryText(option.Desc!));
        }

        return panel;
    }

    /// <summary>
    /// The three switches that decide how much of the host's automatic behavior this plugin gets.
    /// Off by default, because a plugin can have side effects the host cannot see.
    /// </summary>
    private FrameworkElement CreateBobPolicyPanel(SettingsService.InstalledBobPlugin plugin, LocalizationService loc)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(SecondaryText(loc.GetStringOrDefault(
            "BobPluginPolicyHint",
            "Off by default: a plugin can charge, rate-limit or write things Easydict cannot see, so it only gets these once you allow them.")));

        panel.Children.Add(PolicySwitch(
            loc.GetStringOrDefault("BobPluginAllowCache", "Reuse cached results"),
            plugin.AllowResultCache,
            value => { plugin.AllowResultCache = value; ReRegisterBobPlugin(plugin); }));

        panel.Children.Add(PolicySwitch(
            loc.GetStringOrDefault("BobPluginAllowRetry", "Retry after a failure"),
            plugin.AllowHostRetry,
            value => { plugin.AllowHostRetry = value; ReRegisterBobPlugin(plugin); }));

        panel.Children.Add(PolicySwitch(
            loc.GetStringOrDefault("BobPluginAllowPhonetics", "Add phonetics from Youdao"),
            plugin.AllowPhoneticEnrichment,
            value => { plugin.AllowPhoneticEnrichment = value; ReRegisterBobPlugin(plugin); }));

        var action = _settings.TextActions.FirstOrDefault(a =>
            string.Equals(a.ServiceId, plugin.ServiceId, StringComparison.Ordinal));
        panel.Children.Add(PolicySwitch(
            loc.GetStringOrDefault("BobPluginShowOnPopButton", "Show on the selection button"),
            action?.ShowOnPopButton ?? false,
            value =>
            {
                var current = _settings.TextActions.FirstOrDefault(a =>
                    string.Equals(a.ServiceId, plugin.ServiceId, StringComparison.Ordinal));
                _settings.AddOrReplaceTextAction(current is null
                    ? TextActionValidator.ForService(plugin.ServiceId, plugin.DisplayName, value, BobTextActionSource)
                    : current with { ShowOnPopButton = value });
                _settings.Save();
            }));

        return panel;
    }

    private static ToggleSwitch PolicySwitch(string header, bool isOn, Action<bool> onChanged)
    {
        var toggle = new ToggleSwitch { Header = header, IsOn = isOn };
        toggle.Toggled += (s, e) =>
        {
            if (s is ToggleSwitch source)
            {
                onChanged(source.IsOn);
            }
        };
        return toggle;
    }

    private FrameworkElement CreateBobPluginButtons(SettingsService.InstalledBobPlugin plugin, LocalizationService loc)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush")
        };

        var validate = new Button
        {
            Content = loc.GetStringOrDefault("BobPluginValidate", "Validate"),
            Padding = new Thickness(8, 4, 8, 4)
        };
        validate.Click += async (s, e) => await ValidateBobPluginAsync(plugin, validate, status, loc);
        panel.Children.Add(validate);

        var remove = new Button
        {
            Content = loc.GetStringOrDefault("BobPluginRemove", "Remove"),
            Foreground = ThemeBrush("SystemFillColorCriticalBrush"),
            Padding = new Thickness(8, 4, 8, 4)
        };
        remove.Click += async (s, e) => await RemoveBobPluginAsync(plugin, loc);
        panel.Children.Add(remove);

        panel.Children.Add(status);
        return panel;
    }

    private async Task ValidateBobPluginAsync(
        SettingsService.InstalledBobPlugin plugin,
        Button button,
        TextBlock status,
        LocalizationService loc)
    {
        // Options the user just typed have to reach the plugin before it checks them.
        SaveBobPluginOptions();
        _settings.Save();
        ReRegisterBobPlugin(plugin);

        button.IsEnabled = false;
        status.Text = loc.GetStringOrDefault("BobPluginValidating", "Checking…");
        try
        {
            var service = TranslationManagerService.Instance.GetBobPlugin(plugin.ServiceId);
            if (service is null)
            {
                status.Text = loc.GetStringOrDefault("BobPluginValidateFailed", "The plugin could not be loaded.");
                return;
            }

            var outcome = await service.ValidateAsync();
            status.Text = outcome.Status switch
            {
                BobValidationStatus.Valid => loc.GetStringOrDefault("BobPluginValidateSuccess", "The plugin reports it is ready."),
                BobValidationStatus.NotSupported => loc.GetStringOrDefault("BobPluginValidateNotSupported", "This plugin has no self-check."),
                _ => outcome.Message ?? loc.GetStringOrDefault("BobPluginValidateFailed", "The plugin reported a problem.")
            };
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task RemoveBobPluginAsync(SettingsService.InstalledBobPlugin plugin, LocalizationService loc)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = loc.GetStringOrDefault("BobPluginRemove", "Remove"),
            Content = string.Format(
                loc.GetStringOrDefault("BobPluginRemoveConfirm", "Remove '{0}' and delete its files and stored options?"),
                plugin.DisplayName),
            PrimaryButtonText = loc.GetStringOrDefault("BobPluginRemove", "Remove"),
            CloseButtonText = loc.GetStringOrDefault("Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        // Routed through the page's helper: WinUI allows only one dialog per XamlRoot.
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        TranslationManagerService.Instance.UnregisterBobPlugin(plugin.ServiceId);

        _settings.InstalledBobPlugins.RemoveAll(p => string.Equals(p.ServiceId, plugin.ServiceId, StringComparison.Ordinal));
        _settings.MainWindowEnabledServices.Remove(plugin.ServiceId);
        _settings.MiniWindowEnabledServices.Remove(plugin.ServiceId);
        _settings.FixedWindowEnabledServices.Remove(plugin.ServiceId);
        _settings.MainWindowServiceEnabledQuery.Remove(plugin.ServiceId);
        _settings.MiniWindowServiceEnabledQuery.Remove(plugin.ServiceId);
        _settings.FixedWindowServiceEnabledQuery.Remove(plugin.ServiceId);
        _settings.ServiceTestStatus.Remove(plugin.ServiceId);
        _settings.RemoveTextActionsBySource(BobTextActionSource, plugin.ServiceId);
        _settings.RemoveBobPluginSecureOptions(plugin.ServiceId);
        _settings.Save();

        BobPluginInstaller.DeleteFiles(plugin);
        LoadSettings(MinimalThemeService.IsActive);
    }

    /// <summary>
    /// Read the option editors back into settings. Secure values go to the credential store, the
    /// rest into the plugin record. Called alongside the other credential saves.
    /// </summary>
    private void SaveBobPluginOptions()
    {
        if (_bobOptionFields.Count == 0)
        {
            return;
        }

        foreach (var plugin in _settings.InstalledBobPlugins)
        {
            if (!_bobOptionFields.TryGetValue(plugin.ServiceId, out var fields))
            {
                continue;
            }

            foreach (var (option, field) in fields)
            {
                var value = field switch
                {
                    PasswordBox password => password.Password,
                    TextBox text => text.Text,
                    ComboBox combo => (combo.SelectedItem as ComboBoxItem)?.Tag as string,
                    _ => null
                };

                if (option.IsSecure)
                {
                    _settings.SetBobPluginSecureOption(plugin.ServiceId, option.Identifier, value);
                }
                else
                {
                    plugin.OptionValues[option.Identifier] = value ?? string.Empty;
                }
            }
        }
    }

    /// <summary>Rebuild a plugin's service so a changed option or policy takes effect at once.</summary>
    private void ReRegisterBobPlugin(SettingsService.InstalledBobPlugin plugin)
    {
        _settings.Save();
        if (!TranslationManagerService.Instance.TryRegisterBobPlugin(plugin, out var error))
        {
            Debug.WriteLine($"[SettingsPage] Could not re-register Bob plugin '{plugin.ServiceId}': {error}");
        }
    }

    /// <summary>
    /// Look up a theme brush without letting a missing key take the settings page down; the
    /// plugin tokens live in the theme dictionaries and a stripped theme may not carry them.
    /// </summary>
    private static Brush? ThemeBrush(string key)
    {
        try
        {
            return Application.Current.Resources[key] as Brush;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] Theme brush '{key}' is unavailable: {ex.Message}");
            return null;
        }
    }

    private static TextBlock SecondaryText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeBrush("TextFillColorSecondaryBrush")
    };
}
