using System.Text.Json;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services.AnkiConnect;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Views.Controls;

/// <summary>Configure and preview a single, explicitly requested Anki note.</summary>
internal static class AnkiConnectDialog
{
    private static readonly HttpClient Http = new();
    private static string L(string key, string fallback) => LocalizationService.Instance.GetStringOrDefault(key, fallback);

    public static async Task ShowSettingsAsync(XamlRoot root)
    {
        var settings = SettingsService.Instance;
        var endpoint = new TextBox { Header = L("AnkiAddress", "AnkiConnect address"), Text = settings.AnkiEndpoint };
        var connect = new Button { Content = L("AnkiConnect", "Connect and load options") };
        var decks = new ComboBox { Header = L("AnkiDeck", "Deck"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var models = new ComboBox { Header = L("AnkiModel", "Note type"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var fieldPanel = new StackPanel { Spacing = 8 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 10, MinWidth = 300 };
        body.Children.Add(new TextBlock { Text = L("AnkiSettingsInstructions", "Start Anki with the AnkiConnect add-on, then connect and configure your fields."), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(endpoint);
        body.Children.Add(connect);
        body.Children.Add(decks);
        body.Children.Add(models);
        body.Children.Add(new TextBlock { Text = L("AnkiSettingsTemplates", "Field templates: {Word}, {Phonetic}, {Definition}, {Translation}."), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(fieldPanel);
        body.Children.Add(status);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L("AnkiConfigure", "Configure Anki"),
            Content = new ScrollViewer { Content = body, MaxHeight = 500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = L("AnkiSave", "Save"),
            CloseButtonText = L("AnkiCancel", "Cancel"),
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Close
        };
        Dictionary<string, Dictionary<string, string>> templates;
        try { templates = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(settings.AnkiFieldTemplates) ?? new(); }
        catch (JsonException) { templates = new(); }
        var fields = new Dictionary<string, TextBox>();
        AnkiConnectClient? client = null;
        var loading = false;
        var closed = false;
        var adding = false;
        dialog.Closing += (_, args) => { if (adding) args.Cancel = true; };
        var generation = 0;
        dialog.Closed += (_, _) => { closed = true; generation++; };
        endpoint.TextChanged += (_, _) => { client = null; generation++; dialog.IsPrimaryButtonEnabled = false; fieldPanel.Children.Clear(); fields.Clear(); };

        async Task LoadFieldsAsync()
        {
            var current = ++generation;
            fields.Clear(); fieldPanel.Children.Clear(); dialog.IsPrimaryButtonEnabled = false;
            if (client is null || models.SelectedItem is not string model) return;
            try
            {
                var names = await client.GetModelFieldNamesAsync(model);
                if (closed || current != generation) return;
                templates.TryGetValue(model, out var saved);
                for (var i = 0; i < names.Count; i++)
                {
                    var name = names[i];
                    var initial = saved is not null && saved.TryGetValue(name, out var value) ? value : i == 0 ? "{Word}" : i == 1 ? "{Translation}" : "";
                    var input = new TextBox { Header = name, Text = initial, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                    fieldPanel.Children.Add(input); fields[name] = input;
                }
                status.Text = names.Count == 0 ? L("AnkiNoFields", "This note type has no fields.") : "";
                dialog.IsPrimaryButtonEnabled = names.Count > 0 && decks.SelectedItem is string;
            }
            catch (Exception ex) { if (!closed && current == generation) status.Text = ex.Message; }
        }
        models.SelectionChanged += async (_, _) => { if (!loading) await LoadFieldsAsync(); };
        decks.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = client is not null && fields.Count > 0 && decks.SelectedItem is string;
        connect.Click += async (_, _) =>
        {
            var current = ++generation;
            client = null; fields.Clear(); fieldPanel.Children.Clear(); dialog.IsPrimaryButtonEnabled = false;
            connect.IsEnabled = false; status.Text = L("AnkiConnecting", "Connecting…");
            try
            {
                if (!Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out var uri)) throw new ArgumentException(L("AnkiInvalidAddress", "Enter a valid HTTP or HTTPS address."));
                var candidate = new AnkiConnectClient(Http, uri);
                var version = await candidate.GetVersionAsync();
                if (version < 6) throw new InvalidOperationException(L("AnkiVersion", "AnkiConnect version 6 or newer is required."));
                var deckNames = await candidate.GetDeckNamesAsync();
                var modelNames = await candidate.GetModelNamesAsync();
                if (closed || current != generation) return;
                client = candidate; loading = true;
                decks.ItemsSource = deckNames; models.ItemsSource = modelNames;
                decks.SelectedItem = deckNames.Contains(settings.AnkiDeck) ? settings.AnkiDeck : deckNames.FirstOrDefault();
                models.SelectedItem = modelNames.Contains(settings.AnkiModel) ? settings.AnkiModel : modelNames.FirstOrDefault();
                loading = false;
                status.Text = modelNames.Count == 0 || deckNames.Count == 0 ? L("AnkiNoOptions", "No decks or note types were found in Anki.") : "";
                await LoadFieldsAsync();
            }
            catch (Exception ex) { if (!closed && current == generation) status.Text = ex.Message; }
            finally { if (!closed) { loading = false; connect.IsEnabled = true; } }
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            adding = true;
            dialog.IsPrimaryButtonEnabled = false; connect.IsEnabled = false; endpoint.IsEnabled = decks.IsEnabled = models.IsEnabled = false;
            foreach (var input in fields.Values) input.IsEnabled = false;
            try
            {
                if (client is null || decks.SelectedItem is not string deck || models.SelectedItem is not string model || fields.Count == 0) return;
                templates[model] = fields.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
                settings.AnkiEndpoint = endpoint.Text.Trim(); settings.AnkiDeck = deck; settings.AnkiModel = model;
                settings.AnkiFieldTemplates = JsonSerializer.Serialize(templates); settings.Save();
                args.Cancel = false;
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally
            {
                adding = false;
                if (!closed) { connect.IsEnabled = true; endpoint.IsEnabled = decks.IsEnabled = models.IsEnabled = true; foreach (var input in fields.Values) input.IsEnabled = true; dialog.IsPrimaryButtonEnabled = client is not null && fields.Count > 0 && decks.SelectedItem is string; }
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }

    public static async Task ShowAsync(XamlRoot root, TranslationResult result)
    {
        var settings = SettingsService.Instance;
        Dictionary<string, Dictionary<string, string>> templates;
        try { templates = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(settings.AnkiFieldTemplates) ?? new(); }
        catch (JsonException) { templates = new(); }
        var configured = Uri.TryCreate(settings.AnkiEndpoint, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(settings.AnkiDeck) && !string.IsNullOrWhiteSpace(settings.AnkiModel)
            && templates.TryGetValue(settings.AnkiModel, out var defaults) && defaults is { Count: > 0 };
        var (deck, model) = AnkiTargetSelection.Choose(settings.AnkiRememberLast, settings.AnkiDeck, settings.AnkiModel,
            settings.AnkiLastDeck, settings.AnkiLastModel, templates);
        var client = configured ? new AnkiConnectClient(Http, uri!) : null;

        async Task AddAsync(string targetDeck, string targetModel, Dictionary<string, string> mapping)
        {
            var fields = mapping.ToDictionary(pair => pair.Key, pair => AnkiFieldTemplate.Expand(pair.Value, result));
            await client!.AddNoteAsync(new AnkiNote(targetDeck, targetModel, fields));
            templates[targetModel] = mapping;
            settings.AnkiLastDeck = targetDeck; settings.AnkiLastModel = targetModel;
            settings.AnkiFieldTemplates = JsonSerializer.Serialize(templates); settings.Save();
        }

        async Task ShowMessageAsync(string message)
        {
            await new ContentDialog { XamlRoot = root, Title = L("AnkiAdd", "Add to Anki"), Content = message,
                CloseButtonText = L("AnkiClose", "Close") }.ShowAsync();
        }

        if (!configured)
        {
            await ShowMessageAsync(L("AnkiConfigureFirst", "Configure Anki in Settings → Labs first."));
            return;
        }
        if (settings.AnkiQuickAdd)
        {
            try { await AddAsync(deck, model, templates[model]); await ShowMessageAsync(L("AnkiAdded", "Note added to Anki.")); }
            catch (Exception ex) { await ShowMessageAsync(ex.Message + "\n" + L("AnkiCheckAfterError", "If the connection was interrupted, check Anki before retrying: the note may already have been added.")); }
            return;
        }

        var decks = new ComboBox { Header = L("AnkiDeck", "Deck"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var models = new ComboBox { Header = L("AnkiModel", "Note type"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var preview = new StackPanel { Spacing = 8 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = L("AnkiConnecting", "Connecting…") };
        var panel = new StackPanel { Spacing = 8, MinWidth = 260 };
        panel.Children.Add(decks); panel.Children.Add(models); panel.Children.Add(preview); panel.Children.Add(status);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = L("AnkiAdd", "Add to Anki"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = L("AnkiAddButton", "Add"), CloseButtonText = L("AnkiCancel", "Cancel"),
            IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.Close
        };
        var closed = false; var loading = false; var adding = false; var generation = 0;
        Dictionary<string, string>? selectedMapping = null;
        dialog.Closing += (_, args) => { if (adding) args.Cancel = true; };
        dialog.Closed += (_, _) => { closed = true; generation++; };
        async Task LoadPreviewAsync()
        {
            var current = ++generation;
            preview.Children.Clear(); selectedMapping = null; dialog.IsPrimaryButtonEnabled = false;
            if (models.SelectedItem is not string selectedModel) return;
            try
            {
                var names = await client!.GetModelFieldNamesAsync(selectedModel);
                if (closed || current != generation) return;
                templates.TryGetValue(selectedModel, out var saved);
                var mapping = new Dictionary<string, string>();
                for (var i = 0; i < names.Count; i++)
                {
                    var name = names[i];
                    var template = saved is not null && saved.TryGetValue(name, out var value) ? value : i == 0 ? "{Word}" : i == 1 ? (string.IsNullOrWhiteSpace(result.TranslatedText) ? "{Definition}" : "{Translation}") : "";
                    mapping[name] = template;
                    preview.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    preview.Children.Add(new TextBlock { Text = AnkiFieldTemplate.Expand(template, result), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                }
                selectedMapping = mapping;
                status.Text = mapping.Count == 0 ? L("AnkiNoFields", "This note type has no fields.") : "";
                dialog.IsPrimaryButtonEnabled = mapping.Count > 0 && decks.SelectedItem is string;
            }
            catch (Exception ex) { if (!closed && current == generation) status.Text = ex.Message; }
        }
        models.SelectionChanged += async (_, _) => { if (!loading) await LoadPreviewAsync(); };
        decks.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = selectedMapping is { Count: > 0 } && decks.SelectedItem is string;
        dialog.Opened += async (_, _) =>
        {
            try
            {
                var deckNames = await client!.GetDeckNamesAsync();
                var modelNames = await client.GetModelNamesAsync();
                if (closed) return;
                loading = true; decks.ItemsSource = deckNames; models.ItemsSource = modelNames;
                decks.SelectedItem = deckNames.Contains(deck) ? deck : null;
                models.SelectedItem = modelNames.Contains(model) ? model : null;
                loading = false;
                status.Text = deckNames.Count == 0 || modelNames.Count == 0 ? L("AnkiNoOptions", "No decks or note types were found in Anki.")
                    : decks.SelectedItem is null || models.SelectedItem is null ? L("AnkiTargetMissing", "The selected deck or note type is unavailable. Choose an available target.") : "";
                await LoadPreviewAsync();
            }
            catch (Exception ex) { if (!closed) status.Text = ex.Message; }
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral(); adding = true;
            dialog.IsPrimaryButtonEnabled = false; decks.IsEnabled = models.IsEnabled = false;
            try
            {
                if (decks.SelectedItem is not string selectedDeck || models.SelectedItem is not string selectedModel || selectedMapping is null) return;
                await AddAsync(selectedDeck, selectedModel, selectedMapping);
                args.Cancel = false;
            }
            catch (Exception ex) { status.Text = ex.Message + "\n" + L("AnkiCheckAfterError", "If the connection was interrupted, check Anki before retrying: the note may already have been added."); }
            finally { adding = false; decks.IsEnabled = models.IsEnabled = true; dialog.IsPrimaryButtonEnabled = selectedMapping is { Count: > 0 } && decks.SelectedItem is string; deferral.Complete(); }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ShowMessageAsync(L("AnkiAdded", "Note added to Anki."));
    }
}
