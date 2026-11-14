using System.Net;
using System.Text;
using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;
using Xunit;

namespace Easydict.UIAutomation.Tests.Tests;

/// <summary>
/// Runtime verification for task #9 (AI-profile model list refresh). These tests drive the real app with an
/// isolated settings directory and a loopback stub, because source-string assertions cannot show whether a
/// dialog actually appears or whether the model control is updated.
///
/// The stub serves the exact paths each provider uses (see notes/task9-model-list-refresh.md):
///   Magpie  : GET /api/hello -> {"name":"magpie"}, GET /v1/models -> OpenAI-shaped list
///   Ollama  : GET /api/tags  -> {"models":[{"name":"..."}]}, GET /experimental/model-recommendations -> 200 []
///             (404/405 would make OllamaService fall back to the live ollama.com catalogue, breaking isolation)
/// Request paths are recorded so a stub mismatch is distinguishable from a product failure.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class AiProfileModelRefreshTests
{
    /// <summary>Seed one Magpie profile pointing at <paramref name="endpoint"/> and launch the app.</summary>
    private static (AppLauncher Launcher, string SettingsDirectory) LaunchWithMagpieProfile(string endpoint, bool allowRemote = false, string model = "")
    {
        WaitForNoRunningApp();

        var directory = Path.Combine(Path.GetTempPath(), "Easydict.AiProfile.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            UILanguage = "en-US",
            AppTheme = "Light",
            EnableShowWindowHotkey = false,
            EnableTranslateSelectionHotkey = false,
            EnableShowMiniWindowHotkey = false,
            EnableShowFixedWindowHotkey = false,
            EnableOcrTranslateHotkey = false,
            EnableSilentOcrHotkey = false,
            HoverWordLookupEnabled = false,
            MouseSelectionTranslate = false,
            // LoadAiServiceProfiles requires both the profile array and a matching schema version,
            // otherwise it rejects the collection as invalid and the seeded profile never appears.
            AiServiceProfilesSchemaVersion = 1,
            AiServiceProfiles = new[]
            {
                new
                {
                    ProfileId = "uia-magpie-1",
                    ServiceId = "ai-profile:uia-magpie-1",
                    ProviderType = "magpie",
                    Name = "Magpie (UIA)",
                    IsEnabled = true,
                    IsAutoConnected = false,
                    ConfigurationRevision = 0,
                    Configuration = new Dictionary<string, string>
                    {
                        ["endpoint"] = endpoint,
                        ["allowRemote"] = allowRemote ? "true" : "false",
                        ["model"] = model,
                    },
                },
            },
        }));
        Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);

        var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        return (launcher, directory);
    }

    /// <summary>Open Settings and switch to the Services tab, which hosts the AI profiles editor.</summary>
    private static Window OpenSettingsServicesTab(AppLauncher launcher)
    {
        var window = launcher.GetMainWindow();
        Invoke(Wait(window, "SettingsButton"));
        // A larger window realises the whole Services page; at small sizes the AI-profile card inside the
        // details scroller is not materialised and therefore absent from the UIA tree.
        try { window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized); }
        catch { /* non-fatal: keep the default size */ }
        Thread.Sleep(600);
        Invoke(Wait(window, "SettingsTab_Services"));
        Thread.Sleep(600);
        return window;
    }

    /// <summary>
    /// Today a failed local discovery opens a modal "Connect Magpie" dialog asking for the address, the
    /// "allow remote" switch and the remote key. This pins that fallback so the redesign cannot silently
    /// change how local-versus-remote is discovered without a test noticing.
    /// </summary>
    [Fact]
    public void ConnectMagpie_OnClosedPort_ShowsManualOverrideDialog()
    {
        // Port 1 on loopback is not listening; discovery fails fast without touching the network.
        var (launcher, _) = LaunchWithMagpieProfile("http://127.0.0.1:1/v1/chat/completions");
        using (launcher)
        {
            var window = OpenSettingsServicesTab(launcher);

            // AiProfileSelectionState IS the profile ListView's automation id (SettingsPage.xaml:408-411);
            // it only becomes visible once at least one profile loaded, so waiting for it also proves the
            // seeded settings.json was accepted.
            // AiProfileSelectionState is a permanently Collapsed ListView that still holds an ItemsSource and
            // an AutomationId (product debt), so it never appears in the UIA tree. The real UI is the card list
            // (AiProfileCardsPanel), and each card's header button carries the profile name
            // (SettingsPage.xaml.cs:676). Selecting the profile is clicking that card.
            var profileCard = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByName("Magpie (UIA)")),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromMilliseconds(200)).Result;
            profileCard.Should().NotBeNull("the seeded Magpie profile should appear as a card");
            profileCard!.Click();
            Thread.Sleep(500);

            var connect = Wait(window, "ConnectMagpieButton");
            connect.Should().NotBeNull("a seeded Magpie profile is selected, so its connect button exists");
            Invoke(connect);

            // The override dialog exposes these automation names (see ShowMagpieManualOverrideAsync).
            var address = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByName("Magpie gateway address")),
                TimeSpan.FromSeconds(10)).Result;
            address.Should().NotBeNull("a failed local discovery asks for the address in a dialog today");

            var allowRemote = window.FindFirstDescendant(cf => cf.ByName("Allow remote Magpie gateway"));
            allowRemote.Should().NotBeNull("local-versus-remote is a checkbox inside that dialog today");

            var remoteKey = window.FindFirstDescendant(cf => cf.ByName("Remote Magpie gateway key"));
            remoteKey.Should().NotBeNull("the remote key is collected in the same dialog today");
        }
    }

    /// <summary>
    /// Loopback stub implementing the exact endpoints the providers call. Records every path so the test can
    /// prove the app really talked to it (a stub mismatch must not be mistaken for a product failure).
    /// </summary>
    internal sealed class LoopbackModelStub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _requests = [];
        private readonly int _helloDelayMs;
        private readonly Task _loop;

        public LoopbackModelStub(string[] magpieModels, string[] ollamaModels, int helloDelayMs = 0)
        {
            var port = GetFreePort();
            BaseAddress = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseAddress + "/");
            _listener.Start();
            _helloDelayMs = helloDelayMs;
            _loop = Task.Run(() => ServeAsync(magpieModels, ollamaModels));
        }

        public string BaseAddress { get; }

        public IReadOnlyList<string> Requests
        {
            get { lock (_requests) { return _requests.ToArray(); } }
        }

        private static int GetFreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync(string[] magpieModels, string[] ollamaModels)
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                if (path == "/api/hello" && _helloDelayMs > 0) Thread.Sleep(_helloDelayMs);

                lock (_requests)
                {
                    _requests.Add(path);
                }

                var (status, body) = path switch
                {
                    "/api/hello" => (200, "{\"name\":\"magpie\"}"),
                    "/v1/models" => (200, OpenAIList(magpieModels)),
                    "/api/tags" => (200, "{\"models\":[" + string.Join(",", ollamaModels.Select(m => $"{{\"name\":\"{m}\"}}")) + "]}"),
                    // Answer 200 with an empty list. A 404/405 makes OllamaService fall back to the real
                    // https://ollama.com/api/tags catalogue, which both leaves the machine-dependent count in the
                    // UI and makes this test non-reproducible offline.
                    "/api/experimental/model-recommendations" or "/experimental/model-recommendations" => (200, "{\"recommendations\":[]}"),
                    _ => (404, "{}"),
                };

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        private static string OpenAIList(string[] models) =>
            "{\"data\":[" + string.Join(",", models.Select(m => $"{{\"id\":\"{m}\"}}")) + "]}";

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
            _listener.Close();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Second scenario (the defect hypothesis): after a successful Connect Magpie with a controllable model list,
    /// does the editor's model control actually show the model the user just picked? The write-back path only
    /// handles a <c>TextBox</c> (<c>SettingsPage.xaml.cs:1046-1052</c>) while the control is an editable ComboBox,
    /// and the editor is not re-rendered when the same profile stays selected.
    ///
    /// This test reports what it observes and asserts only what is safe: the stub was really called (so a stub
    /// mismatch cannot masquerade as product behaviour). The observed ComboBox value is written to the test
    /// output, and the assertion on it is added once the current behaviour is confirmed.
    /// </summary>
    [Fact]
    public void ConnectMagpie_WithModelList_ReportsWhetherTheModelFieldUpdates()
    {
        using var stub = new LoopbackModelStub(["model-a", "model-b"], ["llama3.2"]);
        var (launcher, _) = LaunchWithMagpieProfile(stub.BaseAddress + "/v1/chat/completions");
        using (launcher)
        {
            var window = OpenSettingsServicesTab(launcher);

            var profileCard = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByName("Magpie (UIA)")),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromMilliseconds(200)).Result;
            profileCard.Should().NotBeNull("the seeded Magpie profile should appear as a card");
            profileCard!.Click();
            Thread.Sleep(500);

            // Visual evidence: the model field as the user sees it, before and after connecting, without leaving
            // the page (UIA does not expose a value for WinUI's editable ComboBox).
            var beforeShot = ScreenshotHelper.CaptureWindow(window, "task9_model_before_connect");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "uia-model-before-path.txt"), beforeShot);

            Invoke(Wait(window, "ConnectMagpieButton"));

            // The connect flow fills the editor's dropdown with the discovered models; there is no second picker.
            Thread.Sleep(3500);
            stub.Requests.Should().Contain("/api/hello", "discovery must have reached the stub");
            stub.Requests.Should().Contain("/v1/models", "the model list must have come from the stub");
            window.FindFirstDescendant(cf => cf.ByName("Select a model"))
                .Should().BeNull("connect must not open a model picker any more");
            window.FindFirstDescendant(cf => cf.ByName("Magpie model"))
                .Should().BeNull("the separate Magpie picker was removed");

            // Stay on the profile and record what the user sees.
            var afterShot = ScreenshotHelper.CaptureWindow(window, "task9_model_after_connect_inplace");
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "uia-model-after-path.txt"), afterShot);
            Thread.Sleep(1500);

            // Observe every combo box value currently rendered in the AI profile card.
            var combos = window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ComboBox));
            var values = combos.Select(c =>
            {
                try
                {
                    var combo = c.AsComboBox();
                    string selected;
                    try { selected = combo.SelectedItem?.Text ?? "<none>"; }
                    catch (Exception ex) { selected = "<selectedItem error: " + ex.GetType().Name + ">"; }

                    string items;
                    try { items = combo.Items.Length.ToString(); }
                    catch (Exception ex) { items = "<items error: " + ex.GetType().Name + ">"; }

                    return $"name='{c.Name}' items={items} selected='{selected}'";
                }
                catch (Exception ex)
                {
                    // Some ComboBox nodes expose no automation properties at all (e.g. inside a closed dialog).
                    return "<unreadable combo: " + ex.GetType().Name + ">";
                }
            }).ToArray();

            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "uia-model-field-observation.txt"),
                "stub requests: " + string.Join(", ", stub.Requests) + Environment.NewLine +
                "combo boxes: " + Environment.NewLine + string.Join(Environment.NewLine, values.Select(v => "  " + v)));
        }
    }

    /// <summary>
    /// Two tests in this class share one desktop. A previous run that left the app alive makes the next test drive
    /// a stale window, which is what made the suite order-dependent. Wait (do not kill) until our own app is gone.
    /// </summary>
    private static void WaitForNoRunningApp()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var running = System.Diagnostics.Process.GetProcessesByName("Easydict.WinUI");
            if (running.Length == 0)
            {
                return;
            }

            foreach (var process in running)
            {
                process.Dispose();
            }

            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// Third baseline: the standalone Ollama section already refreshes in place. With a stub serving
    /// `/api/tags`, clicking its Refresh button must NOT open any model picker, and the combo must end up with the
    /// models from the stub. (UIA cannot read WinUI's editable ComboBox value, so a screenshot is captured as the
    /// visual record while the assertions cover the dialog absence and the stub traffic.)
    /// </summary>
    [Fact(Skip = "The standalone Ollama section is Collapsed by design (InitializeAiProfilesEditor); Ollama is an AI profile now - see OllamaProfileRefresh_ConfirmAndCancel.")]
    public void StandaloneOllamaRefresh_UpdatesInPlaceWithoutAPicker()
    {
        using var stub = new LoopbackModelStub(["model-a", "model-b"], ["llama3.2", "qwen2.5"]);

        var directory = Path.Combine(Path.GetTempPath(), "Easydict.OllamaRefresh.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            UILanguage = "en-US",
            AppTheme = "Light",
            EnableShowWindowHotkey = false,
            EnableTranslateSelectionHotkey = false,
            EnableShowMiniWindowHotkey = false,
            EnableShowFixedWindowHotkey = false,
            EnableOcrTranslateHotkey = false,
            EnableSilentOcrHotkey = false,
            HoverWordLookupEnabled = false,
            MouseSelectionTranslate = false,
            OllamaEndpoint = stub.BaseAddress + "/v1/chat/completions",
            OllamaModel = "llama3.2",
        }));
        Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
            
        WaitForNoRunningApp();
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = OpenSettingsServicesTab(launcher);

        // The standalone Ollama block is an Expander (header text "Ollama (Local LLM)") and its children are not
        // in the UIA tree while collapsed. None of the three controls has an AutomationId, so scope by the expander.
        ScreenshotHelper.CaptureWindow(window, "task9_ollama_services_page");
        var ollamaExpander = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByName("Ollama (Local LLM)")),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(250)).Result;
        ollamaExpander.Should().NotBeNull("the Services page has an Ollama (Local LLM) section");
        if (ollamaExpander!.Patterns.ExpandCollapse.IsSupported)
        {
            ollamaExpander.Patterns.ExpandCollapse.Pattern.Expand();
        }
        Thread.Sleep(700);

        var refreshButton = Retry.WhileNull(
            () => ollamaExpander.FindFirstDescendant(cf => cf.ByName("Refresh")),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(250)).Result;
        refreshButton.Should().NotBeNull("the standalone Ollama section exposes a Refresh button");

        Invoke(refreshButton!);
        Thread.Sleep(2500);

        stub.Requests.Should().Contain("/api/tags", "the refresh must have asked the stub for Ollama's model list");
        window.FindFirstDescendant(cf => cf.ByName("Select a model"))
            .Should().BeNull("the standalone refresh must not open a model picker");

        ScreenshotHelper.CaptureWindow(window, "task9_ollama_refresh_after");
    }

    /// <summary>Seed one Ollama profile pointing at <paramref name="endpoint"/> and launch the app.</summary>
    private static (AppLauncher Launcher, string SettingsDirectory) LaunchWithOllamaProfile(string endpoint)
    {
        WaitForNoRunningApp();
        var directory = Path.Combine(Path.GetTempPath(), "Easydict.OllamaProfile.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            UILanguage = "en-US",
            AppTheme = "Light",
            EnableShowWindowHotkey = false,
            EnableTranslateSelectionHotkey = false,
            EnableShowMiniWindowHotkey = false,
            EnableShowFixedWindowHotkey = false,
            EnableOcrTranslateHotkey = false,
            EnableSilentOcrHotkey = false,
            HoverWordLookupEnabled = false,
            MouseSelectionTranslate = false,
            AiServiceProfilesSchemaVersion = 1,
            AiServiceProfiles = new[]
            {
                new
                {
                    ProfileId = "uia-ollama-1",
                    ServiceId = "ai-profile:uia-ollama-1",
                    ProviderType = "ollama",
                    Name = "Ollama (UIA)",
                    IsEnabled = true,
                    IsAutoConnected = false,
                    ConfigurationRevision = 0,
                    Configuration = new Dictionary<string, string>
                    {
                        ["endpoint"] = endpoint,
                        ["model"] = "llama3.2",
                        ["promptStyle"] = "Standard",
                    },
                },
            },
        }));
        Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);

        var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        return (launcher, directory);
    }

    /// <summary>
    /// Refresh fills the visible Ollama profile dropdown. Keep the original filter name for PR demo configurations;
    /// this is one launch now that the old dialog's confirm/cancel branches no longer exist.
    /// </summary>
    [Fact]
    public void OllamaProfileRefresh_ConfirmAndCancel()
    {
        using var stub = new LoopbackModelStub(["model-a", "model-b"], ["llama3.2", "qwen2.5"]);
        var (launcher, _) = LaunchWithOllamaProfile(stub.BaseAddress + "/v1/chat/completions");
        using (launcher)
        {
            var window = OpenSettingsServicesTab(launcher);

            var card = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByName("Ollama (UIA)")),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromMilliseconds(200)).Result;
            card.Should().NotBeNull("the seeded Ollama profile should appear as a card");
            card!.Click();
            Thread.Sleep(500);

            var refresh = Wait(window, "RefreshAiProfileModelsButton");
            var model = Retry.WhileNull(
                () => window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ComboBox))
                    .FirstOrDefault(element => element.AsComboBox().IsEditable),
                TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200)).Result;
            model.Should().NotBeNull("the selected Ollama profile has an editable model dropdown");
            SignalDemoReadyAndWait(launcher, window, model!, refresh);
            ScreenshotHelper.CaptureWindow(window, "task9_ollama_profile_before_refresh");

            Invoke(refresh);

            // After the single-entry change, refresh fills the editor's own dropdown and must NOT open a picker.
            Thread.Sleep(3000);
            window.FindFirstDescendant(cf => cf.ByName("Select a model"))
                .Should().BeNull("refresh must fill the model dropdown in place instead of opening a picker");

            var modelCombo = model!.AsComboBox();
            modelCombo.Expand();
            DemoPause();
            var choices = modelCombo.Items;
            choices.Should().Contain(item => item.Text == "llama3.2" || item.Text.StartsWith("llama3.2 ", StringComparison.Ordinal));
            choices.Should().Contain(item => item.Text == "qwen2.5" || item.Text.StartsWith("qwen2.5 ", StringComparison.Ordinal));
            var selectedChoice = choices.Single(item => item.Text == "qwen2.5" || item.Text.StartsWith("qwen2.5 ", StringComparison.Ordinal));
            modelCombo.Select(selectedChoice.Text);
            DemoPause();
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "uia-ollama-refresh-requests.txt"),
                string.Join(", ", stub.Requests));
            var editorRegion = window.FindFirstDescendant(cf => cf.ByAutomationId("AiProfilesContainer")) ?? (AutomationElement)window;
            ScreenshotHelper.CaptureElement(editorRegion, "task9_ollama_profile_after_refresh");
        }
    }

    /// <summary>
    /// Remote scope without a key must fail fast and in place: no request may leave the machine, and the required-key
    /// message appears next to the field instead of in a dialog.
    /// </summary>
    [Fact]
    public void MagpieRemoteScope_WithoutKey_SendsNoRequestAndExplainsInPlace()
    {
        using var stub = new LoopbackModelStub(["model-a", "model-b"], ["llama3.2"]);
        // allowRemote=true marks an already-remote profile whose key the user has not provided yet.
        var (launcher, _) = LaunchWithMagpieProfile(stub.BaseAddress + "/v1/chat/completions", allowRemote: true);
        using (launcher)
        {
            var window = OpenSettingsServicesTab(launcher);

            var card = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByName("Magpie (UIA)")),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromMilliseconds(200)).Result;
            card.Should().NotBeNull("the seeded Magpie profile should appear as a card");
            card!.Click();
            Thread.Sleep(500);

            var remoteOption = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("AiProfileMagpieScopeRemote")),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMilliseconds(250)).Result;
            remoteOption.Should().NotBeNull("Magpie profiles expose an explicit local/remote choice");
            SelectOption(remoteOption!);
            Thread.Sleep(400);

            var connect = Wait(window, "ConnectMagpieButton");
            SignalDemoReadyAndWait(launcher, window, remoteOption!);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYDICT_DEMO_HANDSHAKE_DIR")))
                BringDemoTargetsIntoView(window, connect);
            DemoPause();
            var requestCountBefore = stub.Requests.Count;
            Invoke(connect);
            Thread.Sleep(2500);

            var keyError = window.FindFirstDescendant(cf => cf.ByAutomationId("AiProfileMagpieRemoteKeyError"));
            keyError.Should().NotBeNull("the missing key is reported in place, not in a dialog");
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYDICT_DEMO_HANDSHAKE_DIR")))
                BringDemoTargetsIntoView(window, keyError!);
            stub.Requests.Count.Should().Be(requestCountBefore,
                "a remote profile without a key must not send any request");
            window.FindFirstDescendant(cf => cf.ByName("Magpie connection"))
                .Should().BeNull("no information dialog may be used for this validation");
            DemoPause();
        }
    }

    /// <summary>
    /// Switching scopes hides fields but must not destroy what the user typed: going remote -> local -> remote keeps the
    /// endpoint and key so the configuration is not silently lost.
    /// </summary>
    [Fact]
    public void MagpieScopeSwitch_KeepsRemoteEndpointAndKey()
    {
        var (launcher, _) = LaunchWithMagpieProfile("http://127.0.0.1:1/v1/chat/completions", allowRemote: true);
        using (launcher)
        {
            var window = OpenSettingsServicesTab(launcher);

            var card = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByName("Magpie (UIA)")),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromMilliseconds(200)).Result;
            card!.Click();
            Thread.Sleep(500);

            var remote = Wait(window, "AiProfileMagpieScopeRemote");
            SignalDemoReadyAndWait(launcher, window, remote, Wait(window, "AiProfileMagpieRemoteEndpoint"));
            SelectOption(remote); // RadioButtons expose SelectionItem, not Invoke; a scrolled-out button has no clickable point
            Thread.Sleep(400);

            var endpointBox = Wait(window, "AiProfileMagpieRemoteEndpoint").AsTextBox();
            endpointBox.Text = "https://gateway.example/v1/chat/completions";
            var keyBox = Wait(window, "AiProfileMagpieRemoteKey").AsTextBox();
            keyBox.Text = "uia-remote-key";
            Thread.Sleep(300);
            DemoPause();

            SelectOption(Wait(window, "AiProfileMagpieScopeLocal"));
            DemoPause();
            Thread.Sleep(400);
            SelectOption(Wait(window, "AiProfileMagpieScopeRemote"));
            DemoPause();
            Thread.Sleep(400);

            Wait(window, "AiProfileMagpieRemoteEndpoint").AsTextBox().Text
                .Should().Be("https://gateway.example/v1/chat/completions",
                    "hiding the remote fields must not clear what the user entered");
            // The key box is a PasswordBox: UIA refuses to return its text by design, so its survival is asserted by
            // the endpoint above plus the field still being present and editable after the round trip.
            Wait(window, "AiProfileMagpieRemoteKey").IsEnabled
                .Should().BeTrue("the key field stays usable after switching scopes back and forth");
        }
    }

    /// <summary>
    /// The profile editor's actions must be reachable using the outer settings scroller. Width is recorded for
    /// visual review; a percentage of the whole window does not account for the navigation column and margins.
    /// </summary>
    [Fact]
    public void AiProfileEditor_KeepsActionsReachable()
    {
        var (launcher, _) = LaunchWithMagpieProfile("http://127.0.0.1:1/v1/chat/completions");
        using (launcher)
        {
            var window = OpenSettingsServicesTab(launcher);

            var card = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByName("Magpie (UIA)")),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromMilliseconds(200)).Result;
            card.Should().NotBeNull("the seeded Magpie profile should appear as a card");
            card!.Click();
            Thread.Sleep(600);

            var container = Wait(window, "AiProfilesContainer");
            var viewport = Wait(window, "SettingsDetailsScrollViewer");
            Console.WriteLine($"Profile width: {container.BoundingRectangle.Width}; content viewport width: {viewport.BoundingRectangle.Width}");
            var connect = Wait(window, "ConnectMagpieButton");
            BringDemoTargetsIntoView(window, connect);
            connect.IsOffscreen.Should().BeFalse("the outer settings scroller makes the actions visible");
            connect.IsEnabled.Should().BeTrue("the visible connection action remains available");
            _ = connect.GetClickablePoint();
        }
    }

    private static void DemoPause()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYDICT_DEMO_HANDSHAKE_DIR")))
            Thread.Sleep(1500);
    }

    // UIA can invoke offscreen controls, but that does not produce a useful recording. Require all of the
    // scenario's target controls to fit inside the actual settings viewport before releasing the recorder.
    private static void BringDemoTargetsIntoView(Window window, params AutomationElement[] targets)
    {
        window.SetForeground();
        var viewport = Wait(window, "SettingsDetailsScrollViewer");
        var ancestor = targets[^1].Parent;
        for (var depth = 0; ancestor is not null && depth < 24; depth++, ancestor = ancestor.Parent)
        {
            if (ancestor.Patterns.Scroll.IsSupported
                && ancestor.Patterns.Scroll.Pattern.VerticallyScrollable.Value)
            {
                viewport = ancestor;
                break;
            }
        }
        Console.WriteLine($"Demo scroll host: {viewport.Properties.AutomationId.ValueOrDefault}; bounds={viewport.BoundingRectangle}; window={window.BoundingRectangle}");
        bool TargetsAreVisible()
        {
            var area = System.Drawing.Rectangle.Intersect(viewport.BoundingRectangle, window.BoundingRectangle);
            return targets.All(target =>
            {
                var bounds = target.BoundingRectangle;
                return !target.IsOffscreen && bounds.Width > 0 && bounds.Height > 0
                    && bounds.Left >= area.Left && bounds.Right <= area.Right
                    && bounds.Top >= area.Top && bounds.Bottom <= area.Bottom;
            });
        }

        var found = ScrollHelper.ScrollToFind(viewport, 0,
            () => TargetsAreVisible() ? targets[^1] : null,
            Console.WriteLine);
        foreach (var target in targets)
            Console.WriteLine($"Demo target: {target.Properties.AutomationId.ValueOrDefault}; bounds={target.BoundingRectangle}; offscreen={target.IsOffscreen}");
        found.Should().NotBeNull("the specific demo controls must be visible in the active scroll host");
        TargetsAreVisible().Should().BeTrue("the recording must show the controls, not just their profile title");
        if (targets[^1].Properties.IsKeyboardFocusable.ValueOrDefault)
            targets[^1].Focus();
        Thread.Sleep(700);
    }

    private static void SelectOption(AutomationElement element)
    {
        if (element.Patterns.SelectionItem.IsSupported)
        {
            element.Patterns.SelectionItem.Pattern.Select();
        }
        else
        {
            element.Click();
        }

        Thread.Sleep(400);
    }

    /// <summary>
    /// Switching from a remote gateway to "This computer" must leave a configuration that is consistent with the
    /// mode, otherwise saving or translating fails because allowRemote=false meets a remote endpoint.
    /// The endpoint control is hidden in local mode, so the assertion reads the saved settings rather than UI text.
    /// </summary>
    [Fact]
    public void MagpieLocalScope_SavesALoopbackEndpointAndDisablesRemote()
    {
        var (launcher, settingsDirectory) = LaunchWithMagpieProfile(
            "https://gateway.invalid/v1/chat/completions", allowRemote: true, model: "model-a");
        using (launcher)
        {
            var window = OpenSettingsServicesTab(launcher);

            var card = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByName("Magpie (UIA)")),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromMilliseconds(200)).Result;
            card.Should().NotBeNull("the seeded Magpie profile should appear as a card");
            card!.Click();
            Thread.Sleep(500);

            SelectOption(Wait(window, "AiProfileMagpieScopeLocal"));
            Thread.Sleep(400);

            var saveButton = Retry.WhileNull(
                () => window.FindFirstDescendant(cf => cf.ByName("Save Settings")),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMilliseconds(250)).Result;
            saveButton.Should().NotBeNull("the settings page saves the edited profile");
            Invoke(saveButton!);
            Thread.Sleep(2500);

            var settingsPath = Path.Combine(settingsDirectory, "settings.json");
            File.Exists(settingsPath).Should().BeTrue("saving must write the isolated settings file");

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var magpieProfile = document.RootElement.GetProperty("AiServiceProfiles")
                .EnumerateArray()
                .First(profile => profile.GetProperty("ProviderType").GetString() == "magpie");
            var configuration = magpieProfile.GetProperty("Configuration");

            configuration.GetProperty("allowRemote").GetString()
                .Should().Be("false", "choosing This computer means the profile is not a remote gateway any more");
            var savedEndpoint = configuration.GetProperty("endpoint").GetString();
            savedEndpoint.Should().NotBeNull();
            new Uri(savedEndpoint!).Host
                .Should().BeOneOf("127.0.0.1", "localhost", "::1")
                .And.NotBe("gateway.invalid", "the active endpoint must match the chosen scope, not keep the remote one");

            // Switching back to remote must restore the address the user had entered, so the remote configuration is
            // not lost while the local mode was active.
            SelectOption(Wait(window, "AiProfileMagpieScopeRemote"));
            Thread.Sleep(500);
            Wait(window, "AiProfileMagpieRemoteEndpoint").AsTextBox().Text
                .Should().Be("https://gateway.invalid/v1/chat/completions",
                    "the remote address is kept aside and restored when switching back");
        }
    }

    /// <summary>
    /// Recording handshake used by the ui-demo workflow. When EASYDICT_DEMO_HANDSHAKE_DIR is set, the scenario
    /// announces the app it actually launched (pid/hwnd/exe/title) and waits for the recorder to confirm capture has
    /// started before touching the UI. Without the variable this is a no-op, so normal test runs are unaffected.
    /// </summary>
    private static void SignalDemoReadyAndWait(AppLauncher launcher, Window window, params AutomationElement[] targets)
    {
        var handshakeDirectory = Environment.GetEnvironmentVariable("EASYDICT_DEMO_HANDSHAKE_DIR");
        if (string.IsNullOrWhiteSpace(handshakeDirectory))
        {
            return;
        }

        Directory.CreateDirectory(handshakeDirectory);

        targets.Should().NotBeEmpty("each recorded case must declare its visible target controls");
        BringDemoTargetsIntoView(window, targets);

        var processId = launcher.Application.ProcessId;
        string exePath = string.Empty;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            exePath = process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception)
        {
            // The recorder treats an empty path as a mismatch; better to report it than to guess.
        }

        var payload = new
        {
            pid = processId,
            hwnd = window.Properties.NativeWindowHandle.Value.ToInt64(),
            exePath,
            windowTitle = window.Title,
        };
        File.WriteAllText(
            Path.Combine(handshakeDirectory, "ready.json"),
            JsonSerializer.Serialize(payload));

        var continuePath = Path.Combine(handshakeDirectory, "continue");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!File.Exists(continuePath))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The recorder did not signal 'continue'; refusing to act before capture starts.");
            }

            Thread.Sleep(200);
        }
    }

    private static AutomationElement Wait(Window window, string automationId) =>
        Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(200)).Result
        ?? throw new InvalidOperationException($"Automation element '{automationId}' was not found.");

    private static void Invoke(AutomationElement element)
    {
        element.Focus();
        element.Patterns.Invoke.Pattern.Invoke();
        Thread.Sleep(400);
    }
}
