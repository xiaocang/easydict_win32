using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using Xunit;

namespace Easydict.UIAutomation.Tests.Tests;

/// <summary>Drives actual first-launch/replay and the real Mini translation pipeline with an isolated loopback provider.</summary>
[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class QuickStartTests
{
    [Fact]
    public void FreshInstall_CanSkip_AndDoesNotAutoShowAgain()
    {
        using var settings = new SettingsScope();
        settings.EnableAutomaticEntry();
        using (var app = Launch())
        {
            var window = app.GetMainWindow();
            Wait(window, "OnboardingTryIt").Should().NotBeNull();
            Invoke(Wait(window, "OnboardingStartUsing"));
            Wait(window, "SettingsButton").Should().NotBeNull();
        }
        using (var app = Launch())
        {
            var window = app.GetMainWindow();
            Wait(window, "SettingsButton").Should().NotBeNull();
            Find(window, "OnboardingTryIt").Should().BeNull("skipping is persisted across a real restart");
        }
    }

    [Theory]
    [InlineData("{\"UILanguage\":\"en-US\"}")]
    [InlineData("{ broken settings")]
    public void ExistingInstall_IncludingMalformedSettings_DoesNotAutoShow(string existingSettings)
    {
        using var settings = new SettingsScope();
        File.WriteAllText(settings.FilePath, existingSettings);
        settings.EnableAutomaticEntry();
        using var app = Launch();
        var window = app.GetMainWindow();
        Wait(window, "SettingsButton").Should().NotBeNull();
        Find(window, "OnboardingTryIt").Should().BeNull();
    }

    [Fact]
    public void AboutReplay_DemoDoesNotCompletePractice_RealFailureCanRetrySuccessfully()
    {
        using var server = new TranslationServer();
        using var settings = new SettingsScope();
        settings.SeedProvider(server.Endpoint);
        using var app = Launch();
        var window = app.GetMainWindow();
        OpenFromAbout(window);

        // Completing both offline demonstrations cannot award real-practice success or call a provider.
        for (var i = 0; i < 4; i++) Invoke(Wait(window, "FeatureDemoNextStep"));
        Invoke(Wait(window, "OnboardingOcrDemo"));
        for (var i = 0; i < 4; i++) Invoke(Wait(window, "FeatureDemoNextStep"));
        server.RequestCount.Should().Be(0);
        ReadFlag(settings.FilePath, "QuickStartPracticeSucceeded").Should().BeFalse();
        ReadFlag(settings.FilePath, "SupportInvitationWasShown").Should().BeFalse();

        Invoke(Wait(window, "OnboardingTryIt"));
        var practice = Wait(window, "OnboardingPracticeText");
        practice.Focus();
        practice.Patterns.Text.Pattern.DocumentRange.Select();
        Invoke(Wait(window, "OnboardingTranslateSelection"));
        Wait(window, "OnboardingRetry", TimeSpan.FromSeconds(30)).Should().NotBeNull();
        ReadFlag(settings.FilePath, "QuickStartPracticeSucceeded").Should().BeFalse("an HTTP failure is not success");
        ReadFlag(settings.FilePath, "SupportInvitationWasShown").Should().BeFalse();
        server.RequestCount.Should().BeGreaterThan(0);

        server.Succeed = true;
        window.Focus();
        Invoke(Wait(window, "OnboardingRetry"));
        var translated = Wait(window, "OnboardingActualTranslation", TimeSpan.FromSeconds(30));
        translated.Name.Should().Contain(TranslationServer.Translation);
        window.Focus();
        Wait(window, "OnboardingGitHubSupport").Should().NotBeNull();
        Retry.WhileFalse(() => ReadFlag(settings.FilePath, "QuickStartPracticeSucceeded")
            && ReadFlag(settings.FilePath, "SupportInvitationWasShown"), TimeSpan.FromSeconds(5)).Success.Should().BeTrue();
        ReadFlag(settings.FilePath, "GitHubSupportWasOpened").Should().BeFalse("showing an invitation does not open GitHub or claim a Star");

        Invoke(Wait(window, "OnboardingStartUsing"));
        OpenFromAbout(window);
        ReadFlag(settings.FilePath, "QuickStartPracticeSucceeded").Should().BeTrue("manual replay preserves progress");
        Find(window, "OnboardingGitHubSupport").Should().BeNull("replay starts at the demos and does not repeat the invitation");
    }

    [Fact]
    public void DemoKeyboardControls_WorkInANarrowWindow()
    {
        using var settings = new SettingsScope();
        File.WriteAllText(settings.FilePath, "{\"UILanguage\":\"en-US\"}");
        using var app = Launch();
        var window = app.GetMainWindow();
        OpenFromAbout(window);
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        Invoke(Wait(window, "FeatureDemoNextStep"));
        WaitForStep(window, 2);
        Retry.WhileFalse(() => Find(window, "FeatureDemoStepProgress")?.BoundingRectangle.Width > 400,
            TimeSpan.FromSeconds(5)).Success.Should().BeTrue();
        var wideStop = Wait(window, "FeatureDemoStop");
        var wideExit = Wait(window, "OnboardingStartUsing");
        wideStop.BoundingRectangle.Right.Should().BeLessOrEqualTo(wideExit.BoundingRectangle.Right + 1,
            "the last demo button must fit inside the page viewport after maximizing");
        wideStop.BoundingRectangle.Width.Should().BeGreaterOrEqualTo(80, "the Stop button must not be partly clipped");
        ScreenshotHelper.CaptureWindow(window, "onboarding-maximized-controls");
        Invoke(wideStop);
        WaitForStep(window, 1);
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Normal);
        window.Patterns.Transform.Pattern.Resize(520, 680);
        var next = Wait(window, "FeatureDemoNextStep");
        window.Focus();
        next.Focus();
        Retry.WhileFalse(() => next.Properties.HasKeyboardFocus.Value, TimeSpan.FromSeconds(5)).Success.Should().BeTrue();
        Keyboard.Type(VirtualKeyShort.RIGHT);
        WaitForStep(window, 2);
        Keyboard.Type(VirtualKeyShort.HOME);
        WaitForStep(window, 1);
        Retry.WhileFalse(() => Find(window, "FeatureDemoPlayPause")?.Name == "Pause demo",
            TimeSpan.FromSeconds(5)).Success.Should().BeTrue();
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        WaitForStep(window, 1);
        Find(window, "FeatureDemoPlayPause")!.Name.Should().Be("Play demo");
        var play = Wait(window, "FeatureDemoPlayPause");
        play.Focus();
        Retry.WhileFalse(() => play.Properties.HasKeyboardFocus.Value, TimeSpan.FromSeconds(5)).Success.Should().BeTrue();
        Keyboard.Type(VirtualKeyShort.SPACE);
        Retry.WhileFalse(() => play.Name == "Pause demo", TimeSpan.FromSeconds(5)).Success.Should().BeTrue("Space must trigger the button once");
        Keyboard.Type(VirtualKeyShort.SPACE);
        Retry.WhileFalse(() => play.Name == "Play demo", TimeSpan.FromSeconds(5)).Success.Should().BeTrue();
        var exit = Wait(window, "OnboardingStartUsing");
        window.BoundingRectangle.Contains(exit.BoundingRectangle).Should().BeTrue("the exit stays inside a narrow window");
        ScreenshotHelper.CaptureWindow(window, "onboarding-narrow-keyboard");
    }

    [Fact]
    public void LeavingPractice_LateResponseCannotCompleteAReplayedTutorial()
    {
        using var server = new TranslationServer { Succeed = true, HoldResponses = true };
        using var settings = new SettingsScope();
        settings.SeedProvider(server.Endpoint);
        using var app = Launch();
        var window = app.GetMainWindow();
        OpenFromAbout(window);
        StartPractice(window);
        Retry.WhileFalse(() => server.RequestCount > 0, TimeSpan.FromSeconds(10)).Success.Should().BeTrue();
        window.Focus();
        Invoke(Wait(window, "OnboardingStartUsing"));
        OpenFromAbout(window);
        server.ReleaseResponses();
        Retry.WhileFalse(() => server.ResponseCount > 0, TimeSpan.FromSeconds(5)).Success.Should().BeTrue();
        AssertNoPracticeCompletion(settings.FilePath);
        Find(window, "OnboardingActualTranslation").Should().BeNull();
    }

    [Fact]
    public void ChangingThePracticeSelection_CancelsItsPendingResult()
    {
        using var server = new TranslationServer { Succeed = true, HoldResponses = true };
        using var settings = new SettingsScope();
        settings.SeedProvider(server.Endpoint);
        using var app = Launch();
        var window = app.GetMainWindow();
        OpenFromAbout(window);
        StartPractice(window);
        Retry.WhileFalse(() => server.RequestCount > 0, TimeSpan.FromSeconds(10)).Success.Should().BeTrue();
        var range = Find(window, "OnboardingPracticeText")!.Patterns.Text.Pattern.DocumentRange.Clone();
        range.MoveEndpointByUnit(FlaUI.Core.Definitions.TextPatternRangeEndpoint.End, FlaUI.Core.Definitions.TextUnit.Character, -8);
        range.Select();
        Wait(window, "OnboardingTranslateSelection").IsEnabled.Should().BeTrue("a new selection cancels the pending operation and can start its own query");
        server.ReleaseResponses();
        Retry.WhileFalse(() => server.ResponseCount > 0, TimeSpan.FromSeconds(5)).Success.Should().BeTrue();
        AssertNoPracticeCompletion(settings.FilePath);
        Find(window, "OnboardingActualTranslation").Should().BeNull();
    }

    [Fact]
    public void GuidedTour_PlaysBothOfflineDemos_ThenShowsAnActualQueryResult()
    {
        using var server = new TranslationServer { Succeed = true };
        using var settings = new SettingsScope();
        settings.SeedProvider(server.Endpoint, Environment.GetEnvironmentVariable("EASYDICT_ONBOARDING_DEMO_LANGUAGE") ?? "en-US");
        using var app = Launch();
        var window = app.GetMainWindow();
        OpenFromAbout(window);
        SignalRecordingReadyAndWait(app, window);
        Invoke(Wait(window, "FeatureDemoPlayPause"));
        WaitForStep(window, 5, TimeSpan.FromSeconds(22));
        server.RequestCount.Should().Be(0);
        Invoke(Wait(window, "OnboardingOcrDemo"));
        Invoke(Wait(window, "FeatureDemoPlayPause"));
        WaitForStep(window, 5, TimeSpan.FromSeconds(22));
        PauseForRecording(2000);
        server.RequestCount.Should().Be(0, "both demonstrations remain offline");
        StartPractice(window);
        Wait(window, "OnboardingActualTranslation", TimeSpan.FromSeconds(30)).Name.Should().Contain(TranslationServer.Translation);
        PauseForRecording(1500);
        window.Focus();
        Wait(window, "OnboardingGitHubSupport");
        ScreenshotHelper.CaptureWindow(window, "onboarding-complete");
        PauseForRecording(1800);
    }

    private static void StartPractice(Window window)
    {
        Invoke(Wait(window, "OnboardingTryIt"));
        PauseForRecording(700);
        var practice = Wait(window, "OnboardingPracticeText");
        practice.Focus();
        practice.Patterns.Text.Pattern.DocumentRange.Select();
        PauseForRecording(900);
        Invoke(Wait(window, "OnboardingTranslateSelection"));
    }

    private static void PauseForRecording(int milliseconds)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYDICT_DEMO_HANDSHAKE_DIR")))
            Thread.Sleep(milliseconds);
    }

    private static void WaitForStep(Window window, int number, TimeSpan? timeout = null)
    {
        var wait = Retry.WhileFalse(() => Find(window, "FeatureDemoStepProgress")?.AsProgressBar().Value == number - 1,
            timeout ?? TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
        if (!wait.Success) ScreenshotHelper.CaptureWindow(window, $"onboarding-step-timeout-{number}");
        wait.Success.Should().BeTrue($"the visible step should be {number}; actual label is {Find(window, "FeatureDemoStepCounter")?.Name}");
    }

    private static void SignalRecordingReadyAndWait(AppLauncher app, Window window)
    {
        var directory = Environment.GetEnvironmentVariable("EASYDICT_DEMO_HANDSHAKE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        window.Focus();
        Wait(window, "FeatureDemoPlayPause").IsOffscreen.Should().BeFalse();
        Wait(window, "OnboardingTryIt").IsOffscreen.Should().BeFalse();
        using var process = System.Diagnostics.Process.GetProcessById(app.Application.ProcessId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new
        {
            pid = app.Application.ProcessId,
            hwnd = window.Properties.NativeWindowHandle.Value.ToInt64(),
            exePath = process.MainModule!.FileName,
            windowTitle = window.Title
        }));
        Retry.WhileFalse(() => File.Exists(Path.Combine(directory, "continue")), TimeSpan.FromSeconds(90),
            TimeSpan.FromMilliseconds(100), throwOnTimeout: true).Success.Should().BeTrue();
    }

    private static AppLauncher Launch()
    {
        var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        return launcher;
    }

    private static void OpenFromAbout(Window window)
    {
        window.Focus();
        Invoke(Wait(window, "SettingsButton"));
        Invoke(Wait(window, "SettingsTab_About"));
        var replay = Wait(window, "QuickStartFromAbout");
        try { replay.Patterns.ScrollItem.Pattern.ScrollIntoView(); } catch { }
        Invoke(replay);
        Wait(window, "OnboardingTryIt");
    }

    private static AutomationElement? Find(AutomationElement root, string automationId)
        => root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    private static AutomationElement Wait(AutomationElement root, string automationId, TimeSpan? timeout = null)
        => Retry.WhileNull(() =>
        {
            var element = Find(root, automationId);
            return element is { IsOffscreen: false, IsEnabled: true } ? element : null;
        }, timeout ?? TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100), throwOnTimeout: true).Result!;

    private static void Invoke(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported) element.Patterns.Invoke.Pattern.Invoke();
        else if (element.Patterns.SelectionItem.IsSupported) element.Patterns.SelectionItem.Pattern.Select();
        else element.Click();
    }

    private static void AssertNoPracticeCompletion(string settingsPath)
    {
        // Let the released SSE response and UI dispatcher finish; an immediate file read could miss a late callback.
        Retry.WhileFalse(() => ReadFlag(settingsPath, "QuickStartPracticeSucceeded")
            || ReadFlag(settingsPath, "SupportInvitationWasShown"), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100))
            .Success.Should().BeFalse("a cancelled operation must not award progress when its response arrives");
    }

    private static bool ReadFlag(string path, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    private sealed class SettingsScope : IDisposable
    {
        private readonly string? _oldSettings = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        private readonly string? _oldAuto = Environment.GetEnvironmentVariable("EASYDICT_TEST_ONBOARDING_AUTO");
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Easydict.QuickStart.Tests", Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(DirectoryPath, "settings.json");

        public SettingsScope()
        {
            Directory.CreateDirectory(DirectoryPath);
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", DirectoryPath);
            Environment.SetEnvironmentVariable("EASYDICT_TEST_ONBOARDING_AUTO", null);
        }

        public void EnableAutomaticEntry() => Environment.SetEnvironmentVariable("EASYDICT_TEST_ONBOARDING_AUTO", "1");

        public void SeedProvider(string endpoint, string uiLanguage = "en-US")
        {
            const string serviceId = "ai-profile:uia-quickstart";
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new
            {
                UILanguage = uiLanguage, AppTheme = "Light", HistoryEnabled = false,
                SelectedLanguages = new[] { "zh", "en" }, FirstLanguage = "zh", SecondLanguage = "en",
                EnableShowWindowHotkey = false, EnableTranslateSelectionHotkey = false,
                EnableShowMiniWindowHotkey = false, EnableShowFixedWindowHotkey = false,
                EnableOcrTranslateHotkey = false, EnableSilentOcrHotkey = false,
                HoverWordLookupEnabled = false, MouseSelectionTranslate = false,
                MiniWindowEnabledServices = new[] { serviceId },
                MainWindowEnabledServices = new[] { serviceId },
                MiniWindowServiceEnabledQuery = new Dictionary<string, bool> { [serviceId] = true },
                AiServiceProfilesSchemaVersion = 1,
                AiServiceProfiles = new[] { new
                {
                    ProfileId = "uia-quickstart", ServiceId = serviceId, ProviderType = "custom-openai",
                    Name = uiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "本地测试服务" : "Practice test",
                    IsEnabled = true, IsAutoConnected = false, ConfigurationRevision = 0,
                    Configuration = new Dictionary<string, string> { ["endpoint"] = endpoint, ["model"] = "practice-test" }
                } }
            }));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", _oldSettings);
            Environment.SetEnvironmentVariable("EASYDICT_TEST_ONBOARDING_AUTO", _oldAuto);
        }
    }

    private sealed class TranslationServer : IDisposable
    {
        public const string Translation = "一个小小的改变，就能带来很大的不同。";
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<bool> _responseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;
        private int _requestCount;
        private int _responseCount;
        private volatile bool _succeed;
        public bool HoldResponses { get; init; }
        public bool Succeed { get => _succeed; set => _succeed = value; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public int ResponseCount => Volatile.Read(ref _responseCount);
        public void ReleaseResponses() => _responseGate.TrySetResult(true);
        public string Endpoint { get; }

        public TranslationServer()
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            var prefix = $"http://127.0.0.1:{port}/";
            Endpoint = prefix + "v1/chat/completions";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }
                    Interlocked.Increment(ref _requestCount);
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream);
                        await reader.ReadToEndAsync();
                        if (HoldResponses) await _responseGate.Task;
                        if (!_succeed)
                        {
                            context.Response.StatusCode = 503;
                            continue;
                        }
                        context.Response.ContentType = "text/event-stream";
                        var content = JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = Translation }, finish_reason = (string?)null } } });
                        var bytes = Encoding.UTF8.GetBytes($"data: {content}\n\ndata: [DONE]\n\n");
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    catch (HttpListenerException) { /* The cancellation case intentionally closes the client. */ }
                    catch (IOException) { /* The cancellation case intentionally closes the client. */ }
                    catch (ObjectDisposedException) { }
                    finally
                    {
                        try { context.Response.Close(); } catch { }
                        Interlocked.Increment(ref _responseCount);
                    }
                }
            });
        }

        public void Dispose()
        {
            ReleaseResponses();
            _listener.Stop();
            _listener.Close();
            try { _loop.GetAwaiter().GetResult(); } catch (ObjectDisposedException) { }
        }
    }
}
