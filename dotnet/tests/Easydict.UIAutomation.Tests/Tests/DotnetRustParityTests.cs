using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Trait("Category", "DotnetRustParity")]
[Collection("UIAutomation")]
public sealed class DotnetRustParityTests : IDisposable
{
    private const string EnableEnvironmentVariable = "EASYDICT_UIA_DOTNET_RUST_PARITY";
    private const string RustPreviewExeEnvironmentVariable = "EASYDICT_RUST_PREVIEW_EXE_PATH";
    private const string RustPreviewBuildEnvironmentVariable = "EASYDICT_RUST_PREVIEW_BUILD";
    private const string SettingsSectionEnvironmentVariable = "EASYDICT_UIA_PARITY_SETTINGS_SECTION";
    private const string EffectsEnvironmentVariable = "EASYDICT_UIA_PARITY_EFFECTS";
    private const string MainEffectsOnlyEnvironmentVariable = "EASYDICT_UIA_PARITY_MAIN_EFFECTS_ONLY";
    private const string MainInitialOnlyEnvironmentVariable = "EASYDICT_UIA_PARITY_MAIN_INITIAL_ONLY";
    private const string MainOperationsScopeEnvironmentVariable = "EASYDICT_UIA_PARITY_MAIN_OPERATIONS_SCOPE";
    private const string MainDropdownEnvironmentVariable = "EASYDICT_UIA_PARITY_MAIN_DROPDOWN";
    private const string FloatingScopeEnvironmentVariable = "EASYDICT_UIA_PARITY_FLOATING_SCOPE";
    private const string FloatingWindowEnvironmentVariable = "EASYDICT_UIA_PARITY_FLOATING_WINDOW";
    private const string FloatingDropdownEnvironmentVariable = "EASYDICT_UIA_PARITY_FLOATING_DROPDOWN";
    private const string DropdownOptionIndexesEnvironmentVariable = "EASYDICT_UIA_PARITY_DROPDOWN_OPTION_INDEXES";
    private const string AllowOversizedCaptureEnvironmentVariable = "EASYDICT_UIA_ALLOW_OVERSIZED_CAPTURE";
    private const string UiLanguageEnvironmentVariable = "EASYDICT_UIA_PARITY_UI_LANGUAGE";
    private const string ThemeEnvironmentVariable = "EASYDICT_UIA_PARITY_THEME";
    private const string TrayContextMenuPointEnvironmentVariable = "EASYDICT_UIA_TRAY_CONTEXT_MENU_POINT";
    private const string TrayContextMenuDelayEnvironmentVariable = "EASYDICT_UIA_TRAY_CONTEXT_MENU_DELAY_MS";
    private const string TrayExtraItemsEnvironmentVariable = "EASYDICT_UIA_TRAY_EXTRA_ITEMS";
    private const string TrayMaxHeightEnvironmentVariable = "EASYDICT_UIA_TRAY_MAX_HEIGHT_DIPS";
    private const string RustFluentTrayMenuWindowTitle = "WinFluent Tray Menu";
    private const int TrayMenuFluentAuditRoundCount = 20;
    private const int UiaShowTrayContextMenuMessage = 0xAEAD;
    private const uint SendMessageTimeoutAbortIfHung = 0x0002;

    private readonly ITestOutputHelper _output;
    private readonly AppLauncher _dotnetLauncher = new();
    private readonly Dictionary<RustPreviewSessionKey, RustPreviewSession> _rustPreviewSessions = [];
    private static bool _parityDpiAwarenessAttempted;
    private static readonly object RustMetricsLock = new();
    private static int _rustProcessStarts;
    private static int _rustRenderRequests;
    private static readonly List<long> RustRenderDurationsMs = [];
    private static readonly Dictionary<string, string> RustBoundsBySchemaPath =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> RustDiagnosticsBySchemaPath =
        new(StringComparer.OrdinalIgnoreCase);
    private static int _rustTimeouts;
    private static int _rustHarnessInvalid;

    private static void ResetRustPreviewRunMetrics()
    {
        lock (RustMetricsLock)
        {
            _rustProcessStarts = 0;
            _rustRenderRequests = 0;
            RustRenderDurationsMs.Clear();
            RustBoundsBySchemaPath.Clear();
            RustDiagnosticsBySchemaPath.Clear();
            _rustTimeouts = 0;
            _rustHarnessInvalid = 0;
            WriteRustPreviewRunMetricsLocked();
        }
    }


    private static void RecordRustPreviewProcessStart()
    {
        lock (RustMetricsLock)
        {
            _rustProcessStarts++;
            WriteRustPreviewRunMetricsLocked();
        }
    }

    private static void RecordRustPreviewRenderSuccess(long durationMs)
    {
        lock (RustMetricsLock)
        {
            _rustRenderRequests++;
            RustRenderDurationsMs.Add(Math.Max(0, durationMs));
            WriteRustPreviewRunMetricsLocked();
        }
    }

    private static RustPreviewRunMetrics SnapshotRustPreviewRunMetrics()
    {
        lock (RustMetricsLock)
        {
            return new RustPreviewRunMetrics(
                "easydict.ui-parity.run-metrics.v1",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                _rustProcessStarts,
                _rustRenderRequests,
                RustRenderDurationsMs.ToArray(),
                _rustTimeouts,
                _rustHarnessInvalid);
        }
    }

    private static void RecordRustPreviewTimeout()
    {
        lock (RustMetricsLock)
        {
            _rustTimeouts++;
            WriteRustPreviewRunMetricsLocked();
        }
    }

    private static void RecordRustPreviewHarnessInvalid()
    {
        lock (RustMetricsLock)
        {
            _rustHarnessInvalid++;
            WriteRustPreviewRunMetricsLocked();
        }
    }

    private static void WriteRustPreviewRunMetricsLocked()
    {
        try
        {
            var path = Path.Combine(ScreenshotHelper.OutputDir, "ui-parity-run-metrics.json");
            var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
            var metrics = new RustPreviewRunMetrics(
                "easydict.ui-parity.run-metrics.v1",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                _rustProcessStarts,
                _rustRenderRequests,
                RustRenderDurationsMs.ToArray(),
                _rustTimeouts,
                _rustHarnessInvalid);
            var json = JsonSerializer.Serialize(
                metrics,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (IOException)
        {
            // Metrics are diagnostic-only and must not change parity behavior.
        }
        catch (UnauthorizedAccessException)
        {
            // Metrics are diagnostic-only and must not change parity behavior.
        }
    }

    public DotnetRustParityTests(ITestOutputHelper output)
    {
        _output = output;
        ResetRustPreviewRunMetrics();
    }

    private RustPreviewRenderResult RenderSettingsPreview(
        SettingsParityCaptureStep step,
        ITestOutputHelper _)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EASYDICT_PREVIEW_SETTINGS_OPEN"] = "1",
            ["EASYDICT_PREVIEW_SETTINGS_SECTION"] = step.Section.Id,
            ["EASYDICT_PREVIEW_SETTINGS_MOUSE_SELECTION_TRANSLATE"] = "1",
            ["EASYDICT_PREVIEW_SETTINGS_FIXED_ALWAYS_ON_TOP"] = "0",
            ["EASYDICT_PREVIEW_SETTINGS_HIDE_EMPTY_SERVICE_RESULTS"] = "1",
            ["EASYDICT_PREVIEW_SETTINGS_IMPORTED_MDX"] = "1"
        };
        if (step.Section == SettingsParitySection.Views)
        {
            environment["EASYDICT_PREVIEW_SETTINGS_VIEW_SERVICE_PROFILE"] = "dotnet-reference";
        }
        var dotnetVersion = ResolveDotnetWinUiVersion();
        if (!string.IsNullOrWhiteSpace(dotnetVersion))
        {
            environment["EASYDICT_PREVIEW_APP_VERSION"] = dotnetVersion;
        }
        if (step.RustTranslationLanguagesExpanded)
        {
            environment["EASYDICT_PREVIEW_TRANSLATION_LANGUAGES_EXPANDED"] = "1";
        }
        if (step.HoveredTab is { } hoveredTab)
        {
            environment["EASYDICT_PREVIEW_SETTINGS_HOVERED_SECTION"] = hoveredTab.Id;
        }
        if (step.PressedTab is { } pressedTab)
        {
            environment["EASYDICT_PREVIEW_SETTINGS_PRESSED_SECTION"] = pressedTab.Id;
        }
        AddPreviewOverride(environment, "EASYDICT_PREVIEW_SETTINGS_TTS_SPEED_STATE", step.RustTtsSpeedState);
        AddPreviewOverride(environment, "EASYDICT_PREVIEW_SETTINGS_AUTO_PLAY_STATE", step.RustAutoPlayState);
        AddPreviewOverride(environment, "EASYDICT_PREVIEW_SETTINGS_IMPORT_MDX_STATE", step.RustImportMdxState);
        AddPreviewOverride(
            environment,
            "EASYDICT_PREVIEW_SETTINGS_INTERNATIONAL_TOGGLE_STATE",
            step.RustInternationalToggleState);
        AddPreviewOverride(
            environment,
            "EASYDICT_PREVIEW_SETTINGS_DEEPL_EXPANDER_STATE",
            step.RustDeepLExpanderState);
        if (!string.IsNullOrWhiteSpace(step.RustServiceExpanderState) &&
            !string.IsNullOrWhiteSpace(step.RustExpandedServiceConfigurations))
        {
            environment["EASYDICT_PREVIEW_SETTINGS_SERVICE_EXPANDER_ID"] =
                step.RustExpandedServiceConfigurations;
            environment["EASYDICT_PREVIEW_SETTINGS_SERVICE_EXPANDER_STATE"] =
                step.RustServiceExpanderState;
        }
        if (!string.IsNullOrWhiteSpace(step.RustExpandedServiceConfigurations))
        {
            environment["EASYDICT_PREVIEW_SETTINGS_EXPANDED_SERVICE_CONFIGURATIONS"] =
                step.RustExpandedServiceConfigurations;
            if (string.Equals(
                step.RustExpandedServiceConfigurations,
                "ollama",
                StringComparison.OrdinalIgnoreCase))
            {
                environment["EASYDICT_PREVIEW_SETTINGS_OLLAMA_MODEL_EMPTY"] = "1";
            }
        }
        AddPreviewOverride(
            environment,
            "EASYDICT_PREVIEW_SETTINGS_LOCAL_AI_PROVIDER",
            step.RustLocalAiProvider);
        if (step.ScrollPercent > 0)
        {
            environment["EASYDICT_PREVIEW_SCROLL_PERCENT"] =
                step.ScrollPercent.ToString(CultureInfo.InvariantCulture);
            environment["EASYDICT_PREVIEW_SCROLL_TARGET"] = "MainScrollViewer";
        }

        return RenderRustPreview(
            "settings",
            "before_translate",
            ResolveRustPreviewTheme("system"),
            environment,
            step.Key,
            [],
            widthDips: 846,
            heightDips: 913,
            ResolvePreviewDpi());
    }

    private RustPreviewRenderResult RenderMainPreview(
        string scenario,
        string theme,
        ITestOutputHelper _,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        string? schemaSuffix = null)
    {
        var environment = extraEnvironment == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(extraEnvironment, StringComparer.OrdinalIgnoreCase);
        var window = environment.TryGetValue("EASYDICT_PREVIEW_WINDOW", out var requestedWindow)
            ? requestedWindow
            : "main";
        var dpi = ResolvePreviewDpi(environment);
        var (defaultWidth, defaultHeight) = DefaultPreviewDimensions(window, dpi);
        var widthDips = ResolvePreviewDimension(
            environment,
            "EASYDICT_PREVIEW_WIDTH_DIPS",
            defaultWidth);
        var heightDips = ResolvePreviewDimension(
            environment,
            "EASYDICT_PREVIEW_HEIGHT_DIPS",
            defaultHeight);

        return RenderRustPreview(
            window,
            scenario,
            theme,
            environment,
            $"{window}-{scenario}{schemaSuffix ?? string.Empty}",
            [],
            widthDips,
            heightDips,
            dpi);
    }

    private RustPreviewRenderResult RenderWindowPreview(
        string windowKind,
        string theme,
        ITestOutputHelper output,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var environment = extraEnvironment == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(extraEnvironment, StringComparer.OrdinalIgnoreCase);
        environment["EASYDICT_PREVIEW_WINDOW"] = windowKind;
        if (string.Equals(windowKind, "mini", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(windowKind, "fixed", StringComparison.OrdinalIgnoreCase))
        {
            environment["EASYDICT_PREVIEW_FLOATING_CONTENT"] = "empty";
        }
        return RenderMainPreview("initial", theme, output, environment);
    }

    private RustPreviewRenderResult RenderRustPreview(
        string window,
        string scenario,
        string theme,
        IReadOnlyDictionary<string, string> overrides,
        string artifactStem,
        IReadOnlyList<string> requiredControlIds,
        float widthDips,
        float heightDips,
        double dpi)
    {
        var key = new RustPreviewSessionKey(
            window.Trim().ToLowerInvariant(),
            theme.Trim().ToLowerInvariant(),
            ResolveParityUiLanguage().Trim().ToLowerInvariant(),
            Math.Round(dpi, 3));
        if (!_rustPreviewSessions.TryGetValue(key, out var session))
        {
            session = RustPreviewSession.Launch(
                window,
                theme,
                ResolveParityUiLanguage(),
                dpi,
                widthDips,
                heightDips,
                ScreenshotHelper.OutputDir,
                _output);
            _rustPreviewSessions.Add(key, session);
        }

        try
        {
            return session.Render(
                scenario,
                overrides,
                SanitizeFileName(artifactStem),
                requiredControlIds,
                widthDips,
                heightDips);
        }
        catch
        {
            _rustPreviewSessions.Remove(key);
            session.Dispose();
            throw;
        }
    }

    private static void AddPreviewOverride(
        IDictionary<string, string> environment,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            environment[key] = value;
        }
    }

    private static (float Width, float Height) DefaultPreviewDimensions(string window, double dpi) =>
        window.Trim().ToLowerInvariant() switch
        {
            "mini" => (640, 400),
            "fixed" => (640, 560),
            "pop-button" or "popbutton" => (30, 30),
            "capture-overlay" => (
                (float)Math.Max(1, ScreenshotHelper.GetVirtualScreenBounds().Width / (dpi / 96.0)),
                (float)Math.Max(1, ScreenshotHelper.GetVirtualScreenBounds().Height / (dpi / 96.0))),
            "settings" => (846, 913),
            _ => (1000, 700)
        };

    private static float ResolvePreviewDimension(
        IReadOnlyDictionary<string, string> environment,
        string key,
        float fallback)
    {
        var value = environment.TryGetValue(key, out var requestValue)
            ? requestValue
            : Environment.GetEnvironmentVariable(key);
        return float.TryParse(
                   value,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var parsed) &&
               float.IsFinite(parsed) &&
               parsed > 0
            ? parsed
            : fallback;
    }

    private static double ResolvePreviewDpi(
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var value = environment != null &&
                    environment.TryGetValue("EASYDICT_PREVIEW_DPI", out var requestValue)
            ? requestValue
            : Environment.GetEnvironmentVariable("EASYDICT_PREVIEW_DPI");
        return double.TryParse(
                   value,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var dpi) &&
               double.IsFinite(dpi) &&
               dpi > 0
            ? dpi
            : 96.0;
    }

    [Fact]
    public void PreviewControlRequest_ShouldSerializeProtocolFields()
    {
        var request = new PreviewControlRequest(
            "session-1",
            4,
            "render",
            "before_translate",
            "main.target-language-dropdown-open",
            820.5f,
            640.25f,
            new Dictionary<string, string>
            {
                ["EASYDICT_PREVIEW_MAIN_OPEN_DROPDOWN"] = "target"
            },
            ["main.window", "TargetLangCombo"]);

        using var document = JsonDocument.Parse(SerializePreviewControlRequest(request));
        var root = document.RootElement;
        root.GetProperty("sessionId").GetString().Should().Be("session-1");
        root.GetProperty("generation").GetUInt64().Should().Be(4);
        root.GetProperty("command").GetString().Should().Be("render");
        root.GetProperty("artifactStem").GetString().Should().Be("main.target-language-dropdown-open");
        root.GetProperty("widthDips").GetSingle().Should().Be(820.5f);
        root.GetProperty("heightDips").GetSingle().Should().Be(640.25f);
        root.GetProperty("overrides")
            .GetProperty("EASYDICT_PREVIEW_MAIN_OPEN_DROPDOWN")
            .GetString()
            .Should().Be("target");
        root.GetProperty("requiredControlIds")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should().Equal("main.window", "TargetLangCombo");
        root.TryGetProperty("schema", out _).Should().BeFalse();
    }

    [Fact]
    public void PreviewControlAck_ShouldIgnoreOlderGenerationAndMatchCurrentGeneration()
    {
        var staleJson = JsonSerializer.Serialize(new PreviewControlAck(
            "easydict.preview-ack.v1",
            "session-1",
            2,
            "rendered",
            null,
            null,
            new Dictionary<string, string>(),
            [],
            [],
            12));
        var currentJson = JsonSerializer.Serialize(new PreviewControlAck(
            "easydict.preview-ack.v1",
            "session-1",
            3,
            "rendered",
            null,
            null,
            new Dictionary<string, string>(),
            [],
            [],
            14));

        ParseMatchingPreviewAck(staleJson, "session-1", 3).Should().BeNull();
        var currentAck = ParseMatchingPreviewAck(currentJson, "session-1", 3);
        currentAck.Should().NotBeNull();
        currentAck!.Generation.Should().Be(3);
    }

    [Fact]
    public void PreviewControlValidation_ShouldRejectInvalidRequestsAndErrorAcks()
    {
        var invalidRequest = new PreviewControlRequest(
            "session-1",
            1,
            "render",
            "before_translate",
            "../escape",
            float.NaN,
            640,
            new Dictionary<string, string>(),
            []);
        Action serialize = () => SerializePreviewControlRequest(invalidRequest);
        serialize.Should()
            .Throw<RustPreviewControlException>()
            .WithMessage("*preview-invalid-artifact-stem*");

        var errorAck = new PreviewControlAck(
            "easydict.preview-ack.v1",
            "session-1",
            1,
            "error",
            "preview-invalid-dimensions",
            "widthDips must be positive",
            new Dictionary<string, string>(),
            [],
            [],
            null);
        Action validate = () => ValidateRenderedPreviewAck(errorAck);
        validate.Should()
            .Throw<RustPreviewControlException>()
            .Where(error => error.ErrorCode == "preview-invalid-dimensions");

        Action changeFixedTheme = () => RustPreviewSession.ValidateSessionEnvironment(
            new Dictionary<string, string>
            {
                ["EASYDICT_PREVIEW_THEME"] = "dark"
            },
            "main",
            "light",
            "zh-CN",
            96);
        changeFixedTheme.Should()
            .Throw<RustPreviewControlException>()
            .Where(error => error.ErrorCode == "session-invariant-mismatch");
    }

    [Fact]
    public void RustComboBoxes_ShouldExpandAndCommitMouseSelection()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine($"Skipped: set {EnableEnvironmentVariable}=1.");
            return;
        }
        EnsureParityDpiAwareness();

        var cases = new[]
        {
            new RustComboBoxMouseSelectionCase("main", 419, 820, "SourceLangCombo", "Chinese (Simplified)", 1, 0, 9, "message=SourceLanguageChanged value=zh-Hans", new Dictionary<string, string> { ["EASYDICT_PREVIEW_MAIN_SOURCE_LANGUAGE"] = "auto", ["EASYDICT_PREVIEW_MAIN_TARGET_LANGUAGE"] = "auto" }),
            new RustComboBoxMouseSelectionCase("main", 419, 820, "TargetLangCombo", "Chinese (Simplified)", 1, 0, 9, "message=TargetLanguageChanged value=zh-Hans", new Dictionary<string, string> { ["EASYDICT_PREVIEW_MAIN_SOURCE_LANGUAGE"] = "auto", ["EASYDICT_PREVIEW_MAIN_TARGET_LANGUAGE"] = "auto" }),
            new RustComboBoxMouseSelectionCase("main", 320, 820, "SourceLangComboNarrow", "Chinese (Simplified)", 1, 0, 9, "message=SourceLanguageChanged value=zh-Hans", new Dictionary<string, string> { ["EASYDICT_PREVIEW_MAIN_SOURCE_LANGUAGE"] = "auto", ["EASYDICT_PREVIEW_MAIN_TARGET_LANGUAGE"] = "auto" }),
            new RustComboBoxMouseSelectionCase("main", 320, 820, "TargetLangComboNarrow", "Chinese (Simplified)", 1, 0, 9, "message=TargetLanguageChanged value=zh-Hans", new Dictionary<string, string> { ["EASYDICT_PREVIEW_MAIN_SOURCE_LANGUAGE"] = "auto", ["EASYDICT_PREVIEW_MAIN_TARGET_LANGUAGE"] = "auto" }),
            new RustComboBoxMouseSelectionCase("fixed", 419, 820, "fixed.source_language", "Chinese (Simplified)", 1, 0, 9, "message=FloatingSourceLanguageChanged value=zh-Hans", new Dictionary<string, string> { ["EASYDICT_PREVIEW_MAIN_SOURCE_LANGUAGE"] = "auto", ["EASYDICT_PREVIEW_MAIN_TARGET_LANGUAGE"] = "auto" }, PreviewWindow: "fixed"),
            new RustComboBoxMouseSelectionCase("fixed", 419, 820, "fixed.target_language", "Chinese (Simplified)", 1, 0, 9, "message=FloatingTargetLanguageChanged value=zh-Hans", new Dictionary<string, string> { ["EASYDICT_PREVIEW_MAIN_SOURCE_LANGUAGE"] = "auto", ["EASYDICT_PREVIEW_MAIN_TARGET_LANGUAGE"] = "auto" }, PreviewWindow: "fixed"),
            new RustComboBoxMouseSelectionCase("long_document", 1200, 820, "main.long-doc.source_language", "Chinese (Simplified)", 1, 0, 9, "message=LongDocumentSourceLanguageChanged value=zh-Hans", new Dictionary<string, string> { ["EASYDICT_PREVIEW_SCROLL_TARGET"] = "main.long-doc.scroll", ["EASYDICT_PREVIEW_SCROLL_PERCENT"] = "1" }),
            new RustComboBoxMouseSelectionCase("long_document", 1200, 820, "main.long-doc.target_language", "Chinese (Simplified)", 0, 1, 8, "message=LongDocumentTargetLanguageChanged value=zh-Hans", new Dictionary<string, string> { ["EASYDICT_PREVIEW_SCROLL_TARGET"] = "main.long-doc.scroll", ["EASYDICT_PREVIEW_SCROLL_PERCENT"] = "1" }),
            new RustComboBoxMouseSelectionCase("long_document", 1200, 820, "main.long-doc.service", "Volcano", 15, 16, 17, "message=LongDocumentServiceChanged value=volcano", new Dictionary<string, string> { ["EASYDICT_PREVIEW_SCROLL_TARGET"] = "main.long-doc.scroll", ["EASYDICT_PREVIEW_SCROLL_PERCENT"] = "1" }, ScrollAwayBeforeSelection: true),
            new RustComboBoxMouseSelectionCase("long_document", 1200, 820, "main.long-doc.input_mode", "Text", 0, 2, 3, "message=LongDocumentInputModeChanged value=plaintext", new Dictionary<string, string> { ["EASYDICT_PREVIEW_SCROLL_TARGET"] = "main.long-doc.scroll", ["EASYDICT_PREVIEW_SCROLL_PERCENT"] = "1" }),
            new RustComboBoxMouseSelectionCase("long_document", 1200, 820, "main.long-doc.output_mode", "Bilingual", 1, 0, 3, "message=LongDocumentOutputModeChanged value=bilingual", new Dictionary<string, string> { ["EASYDICT_PREVIEW_SCROLL_TARGET"] = "main.long-doc.scroll", ["EASYDICT_PREVIEW_SCROLL_PERCENT"] = "1" })
        };

        foreach (var testCase in cases)
        {
            AssertRustComboBoxMouseSelection(testCase);
        }
    }

    private void AssertRustComboBoxMouseSelection(RustComboBoxMouseSelectionCase testCase)
    {
        using var session = RustPreviewSession.Launch(
            testCase.PreviewWindow,
            "light",
            "en-US",
            ResolvePreviewDpi(),
            testCase.WidthDips,
            testCase.HeightDips,
            ScreenshotHelper.OutputDir,
            _output,
            new Dictionary<string, string>(testCase.EnvironmentOverrides)
            {
                ["EASYDICT_RS_DEBUG"] = "1"
            });
        session.WaitForDebugLine(0, "subscriptions", TimeSpan.FromSeconds(5));
        var rendered = session.Render(
            testCase.Scenario,
            testCase.EnvironmentOverrides,
            $"combo-{testCase.ControlId}",
            [testCase.ControlId],
            testCase.WidthDips,
            testCase.HeightDips);
        var dimensions = TryReadRustBoundsControlDimensions(rendered.BoundsPath);
        CaptureRustArtifacts(rendered, $"combo-open-{testCase.ControlId}");
        dimensions.Should().ContainKey(testCase.ControlId);
        var window = rendered.Window;
        EnsureWindowForegroundForMouseInput(
            window,
            $"mouse-select {testCase.ControlId}");
        ScreenshotHelper.EnsureWindowReadyForCapture(
            window,
            $"mouse-select {testCase.ControlId}");
        var comboBounds = dimensions[testCase.ControlId].BoundsDips
            ?? throw new InvalidOperationException(
                $"Missing bounds for {testCase.ControlId}.");
        comboBounds = AdjustRustBoundsForPreviewScroll(
            testCase,
            dimensions,
            comboBounds);
        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        var comboPoint = new Point(
            windowBounds.Left + (int)Math.Round(
                (comboBounds.Left + comboBounds.Width / 2) * dpiScale),
            windowBounds.Top + (int)Math.Round(
                (comboBounds.Top + comboBounds.Height / 2) * dpiScale));
        _output.WriteLine(
            $"{testCase.ControlId}: window={windowBounds}, dpi={dpiScale:0.###}, comboDips={comboBounds}, comboPoint={comboPoint}.");
        if (testCase.ScrollAwayBeforeSelection)
        {
            ScrollAwayAndDismiss(testCase.SelectedRow);
        }
        ClickSelection(
            testCase.OptionText,
            testCase.OptionRow,
            testCase.SelectedRow,
            testCase.ExpectedDebugLine);

        void ClickSelection(
            string optionText,
            int optionRow,
            int selectedRow,
            string expectedDebugLine)
        {
            var marker = session.CaptureDebugLineMarker();
            Mouse.MoveTo(comboPoint);
            Thread.Sleep(150);
            Mouse.Down(MouseButton.Left);
            Thread.Sleep(100);
            Mouse.Up(MouseButton.Left);
            var geometry = ParseRustComboOverlayGeometry(
                session.WaitForDebugLineValue(
                    marker,
                    $"[parity] combo_overlay_geometry control_id={testCase.ControlId}",
                    TimeSpan.FromSeconds(5)),
                testCase.ControlId);
            AssertOverlayWithinWindow(geometry, testCase.ItemCount, testCase.WidthDips, testCase.HeightDips);
            Thread.Sleep(200);
            var optionPoint = RustComboBoxOptionClickPoint(
                window,
                geometry,
                testCase,
                optionText,
                optionRow,
                selectedRow);
            if (optionRow * geometry.RowHeight + geometry.RowHeight / 2 > geometry.MenuHeight)
            {
                Mouse.MoveTo(optionPoint);
                Thread.Sleep(150);
                Mouse.Scroll(-20);
                session.WaitForDebugLine(
                    marker,
                    $"[parity] combo_scroll_offset control_id={testCase.ControlId}",
                    TimeSpan.FromSeconds(5));
                Thread.Sleep(150);
            }
            CaptureForegroundWindow(window, $"combo-open-{testCase.ControlId}");
            _output.WriteLine(
                $"{testCase.ControlId}: option={optionText}, optionPoint={optionPoint}.");
            Mouse.MoveTo(optionPoint);
            Thread.Sleep(150);
            Mouse.Down(MouseButton.Left);
            Thread.Sleep(100);
            Mouse.Up(MouseButton.Left);
            session.WaitForDebugLine(marker, expectedDebugLine, TimeSpan.FromSeconds(5));
            Thread.Sleep(300);
        }

        void ScrollAwayAndDismiss(int selectedRow)
        {
            var marker = session.CaptureDebugLineMarker();
            Mouse.MoveTo(comboPoint);
            Thread.Sleep(150);
            Mouse.Down(MouseButton.Left);
            Thread.Sleep(100);
            Mouse.Up(MouseButton.Left);
            var geometry = ParseRustComboOverlayGeometry(
                session.WaitForDebugLineValue(
                    marker,
                    $"[parity] combo_overlay_geometry control_id={testCase.ControlId}",
                    TimeSpan.FromSeconds(5)),
                testCase.ControlId);
            AssertOverlayWithinWindow(geometry, testCase.ItemCount, testCase.WidthDips, testCase.HeightDips);
            var selectedPoint = RustComboBoxOptionClickPoint(
                window,
                geometry,
                testCase,
                testCase.OptionText,
                selectedRow,
                selectedRow);
            Mouse.MoveTo(selectedPoint);
            Thread.Sleep(150);
            Mouse.Scroll(20);
            session.WaitForDebugLine(
                marker,
                $"[parity] combo_scroll_offset control_id={testCase.ControlId}",
                TimeSpan.FromSeconds(5));
            Thread.Sleep(300);
            Mouse.Click(GetWindowRelativePoint(window, 0.50, 0.02));
            Thread.Sleep(300);
        }
    }

    [Fact]
    public void RustLongDocumentLayout_ShouldHonorSpansAndWrapping()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine($"Skipped: set {EnableEnvironmentVariable}=1.");
            return;
        }

        using var session = RustPreviewSession.Launch(
            "main",
            "light",
            "en-US",
            ResolvePreviewDpi(),
            1200,
            820,
            ScreenshotHelper.OutputDir,
            _output,
            new Dictionary<string, string> { ["EASYDICT_RS_DEBUG"] = "1" });

        var wide = session.Render(
            "long_document",
            new Dictionary<string, string>(),
            "longdoc-layout-1200x820-rust-win-fluent-iced",
            [
                "main.long-doc.source_language",
                "main.long-doc.target_language",
                "main.long-doc.service",
                "main.long-doc.input_mode",
                "main.long-doc.two_pass",
                "main.long-doc.translate"
            ],
            1200,
            820);
        AssertLongDocumentGridBounds(wide.BoundsPath);
        CaptureRustArtifacts(wide, "longdoc-layout-1200x820-rust-win-fluent-iced");
        CaptureForegroundWindow(wide.Window, "longdoc-layout-1200x820-rust-win-fluent-iced");

        var narrow = session.Render(
            "long_document",
            new Dictionary<string, string>(),
            "longdoc-layout-419x820-rust-win-fluent-iced",
            ["LongDocDocumentContextPassCheckBox", "main.long-doc.two_pass", "main.long-doc.translate", "LongDocControlGrid"],
            419,
            820);
        var narrowDimensions = TryReadRustBoundsControlDimensions(narrow.BoundsPath);
        narrowDimensions.Should().ContainKeys("LongDocDocumentContextPassCheckBox", "main.long-doc.translate", "LongDocControlGrid");
        var twoPass = narrowDimensions["LongDocDocumentContextPassCheckBox"].BoundsDips
            ?? throw new InvalidOperationException("Two-pass bounds are missing.");
        var translate = narrowDimensions["main.long-doc.translate"].BoundsDips
            ?? throw new InvalidOperationException("Translate bounds are missing.");
        twoPass.Height.Should().BeGreaterThan(16);
        var grid = narrowDimensions["LongDocControlGrid"].BoundsDips
            ?? throw new InvalidOperationException("Long-document grid bounds are missing.");
        twoPass.Top.Should().BeGreaterThanOrEqualTo(grid.Top + 60 + 58 + 8 - 2);
        (twoPass.Top + twoPass.Height).Should().BeLessThanOrEqualTo(grid.Top + grid.Height + 2);
        (twoPass.Left + twoPass.Width).Should().BeLessThanOrEqualTo(translate.Left + 2);
        CaptureRustArtifacts(narrow, "longdoc-layout-419x820-rust-win-fluent-iced");
        CaptureForegroundWindow(narrow.Window, "longdoc-layout-419x820-rust-win-fluent-iced");

        var running = session.Render(
            "long_document_running",
            new Dictionary<string, string>(),
            "longdoc-running-wrap-419x820-rust-win-fluent-iced",
            ["LongDocOutputTitle", "main.long-doc.retry", "main.long-doc.output_card"],
            419,
            820);
        var runningDimensions = TryReadRustBoundsControlDimensions(running.BoundsPath);
        runningDimensions.Should().ContainKeys("LongDocOutputTitle", "main.long-doc.retry", "main.long-doc.output_card");
        var title = runningDimensions["LongDocOutputTitle"].BoundsDips
            ?? throw new InvalidOperationException("Long-document output title bounds are missing.");
        var retry = runningDimensions["main.long-doc.retry"].BoundsDips
            ?? throw new InvalidOperationException("Long-document retry bounds are missing.");
        var outputCard = runningDimensions["main.long-doc.output_card"].BoundsDips
            ?? throw new InvalidOperationException("Long-document output card bounds are missing.");
        title.Height.Should().BeGreaterThan(16);
        retry.Left.Should().BeGreaterThanOrEqualTo(outputCard.Left - 2);
        retry.Top.Should().BeGreaterThanOrEqualTo(outputCard.Top - 2);
        (retry.Left + retry.Width).Should().BeLessThanOrEqualTo(outputCard.Left + outputCard.Width + 2);
        (retry.Top + retry.Height).Should().BeLessThanOrEqualTo(outputCard.Top + outputCard.Height + 2);
        (title.Left + title.Width).Should().BeLessThanOrEqualTo(retry.Left - 8 + 2);
        CaptureRustArtifacts(running, "longdoc-running-wrap-419x820-rust-win-fluent-iced");
        CaptureForegroundWindow(running.Window, "longdoc-running-wrap-419x820-rust-win-fluent-iced");
    }

    [Fact]
    public void RustLongDocumentService_ShouldMatchDotnetAcrossWidths()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine($"Skipped: set {EnableEnvironmentVariable}=1.");
            return;
        }

        SeedDotnetParitySettings();
        _dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
        var dotnetWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
        PrepareDotnetLongDocument(dotnetWindow);

        using var session = RustPreviewSession.Launch(
            "main",
            "light",
            ResolveParityUiLanguage(),
            ResolvePreviewDpi(),
            1200,
            820,
            ScreenshotHelper.OutputDir,
            _output,
            new Dictionary<string, string> { ["EASYDICT_RS_DEBUG"] = "1" });

        foreach (var widthDips in new[] { 1200, 419 })
        {
            var rendered = session.Render(
                "long_document",
                new Dictionary<string, string>(),
                $"longdoc-service-{widthDips}x820-rust-win-fluent-iced",
                [
                    "LongDocControlGrid",
                    "main.long-doc.source_language",
                    "main.long-doc.service"
                ],
                widthDips,
                820);
            var rustWindow = rendered.Window;
            var dotnetScale = ScreenshotHelper.GetWindowDpiScale(dotnetWindow);
            var rustBounds = ScreenshotHelper.GetWindowPhysicalBounds(rustWindow);
            var dotnetCurrent = ScreenshotHelper.GetWindowPhysicalBounds(dotnetWindow);
            var target = new Rectangle(
                dotnetCurrent.Left,
                dotnetCurrent.Top,
                rustBounds.Width,
                rustBounds.Height);
            TrySetWindowToPhysicalTargetWithFrameCompensation(dotnetWindow, target);
            var dotnetBounds = ScreenshotHelper.GetWindowPhysicalBounds(dotnetWindow);
            WindowSizeDistance(dotnetBounds, rustBounds)
                .Should()
                .BeLessThanOrEqualTo(8, "capture geometry must match the rendered Rust bounds");
            PrepareDotnetLongDocument(dotnetWindow);
            WaitForLongDocumentReady(rustWindow, "rust");
            AssertWindowFullyVisible(dotnetWindow, $"longdoc-service-{widthDips}", "dotnet");
            AssertWindowFullyVisible(rustWindow, $"longdoc-service-{widthDips}", "rust");
            ScreenshotHelper.GetWindowDpiScale(rustWindow)
                .Should()
                .BeApproximately(dotnetScale, 0.01, "both windows must use the same monitor DPI");

            var dotnetService = FindVisibleByAutomationId(dotnetWindow, "LongDocServiceCombo")
                ?? throw new InvalidOperationException("LongDocServiceCombo is missing.");
            var dotnetConcurrency = FindVisibleByAutomationId(dotnetWindow, "LongDocConcurrencyBox")
                ?? throw new InvalidOperationException("LongDocConcurrencyBox is missing.");
            var dotnetPageRange = FindVisibleByAutomationId(dotnetWindow, "LongDocPageRangeBox")
                ?? throw new InvalidOperationException("LongDocPageRangeBox is missing.");
            var serviceBounds = dotnetService.BoundingRectangle;
            var concurrencyBounds = dotnetConcurrency.BoundingRectangle;
            var pageRangeBounds = dotnetPageRange.BoundingRectangle;
            ((double)serviceBounds.Left).Should().BeApproximately(
                concurrencyBounds.Left,
                5 * dotnetScale);
            ((double)serviceBounds.Width).Should().BeApproximately(
                pageRangeBounds.Right - concurrencyBounds.Left,
                5 * dotnetScale);
            ((double)serviceBounds.Right).Should().BeApproximately(
                pageRangeBounds.Right,
                5 * dotnetScale);

            var rustDimensions = TryReadRustBoundsControlDimensions(rendered.BoundsPath);
            rustDimensions.Should().ContainKeys(
                "LongDocControlGrid",
                "main.long-doc.source_language",
                "main.long-doc.service");
            var rustGrid = rustDimensions["LongDocControlGrid"].BoundsDips
                ?? throw new InvalidOperationException("Rust LongDocControlGrid bounds are missing.");
            var rustService = rustDimensions["main.long-doc.service"].BoundsDips
                ?? throw new InvalidOperationException("Rust service bounds are missing.");
            var singleColumn = (rustGrid.Width - 3 * 8) / 4;
            rustService.Width.Should().BeApproximately(2 * singleColumn + 8, 2);
            (rustService.Left + rustService.Width)
                .Should()
                .BeApproximately(rustGrid.Left + rustGrid.Width, 2);

            CaptureFocusedParityComparison(
                dotnetWindow,
                rustWindow,
                $"longdoc-service-{widthDips}x820");
            CaptureRustArtifacts(
                rendered,
                $"longdoc-service-{widthDips}x820-rust-win-fluent-iced");
        }
    }

    [Fact]
    public void RustLongDocumentTwoPass_ShouldMatchDotnetAtNarrowWidth()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine($"Skipped: set {EnableEnvironmentVariable}=1.");
            return;
        }

        SeedDotnetParitySettings();
        _dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
        var dotnetWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
        PrepareDotnetLongDocument(dotnetWindow);

        using var session = RustPreviewSession.Launch(
            "main",
            "light",
            ResolveParityUiLanguage(),
            ResolvePreviewDpi(),
            1200,
            820,
            ScreenshotHelper.OutputDir,
            _output,
            new Dictionary<string, string> { ["EASYDICT_RS_DEBUG"] = "1" });

        var wide = session.Render(
            "long_document",
            new Dictionary<string, string>(),
            "longdoc-two-pass-1200x820-rust-win-fluent-iced",
            ["LongDocControlGrid", "LongDocDocumentContextPassCheckBox", "main.long-doc.translate"],
            1200,
            820);
        MatchDotnetWindowToRust(dotnetWindow, wide.Window, "wide Two-pass");
        PrepareDotnetLongDocument(dotnetWindow);
        var dotnetWideTwoPass = FindVisibleByAutomationId(
                dotnetWindow,
                "LongDocDocumentContextPassCheckBox")
            ?? throw new InvalidOperationException("Wide .NET Two-pass checkbox is missing.");
        var dotnetWideHeight = dotnetWideTwoPass.BoundingRectangle.Height;
        var wideDimensions = TryReadRustBoundsControlDimensions(wide.BoundsPath);
        var rustWideTwoPass = wideDimensions["LongDocDocumentContextPassCheckBox"].BoundsDips
            ?? throw new InvalidOperationException("Wide Rust Two-pass bounds are missing.");

        var narrow = session.Render(
            "long_document",
            new Dictionary<string, string>(),
            "longdoc-two-pass-419x820-rust-win-fluent-iced",
            ["LongDocControlGrid", "LongDocDocumentContextPassCheckBox", "main.long-doc.translate"],
            419,
            820);
        MatchDotnetWindowToRust(dotnetWindow, narrow.Window, "narrow Two-pass");
        PrepareDotnetLongDocument(dotnetWindow);
        AssertWindowFullyVisible(dotnetWindow, "longdoc-two-pass-419x820", "dotnet");
        AssertWindowFullyVisible(narrow.Window, "longdoc-two-pass-419x820", "rust");

        var dotnetTwoPass = FindVisibleByAutomationId(
                dotnetWindow,
                "LongDocDocumentContextPassCheckBox")
            ?? throw new InvalidOperationException("Narrow .NET Two-pass checkbox is missing.");
        var dotnetTranslate = FindVisibleByAutomationId(dotnetWindow, "LongDocTranslateButton")
            ?? throw new InvalidOperationException("Narrow .NET Translate button is missing.");
        var dotnetTwoPassBounds = dotnetTwoPass.BoundingRectangle;
        var dotnetTranslateBounds = dotnetTranslate.BoundingRectangle;
        dotnetTwoPass.Name.Should().Be(
            "Two-pass translation (extract glossary + summary first for terminology consistency)");
        dotnetTwoPassBounds.Height.Should().BeGreaterThan(dotnetWideHeight + 4);
        dotnetTwoPassBounds.Right.Should().BeLessThanOrEqualTo(dotnetTranslateBounds.Left + 2);

        var narrowDimensions = TryReadRustBoundsControlDimensions(narrow.BoundsPath);
        narrowDimensions.Should().ContainKeys(
            "LongDocControlGrid",
            "LongDocDocumentContextPassCheckBox",
            "main.long-doc.translate");
        var rustGrid = narrowDimensions["LongDocControlGrid"].BoundsDips
            ?? throw new InvalidOperationException("Narrow Rust Grid bounds are missing.");
        var rustTwoPass = narrowDimensions["LongDocDocumentContextPassCheckBox"].BoundsDips
            ?? throw new InvalidOperationException("Narrow Rust Two-pass bounds are missing.");
        var rustTranslate = narrowDimensions["main.long-doc.translate"].BoundsDips
            ?? throw new InvalidOperationException("Narrow Rust Translate bounds are missing.");
        var singleColumn = (rustGrid.Width - 3 * 8) / 4;
        rustTwoPass.Width.Should().BeApproximately(3 * singleColumn + 2 * 8, 2);
        rustTwoPass.Height.Should().BeGreaterThan(rustWideTwoPass.Height + 8);
        (rustTwoPass.Left + rustTwoPass.Width)
            .Should()
            .BeLessThanOrEqualTo(rustTranslate.Left + 2);

        CaptureFocusedParityComparison(
            dotnetWindow,
            narrow.Window,
            "longdoc-two-pass-419x820");
        CaptureRustArtifacts(
            narrow,
            "longdoc-two-pass-419x820-rust-win-fluent-iced");

    }

    [Fact]
    public void RustLongDocumentOutputTitle_ShouldMatchDotnetWithoutOverlap()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine($"Skipped: set {EnableEnvironmentVariable}=1.");
            return;
        }

        SeedDotnetParitySettings();
        _dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
        var dotnetWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
        PrepareDotnetLongDocument(dotnetWindow);

        using var session = RustPreviewSession.Launch(
            "main",
            "light",
            ResolveParityUiLanguage(),
            ResolvePreviewDpi(),
            419,
            820,
            ScreenshotHelper.OutputDir,
            _output,
            new Dictionary<string, string> { ["EASYDICT_RS_DEBUG"] = "1" });
        var running = session.Render(
            "long_document_running",
            new Dictionary<string, string>(),
            "longdoc-output-title-419x820-rust-win-fluent-iced",
            ["main.long-doc.retry", "main.long-doc.output_card"],
            419,
            820);
        MatchDotnetWindowToRust(dotnetWindow, running.Window, "output-title");
        PrepareDotnetLongDocument(dotnetWindow);
        AssertWindowFullyVisible(dotnetWindow, "longdoc-output-title-419x820", "dotnet");
        AssertWindowFullyVisible(running.Window, "longdoc-output-title-419x820", "rust");

        var dotnetTitle = FindVisibleByAutomationId(dotnetWindow, "LongDocOutputTitle")
            ?? throw new InvalidOperationException(".NET output title is missing.");
        var dotnetRetry = FindVisibleByAutomationId(dotnetWindow, "LongDocRetryButton")
            ?? throw new InvalidOperationException(".NET output retry button is missing.");
        dotnetTitle.BoundingRectangle.Right
            .Should()
            .BeLessThanOrEqualTo(dotnetRetry.BoundingRectangle.Left + 1);

        var dimensions = TryReadRustBoundsControlDimensions(running.BoundsPath);
        dimensions.Should().ContainKeys(
            "LongDocOutputTitle",
            "main.long-doc.retry",
            "main.long-doc.output_card");
        var title = dimensions["LongDocOutputTitle"].BoundsDips
            ?? throw new InvalidOperationException("Rust output title bounds are missing.");
        var retry = dimensions["main.long-doc.retry"].BoundsDips
            ?? throw new InvalidOperationException("Rust output retry bounds are missing.");
        title.Height.Should().BeInRange(16, 24, "the output title must remain one line");
        (title.Left + title.Width)
            .Should()
            .BeLessThanOrEqualTo(retry.Left - 8 + 2);

        CaptureFocusedParityComparison(
            dotnetWindow,
            running.Window,
            "longdoc-output-title-419x820");
        CaptureRustArtifacts(
            running,
            "longdoc-output-title-419x820-rust-win-fluent-iced");
    }

    [Fact]
    public void RustMainWindowBorder_ShouldExposeResizeCursorsAndResize()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine($"Skipped: set {EnableEnvironmentVariable}=1.");
            return;
        }

        SeedDotnetParitySettings();
        _dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
        var dotnetWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
        DismissHotkeyRegistrationDialogIfPresent(dotnetWindow);
        var rustMain = RenderMainPreview(
            "initial",
            ResolveRustPreviewTheme("light"),
            _output,
            new Dictionary<string, string>
            {
                ["EASYDICT_PREVIEW_WIDTH_DIPS"] = "900",
                ["EASYDICT_PREVIEW_HEIGHT_DIPS"] = "700"
            },
            schemaSuffix: "-resize-border");
        var rustWindow = rustMain.Window;
        AssertNativeResizablePopupStyle(rustWindow, "Rust main");
        ArrangeFloatingSideBySide(dotnetWindow, rustWindow, 900, 700);
        WindowSizeDistance(
                ScreenshotHelper.GetWindowPhysicalBounds(dotnetWindow),
                ScreenshotHelper.GetWindowPhysicalBounds(rustWindow))
            .Should()
            .BeLessThanOrEqualTo(8, "resize parity screenshots require matching initial dimensions");


        var dotnetBefore = CaptureForegroundWindow(
            dotnetWindow,
            "resize-main-before-dotnet-winui-reference");
        var rustBefore = CaptureForegroundWindow(
            rustWindow,
            "resize-main-before-rust-win-fluent-iced");
        var beforeSideBySide = SaveSideBySideComparison(
            dotnetBefore,
            rustBefore,
            "resize-main-before-dotnet-vs-rust-side-by-side");

        AssertResizeCursorsAndResize(dotnetWindow, ".NET main");
        AssertResizeCursorsAndResize(rustWindow, "Rust main");
        WindowSizeDistance(
                ScreenshotHelper.GetWindowPhysicalBounds(dotnetWindow),
                ScreenshotHelper.GetWindowPhysicalBounds(rustWindow))
            .Should()
            .BeLessThanOrEqualTo(8, "both windows must preserve matching dimensions after the same drag");


        var dotnetAfter = CaptureForegroundWindow(
            dotnetWindow,
            "resize-main-after-dotnet-winui-reference");
        var rustAfter = CaptureForegroundWindow(
            rustWindow,
            "resize-main-after-rust-win-fluent-iced");
        var afterSideBySide = SaveSideBySideComparison(
            dotnetAfter,
            rustAfter,
            "resize-main-after-dotnet-vs-rust-side-by-side");
        AssertImageHasVisibleContent(beforeSideBySide);
        AssertImageHasVisibleContent(afterSideBySide);

        foreach (var windowKind in new[] { "mini", "fixed" })
        {
            MoveMouseToNeutralPoint();
            var preview = RenderWindowPreview(
                windowKind,
                ResolveRustPreviewTheme("light"),
                _output);
            AssertNativeResizablePopupStyle(preview.Window, $"Rust {windowKind}");
            AssertResizeCursorsAndResize(preview.Window, $"Rust {windowKind}");
        }

        var popButton = RenderWindowPreview(
            "pop-button",
            ResolveRustPreviewTheme("light"),
            _output);
        AssertNativeFixedPopupStyle(popButton.Window, "Rust pop-button");
        AssertNoSizingCursorOrResize(popButton.Window, "Rust pop-button");

        using var captureSession = RustPreviewSession.Launch(
            "capture-overlay",
            ResolveRustPreviewTheme("light"),
            ResolveParityUiLanguage(),
            ResolvePreviewDpi(),
            800,
            600,
            ScreenshotHelper.OutputDir,
            _output);
        var captureWindow = captureSession.GetMainWindow(TimeSpan.FromSeconds(30));
        AssertNativeFixedPopupStyle(captureWindow, "Rust capture-overlay");
        AssertNoSizingCursorOrResize(captureWindow, "Rust capture-overlay");
    }

    [Fact]
    public void RustLongDocumentHistory_ShouldRenderCollapsedAndExpandDownward()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine($"Skipped: set {EnableEnvironmentVariable}=1.");
            return;
        }

        using var session = RustPreviewSession.Launch(
            "main",
            "light",
            "en-US",
            ResolvePreviewDpi(),
            419,
            820,
            ScreenshotHelper.OutputDir,
            _output,
            new Dictionary<string, string> { ["EASYDICT_RS_DEBUG"] = "1" });
        var empty = session.Render(
            "long_document",
            new Dictionary<string, string>(),
            "longdoc-history-empty-collapsed-rust-win-fluent-iced",
            ["main.long-doc.history", "LongDocHistoryTitle", "main.long-doc.clear_history"],
            419,
            820);
        var emptyDimensions = TryReadRustBoundsControlDimensions(empty.BoundsPath);
        emptyDimensions.Should().ContainKeys("main.long-doc.history", "main.long-doc.clear_history");
        CaptureRustArtifacts(empty, "longdoc-history-empty-collapsed-rust-win-fluent-iced");
        CaptureForegroundWindow(empty.Window, "longdoc-history-empty-collapsed-rust-win-fluent-iced");

        var header = emptyDimensions["main.long-doc.history"].BoundsDips
            ?? throw new InvalidOperationException("History header bounds are missing.");
        var collapsedHistoryHeight = header.Height;
        var marker = session.CaptureDebugLineMarker();
        ClickRustBoundsCenter(empty.Window, header);
        session.WaitForDebugLine(marker, "message=ToggleLongDocumentHistoryExpanded value=true", TimeSpan.FromSeconds(5));

        var populated = session.Render(
            "long_document_error",
            new Dictionary<string, string>(),
            "longdoc-history-expanded-rust-win-fluent-iced",
            ["main.long-doc.history", "main.long-doc.history_list", "LongDocHistoryTitle"],
            419,
            820);
        var populatedBounds = TryReadRustBoundsControlDimensions(populated.BoundsPath);
        var populatedHeader = populatedBounds["main.long-doc.history"].BoundsDips
            ?? throw new InvalidOperationException("Populated history header bounds are missing.");
        var populatedMarker = session.CaptureDebugLineMarker();
        ClickRustBoundsCenter(populated.Window, populatedHeader);
        session.WaitForDebugLine(
            populatedMarker,
            "message=ToggleLongDocumentHistoryExpanded value=true",
            TimeSpan.FromSeconds(5));

        var expanded = session.Render(
            "long_document_error",
            new Dictionary<string, string> { ["EASYDICT_PREVIEW_LONG_DOC_HISTORY_EXPANDED"] = "1" },
            "longdoc-history-expanded-rust-win-fluent-iced-final",
            ["main.long-doc.history", "main.long-doc.history_list"],
            419,
            820);
        var expandedBounds = TryReadRustBoundsControlDimensions(expanded.BoundsPath);
        expandedBounds.Should().ContainKeys("main.long-doc.history", "main.long-doc.history_list");
        var expandedHeader = expandedBounds["main.long-doc.history"].BoundsDips
            ?? throw new InvalidOperationException("Expanded history header bounds are missing.");
        var list = expandedBounds["main.long-doc.history_list"].BoundsDips
            ?? throw new InvalidOperationException("Expanded history list bounds are missing.");
        list.Top.Should().BeGreaterThanOrEqualTo(expandedHeader.Top + collapsedHistoryHeight - 2);
        CaptureRustArtifacts(expanded, "longdoc-history-expanded-rust-win-fluent-iced");
        CaptureForegroundWindow(expanded.Window, "longdoc-history-expanded-rust-win-fluent-iced");
    }

    [Fact]
    public void RustModeMenu_ShouldOpenAboveAndCommitSelection()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine($"Skipped: set {EnableEnvironmentVariable}=1.");
            return;
        }

        EnsureParityDpiAwareness();
        var dotnetReferenceExecutable = ResolveDotnetReferenceExecutable();
        using var dotnetReferenceScope = new EnvironmentVariableScope(
            "EASYDICT_EXE_PATH",
            dotnetReferenceExecutable);
        using var popupThemeScope = new EnvironmentVariableScope(
            ThemeEnvironmentVariable,
            ResolveRustPreviewTheme("light"));
        SeedDotnetParitySettings();
        _dotnetLauncher.LaunchFromExe(
            dotnetReferenceExecutable,
            TimeSpan.FromSeconds(45));
        var dotnetWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
        var dotnetOpenPath = OpenDotnetModeMenuAndSelect(
            dotnetWindow,
            "ModeLongDocItem",
            "LongDocSourceLangCombo",
            "main-mode-popup-open-dotnet-winui-reference");
        OpenDotnetModeMenuAndSelect(
            dotnetWindow,
            "ModeTranslationItem",
            "InputTextBox",
            screenshotName: null);

        var rustOpenPath = AssertRustModeMenuSelection(
            "Long Document",
            "message=ModeChanged value=long-document",
            "long-document");
        AssertRustModeMenuSelection("Translate", "message=ModeChanged value=quick", "quick");
        var sideBySidePath = SaveSideBySideComparison(
            dotnetOpenPath!,
            rustOpenPath!,
            "main-mode-popup-open-dotnet-vs-rust-side-by-side");
        AssertImageHasVisibleContent(sideBySidePath);
    }

    [Fact]
    public void RustMainSourceEditor_ShouldKeepTypedCharactersInEntryOrder()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine($"Skipped: set {EnableEnvironmentVariable}=1.");
            return;
        }

        EnsureParityDpiAwareness();
        using var session = RustPreviewSession.Launch(
            "main",
            "light",
            "en-US",
            ResolvePreviewDpi(),
            1000,
            700,
            ScreenshotHelper.OutputDir,
            _output,
            new Dictionary<string, string>
            {
                ["EASYDICT_RS_DEBUG"] = "1",
                ["EASYDICT_RS_DEBUG_VERBOSE"] = "1"
            });
        var rendered = session.Render(
            "initial",
            new Dictionary<string, string>(),
            "main-source-editor-typing-rust-win-fluent-iced",
            [],
            1000,
            700);
        var dimensions = TryReadRustBoundsControlDimensions(rendered.BoundsPath);
        var sourceEditor = dimensions["InputTextBox"].BoundsDips
            ?? throw new InvalidOperationException("Rust source-editor bounds are missing.");

        EnsureWindowForegroundForMouseInput(rendered.Window, "Rust source editor");
        ClickRustBoundsCenter(rendered.Window, sourceEditor);
        var marker = session.CaptureDebugLineMarker();
        TypeUnicodeText("abc");
        session.WaitForDebugLine(
            marker,
            "message=SourceTextChanged text_len=3 text_hash=e71fa2190541574b",
            TimeSpan.FromSeconds(5));
        CaptureForegroundWindow(
            rendered.Window,
            "main-source-editor-typing-rust-win-fluent-iced");
    }

    [Fact]
    public void RustMainWindow_MaximizeButton_ShouldUseMonitorWorkArea()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine($"Skipped: set {EnableEnvironmentVariable}=1.");
            return;
        }

        EnsureParityDpiAwareness();
        using var session = RustPreviewSession.Launch(
            "main",
            "light",
            "en-US",
            ResolvePreviewDpi(),
            1000,
            700,
            ScreenshotHelper.OutputDir,
            _output);
        var rendered = session.Render(
            "initial",
            new Dictionary<string, string>(),
            "main-maximized-work-area-rust-win-fluent-iced",
            [],
            1000,
            700);
        var dimensions = TryReadRustBoundsControlDimensions(rendered.BoundsPath);
        var maximize = dimensions["Maximize"].BoundsDips
            ?? throw new InvalidOperationException("Rust maximize-button bounds are missing.");
        var hwnd = SafeNativeWindowHandle(rendered.Window);

        EnsureWindowForegroundForMouseInput(rendered.Window, "Rust maximize button");
        ClickRustBoundsCenter(rendered.Window, maximize);
        SpinWait.SpinUntil(() => IsZoomed(hwnd), TimeSpan.FromSeconds(5))
            .Should()
            .BeTrue("clicking the Rust maximize caption button must maximize the window");

        var maximizedBounds = GetNativeWindowBounds(rendered.Window);
        var workArea = GetMonitorWorkArea(hwnd);
        maximizedBounds.Should().Be(
            workArea,
            "a maximized borderless window must stay inside the monitor work area");
        Thread.Sleep(500);
        EnsureWindowForegroundForMouseInput(rendered.Window, "Rust maximized preview");
        var screenshotPath = ScreenshotHelper.CaptureWindowHandlePhysical(
            hwnd,
            "main-maximized-work-area-rust-win-fluent-iced");
        MaskFloatingLanguageBarOcclusions(screenshotPath, rendered.Window);
        using (var screenshot = new Bitmap(screenshotPath))
        {
            var sourceSurface = screenshot.GetPixel(screenshot.Width / 2, 100);
            ColorDistance(sourceSurface, Color.White).Should().BeLessThan(
                12,
                "the maximized Rust window must keep rendering its light source-editor surface");
        }
        AssertImageHasVisibleContent(screenshotPath);

        var dpiScale = ScreenshotHelper.GetWindowDpiScale(rendered.Window);
        Mouse.MoveTo(new Point(
            maximizedBounds.Right - (int)Math.Round(72 * dpiScale),
            maximizedBounds.Top + (int)Math.Round(14 * dpiScale)));
        Mouse.Click();
        SpinWait.SpinUntil(() => !IsZoomed(hwnd), TimeSpan.FromSeconds(5))
            .Should()
            .BeTrue("clicking the maximized Rust caption button must restore the window");
    }

    private static Rectangle GetMonitorWorkArea(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        monitor.Should().NotBe(IntPtr.Zero, "the window must resolve to a monitor");
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info).Should().BeTrue("GetMonitorInfo must succeed");
        return Rectangle.FromLTRB(
            info.WorkArea.Left,
            info.WorkArea.Top,
            info.WorkArea.Right,
            info.WorkArea.Bottom);
    }

    private static void AssertLongDocumentGridBounds(string boundsPath)
    {
        var dimensions = TryReadRustBoundsControlDimensions(boundsPath);
        dimensions.Should().ContainKeys(
            "main.long-doc.source_language",
            "main.long-doc.target_language",
            "main.long-doc.service",
            "main.long-doc.input_mode",
            "main.long-doc.output_mode",
            "main.long-doc.page_range");
        var source = dimensions["main.long-doc.source_language"].BoundsDips
            ?? throw new InvalidOperationException("Long-document source bounds are missing.");
        var service = dimensions["main.long-doc.service"].BoundsDips
            ?? throw new InvalidOperationException("Long-document service bounds are missing.");
        var input = dimensions["main.long-doc.input_mode"].BoundsDips
            ?? throw new InvalidOperationException("Long-document input mode bounds are missing.");
        var page = dimensions["main.long-doc.page_range"].BoundsDips
            ?? throw new InvalidOperationException("Long-document page range bounds are missing.");
        service.Width.Should().BeApproximately(2 * input.Width + 8, 2);
        (service.Left + service.Width).Should().BeApproximately(page.Left + page.Width, 2);
        source.Width.Should().BeApproximately(input.Width, 2);
    }

    private static void CaptureRustArtifacts(RustPreviewRenderResult rendered, string artifactStem)
    {
        File.Copy(rendered.BoundsPath, Path.Combine(ScreenshotHelper.OutputDir, $"{artifactStem}.bounds"), true);
        File.Copy(rendered.DiagnosticsPath, Path.Combine(ScreenshotHelper.OutputDir, $"{artifactStem}.diagnostics"), true);
    }

    private static void MatchDotnetWindowToRust(
        Window dotnetWindow,
        Window rustWindow,
        string context)
    {
        var rustBounds = ScreenshotHelper.GetWindowPhysicalBounds(rustWindow);
        var dotnetCurrent = ScreenshotHelper.GetWindowPhysicalBounds(dotnetWindow);
        TrySetWindowToPhysicalTargetWithFrameCompensation(
            dotnetWindow,
            new Rectangle(
                dotnetCurrent.Left,
                dotnetCurrent.Top,
                rustBounds.Width,
                rustBounds.Height));
        WindowSizeDistance(
                ScreenshotHelper.GetWindowPhysicalBounds(dotnetWindow),
                rustBounds)
            .Should()
            .BeLessThanOrEqualTo(8, $"{context} windows must use matching capture geometry");
    }

    private static void CaptureFocusedParityComparison(
        Window dotnetWindow,
        Window rustWindow,
        string artifactStem)
    {
        MoveMouseToNeutralPoint();
        var dotnetPath = CaptureForegroundWindow(
            dotnetWindow,
            $"{artifactStem}-dotnet-winui-reference");
        var rustPath = CaptureForegroundWindow(
            rustWindow,
            $"{artifactStem}-rust-win-fluent-iced");
        var sideBySidePath = SaveSideBySideComparison(
            dotnetPath,
            rustPath,
            $"{artifactStem}-dotnet-vs-rust-side-by-side");
        AssertImageHasVisibleContent(dotnetPath);
        AssertImageHasVisibleContent(rustPath);
        AssertImageHasVisibleContent(sideBySidePath);
    }

    private static void AssertNativeResizablePopupStyle(Window window, string label)
    {
        var style = GetNativeWindowStyle(window);
        (style & WsPopup).Should().NotBe(0, $"{label} must remain a popup window");
        (style & WsThickFrame).Should().NotBe(0, $"{label} must expose native resize hit testing");
        (style & WsVisible).Should().NotBe(0, $"{label} must preserve the runtime WS_VISIBLE bit");
    }

    private static void AssertNativeFixedPopupStyle(Window window, string label)
    {
        var style = GetNativeWindowStyle(window);
        (style & WsPopup).Should().NotBe(0, $"{label} must remain a popup window");
        (style & WsThickFrame).Should().Be(0, $"{label} must not expose native resize hit testing");
        (style & WsVisible).Should().NotBe(0, $"{label} must preserve the runtime WS_VISIBLE bit");
    }

    private static uint GetNativeWindowStyle(Window window)
    {
        var hwnd = SafeNativeWindowHandle(window);
        hwnd.Should().NotBe(IntPtr.Zero, "a live HWND is required for native style validation");
        return unchecked((uint)GetWindowLongPtrNative(hwnd, GwlStyle).ToInt64());
    }

    private static void AssertResizeCursorsAndResize(Window window, string label)
    {
        EnsureWindowForegroundForMouseInput(window, label);
        var before = GetNativeWindowBounds(window);
        before.Width.Should().BeGreaterThan(80);
        before.Height.Should().BeGreaterThan(80);
        var centerX = before.Left + before.Width / 2;
        var centerY = before.Top + before.Height / 2;
        var probes = new[]
        {
            (new Point(before.Left + 1, centerY), IdcSizeWe, "left"),
            (new Point(before.Right - 2, centerY), IdcSizeWe, "right"),
            (new Point(centerX, before.Top + 1), IdcSizeNs, "top"),
            (new Point(centerX, before.Bottom - 2), IdcSizeNs, "bottom"),
            (new Point(before.Left + 1, before.Top + 1), IdcSizeNwSe, "top-left"),
            (new Point(before.Right - 2, before.Top + 1), IdcSizeNeSw, "top-right"),
            (new Point(before.Left + 1, before.Bottom - 2), IdcSizeNeSw, "bottom-left"),
            (new Point(before.Right - 2, before.Bottom - 2), IdcSizeNwSe, "bottom-right")
        };
        foreach (var (point, cursorId, edge) in probes)
        {
            Mouse.MoveTo(point);
            Thread.Sleep(160);
            GetCurrentCursorHandle()
                .Should()
                .Be(LoadCursor(IntPtr.Zero, new IntPtr(cursorId)), $"{label} {edge} edge");
        }

        var start = new Point(before.Right - 2, centerY);
        Mouse.MoveTo(start);
        Thread.Sleep(100);
        Mouse.Down(MouseButton.Left);
        try
        {
            for (var step = 1; step <= 8; step++)
            {
                Mouse.MoveTo(new Point(start.X + step * 5, start.Y));
                Thread.Sleep(25);
            }
        }
        finally
        {
            Mouse.Up(MouseButton.Left);
        }

        Retry.WhileFalse(
                () => GetNativeWindowBounds(window).Width >= before.Width + 20,
                TimeSpan.FromSeconds(3))
            .Result
            .Should()
            .BeTrue($"{label} right-edge drag must increase window width");
    }

    private static void AssertNoSizingCursorOrResize(Window window, string label)
    {
        var before = GetNativeWindowBounds(window);
        var centerY = before.Top + before.Height / 2;
        var sizingCursors = new[]
        {
            LoadCursor(IntPtr.Zero, new IntPtr(IdcSizeWe)),
            LoadCursor(IntPtr.Zero, new IntPtr(IdcSizeNs)),
            LoadCursor(IntPtr.Zero, new IntPtr(IdcSizeNwSe)),
            LoadCursor(IntPtr.Zero, new IntPtr(IdcSizeNeSw))
        };
        var start = new Point(before.Right - 2, centerY);
        Mouse.MoveTo(start);
        Thread.Sleep(160);
        sizingCursors.Should().NotContain(GetCurrentCursorHandle(), $"{label} must not show a sizing cursor");

        Mouse.Down(MouseButton.Left);
        try
        {
            Mouse.MoveTo(new Point(start.X - 40, start.Y));
            Thread.Sleep(200);
        }
        finally
        {
            Mouse.Up(MouseButton.Left);
        }
        Thread.Sleep(250);
        GetNativeWindowBounds(window).Width
            .Should()
            .BeInRange(before.Width - 2, before.Width + 2, $"{label} must not resize from its border");
    }

    private static IntPtr GetCurrentCursorHandle()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        GetCursorInfo(ref info).Should().BeTrue("GetCursorInfo must succeed");
        return info.Cursor;
    }

    private static Rectangle GetNativeWindowBounds(Window window)
    {
        var hwnd = SafeNativeWindowHandle(window);
        hwnd.Should().NotBe(IntPtr.Zero, "a live HWND is required for native bounds validation");
        GetWindowRect(hwnd, out var bounds).Should().BeTrue("GetWindowRect must succeed");
        return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    private static void ClickRustBoundsCenter(Window window, UiParityControlBoundsDips bounds)
    {
        var hwnd = SafeNativeWindowHandle(window);
        hwnd.Should().NotBe(IntPtr.Zero, "a live HWND is required for Rust bounds input");
        var clientOrigin = new NativePoint();
        ClientToScreen(hwnd, ref clientOrigin)
            .Should()
            .BeTrue("ClientToScreen must resolve Rust client-layout coordinates");
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        var point = new Point(
            clientOrigin.X + (int)Math.Round((bounds.Left + bounds.Width / 2) * dpiScale),
            clientOrigin.Y + (int)Math.Round((bounds.Top + bounds.Height / 2) * dpiScale));
        GetNativeWindowBounds(window)
            .Contains(point)
            .Should().BeTrue($"physical point {point} must lie inside Rust preview window");
        Mouse.MoveTo(point);
        Thread.Sleep(100);
        Mouse.Click(point);
        Thread.Sleep(300);
    }
    private static void TypeUnicodeText(string text)
    {
        var inputs = new NativeInput[text.Length * 2];
        for (var index = 0; index < text.Length; index++)
        {
            inputs[index * 2] = NativeInput.UnicodeKey(text[index], keyUp: false);
            inputs[index * 2 + 1] = NativeInput.UnicodeKey(text[index], keyUp: true);
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>())
            .Should().Be((uint)inputs.Length, "Unicode SendInput must type every UTF-16 code unit");
    }


    private static string? OpenDotnetModeMenuAndSelect(
        Window window,
        string optionId,
        string expectedControlId,
        string? screenshotName)
    {
        DismissHotkeyRegistrationDialogIfPresent(window);
        var currentBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var screen = ScreenshotHelper.GetVirtualScreenBounds();
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        var targetWidth = DipsToPhysicalPixels(1200, dpiScale);
        var targetHeight = DipsToPhysicalPixels(820, dpiScale);
        var target = new Rectangle(
            Math.Clamp(
                currentBounds.Left,
                screen.Left,
                Math.Max(screen.Left, screen.Right - targetWidth)),
            Math.Min(
                screen.Top + DipsToPhysicalPixels(180, dpiScale),
                Math.Max(screen.Top, screen.Bottom - targetHeight)),
            targetWidth,
            targetHeight);
        TrySetWindowToPhysicalTargetWithFrameCompensation(window, target);
        AssertWindowFullyVisible(window, "mode-menu", "dotnet");
        window.SetForeground();

        var modeButton = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "ModeMenuButton"),
                TimeSpan.FromSeconds(8))
            .Result;
        modeButton.Should().NotBeNull("the .NET mode selector must be visible");
        var triggerBounds = AutomationElementPhysicalBounds(modeButton!);
        UITestHelper.ClickElement(modeButton!);
        Thread.Sleep(250);

        var translationItem = Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, "ModeTranslationItem"),
                TimeSpan.FromSeconds(5))
            .Result;
        var longDocumentItem = Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, "ModeLongDocItem"),
                TimeSpan.FromSeconds(5))
            .Result;
        translationItem.Should().NotBeNull("the .NET translation mode item must be visible");
        longDocumentItem.Should().NotBeNull("the .NET long-document mode item must be visible");
        var translationBounds = AutomationElementPhysicalBounds(translationItem!);
        var longDocumentBounds = AutomationElementPhysicalBounds(longDocumentItem!);
        translationBounds.Top.Should().BeLessThan(
            longDocumentBounds.Top,
            "the .NET flyout must keep Translation before Long Document");

        var popup = FindAncestorByControlType(longDocumentItem!, ControlType.Menu);
        popup.Should().NotBeNull("the .NET mode items must belong to a MenuFlyout");
        var popupBounds = AutomationElementPhysicalBounds(popup!);
        ScreenshotHelper.GetVirtualScreenBounds().Contains(popupBounds)
            .Should().BeTrue("the .NET mode flyout must stay fully visible");
        popupBounds.Bottom.Should().BeLessThanOrEqualTo(
            triggerBounds.Top + 2,
            "the complete .NET mode flyout should open above its trigger");

        string? screenshotPath = null;
        if (screenshotName != null)
        {
            screenshotPath = ScreenshotHelper.CaptureScreenRegion(
                Rectangle.Union(ScreenshotHelper.GetWindowPhysicalBounds(window), popupBounds),
                screenshotName);
        }

        var option = optionId == "ModeLongDocItem" ? longDocumentItem! : translationItem!;
        UITestHelper.ClickElement(option);
        Retry.WhileNull(
                () => FindVisibleByAutomationId(window, expectedControlId),
                TimeSpan.FromSeconds(8))
            .Result
            .Should().NotBeNull(
                $"the .NET mode selection must expose '{expectedControlId}'");
        return screenshotPath;
    }

    private static AutomationElement? FindAncestorByControlType(
        AutomationElement element,
        ControlType controlType)
    {
        AutomationElement? current = element;
        for (var depth = 0; current != null && depth < 6; depth++)
        {
            if (current.ControlType == controlType)
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static Rectangle AutomationElementPhysicalBounds(AutomationElement element)
    {
        var bounds = element.BoundingRectangle;
        return Rectangle.FromLTRB(
            (int)Math.Floor((double)bounds.Left),
            (int)Math.Floor((double)bounds.Top),
            (int)Math.Ceiling((double)bounds.Right),
            (int)Math.Ceiling((double)bounds.Bottom));
    }

    private string? AssertRustModeMenuSelection(
        string optionText,
        string expectedDebugLine,
        string optionId)
    {
        string? openScreenshotPath = null;
        EnsureParityDpiAwareness();
        using var session = RustPreviewSession.Launch(
            "main", ResolveRustPreviewTheme("light"), ResolveParityUiLanguage(), ResolvePreviewDpi(), 1200, 820,
            ScreenshotHelper.OutputDir, _output,
            new Dictionary<string, string> { ["EASYDICT_RS_DEBUG"] = "1" });
        var rendered = session.Render(
            optionId == "quick" ? "long_document" : "initial",
            new Dictionary<string, string>(),
            "mode-menu",
            ["ModeMenuButton"],
            1200,
            820);
        var initialMainBounds = ScreenshotHelper.GetWindowPhysicalBounds(rendered.Window);
        var screenForPlacement = ScreenshotHelper.GetVirtualScreenBounds();
        var mainTarget = new Rectangle(
            Math.Clamp(
                initialMainBounds.Left,
                screenForPlacement.Left,
                Math.Max(screenForPlacement.Left, screenForPlacement.Right - initialMainBounds.Width)),
            Math.Min(
                screenForPlacement.Top + 180,
                Math.Max(screenForPlacement.Top, screenForPlacement.Bottom - initialMainBounds.Height)),
            initialMainBounds.Width,
            initialMainBounds.Height);
        ScreenshotHelper.TrySetWindowPhysicalBounds(rendered.Window, mainTarget)
            .Should()
            .BeTrue("the main window must leave normal screen space above its mode title");
        Thread.Sleep(250);
        EnsureWindowForegroundForMouseInput(rendered.Window, "mode menu");
        var dimensions = TryReadRustBoundsControlDimensions(rendered.BoundsPath);
        var trigger = dimensions["ModeMenuButton"].BoundsDips
            ?? throw new InvalidOperationException("Mode menu trigger bounds are missing.");
        var mainBounds = ScreenshotHelper.GetWindowPhysicalBounds(rendered.Window);
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(rendered.Window);
        var triggerPoint = new Point(
            mainBounds.Left + (int)Math.Round((trigger.Left + trigger.Width / 2) * dpiScale),
            mainBounds.Top + (int)Math.Round((trigger.Top + trigger.Height / 2) * dpiScale));
        if (optionId == "long-document")
        {
            CaptureForegroundWindow(
                rendered.Window,
                "main-mode-title-rust-win-fluent-iced");
        }

        _output.WriteLine(
            $"Mode trigger: bounds={trigger}, main={mainBounds}, dpi={dpiScale:F3}, point={triggerPoint}");
        var visiblePopupStyles = new System.Collections.Concurrent.ConcurrentQueue<int>();
        using var styleProbeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var styleProbe = Task.Run(() =>
        {
            while (!styleProbeCancellation.IsCancellationRequested)
            {
                var hwnd = FindProcessWindowByTitle(session.ProcessId, "Easydict Mode Menu");
                if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
                {
                    visiblePopupStyles.Enqueue(GetWindowLongPtr(hwnd, GwlStyle));
                }

                Thread.Yield();
            }
        });

        var marker = session.CaptureDebugLineMarker();
        Mouse.MoveTo(triggerPoint);
        Thread.Sleep(100);
        Mouse.Click(triggerPoint);
        session.WaitForDebugLine(marker, "message=OpenModeMenu", TimeSpan.FromSeconds(5));

        var popupHwnd = WaitForProcessWindowByTitle(
            session.ProcessId,
            "Easydict Mode Menu",
            TimeSpan.FromSeconds(5));
        CountVisibleProcessWindowsByTitle(session.ProcessId, "Easydict Mode Menu")
            .Should()
            .Be(1, "opening the mode menu must create exactly one visible popup");
        var popupBounds = TryGetNativeWindowRectangle(popupHwnd)
            ?? throw new InvalidOperationException("Mode menu popup bounds are unavailable.");
        var virtualScreen = ScreenshotHelper.GetVirtualScreenBounds();
        virtualScreen.Contains(popupBounds)
            .Should()
            .BeTrue("mode popup must stay fully visible on the active virtual screen");
        ((double)popupBounds.Width).Should().BeApproximately(220 * dpiScale, 2);
        ((double)popupBounds.Height).Should().BeApproximately(80 * dpiScale, 2);
        var triggerTop = mainBounds.Top + (int)Math.Round(trigger.Top * dpiScale);
        popupBounds.Bottom
            .Should()
            .BeLessThanOrEqualTo(
                triggerTop - Math.Max(4, (int)Math.Round(6 * dpiScale)),
                "the complete mode popup should open above the title trigger");
        var styleStopwatch = Stopwatch.StartNew();
        var extendedStyle = GetWindowLongPtr(popupHwnd, GWL_EXSTYLE);
        while ((extendedStyle & WS_EX_TOOLWINDOW) == 0 &&
               styleStopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            Thread.Sleep(50);
            extendedStyle = GetWindowLongPtr(popupHwnd, GWL_EXSTYLE);
        }
        var nativeStyle = GetWindowLongPtr(popupHwnd, GwlStyle);
        while ((nativeStyle & unchecked((int)WsPopup)) == 0 &&
               styleStopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            Thread.Sleep(50);
            nativeStyle = GetWindowLongPtr(popupHwnd, GwlStyle);
        }
        (extendedStyle & WS_EX_TOOLWINDOW).Should().NotBe(0);
        (extendedStyle & WS_EX_NOACTIVATE).Should().Be(0);
        (nativeStyle & unchecked((int)WsPopup)).Should().NotBe(0);
        (nativeStyle & unchecked((int)WsCaption)).Should().Be(0);
        Thread.Sleep(250);
        styleProbeCancellation.Cancel();
        styleProbe.Wait(TimeSpan.FromSeconds(2))
            .Should().BeTrue("the temporal popup-style probe must stop promptly");
        var sampledPopupStyles = visiblePopupStyles.ToArray();
        sampledPopupStyles.Should().NotBeEmpty("the probe must observe the popup from its first visible frame");
        var invalidPopupStyles = sampledPopupStyles
            .Where(style =>
                (style & unchecked((int)WsPopup)) == 0 ||
                (style & unchecked((int)WsCaption)) != 0)
            .Select(style => $"0x{unchecked((uint)style):X8}")
            .Distinct()
            .ToArray();
        invalidPopupStyles.Should().BeEmpty(
            "the popup must never expose native caption chrome while becoming visible; observed {0}",
            string.Join(", ", invalidPopupStyles));


        if (optionId == "long-document")
        {
            openScreenshotPath = ScreenshotHelper.CaptureScreenRegion(
                Rectangle.Union(mainBounds, popupBounds),
                "main-mode-popup-open-rust-win-fluent-iced");
        }

        var optionControlId = optionId == "long-document"
            ? "ModeLongDocItem"
            : "ModeTranslationItem";
        var optionBounds = session.WaitForLiveControlBounds(
            optionControlId,
            TimeSpan.FromSeconds(5));
        var optionPhysicalBounds = Rectangle.FromLTRB(
            popupBounds.Left + (int)Math.Floor(optionBounds.Left * dpiScale),
            popupBounds.Top + (int)Math.Floor(optionBounds.Top * dpiScale),
            popupBounds.Left + (int)Math.Ceiling((optionBounds.Left + optionBounds.Width) * dpiScale),
            popupBounds.Top + (int)Math.Ceiling((optionBounds.Top + optionBounds.Height) * dpiScale));
        popupBounds.Contains(optionPhysicalBounds)
            .Should()
            .BeTrue(
                $"Mode option '{optionText}' ({optionControlId}) must lie inside the popup window");
        var optionPoint = new Point(
            optionPhysicalBounds.Left + optionPhysicalBounds.Width / 2,
            optionPhysicalBounds.Top + optionPhysicalBounds.Height / 2);
        System.Collections.Concurrent.ConcurrentQueue<int>? transitionPixels = null;
        CancellationTokenSource? transitionProbeCancellation = null;
        Task? transitionProbe = null;
        if (ResolveRustPreviewTheme("light") == "dark")
        {
            transitionPixels = new System.Collections.Concurrent.ConcurrentQueue<int>();
            transitionProbeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var samplePoint = new Point(
                mainBounds.Left + mainBounds.Width / 2,
                mainBounds.Bottom - Math.Max(16, (int)Math.Round(24 * dpiScale)));
            var pixels = transitionPixels;
            var cancellation = transitionProbeCancellation;
            transitionProbe = Task.Run(() =>
            {
                using var pixel = new Bitmap(1, 1);
                using var graphics = Graphics.FromImage(pixel);
                while (!cancellation.IsCancellationRequested)
                {
                    graphics.CopyFromScreen(samplePoint, Point.Empty, pixel.Size);
                    pixels.Enqueue(pixel.GetPixel(0, 0).ToArgb());
                    Thread.Sleep(1);
                }
            });
        }

        Mouse.Click(optionPoint);
        session.WaitForDebugLine(marker, expectedDebugLine, TimeSpan.FromSeconds(5));
        var closeStopwatch = Stopwatch.StartNew();
        while (IsWindowVisible(popupHwnd) && closeStopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            Thread.Sleep(80);
        }
        IsWindowVisible(popupHwnd)
            .Should()
            .BeFalse("selecting a mode must close the popup");
        if (transitionProbe != null &&
            transitionProbeCancellation != null &&
            transitionPixels != null)
        {
            Thread.Sleep(250);
            transitionProbeCancellation.Cancel();
            transitionProbe.Wait(TimeSpan.FromSeconds(2))
                .Should().BeTrue("the transition theme probe must stop promptly");
            var sampledPixels = transitionPixels.ToArray();
            sampledPixels.Should().NotBeEmpty(
                "the probe must observe the first frames after the mode change");
            var brightPixels = sampledPixels
                .Where(argb =>
                {
                    var red = (argb >> 16) & 0xFF;
                    var green = (argb >> 8) & 0xFF;
                    var blue = argb & 0xFF;
                    return 0.299 * red + 0.587 * green + 0.114 * blue > 180;
                })
                .ToArray();
            _output.WriteLine(
                $"Dark mode transition probe: samples={sampledPixels.Length}, bright={brightPixels.Length}");
            brightPixels.Should().BeEmpty(
                "a dark mode transition must not expose a light pre-themed frame");
        }

        if (optionId == "long-document")
        {
            EnsureWindowForegroundForMouseInput(rendered.Window, "mode menu focus dismissal");
            var reopenedTrigger = session.WaitForLiveControlBounds(
                "ModeMenuButton",
                TimeSpan.FromSeconds(5));
            var reopenedMainBounds = ScreenshotHelper.GetWindowPhysicalBounds(rendered.Window);
            var reopenedTriggerPoint = new Point(
                reopenedMainBounds.Left +
                    (int)Math.Round((reopenedTrigger.Left + reopenedTrigger.Width / 2) * dpiScale),
                reopenedMainBounds.Top +
                    (int)Math.Round((reopenedTrigger.Top + reopenedTrigger.Height / 2) * dpiScale));
            var reopenMarker = session.CaptureDebugLineMarker();
            Mouse.MoveTo(reopenedTriggerPoint);
            Thread.Sleep(100);
            Mouse.Click(reopenedTriggerPoint);
            session.WaitForDebugLine(reopenMarker, "message=OpenModeMenu", TimeSpan.FromSeconds(5));
            var focusPopupHwnd = WaitForProcessWindowByTitle(
                session.ProcessId,
                "Easydict Mode Menu",
                TimeSpan.FromSeconds(5));
            var popupFocusStopwatch = Stopwatch.StartNew();
            while (GetForegroundWindow() != focusPopupHwnd &&
                   popupFocusStopwatch.Elapsed < TimeSpan.FromSeconds(3))
            {
                Thread.Sleep(50);
            }
            GetForegroundWindow()
                .Should()
                .Be(focusPopupHwnd, "the opened mode popup must receive focus before focus-loss is tested");
            EnsureWindowForegroundForMouseInput(rendered.Window, "mode menu focus dismissal");
            var focusCloseStopwatch = Stopwatch.StartNew();
            while (IsWindowVisible(focusPopupHwnd) &&
                   focusCloseStopwatch.Elapsed < TimeSpan.FromSeconds(3))
            {
                Thread.Sleep(80);
            }
            IsWindowVisible(focusPopupHwnd)
                .Should()
                .BeFalse("moving focus back to the main window must close the popup");
        }
        return openScreenshotPath;
    }

    private static IntPtr WaitForProcessWindowByTitle(
        int processId,
        string title,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var hwnd = FindProcessWindowByTitle(processId, title);
            if (hwnd != IntPtr.Zero)
            {
                return hwnd;
            }

            Thread.Sleep(80);
        }

        throw new TimeoutException(
            $"Visible window '{title}' did not appear for process {processId} within {timeout}.");
    }

    private static IntPtr FindProcessWindowByTitle(int processId, string title)
    {
        var result = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var ownerProcessId);
            if (ownerProcessId == processId &&
                IsWindowVisible(hwnd) &&
                string.Equals(GetWindowTitle(hwnd), title, StringComparison.Ordinal))
            {
                result = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static int CountVisibleProcessWindowsByTitle(int processId, string title)
    {
        var count = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var ownerProcessId);
            if (ownerProcessId == processId &&
                IsWindowVisible(hwnd) &&
                string.Equals(GetWindowTitle(hwnd), title, StringComparison.Ordinal))
            {
                count++;
            }

            return true;
        }, IntPtr.Zero);
        return count;
    }

    private static void EnsureWindowForegroundForMouseInput(
        Window window,
        string name)
    {
        var hwnd = SafeNativeWindowHandle(window);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            window.SetForeground();
            Thread.Sleep(150);
            if (hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd)
            {
                return;
            }
        }

        Mouse.Click(GetWindowRelativePoint(window, 0.50, 0.02));
        Thread.Sleep(200);
        window.SetForeground();
        Thread.Sleep(150);
        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{name} window did not become foreground for mouse input.");
    }

    private static UiParityControlBoundsDips AdjustRustBoundsForPreviewScroll(
        RustComboBoxMouseSelectionCase testCase,
        IReadOnlyDictionary<string, UiParityControlDimension> dimensions,
        UiParityControlBoundsDips bounds)
    {
        if (!testCase.EnvironmentOverrides.TryGetValue(
                "EASYDICT_PREVIEW_SCROLL_TARGET",
                out var scrollTargetId) ||
            !testCase.EnvironmentOverrides.TryGetValue(
                "EASYDICT_PREVIEW_SCROLL_PERCENT",
                out var rawScrollPercent) ||
            !double.TryParse(
                rawScrollPercent,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var scrollPercent))
        {
            return bounds;
        }

        var scrollContentId = scrollTargetId switch
        {
            "main.long-doc.scroll" => "main.long-doc.content",
            _ => throw new InvalidOperationException(
                $"Missing scroll-content mapping for {scrollTargetId}.")
        };
        var scrollBounds = dimensions.TryGetValue(scrollTargetId, out var scroll) ?
            scroll.BoundsDips : null;
        var contentBounds = dimensions.TryGetValue(scrollContentId, out var content) ?
            content.BoundsDips : null;
        if (scrollBounds is null || contentBounds is null)
        {
            throw new InvalidOperationException(
                $"Missing bounds for preview scroll '{scrollTargetId}' or '{scrollContentId}'.");
        }

        var normalizedPercent = Math.Clamp(
            scrollPercent > 1 ? scrollPercent / 100 : scrollPercent,
            0,
            1);
        var scrollOffset = Math.Max(
            0,
            contentBounds.Height - scrollBounds.Height) * normalizedPercent;
        return bounds with { Top = bounds.Top - scrollOffset };
    }

    private static RustComboOverlayGeometry ParseRustComboOverlayGeometry(
        string line,
        string expectedControlId)
    {
        var values = line
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(2)
            .Select(token => token.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (!values.TryGetValue("control_id", out var controlId) ||
            !string.Equals(controlId, expectedControlId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Overlay diagnostic control id did not match '{expectedControlId}': {line}");
        }

        static double Required(IReadOnlyDictionary<string, string> source, string key)
        {
            if (!source.TryGetValue(key, out var raw) ||
                !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value))
            {
                throw new InvalidOperationException($"Overlay diagnostic is missing numeric '{key}'.");
            }

            return value;
        }

        if (!values.TryGetValue("selected_index", out var selectedRaw) ||
            !int.TryParse(selectedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedIndex) ||
            !values.TryGetValue("viewport_clamped", out var clampedRaw) ||
            !bool.TryParse(clampedRaw, out var viewportClamped))
        {
            throw new InvalidOperationException($"Overlay diagnostic has invalid selection fields: {line}");
        }

        return new RustComboOverlayGeometry(
            controlId,
            Required(values, "collapsed_x"),
            Required(values, "collapsed_y"),
            Required(values, "collapsed_width"),
            Required(values, "collapsed_height"),
            Required(values, "menu_x"),
            Required(values, "menu_y"),
            Required(values, "menu_width"),
            Required(values, "menu_height"),
            Required(values, "row_height"),
            selectedIndex,
            viewportClamped);
    }

    private static void AssertOverlayWithinWindow(
        RustComboOverlayGeometry geometry,
        int itemCount,
        float widthDips,
        float heightDips)
    {
        const double tolerance = 2;
        geometry.MenuLeft.Should().BeGreaterThanOrEqualTo(-tolerance);
        geometry.MenuTop.Should().BeGreaterThanOrEqualTo(-tolerance);
        (geometry.MenuLeft + geometry.MenuWidth)
            .Should().BeLessThanOrEqualTo(widthDips + tolerance);
        (geometry.MenuTop + geometry.MenuHeight)
            .Should().BeLessThanOrEqualTo(float.IsFinite(heightDips) ? heightDips + tolerance : double.MaxValue);
        if (!geometry.ViewportClamped && geometry.SelectedIndex >= 0)
        {
            var rowHeight = Math.Max(1, geometry.RowHeight);
            var maximumScrollOffset = Math.Max(0, itemCount * rowHeight - geometry.MenuHeight);
            var scrollOffset = Math.Min(
                geometry.SelectedIndex * rowHeight,
                maximumScrollOffset);
            var selectedCenter = geometry.MenuTop
                + geometry.SelectedIndex * rowHeight
                - scrollOffset
                + rowHeight / 2;
            (selectedCenter - (geometry.CollapsedTop + geometry.CollapsedHeight / 2))
                .Should().BeInRange(-tolerance, tolerance);
        }
    }

    private static Point RustComboBoxOptionClickPoint(
        Window window,
        RustComboOverlayGeometry geometry,
        RustComboBoxMouseSelectionCase testCase,
        string optionText,
        int optionRow,
        int selectedRow)
    {
        var rowHeight = Math.Max(1, geometry.RowHeight);
        var contentHeight = testCase.ItemCount * rowHeight;
        var maximumScrollOffset = Math.Max(0, contentHeight - geometry.MenuHeight);
        var scrollOffset = Math.Min(selectedRow * rowHeight, maximumScrollOffset);
        var optionX = geometry.MenuLeft + geometry.MenuWidth / 2;
        var optionY = geometry.MenuTop + optionRow * rowHeight - scrollOffset + rowHeight / 2;
        if (optionX < geometry.MenuLeft ||
            optionX > geometry.MenuLeft + geometry.MenuWidth ||
            optionY < geometry.MenuTop ||
            optionY > geometry.MenuTop + geometry.MenuHeight)
        {
            throw new InvalidOperationException(
                $"ComboBox option '{optionText}' resolved outside menu bounds at ({optionX:0.##}, {optionY:0.##}) DIP.");
        }

        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        var point = new Point(
            windowBounds.Left + (int)Math.Round(optionX * dpiScale),
            windowBounds.Top + (int)Math.Round(optionY * dpiScale));
        if (!new Rectangle(windowBounds.Left, windowBounds.Top, windowBounds.Width, windowBounds.Height)
                .Contains(point))
        {
            throw new InvalidOperationException(
                $"ComboBox option '{optionText}' resolved outside physical window at {point}.");
        }

        return point;
    }

    [Fact]
    public void RustPreviewMetrics_ShouldCountOneSessionProcessAndTwoSuccessfulGenerations()
    {
        ResetRustPreviewRunMetrics();
        RecordRustPreviewProcessStart();
        RecordRustPreviewRenderSuccess(11);
        RecordRustPreviewRenderSuccess(17);

        var metrics = SnapshotRustPreviewRunMetrics();
        metrics.RustProcessStarts.Should().Be(1);
        metrics.RustRenderRequests.Should().Be(2);
        metrics.RustRenderDurationsMs.Should().Equal(11, 17);
        metrics.RustTimeouts.Should().Be(0);
        metrics.HarnessInvalid.Should().Be(0);
    }

    [Fact]
    public void RustPreviewExecutableResolution_ShouldHonorExplicitBuildRequest()
    {
        SelectRustPreviewDefaultExecutable(buildRequested: true, defaultExecutableExists: true)
            .Should().Be(RustPreviewDefaultExecutableResolution.Build);
        SelectRustPreviewDefaultExecutable(buildRequested: true, defaultExecutableExists: false)
            .Should().Be(RustPreviewDefaultExecutableResolution.Build);
        SelectRustPreviewDefaultExecutable(buildRequested: false, defaultExecutableExists: true)
            .Should().Be(RustPreviewDefaultExecutableResolution.Existing);
        SelectRustPreviewDefaultExecutable(buildRequested: false, defaultExecutableExists: false)
            .Should().Be(RustPreviewDefaultExecutableResolution.Missing);
    }

    [Fact]
    public void RustSchemaQuotedValueParser_ShouldUnescapeVisibleTextValues()
    {
        var pathLine =
            "Text value=\"C:\\\\Users\\\\johnn\\\\Documents\\\\Easydict\\\\LongDocOutputs\" id=\"main.long-doc.output_folder\"";
        var quotedLine = "Button label=\"Say \\\"Hello\\\"\" id=\"quote-button\"";
        var iconLine = "Text value=\"\\u{e897}\" id=\"LongDocServiceHint\"";

        TryExtractRustSchemaQuotedValue(pathLine, "value")
            .Should().Be(@"C:\Users\johnn\Documents\Easydict\LongDocOutputs");
        TryExtractRustSchemaQuotedValue(quotedLine, "label")
            .Should().Be("Say \"Hello\"");
        TryExtractRustSchemaQuotedValue(iconLine, "value")
            .Should().Be(@"\u{e897}");

        var splitHeaderTexts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRustSchemaVisibleText(splitHeaderTexts, "main.long-doc.page_range_cell.label", "📑 Pages");
        splitHeaderTexts.Should().BeEquivalentTo(["📑", "Pages"]);

        var sourceHeaderTexts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRustSchemaVisibleText(sourceHeaderTexts, "main.long-doc.source_language_cell.label", "🌐 Source");
        sourceHeaderTexts.Should().BeEquivalentTo(["🌐 Source"]);
    }

    [Fact]
    public void FilterDropdownOptionCaptures_WithUnsetEnvironment_ReturnsAllOptions()
    {
        using var scope = new EnvironmentVariableScope(DropdownOptionIndexesEnvironmentVariable, null);
        var options = new[]
        {
            new SettingsDropdownOptionCapture("A", 0),
            new SettingsDropdownOptionCapture("B", 1),
            new SettingsDropdownOptionCapture("C", 2)
        };
        var step = new SettingsParityCaptureStep(
            "test.dropdown",
            SettingsParitySection.General,
            0,
            DropdownOptions: options);

        FilterDropdownOptionCaptures(step).Should().BeEquivalentTo(options);
    }

    [Fact]
    public void FilterDropdownOptionCaptures_WithOneBasedIndexes_ReturnsMatchingDotnetIndexes()
    {
        using var scope = new EnvironmentVariableScope(DropdownOptionIndexesEnvironmentVariable, "1, 3");
        var options = new[]
        {
            new SettingsDropdownOptionCapture("A", 0),
            new SettingsDropdownOptionCapture("B", 1),
            new SettingsDropdownOptionCapture("C", 2)
        };
        var step = new SettingsParityCaptureStep(
            "test.dropdown",
            SettingsParitySection.General,
            0,
            DropdownOptions: options);

        FilterDropdownOptionCaptures(step)
            .Select(option => option.DotnetIndex)
            .Should().Equal(0, 2);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void FilterDropdownOptionCaptures_WithInvalidIndex_Throws(string value)
    {
        using var scope = new EnvironmentVariableScope(DropdownOptionIndexesEnvironmentVariable, value);
        var step = new SettingsParityCaptureStep(
            "test.dropdown",
            SettingsParitySection.General,
            0,
            DropdownOptions:
            [
                new SettingsDropdownOptionCapture("A", 0)
            ]);

        Action act = () => FilterDropdownOptionCaptures(step);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FilterDropdownOptionCaptures_WithOutOfRangeIndex_ThrowsForDropdown()
    {
        using var scope = new EnvironmentVariableScope(DropdownOptionIndexesEnvironmentVariable, "3");
        var step = new SettingsParityCaptureStep(
            "test.dropdown",
            SettingsParitySection.General,
            0,
            DropdownOptions:
            [
                new SettingsDropdownOptionCapture("A", 0),
                new SettingsDropdownOptionCapture("B", 1)
            ]);

        Action act = () => FilterDropdownOptionCaptures(step);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*test.dropdown*2*");
    }

    [Theory]
    [InlineData("德语", "德语", true)]
    [InlineData(" 德语 ", "德语", true)]
    [InlineData("DEUTSCH", "deutsch", true)]
    [InlineData("选择德语", "德语", false)]
    [InlineData("越南语", "德语", false)]
    public void DropdownOptionNameEquals_RequiresExactDisplayedText(
        string actualName,
        string expectedName,
        bool expected)
    {
        DropdownOptionNameEquals(actualName, expectedName).Should().Be(expected);
    }


    [Fact]
    public void RustSchemaUiSummary_ShouldIncludeMainIconButtonAutomationNames()
    {
        var schemaPath = Path.Combine(
            Path.GetTempPath(),
            $"easydict-rust-schema-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(
                schemaPath,
                """
                ViewSchema version=1
                Button label="固定窗口（保持置顶）" kind=Icon icon=pin tooltip="固定窗口（保持置顶）" id="PinButton"
                Button label="设置" kind=Icon icon=settings tooltip="设置" id="SettingsButton"
                Button label="朗读源文本" kind=Icon icon=play tooltip="朗读源文本" id="main.quick.play_source"
                Button label="交换源语言和目标语言" kind=Icon icon=swap tooltip="交换源语言和目标语言" id="SwapLanguageButton"
                Button label="" kind=PrimaryRound icon=translate tooltip="翻译" id="TranslateButton"
                Button label="" kind=PrimaryRound icon=translate tooltip="翻译" id="main.long-doc.translate"
                Button label="Decorative" kind=Icon icon=info tooltip="Decorative" id="DecorativeIconButton"
                """);

            var summary = TryReadRustSchemaUiSummary(schemaPath);

            summary.Should().NotBeNull();
            summary!.VisibleTexts.Should().Contain(
            [
                "固定窗口（保持置顶）",
                "设置",
                "朗读源文本",
                "交换源语言和目标语言",
                "翻译"
            ]);
            summary.VisibleTexts.Should().NotContain("Decorative");
            summary.VisibleControlCounts["text"].Should().Be(0);
        }
        finally
        {
            File.Delete(schemaPath);
        }
    }

    [Fact]
    public void RustSchemaUiSummary_ShouldDerivePendingQueryStatusText()
    {
        var schemaPath = Path.Combine(
            Path.GetTempPath(),
            $"easydict-rust-schema-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(
                schemaPath,
                """
                ViewSchema version=1
                ResultItem title="Windows Local AI" body_len=0 icon=service-local-ai metadata=none pending_hint="点击服务标题查询" expanded=false id="windows-local-ai"
                """);

            var summary = TryReadRustSchemaUiSummary(schemaPath);

            summary.Should().NotBeNull();
            summary!.VisibleTexts.Should().Contain(
            [
                "Windows Local AI",
                "点击服务标题查询",
                "点击查询"
            ]);
            summary.VisibleControlCounts["text"].Should().Be(2);
        }
        finally
        {
            File.Delete(schemaPath);
        }
    }

    [Fact]
    public void RustSchemaUiSummary_ShouldNotCountLongDocAuxiliaryGlyphsAsTextControls()
    {
        var schemaPath = Path.Combine(
            Path.GetTempPath(),
            $"easydict-rust-schema-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(
                schemaPath,
                """
                ViewSchema version=1
                Text value="🌐 Source" id="main.long-doc.source_language_cell.label"
                Text value="🤖 Service" id="main.long-doc.service_cell.label"
                Text value="\u{e897}" id="LongDocServiceHint"
                Text value="Output Folder" id="main.long-doc.output_folder_label"
                Button label="Browse..." kind=Default id="main.long-doc.browse"
                Button label="Retry Failed" kind=Default id="main.long-doc.retry"
                """);

            var summary = TryReadRustSchemaUiSummary(schemaPath);

            summary.Should().NotBeNull();
            summary!.VisibleTexts.Should().Contain(["🌐 Source", "Output Folder", "Browse...", "Retry Failed"]);
            summary.VisibleTexts.Should().NotContain(["🤖 Service", @"\u{e897}"]);
            summary.VisibleControlCounts["text"].Should().Be(2);
        }
        finally
        {
            File.Delete(schemaPath);
        }
    }

    [Fact]
    public void RustSchemaUiSummary_ShouldPreserveGridSpacingEvidence()
    {
        var schemaPath = Path.Combine(
            Path.GetTempPath(),
            $"easydict-rust-schema-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(
                schemaPath,
                """
                ViewSchema version=1
                Grid rows=[Fixed(60),Fixed(58),Shrink] columns=[Fill,Fill,Fill,Fill] row_spacing=4 column_spacing=8 padding=0 width=Fill height=Shrink align=Start children=9 id="LongDocControlGrid"
                """);

            var summary = TryReadRustSchemaUiSummary(schemaPath);

            summary.Should().NotBeNull();
            summary!.VisibleControlDimensions.Should().ContainKey("LongDocControlGrid");
            summary.VisibleAutomationIds.Should().NotContain("LongDocControlGrid");
            var dimension = summary.VisibleControlDimensions["LongDocControlGrid"];
            dimension.RowSpacing.Should().Be("4");
            dimension.ColumnSpacing.Should().Be("8");
            dimension.Columns.Should().Be("[Fill,Fill,Fill,Fill]");
        }
        finally
        {
            File.Delete(schemaPath);
        }
    }

    [Fact]
    public void RustSchemaUiSummary_ShouldUseLongDocCellHeightForTextEditorReferenceControls()
    {
        var schemaPath = Path.Combine(
            Path.GetTempPath(),
            $"easydict-rust-schema-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(
                schemaPath,
                """
                ViewSchema version=1
                TextEditor placeholder="Threads" width=Fill height=Fixed(36) max_height=36 id="main.long-doc.concurrency"
                TextEditor placeholder="1-3,5,7-10" width=Fill height=Fixed(36) max_height=36 id="main.long-doc.page_range"
                """);

            var summary = TryReadRustSchemaUiSummary(schemaPath);

            summary.Should().NotBeNull();
            summary!.VisibleControlDimensions.Should().ContainKeys(
                "LongDocConcurrencyBox",
                "LongDocPageRangeBox");
            summary.VisibleControlDimensions["LongDocConcurrencyBox"].Height.Should().Be("Fixed(36)");
            summary.VisibleControlDimensions["LongDocConcurrencyBox"].LabeledHeight.Should().Be("Fixed(58)");
            summary.VisibleControlDimensions["LongDocPageRangeBox"].Height.Should().Be("Fixed(36)");
            summary.VisibleControlDimensions["LongDocPageRangeBox"].LabeledHeight.Should().Be("Fixed(58)");
        }
        finally
        {
            File.Delete(schemaPath);
        }
    }

    [Fact]
    public void RustBoundsParser_ShouldReadRuntimeControlBoundsEvidence()
    {
        var boundsPath = Path.Combine(
            Path.GetTempPath(),
            $"easydict-rust-bounds-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(
                boundsPath,
                """
                ViewBounds version=1
                Bounds id="InputTextBox" kind=TextEditor x=28.50 y=117.00 width=373.00 height=80.00
                Bounds id="main.long-doc.browse" kind=Button x=330.50 y=120.00 width=78.00 height=32.00
                Bounds id="TargetLangCombo" kind=ComboBox x=451.25 y=117.00 width=188.50 height=32.00
                """);

            var dimensions = TryReadRustBoundsControlDimensions(boundsPath);

            dimensions.Should().ContainKeys("InputTextBox", "main.long-doc.browse", "TargetLangCombo");
            dimensions["InputTextBox"].Kind.Should().Be("TextEditor");
            dimensions["InputTextBox"].Width.Should().Be("373");
            dimensions["InputTextBox"].Height.Should().Be("80");
            dimensions["InputTextBox"].BoundsDips.Should().Be(
                new UiParityControlBoundsDips(28.5, 117, 373, 80));
            dimensions["main.long-doc.browse"].BoundsDips.Should().Be(
                new UiParityControlBoundsDips(330.5, 120, 78, 32));
            dimensions["TargetLangCombo"].BoundsDips.Should().Be(
                new UiParityControlBoundsDips(451.25, 117, 188.5, 32));
        }
        finally
        {
            File.Delete(boundsPath);
        }
    }

    [Fact]
    public void Settings_ShouldRenderDotnetAndRustPreviewSideBySide()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust parity run is opt-in. Set {EnableEnvironmentVariable}=1 to launch both UI processes.");
            return;
        }

        var steps = ResolveCaptureSteps();
        var manifestEntries = new List<UiParityManifestEntry>();

        EnsureParityDpiAwareness();
        SeedDotnetParitySettings();
        _dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
        var dotnetWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
        using var settingsDpiScope = new EnvironmentVariableScope(
            "EASYDICT_PREVIEW_DPI",
            (ScreenshotHelper.GetWindowDpiScale(dotnetWindow) * 96.0)
            .ToString("0.###", CultureInfo.InvariantCulture));

        foreach (var step in steps)
        {
            var rustPreview = RenderSettingsPreview(step, _output);
            var rustWindow = rustPreview.GetMainWindow(TimeSpan.FromSeconds(30));
            var dotnetScrollViewer = OpenDotnetSettingsSection(dotnetWindow, step.Section);

            ArrangeSettingsWindowsForCapture(dotnetWindow, rustWindow);

            if (step.ExpandAvailableLanguages)
            {
                ExpandDotnetAvailableLanguages(dotnetWindow, dotnetScrollViewer, step);
            }
            if (!string.IsNullOrWhiteSpace(step.DotnetExpandElement))
            {
                ExpandDotnetSettingsExpander(dotnetWindow, dotnetScrollViewer, step);
            }

            ScrollBothWindowsToPercent(dotnetScrollViewer, rustWindow, step);
            AssertCaptureStepReady(dotnetWindow, dotnetScrollViewer, rustWindow, step);
            ApplyDotnetSettingsInteractionState(dotnetWindow, step);
            AssertWindowFullyVisible(dotnetWindow, step.Key, "dotnet");
            AssertWindowFullyVisible(rustWindow, step.Key, "rust");

            dotnetWindow.SetForeground();
            Thread.Sleep(150);
            HideFloatingLanguageBars();
            var dotnetPath = step.CapturesExpandedDropdown
                ? CaptureExpandedSettingsDropdownStep(
                    dotnetWindow,
                    step,
                    $"{step.Key}-dotnet-winui-reference")
                : CaptureDotnetSettingsStep(
                    dotnetWindow,
                    step,
                    $"{step.Key}-dotnet-winui-reference");
            MaskFloatingLanguageBarOcclusions(dotnetPath, dotnetWindow);
            var dotnetDropdownOptionPaths = step.CapturesDropdownOptionSelections
                ? CaptureSettingsDropdownOptionSelections(
                    dotnetWindow,
                    step,
                    "dotnet-winui-reference",
                    rustSchemaPath: null)
                : Array.Empty<SettingsDropdownOptionCaptureResult>();
            DismissExpandedDropdownIfNeeded(step);
            MoveMouseToNeutralPoint();
            rustWindow.SetForeground();
            Thread.Sleep(150);
            MoveMouseToNeutralPoint();
            HideFloatingLanguageBars();
            var rustPath = step.CapturesExpandedDropdown
                ? CaptureExpandedSettingsDropdownStep(
                    rustWindow,
                    step,
                    $"{step.Key}-rust-win-fluent-iced",
                    rustPreview.SchemaPath)
                : CaptureWindowPreferHwnd(
                    rustWindow,
                    $"{step.Key}-rust-win-fluent-iced");
            MaskFloatingLanguageBarOcclusions(rustPath, rustWindow);
            var rustDropdownOptionPaths = step.CapturesDropdownOptionSelections
                ? CaptureSettingsDropdownOptionSelections(
                    rustWindow,
                    step,
                    "rust-win-fluent-iced",
                    rustPreview.SchemaPath)
                : Array.Empty<SettingsDropdownOptionCaptureResult>();
            DismissExpandedDropdownIfNeeded(step);
            var sideBySidePath = SaveSideBySideComparison(
                dotnetPath,
                rustPath,
                $"{step.Key}-dotnet-vs-rust-side-by-side");
            manifestEntries.Add(CreateManifestEntry(
                step,
                dotnetWindow,
                rustWindow,
                dotnetPath,
                rustPath,
                sideBySidePath,
                rustPreview.SchemaPath));
            SaveManifest(manifestEntries);

            AssertImageHasVisibleContent(dotnetPath);
            AssertImageHasVisibleContent(rustPath);
            AssertImageHasVisibleContent(sideBySidePath);

            _output.WriteLine($"[{step.Key}] Dotnet screenshot: {dotnetPath}");
            _output.WriteLine($"[{step.Key}] Rust screenshot: {rustPath}");
            _output.WriteLine($"[{step.Key}] Side-by-side comparison: {sideBySidePath}");

            foreach (var optionPair in PairDropdownOptionCaptures(dotnetDropdownOptionPaths, rustDropdownOptionPaths))
            {
                var optionSideBySidePath = SaveSideBySideComparison(
                    optionPair.Dotnet.ScreenshotPath,
                    optionPair.Rust.ScreenshotPath,
                    $"{optionPair.Dotnet.ScenarioId}-dotnet-vs-rust-side-by-side");
                var optionStep = step with
                {
                    Key = optionPair.Dotnet.ScenarioId,
                    HoveredElement = null,
                    FocusedElement = null,
                    PressedElement = null,
                    ExpandedDropdownElement = null,
                    ExpectedDropdownItems = null,
                    BaselineScenarioId = step.Key
                };
                manifestEntries.Add(CreateManifestEntry(
                    optionStep,
                    dotnetWindow,
                    rustWindow,
                    optionPair.Dotnet.ScreenshotPath,
                    optionPair.Rust.ScreenshotPath,
                    optionSideBySidePath,
                    rustPreview.SchemaPath,
                    operatedDropdownElement: step.ExpandedDropdownElement,
                    selectedDropdownOption: optionPair.Dotnet.Option));
                SaveManifest(manifestEntries);

                AssertImageHasVisibleContent(optionPair.Dotnet.ScreenshotPath);
                AssertImageHasVisibleContent(optionPair.Rust.ScreenshotPath);
                AssertImageHasVisibleContent(optionSideBySidePath);

                _output.WriteLine(
                    $"[{optionPair.Dotnet.ScenarioId}] Dropdown option: {optionPair.Dotnet.Option.Label}");
                _output.WriteLine(
                    $"[{optionPair.Dotnet.ScenarioId}] Dotnet screenshot: {optionPair.Dotnet.ScreenshotPath}");
                _output.WriteLine(
                    $"[{optionPair.Dotnet.ScenarioId}] Rust screenshot: {optionPair.Rust.ScreenshotPath}");
                _output.WriteLine(
                    $"[{optionPair.Dotnet.ScenarioId}] Side-by-side comparison: {optionSideBySidePath}");
            }
        }

        SaveManifest(manifestEntries);
    }

    [Fact]
    public void MainWindowOperations_ShouldRenderDotnetAndRustPreviewSideBySide()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust parity run is opt-in. Set {EnableEnvironmentVariable}=1 to launch both UI processes.");
            return;
        }

        var captureScope = ResolveMainOperationsCaptureScope();
        var manifestEntries = new List<UiParityManifestEntry>();

        EnsureParityDpiAwareness();
        SeedDotnetParitySettings();
        _dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
        var dotnetWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        ConfigureRustMainPreviewSizeFromReference(dotnetWindow);
        SetDotnetMainInputText(dotnetWindow, "Hello from the Rust main window preview");

        var rustEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        var detectedLanguageText = WaitForMainDetectedLanguageText(dotnetWindow, TimeSpan.FromSeconds(5));
        if (!string.IsNullOrWhiteSpace(detectedLanguageText))
        {
            rustEnvironment["EASYDICT_PREVIEW_MAIN_DETECTED_LANGUAGE"] = detectedLanguageText;
        }
        rustEnvironment["EASYDICT_PREVIEW_MAIN_EFFECTIVE_SOURCE_LANGUAGE"] = "en";
        rustEnvironment["EASYDICT_PREVIEW_MAIN_GRAMMAR_CAPABLE_SERVICE"] = "windows-local-ai";
        if (!captureScope.CaptureButtons &&
            (captureScope.CaptureDropdowns || captureScope.CaptureDropdownOptions))
        {
            foreach (var dropdown in MainDropdownCaptures())
            {
                var dropdownAlias = dropdown.Key.StartsWith("source", StringComparison.OrdinalIgnoreCase)
                    ? "source"
                    : "target";
                if (MatchesOptionalEnvironmentFilter(
                    MainDropdownEnvironmentVariable,
                    dropdown.Key,
                    dropdown.Label,
                    dropdownAlias))
                {
                    CaptureMainDropdownInteraction(
                        manifestEntries,
                        dotnetWindow,
                        rustEnvironment,
                        dropdown,
                        captureScope.CaptureDropdownOptions);
                }
            }
            SaveManifest(manifestEntries);
            return;
        }


        var rustPreview = RenderMainPreview("before_translate",
        ResolveRustPreviewTheme("light"),
        _output,
        rustEnvironment);
        var rustWindow = rustPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustWindow);
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        WaitForMainWindowReady(rustWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "main.operations", "dotnet");
        AssertWindowFullyVisible(rustWindow, "main.operations", "rust");

        if (!captureScope.CaptureButtons &&
            !captureScope.CaptureDropdowns &&
            !captureScope.CaptureDropdownOptions)
        {
            PrepareNeutralMainCapture(dotnetWindow);
            var initialDotnetPath = CaptureForegroundWindow(
                dotnetWindow,
                "main.initial-dotnet-winui-reference");
            var initialRustPath = CaptureForegroundWindow(
                rustWindow,
                "main.initial-rust-win-fluent-iced");
            var initialSideBySidePath = SaveSideBySideComparison(
                initialDotnetPath,
                initialRustPath,
                "main.initial-dotnet-vs-rust-side-by-side");
            manifestEntries.Add(CreateMainManifestEntry(
                "main.initial",
                "Initial",
                dotnetWindow,
                rustWindow,
                initialDotnetPath,
                initialRustPath,
                initialSideBySidePath,
                UiParityRegion.DefaultMainRegions,
                ["QuickInputCard", "QuickOutputCard"],
                rustSchemaPath: rustPreview.SchemaPath));
            SaveManifest(manifestEntries);

            AssertImageHasVisibleContent(initialDotnetPath);
            AssertImageHasVisibleContent(initialRustPath);
            AssertImageHasVisibleContent(initialSideBySidePath);

            _output.WriteLine($"[main.initial] Dotnet screenshot: {initialDotnetPath}");
            _output.WriteLine($"[main.initial] Rust screenshot: {initialRustPath}");
            return;
        }

        if (captureScope.CaptureButtons)
        {
            foreach (var control in MainInteractionCaptures())
            {
                CaptureMainControlInteraction(
                    manifestEntries,
                    dotnetWindow,
                    rustWindow,
                    rustPreview.SchemaPath,
                    control);
            }
        }

        if (captureScope.CaptureDropdowns)
        {
            foreach (var dropdown in MainDropdownCaptures())
            {
                var dropdownAlias = dropdown.Key.StartsWith("source", StringComparison.OrdinalIgnoreCase)
                    ? "source"
                    : "target";
                if (!MatchesOptionalEnvironmentFilter(
                    MainDropdownEnvironmentVariable,
                    dropdown.Key,
                    dropdown.Label,
                    dropdownAlias))
                {
                    continue;
                }

                CaptureMainDropdownInteraction(
                    manifestEntries,
                    dotnetWindow,
                    rustEnvironment,
                    dropdown,
                    captureScope.CaptureDropdownOptions);
            }
        }

        SaveManifest(manifestEntries);
    }

    [Fact]
    public void MainWindowEffects_ShouldRenderDotnetAndRustPreviewSideBySide()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust parity run is opt-in. Set {EnableEnvironmentVariable}=1 to launch both UI processes.");
            return;
        }

        if (IsExplicitFalse(Environment.GetEnvironmentVariable(EffectsEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust main/effects parity run skipped because {EffectsEnvironmentVariable}=0.");
            return;
        }

        var manifestEntries = new List<UiParityManifestEntry>();

        EnsureParityDpiAwareness();
        SeedDotnetParitySettings();
        _dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
        var dotnetWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        ConfigureRustMainPreviewSizeFromReference(dotnetWindow);

        var rustPreview = RenderMainPreview("initial", ResolveRustPreviewTheme("light"), _output);
        var rustWindow = rustPreview.GetMainWindow(TimeSpan.FromSeconds(30));

        ArrangeSideBySide(dotnetWindow, rustWindow);
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        WaitForMainWindowReady(rustWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "main.initial", "dotnet");
        AssertWindowFullyVisible(rustWindow, "main.initial", "rust");

        PrepareNeutralMainCapture(dotnetWindow);
        var initialDotnetPath = CaptureForegroundWindow(
            dotnetWindow,
            "main.initial-dotnet-winui-reference");
        var initialRustPath = CaptureForegroundWindow(
            rustWindow,
            "main.initial-rust-win-fluent-iced");
        var initialSideBySidePath = SaveSideBySideComparison(
            initialDotnetPath,
            initialRustPath,
            "main.initial-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "main.initial",
            "Initial",
            dotnetWindow,
            rustWindow,
            initialDotnetPath,
            initialRustPath,
            initialSideBySidePath,
            UiParityRegion.DefaultMainRegions,
            ["QuickInputCard", "QuickOutputCard"],
            rustSchemaPath: rustPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(initialDotnetPath);
        AssertImageHasVisibleContent(initialRustPath);
        AssertImageHasVisibleContent(initialSideBySidePath);

        if (IsTruthy(Environment.GetEnvironmentVariable(MainInitialOnlyEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust main parity run stopped after initial capture because {MainInitialOnlyEnvironmentVariable}=1.");
            SaveManifest(manifestEntries);
            return;
        }

        MoveMouseToHoverTarget(dotnetWindow, "TranslateButton", fallbackX: 0.92, fallbackY: 0.24);
        var hoverDotnetPath = CaptureForegroundWindow(
            dotnetWindow,
            "effects.primary-hover-dotnet-winui-reference");
        MoveMouseToHoverTarget(rustWindow, "TranslateButton", fallbackX: 0.92, fallbackY: 0.24);
        var hoverRustPath = CaptureForegroundWindow(
            rustWindow,
            "effects.primary-hover-rust-win-fluent-iced");
        var hoverSideBySidePath = SaveSideBySideComparison(
            hoverDotnetPath,
            hoverRustPath,
            "effects.primary-hover-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "effects.primary-hover",
            "Primary Hover",
            dotnetWindow,
            rustWindow,
            hoverDotnetPath,
            hoverRustPath,
            hoverSideBySidePath,
            UiParityRegion.PrimaryButtonEffectRegions,
            [],
            rustSchemaPath: rustPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(hoverDotnetPath);
        AssertImageHasVisibleContent(hoverRustPath);
        AssertImageHasVisibleContent(hoverSideBySidePath);

        var pressedDotnetPath = CapturePressedWindow(
            dotnetWindow,
            "TranslateButton",
            fallbackX: 0.92,
            fallbackY: 0.24,
            "effects.primary-pressed-dotnet-winui-reference");
        var pressedRustPath = CapturePressedWindow(
            rustWindow,
            "TranslateButton",
            fallbackX: 0.92,
            fallbackY: 0.24,
            "effects.primary-pressed-rust-win-fluent-iced");
        var pressedSideBySidePath = SaveSideBySideComparison(
            pressedDotnetPath,
            pressedRustPath,
            "effects.primary-pressed-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "effects.primary-pressed",
            "Primary Pressed",
            dotnetWindow,
            rustWindow,
            pressedDotnetPath,
            pressedRustPath,
            pressedSideBySidePath,
            UiParityRegion.PrimaryButtonEffectRegions,
            [],
            rustSchemaPath: rustPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(pressedDotnetPath);
        AssertImageHasVisibleContent(pressedRustPath);
        AssertImageHasVisibleContent(pressedSideBySidePath);

        var rustResultHeaderPreview = RenderMainPreview("result_header_hover", ResolveRustPreviewTheme("light"), _output);
        var rustResultHeaderWindow = rustResultHeaderPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustResultHeaderWindow);
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        WaitForMainWindowReady(rustResultHeaderWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "effects.result-header-hover", "dotnet");
        AssertWindowFullyVisible(rustResultHeaderWindow, "effects.result-header-hover", "rust");

        MoveMouseToHoverTarget(dotnetWindow, "QuickOutputCard", fallbackX: 0.50, fallbackY: 0.65);
        var resultHeaderHoverDotnetPath = CaptureForegroundWindow(
            dotnetWindow,
            "effects.result-header-hover-dotnet-winui-reference");
        var resultHeaderHoverRustPath = CaptureForegroundWindow(
            rustResultHeaderWindow,
            "effects.result-header-hover-rust-win-fluent-iced");
        var resultHeaderHoverSideBySidePath = SaveSideBySideComparison(
            resultHeaderHoverDotnetPath,
            resultHeaderHoverRustPath,
            "effects.result-header-hover-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "effects.result-header-hover",
            "Result Header Hover",
            dotnetWindow,
            rustResultHeaderWindow,
            resultHeaderHoverDotnetPath,
            resultHeaderHoverRustPath,
            resultHeaderHoverSideBySidePath,
            UiParityRegion.ResultHeaderEffectRegions,
            [],
            rustSchemaPath: rustResultHeaderPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(resultHeaderHoverDotnetPath);
        AssertImageHasVisibleContent(resultHeaderHoverRustPath);
        AssertImageHasVisibleContent(resultHeaderHoverSideBySidePath);

        SetDotnetMainInputText(dotnetWindow, "Hello from the Rust main window preview");
        var rustBeforeTranslatePreview = RenderMainPreview("before_translate", ResolveRustPreviewTheme("light"), _output);
        var rustBeforeTranslateWindow = rustBeforeTranslatePreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustBeforeTranslateWindow);
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        WaitForMainWindowReady(rustBeforeTranslateWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "main.before-translate", "dotnet");
        AssertWindowFullyVisible(rustBeforeTranslateWindow, "main.before-translate", "rust");

        PrepareNeutralMainCapture(dotnetWindow);
        var beforeTranslateDotnetPath = CaptureForegroundWindow(
            dotnetWindow,
            "main.before-translate-dotnet-winui-reference");
        var beforeTranslateRustPath = CaptureForegroundWindow(
            rustBeforeTranslateWindow,
            "main.before-translate-rust-win-fluent-iced");
        var beforeTranslateSideBySidePath = SaveSideBySideComparison(
            beforeTranslateDotnetPath,
            beforeTranslateRustPath,
            "main.before-translate-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "main.before-translate",
            "Before Translate",
            dotnetWindow,
            rustBeforeTranslateWindow,
            beforeTranslateDotnetPath,
            beforeTranslateRustPath,
            beforeTranslateSideBySidePath,
            UiParityRegion.DefaultMainRegions,
            ["InputTextBox", "TranslateButton", "QuickInputCard"],
            rustSchemaPath: rustBeforeTranslatePreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(beforeTranslateDotnetPath);
        AssertImageHasVisibleContent(beforeTranslateRustPath);
        AssertImageHasVisibleContent(beforeTranslateSideBySidePath);

        var rustSourceInputHoverPreview = RenderMainPreview("before_translate",
        ResolveRustPreviewTheme("light"),
        _output,
        new Dictionary<string, string>
        {
            ["EASYDICT_PREVIEW_SOURCE_TEXT_STATE"] = "hovered"
        },
        schemaSuffix: "-source-hover");
        var rustSourceInputHoverWindow = rustSourceInputHoverPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustSourceInputHoverWindow);
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        WaitForMainWindowReady(rustSourceInputHoverWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "effects.source-input-hover", "dotnet");
        AssertWindowFullyVisible(rustSourceInputHoverWindow, "effects.source-input-hover", "rust");

        MoveMouseToHoverTarget(dotnetWindow, "InputTextBox", fallbackX: 0.50, fallbackY: 0.45);
        var sourceInputHoverDotnetPath = CaptureForegroundWindow(
            dotnetWindow,
            "effects.source-input-hover-dotnet-winui-reference");
        var sourceInputHoverRustPath = CaptureForegroundWindow(
            rustSourceInputHoverWindow,
            "effects.source-input-hover-rust-win-fluent-iced");
        var sourceInputHoverSideBySidePath = SaveSideBySideComparison(
            sourceInputHoverDotnetPath,
            sourceInputHoverRustPath,
            "effects.source-input-hover-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "effects.source-input-hover",
            "Source Input Hover",
            dotnetWindow,
            rustSourceInputHoverWindow,
            sourceInputHoverDotnetPath,
            sourceInputHoverRustPath,
            sourceInputHoverSideBySidePath,
            UiParityRegion.SourceInputEffectRegions,
            ["InputTextBox", "QuickInputCard"],
            rustSchemaPath: rustSourceInputHoverPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(sourceInputHoverDotnetPath);
        AssertImageHasVisibleContent(sourceInputHoverRustPath);
        AssertImageHasVisibleContent(sourceInputHoverSideBySidePath);

        var rustSourceInputFocusPreview = RenderMainPreview("before_translate",
        ResolveRustPreviewTheme("light"),
        _output,
        new Dictionary<string, string>
        {
            ["EASYDICT_PREVIEW_SOURCE_TEXT_STATE"] = "focused"
        },
        schemaSuffix: "-source-focus");
        var rustSourceInputFocusWindow = rustSourceInputFocusPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustSourceInputFocusWindow);
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        WaitForMainWindowReady(rustSourceInputFocusWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "effects.source-input-focus", "dotnet");
        AssertWindowFullyVisible(rustSourceInputFocusWindow, "effects.source-input-focus", "rust");

        FocusElement(dotnetWindow, "InputTextBox", fallbackX: 0.50, fallbackY: 0.45);
        var sourceInputFocusDotnetPath = CaptureForegroundWindow(
            dotnetWindow,
            "effects.source-input-focus-dotnet-winui-reference");
        var sourceInputFocusRustPath = CaptureForegroundWindow(
            rustSourceInputFocusWindow,
            "effects.source-input-focus-rust-win-fluent-iced");
        var sourceInputFocusSideBySidePath = SaveSideBySideComparison(
            sourceInputFocusDotnetPath,
            sourceInputFocusRustPath,
            "effects.source-input-focus-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "effects.source-input-focus",
            "Source Input Focus",
            dotnetWindow,
            rustSourceInputFocusWindow,
            sourceInputFocusDotnetPath,
            sourceInputFocusRustPath,
            sourceInputFocusSideBySidePath,
            UiParityRegion.SourceInputEffectRegions,
            ["InputTextBox", "QuickInputCard"],
            rustSchemaPath: rustSourceInputFocusPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(sourceInputFocusDotnetPath);
        AssertImageHasVisibleContent(sourceInputFocusRustPath);
        AssertImageHasVisibleContent(sourceInputFocusSideBySidePath);

        var rustModeOverlayPreview = RenderMainPreview("mode_overlay", ResolveRustPreviewTheme("light"), _output);
        var rustModeOverlayWindow = rustModeOverlayPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustModeOverlayWindow);
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        WaitForMainWindowReady(rustModeOverlayWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "effects.overlay-fade", "dotnet");
        AssertWindowFullyVisible(rustModeOverlayWindow, "effects.overlay-fade", "rust");

        var overlayFadeDotnetPath = CaptureDotnetModeSwitchOverlayToLongDocument(
            dotnetWindow,
            "effects.overlay-fade-dotnet-winui-reference");
        var overlayFadeRustPath = CaptureForegroundWindow(
            rustModeOverlayWindow,
            "effects.overlay-fade-rust-win-fluent-iced");
        var overlayFadeSideBySidePath = SaveSideBySideComparison(
            overlayFadeDotnetPath,
            overlayFadeRustPath,
            "effects.overlay-fade-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "effects.overlay-fade",
            "Overlay Fade",
            dotnetWindow,
            rustModeOverlayWindow,
            overlayFadeDotnetPath,
            overlayFadeRustPath,
            overlayFadeSideBySidePath,
            UiParityRegion.OverlayEffectRegions,
            [],
            rustSchemaPath: rustModeOverlayPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(overlayFadeDotnetPath);
        AssertImageHasVisibleContent(overlayFadeRustPath);
        AssertImageHasVisibleContent(overlayFadeSideBySidePath);

        if (IsTruthy(Environment.GetEnvironmentVariable(MainEffectsOnlyEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust main/effects parity run stopped before long-document captures because {MainEffectsOnlyEnvironmentVariable}=1.");
            SaveManifest(manifestEntries);
            return;
        }

        WaitForLongDocumentReady(dotnetWindow, "dotnet");
        var rustLongDocumentPreview = RenderMainPreview("long_document",
        ResolveRustPreviewTheme("light"),
        _output,
        schemaSuffix: "-tab");
        var rustLongDocumentWindow = rustLongDocumentPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustLongDocumentWindow);
        WaitForLongDocumentReady(dotnetWindow, "dotnet");
        WaitForLongDocumentReady(rustLongDocumentWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "long-doc.tab", "dotnet");
        AssertWindowFullyVisible(rustLongDocumentWindow, "long-doc.tab", "rust");

        MoveMouseToNeutralPoint();
        var longDocDotnetPath = CaptureForegroundWindow(
            dotnetWindow,
            "long-doc.tab-dotnet-winui-reference");
        var longDocRustPath = CaptureForegroundWindow(
            rustLongDocumentWindow,
            "long-doc.tab-rust-win-fluent-iced");
        var longDocSideBySidePath = SaveSideBySideComparison(
            longDocDotnetPath,
            longDocRustPath,
            "long-doc.tab-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "long-doc.tab",
            "Long Document",
            dotnetWindow,
            rustLongDocumentWindow,
            longDocDotnetPath,
            longDocRustPath,
            longDocSideBySidePath,
            UiParityRegion.LongDocumentRegions,
            ["LongDocSourceLangCombo", "LongDocTargetLangCombo", "LongDocServiceCombo", "LongDocTranslateButton"],
            windowKindOverride: "long-document",
            rustSchemaPath: rustLongDocumentPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(longDocDotnetPath);
        AssertImageHasVisibleContent(longDocRustPath);
        AssertImageHasVisibleContent(longDocSideBySidePath);

        SetDotnetLongDocumentModes(dotnetWindow, inputModeIndex: 0, outputModeIndex: 1);
        var rustLongDocumentModesPreview = RenderMainPreview("long_document",
        ResolveRustPreviewTheme("light"),
        _output,
        new Dictionary<string, string>
        {
            ["EASYDICT_PREVIEW_LONG_DOC_INPUT_MODE"] = "plaintext",
            ["EASYDICT_PREVIEW_LONG_DOC_OUTPUT_MODE"] = "bilingual"
        },
        schemaSuffix: "-output-modes");
        var rustLongDocumentModesWindow =
            rustLongDocumentModesPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustLongDocumentModesWindow);
        WaitForLongDocumentReady(dotnetWindow, "dotnet");
        WaitForLongDocumentReady(rustLongDocumentModesWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "long-doc.output-modes", "dotnet");
        AssertWindowFullyVisible(rustLongDocumentModesWindow, "long-doc.output-modes", "rust");

        MoveMouseToNeutralPoint();
        var longDocModesDotnetPath = CaptureForegroundWindow(
            dotnetWindow,
            "long-doc.output-modes-dotnet-winui-reference");
        var longDocModesRustPath = CaptureForegroundWindow(
            rustLongDocumentModesWindow,
            "long-doc.output-modes-rust-win-fluent-iced");
        var longDocModesSideBySidePath = SaveSideBySideComparison(
            longDocModesDotnetPath,
            longDocModesRustPath,
            "long-doc.output-modes-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "long-doc.output-modes",
            "Long Document Output Modes",
            dotnetWindow,
            rustLongDocumentModesWindow,
            longDocModesDotnetPath,
            longDocModesRustPath,
            longDocModesSideBySidePath,
            UiParityRegion.LongDocumentRegions,
            ["LongDocInputModeCombo", "LongDocOutputModeCombo", "LongDocTranslateButton"],
            windowKindOverride: "long-document",
            rustSchemaPath: rustLongDocumentModesPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(longDocModesDotnetPath);
        AssertImageHasVisibleContent(longDocModesRustPath);
        AssertImageHasVisibleContent(longDocModesSideBySidePath);


        ExpandDotnetComboBox(dotnetWindow, "LongDocServiceCombo");
        var longDocServiceDropdownDotnetPath = ScreenshotHelper.CaptureScreen(
            "long-doc.service-dropdown-dotnet-winui-reference");
        Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);

        var rustLongDocumentServiceDropdownPreview = RenderMainPreview("long_document",
        ResolveRustPreviewTheme("light"),
        _output,
        new Dictionary<string, string>
        {
            ["EASYDICT_PREVIEW_LONG_DOC_INPUT_MODE"] = "plaintext",
            ["EASYDICT_PREVIEW_LONG_DOC_OUTPUT_MODE"] = "bilingual",
            ["EASYDICT_PREVIEW_LONG_DOC_SERVICE_STATE"] = "hovered",
            ["EASYDICT_PREVIEW_LONG_DOC_SERVICE_DROPDOWN"] = "open"
        },
        schemaSuffix: "-service-dropdown");
        var rustLongDocumentServiceDropdownWindow =
            rustLongDocumentServiceDropdownPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustLongDocumentServiceDropdownWindow);
        WaitForLongDocumentReady(rustLongDocumentServiceDropdownWindow, "rust");
        AssertWindowFullyVisible(
            rustLongDocumentServiceDropdownWindow,
            "long-doc.service-dropdown",
            "rust");
        var longDocServiceDropdownRustPath = ScreenshotHelper.CaptureScreen(
            "long-doc.service-dropdown-rust-win-fluent-iced");
        var longDocServiceDropdownSideBySidePath = SaveSideBySideComparison(
            longDocServiceDropdownDotnetPath,
            longDocServiceDropdownRustPath,
            "long-doc.service-dropdown-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            "long-doc.service-dropdown",
            "Long Document Service Dropdown",
            dotnetWindow,
            rustLongDocumentServiceDropdownWindow,
            longDocServiceDropdownDotnetPath,
            longDocServiceDropdownRustPath,
            longDocServiceDropdownSideBySidePath,
            UiParityRegion.LongDocumentServiceDropdownRegions,
            ["LongDocServiceCombo"],
            windowKindOverride: "long-document",
            rustSchemaPath: rustLongDocumentServiceDropdownPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(longDocServiceDropdownDotnetPath);
        AssertImageHasVisibleContent(longDocServiceDropdownRustPath);
        AssertImageHasVisibleContent(longDocServiceDropdownSideBySidePath);

        _output.WriteLine($"[main.initial] Dotnet screenshot: {initialDotnetPath}");
        _output.WriteLine($"[main.initial] Rust screenshot: {initialRustPath}");
        _output.WriteLine($"[main.before-translate] Dotnet screenshot: {beforeTranslateDotnetPath}");
        _output.WriteLine($"[main.before-translate] Rust screenshot: {beforeTranslateRustPath}");
        _output.WriteLine($"[long-doc.tab] Dotnet screenshot: {longDocDotnetPath}");
        _output.WriteLine($"[long-doc.tab] Rust screenshot: {longDocRustPath}");
        _output.WriteLine($"[long-doc.output-modes] Dotnet screenshot: {longDocModesDotnetPath}");
        _output.WriteLine($"[long-doc.output-modes] Rust screenshot: {longDocModesRustPath}");
        _output.WriteLine($"[long-doc.service-dropdown] Dotnet screenshot: {longDocServiceDropdownDotnetPath}");
        _output.WriteLine($"[long-doc.service-dropdown] Rust screenshot: {longDocServiceDropdownRustPath}");
        _output.WriteLine($"[effects.primary-hover] Dotnet screenshot: {hoverDotnetPath}");
        _output.WriteLine($"[effects.primary-hover] Rust screenshot: {hoverRustPath}");
        _output.WriteLine($"[effects.primary-pressed] Dotnet screenshot: {pressedDotnetPath}");
        _output.WriteLine($"[effects.primary-pressed] Rust screenshot: {pressedRustPath}");
        _output.WriteLine($"[effects.result-header-hover] Dotnet screenshot: {resultHeaderHoverDotnetPath}");
        _output.WriteLine($"[effects.result-header-hover] Rust screenshot: {resultHeaderHoverRustPath}");
        _output.WriteLine($"[effects.source-input-hover] Dotnet screenshot: {sourceInputHoverDotnetPath}");
        _output.WriteLine($"[effects.source-input-hover] Rust screenshot: {sourceInputHoverRustPath}");
        _output.WriteLine($"[effects.source-input-focus] Dotnet screenshot: {sourceInputFocusDotnetPath}");
        _output.WriteLine($"[effects.source-input-focus] Rust screenshot: {sourceInputFocusRustPath}");
        _output.WriteLine($"[effects.overlay-fade] Dotnet screenshot: {overlayFadeDotnetPath}");
        _output.WriteLine($"[effects.overlay-fade] Rust screenshot: {overlayFadeRustPath}");
    }

    [Fact]
    public void FloatingWindows_ShouldRenderDotnetAndRustPreviewSideBySide()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust parity run is opt-in. Set {EnableEnvironmentVariable}=1 to launch both UI processes.");
            return;
        }

        var manifestEntries = new List<UiParityManifestEntry>();

        EnsureParityDpiAwareness();
        SeedDotnetParitySettings();
        _dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
        var dotnetMainWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
        WaitForMainWindowReady(dotnetMainWindow, "dotnet");

        if (MatchesOptionalEnvironmentFilter(FloatingWindowEnvironmentVariable, "mini", "Mini"))
        {
            var miniWindow = OpenDotnetFloatingWindow("Mini", VirtualKeyShort.KEY_M);
            CaptureFloatingWindowScenarios(
                manifestEntries,
                miniWindow,
                "mini",
                "Mini",
                "EASYDICT_PREVIEW_MINI_TRANSLATE_STATE",
                targetWidth: 640,
                targetHeight: 400);
            CloseDotnetFloatingWindow(miniWindow, "MiniWindowCloseButton");
        }

        if (MatchesOptionalEnvironmentFilter(FloatingWindowEnvironmentVariable, "fixed", "Fixed"))
        {
            var fixedWindow = OpenDotnetFloatingWindow("Fixed", VirtualKeyShort.KEY_F);
            CaptureFloatingWindowScenarios(
                manifestEntries,
                fixedWindow,
                "fixed",
                "Fixed",
                "EASYDICT_PREVIEW_FIXED_TRANSLATE_STATE",
                targetWidth: 640,
                targetHeight: 560);
            CloseDotnetFloatingWindow(fixedWindow, "FixedWindowCloseButton");
        }

        SaveManifest(manifestEntries);
    }

    [Fact]
    public void PopButton_ShouldRenderDotnetAndRustPreviewSideBySide()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust parity run is opt-in. Set {EnableEnvironmentVariable}=1 to launch both UI processes.");
            return;
        }

        var manifestEntries = new List<UiParityManifestEntry>();

        EnsureParityDpiAwareness();
        SeedDotnetParitySettings();
        using var fixture = new PopButtonSelectionFixture();
        foreach (var msg in fixture.SetupLog)
        {
            _output.WriteLine($"[PopButtonFixture] {msg}");
        }

        if (!fixture.SettingEnabled || fixture.Notepad == null)
        {
            _output.WriteLine("PopButton parity capture skipped because mouse selection translate or Notepad setup is unavailable.");
            return;
        }

        var dotnetPopHwnd = TriggerDotnetPopButton(fixture);
        if (dotnetPopHwnd == IntPtr.Zero)
        {
            _output.WriteLine("PopButton parity capture skipped because the .NET PopButton did not appear.");
            return;
        }

        var dotnetRect = PopButtonFinder.GetRect(dotnetPopHwnd);
        var center = new Point(dotnetRect.CenterX, dotnetRect.CenterY);
        Mouse.MoveTo(center);
        Thread.Sleep(300);

        var dotnetHoverPath = ScreenshotHelper.CaptureWindowHandlePhysical(
            dotnetPopHwnd,
            "popbutton.hover-dotnet-winui-reference");
        var dotnetHoverManifest = CaptureWindowManifest(dotnetPopHwnd);
        using var popButtonDpiScope = new EnvironmentVariableScope(
            "EASYDICT_PREVIEW_DPI",
            (dotnetHoverManifest.Dpi ?? 96).ToString(CultureInfo.InvariantCulture));

        var rustHoverPreview = RenderWindowPreview("pop-button",
        ResolveRustPreviewTheme("light"),
        _output,
        new Dictionary<string, string>
        {
            ["EASYDICT_PREVIEW_POPBUTTON_STATE"] = "hovered"
        });
        var rustHoverWindow = rustHoverPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        var rustHoverHwnd = SafeNativeWindowHandle(rustHoverWindow);
        var rustHoverPath = ScreenshotHelper.CaptureWindowHandlePhysical(
            rustHoverHwnd,
            "popbutton.hover-rust-win-fluent-iced");
        var rustHoverManifest = CaptureWindowManifest(rustHoverHwnd);
        var hoverSideBySidePath = SaveSideBySideComparison(
            dotnetHoverPath,
            rustHoverPath,
            "popbutton.hover-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreatePopButtonManifestEntry(
            "popbutton.hover",
            "PopButton Hover",
            dotnetHoverManifest,
            rustHoverManifest,
            dotnetHoverPath,
            rustHoverPath,
            hoverSideBySidePath,
            rustHoverPreview.DiagnosticsPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(dotnetHoverPath);
        AssertImageHasVisibleContent(rustHoverPath);
        AssertImageHasVisibleContent(hoverSideBySidePath);

        var rustPressedPreview = RenderWindowPreview("pop-button",
        ResolveRustPreviewTheme("light"),
        _output,
        new Dictionary<string, string>
        {
            ["EASYDICT_PREVIEW_POPBUTTON_STATE"] = "pressed"
        });
        var rustPressedWindow = rustPressedPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        var rustPressedHwnd = SafeNativeWindowHandle(rustPressedWindow);

        Mouse.MoveTo(center);
        Mouse.Down(MouseButton.Left);
        string dotnetPressedPath;
        UiParityWindowManifest dotnetPressedManifest;
        try
        {
            Thread.Sleep(180);
            dotnetPressedPath = ScreenshotHelper.CaptureWindowHandlePhysical(
                dotnetPopHwnd,
                "popbutton.pressed-dotnet-winui-reference");
            dotnetPressedManifest = CaptureWindowManifest(dotnetPopHwnd);
        }
        finally
        {
            Mouse.Up(MouseButton.Left);
            Thread.Sleep(200);
        }

        var rustPressedPath = ScreenshotHelper.CaptureWindowHandlePhysical(
            rustPressedHwnd,
            "popbutton.pressed-rust-win-fluent-iced");
        var rustPressedManifest = CaptureWindowManifest(rustPressedHwnd);
        var pressedSideBySidePath = SaveSideBySideComparison(
            dotnetPressedPath,
            rustPressedPath,
            "popbutton.pressed-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreatePopButtonManifestEntry(
            "popbutton.pressed",
            "PopButton Pressed",
            dotnetPressedManifest,
            rustPressedManifest,
            dotnetPressedPath,
            rustPressedPath,
            pressedSideBySidePath,
            rustPressedPreview.DiagnosticsPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(dotnetPressedPath);
        AssertImageHasVisibleContent(rustPressedPath);
        AssertImageHasVisibleContent(pressedSideBySidePath);

        _output.WriteLine($"[popbutton.hover] Dotnet screenshot: {dotnetHoverPath}");
        _output.WriteLine($"[popbutton.hover] Rust screenshot: {rustHoverPath}");
        _output.WriteLine($"[popbutton.pressed] Dotnet screenshot: {dotnetPressedPath}");
        _output.WriteLine($"[popbutton.pressed] Rust screenshot: {rustPressedPath}");
    }

    [Fact]
    public void OcrOverlay_ShouldRenderDotnetAndRustPreviewSideBySide()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust parity run is opt-in. Set {EnableEnvironmentVariable}=1 to launch both UI processes.");
            return;
        }

        var manifestEntries = new List<UiParityManifestEntry>();

        EnsureParityDpiAwareness();
        SeedDotnetParitySettings();
        _dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
        var dotnetWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        var processId = (uint)_dotnetLauncher.Application.ProcessId;

        var dotnetWindowDetectHwnd = TriggerDotnetOcrOverlay(dotnetWindow, processId, "ocr.window-detect");
        if (dotnetWindowDetectHwnd == IntPtr.Zero)
        {
            _output.WriteLine("OCR overlay parity capture skipped because the .NET capture overlay did not appear.");
            return;
        }

        string dotnetWindowDetectPath;
        UiParityWindowManifest dotnetWindowDetectManifest;
        try
        {
            MoveMouseToDotnetOcrDetectionTarget(dotnetWindow);
            dotnetWindowDetectPath = ScreenshotHelper.CaptureWindowHandlePhysical(
                dotnetWindowDetectHwnd,
                "ocr.window-detect-dotnet-winui-reference");
            dotnetWindowDetectManifest = CaptureWindowManifest(dotnetWindowDetectHwnd);
        }
        finally
        {
            DismissDotnetOcrOverlay(processId);
        }
        using var ocrDpiScope = new EnvironmentVariableScope(
            "EASYDICT_PREVIEW_DPI",
            (dotnetWindowDetectManifest.Dpi ?? 96).ToString(CultureInfo.InvariantCulture));

        var rustWindowDetectPreview = RenderWindowPreview("capture-overlay",
        ResolveRustPreviewTheme("light"),
        _output,
        new Dictionary<string, string>
        {
            ["EASYDICT_PREVIEW_CAPTURE_OVERLAY_STATE"] = "window-detect"
        });
        var rustWindowDetectWindow = rustWindowDetectPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        var rustWindowDetectPath = CaptureWindowPreferHwnd(
            rustWindowDetectWindow,
            "ocr.window-detect-rust-win-fluent-iced");
        var rustWindowDetectManifest = CaptureWindowManifest(rustWindowDetectWindow);
        var windowDetectSideBySidePath = SaveSideBySideComparison(
            dotnetWindowDetectPath,
            rustWindowDetectPath,
            "ocr.window-detect-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateOcrOverlayManifestEntry(
            "ocr.window-detect",
            "OCR Window Detect",
            dotnetWindowDetectManifest,
            rustWindowDetectManifest,
            dotnetWindowDetectPath,
            rustWindowDetectPath,
            windowDetectSideBySidePath,
            ["capture.overlay.layers", "capture.detected_region"],
            CaptureUiSummary(rustWindowDetectWindow),
            rustWindowDetectPreview.DiagnosticsPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(dotnetWindowDetectPath);
        AssertImageHasVisibleContent(rustWindowDetectPath);
        AssertImageHasVisibleContent(windowDetectSideBySidePath);

        var dotnetDragHwnd = TriggerDotnetOcrOverlay(dotnetWindow, processId, "ocr.drag-selection");
        if (dotnetDragHwnd == IntPtr.Zero)
        {
            _output.WriteLine("OCR drag-selection parity capture skipped because the .NET capture overlay did not reappear.");
            SaveManifest(manifestEntries);
            return;
        }

        string dotnetDragPath;
        UiParityWindowManifest dotnetDragManifest;
        try
        {
            dotnetDragPath = CaptureDotnetOcrDragSelection(
                dotnetDragHwnd,
                "ocr.drag-selection-dotnet-winui-reference");
            dotnetDragManifest = CaptureWindowManifest(dotnetDragHwnd);
        }
        finally
        {
            try
            {
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                Thread.Sleep(250);
            }
            finally
            {
                Mouse.Up(MouseButton.Left);
                Thread.Sleep(250);
            }

            DismissDotnetOcrOverlay(processId);
        }

        var rustDragPreview = RenderWindowPreview("capture-overlay",
        ResolveRustPreviewTheme("light"),
        _output,
        new Dictionary<string, string>
        {
            ["EASYDICT_PREVIEW_CAPTURE_OVERLAY_STATE"] = "drag-selection"
        });
        var rustDragWindow = rustDragPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        var rustDragPath = CaptureWindowPreferHwnd(
            rustDragWindow,
            "ocr.drag-selection-rust-win-fluent-iced");
        var rustDragManifest = CaptureWindowManifest(rustDragWindow);
        var dragSideBySidePath = SaveSideBySideComparison(
            dotnetDragPath,
            rustDragPath,
            "ocr.drag-selection-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateOcrOverlayManifestEntry(
            "ocr.drag-selection",
            "OCR Drag Selection",
            dotnetDragManifest,
            rustDragManifest,
            dotnetDragPath,
            rustDragPath,
            dragSideBySidePath,
            ["capture.overlay.layers", "capture.selection_rect", "capture.magnifier"],
            CaptureUiSummary(rustDragWindow),
            rustDragPreview.DiagnosticsPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(dotnetDragPath);
        AssertImageHasVisibleContent(rustDragPath);
        AssertImageHasVisibleContent(dragSideBySidePath);

        _output.WriteLine($"[ocr.window-detect] Dotnet screenshot: {dotnetWindowDetectPath}");
        _output.WriteLine($"[ocr.window-detect] Rust screenshot: {rustWindowDetectPath}");
        _output.WriteLine($"[ocr.drag-selection] Dotnet screenshot: {dotnetDragPath}");
        _output.WriteLine($"[ocr.drag-selection] Rust screenshot: {rustDragPath}");
    }

    [Fact]
    public void SystemTrayMenu_ShouldRenderDotnetAndRustSideBySide()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust parity run is opt-in. Set {EnableEnvironmentVariable}=1 to launch both UI processes.");
            return;
        }

        EnsureParityDpiAwareness();
        SeedDotnetParitySettings();

        var manifestEntries = new List<UiParityManifestEntry>();
        var anchor = ResolveTrayMenuAnchorPoint();
        var anchorValue = $"{anchor.X.ToString(CultureInfo.InvariantCulture)},{anchor.Y.ToString(CultureInfo.InvariantCulture)}";
        _output.WriteLine($"[tray-menu] Capture anchor: {anchorValue}");

        var standard = CaptureTrayMenuPair(
            "tray-menu",
            "System Tray Menu",
            "tray-menu-dotnet-winui-reference",
            "tray-menu-rust-win-fluent-iced",
            "tray-menu-dotnet-vs-rust-side-by-side",
            anchor,
            anchorValue,
            extraItemCount: 0,
            maxHeightDips: null);
        manifestEntries.Add(standard.ManifestEntry);

        SaveManifest(manifestEntries);

        AssertTrayMenuCapture(standard, expectScrolling: false, extraItemCount: 0, maxHeightDips: null);
        var auditRounds = AnalyzeTrayMenuFluentAuditRounds(standard);
        AssertTrayMenuFluentAuditRounds(auditRounds);
        var auditPath = SaveTrayMenuFluentAudit(auditRounds);

        _output.WriteLine($"[tray-menu] Dotnet screenshot: {standard.DotnetScreenshot}");
        _output.WriteLine($"[tray-menu] Rust screenshot: {standard.RustScreenshot}");
        _output.WriteLine($"[tray-menu] Side-by-side screenshot: {standard.SideBySideScreenshot}");
        _output.WriteLine($"[tray-menu.audit] Fluent audit: {auditPath}");
    }

    private static IReadOnlyList<SettingsParityCaptureStep> ResolveCaptureSteps()
    {
        var configured = Environment.GetEnvironmentVariable(SettingsSectionEnvironmentVariable);
        var steps = SettingsParityCaptureStep.All;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return steps;
        }

        return steps
            .Where(step =>
                string.Equals(step.Section.Id, configured, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(step.Section.Label, configured, StringComparison.OrdinalIgnoreCase) ||
                step.Key.Contains(configured, StringComparison.OrdinalIgnoreCase))
            .DefaultIfEmpty(steps[0])
            .ToArray();
    }

    private Window OpenDotnetFloatingWindow(string windowType, VirtualKeyShort key)
    {
        var mainWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(10));
        mainWindow.SetForeground();
        Thread.Sleep(600);

        Window? floatingWindow = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            mainWindow.SetForeground();
            Thread.Sleep(500);
            _output.WriteLine($"Opening {windowType} window with Ctrl+Alt+{key} (attempt {attempt})");
            UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, key);
            Thread.Sleep(3000);

            floatingWindow = UITestHelper.FindSecondaryWindow(
                _dotnetLauncher.Application,
                _dotnetLauncher.Automation,
                windowType,
                _output);
            if (floatingWindow is not null)
            {
                break;
            }
        }

        floatingWindow.Should().NotBeNull($"{windowType} window must open before parity capture");
        floatingWindow!.SetForeground();
        Thread.Sleep(500);
        return floatingWindow;
    }

    private IntPtr TriggerDotnetPopButton(PopButtonSelectionFixture fixture)
    {
        fixture.Notepad!.BringToForeground();
        var bounds = fixture.Notepad.GetTextBounds();
        var startX = bounds.Left + 15;
        var startY = bounds.Top + 15;
        var endX = startX + 180;
        var endY = startY;

        _output.WriteLine($"Triggering PopButton via drag-select from ({startX},{startY}) to ({endX},{endY})");
        SimulateDragSelect(startX, startY, endX, endY);

        return PopButtonFinder.WaitForPopButton(
            fixture.EasydictProcessId,
            TimeSpan.FromSeconds(5),
            pollIntervalMs: 80);
    }

    private IntPtr TriggerDotnetOcrOverlay(Window dotnetWindow, uint processId, string scenarioId)
    {
        dotnetWindow.SetForeground();
        Thread.Sleep(600);

        _output.WriteLine($"Triggering .NET OCR overlay for {scenarioId} with Ctrl+Alt+S");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_S);

        var hwnd = ScreenCaptureOverlayFinder.WaitForOverlay(
            processId,
            TimeSpan.FromSeconds(6),
            pollIntervalMs: 80);
        if (hwnd != IntPtr.Zero)
        {
            Thread.Sleep(450);
        }

        return hwnd;
    }

    private static void MoveMouseToDotnetOcrDetectionTarget(Window dotnetWindow)
    {
        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(dotnetWindow);
        var target = new Point(
            bounds.Left + Math.Max(40, bounds.Width / 2),
            bounds.Top + Math.Max(40, Math.Min(bounds.Height / 2, bounds.Height - 40)));
        Mouse.MoveTo(target);
        Thread.Sleep(550);
    }

    private static string CaptureDotnetOcrDragSelection(IntPtr overlayHwnd, string screenshotName)
    {
        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(overlayHwnd);
        var start = new Point(
            bounds.Left + Math.Max(80, bounds.Width / 4),
            bounds.Top + Math.Max(80, bounds.Height / 4));
        var end = new Point(
            bounds.Left + Math.Min(bounds.Width - 80, (bounds.Width * 2) / 3),
            bounds.Top + Math.Min(bounds.Height - 80, (bounds.Height * 2) / 3));

        Mouse.MoveTo(start);
        Thread.Sleep(80);
        Mouse.Down(MouseButton.Left);
        Thread.Sleep(120);

        const int steps = 18;
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            Mouse.MoveTo(new Point(
                (int)Math.Round(start.X + ((end.X - start.X) * t)),
                (int)Math.Round(start.Y + ((end.Y - start.Y) * t))));
            Thread.Sleep(14);
        }

        Thread.Sleep(400);
        return ScreenshotHelper.CaptureWindowHandlePhysical(overlayHwnd, screenshotName);
    }

    private bool DismissDotnetOcrOverlay(uint processId)
    {
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(450);

        if (ScreenCaptureOverlayFinder.Find(processId) != IntPtr.Zero)
        {
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Thread.Sleep(450);
        }

        if (ScreenCaptureOverlayFinder.Find(processId) != IntPtr.Zero)
        {
            Keyboard.Type(VirtualKeyShort.ENTER);
            Thread.Sleep(450);
        }

        var dismissed = ScreenCaptureOverlayFinder.WaitForDismiss(
            processId,
            TimeSpan.FromSeconds(5),
            pollIntervalMs: 100);
        if (!dismissed)
        {
            _output.WriteLine("WARNING: .NET OCR overlay did not dismiss within timeout.");
        }

        return dismissed;
    }

    private static void SimulateDragSelect(int startX, int startY, int endX, int endY)
    {
        Mouse.MoveTo(new Point(startX, startY));
        Thread.Sleep(100);

        Mouse.Down(MouseButton.Left);
        Thread.Sleep(50);

        var totalDistance = Math.Abs(endX - startX) + Math.Abs(endY - startY);
        var steps = Math.Max(totalDistance / 10, 2);
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var x = (int)(startX + ((endX - startX) * t));
            var y = (int)(startY + ((endY - startY) * t));
            Mouse.MoveTo(new Point(x, y));
            Thread.Sleep(10);
        }

        Thread.Sleep(50);
        Mouse.Up(MouseButton.Left);
    }

    private void CaptureFloatingWindowScenarios(
        List<UiParityManifestEntry> manifestEntries,
        Window dotnetWindow,
        string windowKind,
        string sectionLabel,
        string rustTranslateStateEnvironmentVariable,
        int targetWidth,
        int targetHeight)
    {
        using var floatingDpiScope = new EnvironmentVariableScope(
            "EASYDICT_PREVIEW_DPI",
            (ScreenshotHelper.GetWindowDpiScale(dotnetWindow) * 96.0)
            .ToString("0.###", CultureInfo.InvariantCulture));
        var rustInitialPreview = RenderWindowPreview(windowKind, ResolveRustPreviewTheme("light"), _output);
        var rustInitialWindow = rustInitialPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeFloatingSideBySide(dotnetWindow, rustInitialWindow, targetWidth, targetHeight);
        AssertWindowFullyVisible(dotnetWindow, $"{windowKind}.initial", "dotnet");
        AssertWindowFullyVisible(rustInitialWindow, $"{windowKind}.initial", "rust");

        MoveMouseToNeutralPoint();
        dotnetWindow.SetForeground();
        Thread.Sleep(180);
        var initialDotnetPath = CaptureWindowPreferHwnd(
            dotnetWindow,
            $"{windowKind}.initial-dotnet-winui-reference",
            requireForeground: false);
        rustInitialWindow.SetForeground();
        Thread.Sleep(180);
        var initialRustPath = CaptureWindowPreferHwnd(
            rustInitialWindow,
            $"{windowKind}.initial-rust-win-fluent-iced",
            requireForeground: false);
        var initialSideBySidePath = SaveSideBySideComparison(
            initialDotnetPath,
            initialRustPath,
            $"{windowKind}.initial-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            $"{windowKind}.initial",
            $"{sectionLabel} Initial",
            dotnetWindow,
            rustInitialWindow,
            initialDotnetPath,
            initialRustPath,
            initialSideBySidePath,
            UiParityRegion.FloatingWindowRegions,
            [$"{windowKind}.translate", $"{windowKind}.results"],
            windowKindOverride: windowKind,
            rustSchemaPath: rustInitialPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(initialDotnetPath);
        AssertImageHasVisibleContent(initialRustPath);
        AssertImageHasVisibleContent(initialSideBySidePath);

        var floatingScope = ResolveFloatingCaptureScope();

        if (floatingScope.CaptureTranslateButton)
        {
            var rustHoverPreview = RenderWindowPreview(windowKind,
            ResolveRustPreviewTheme("light"),
            _output,
            new Dictionary<string, string>
            {
                [rustTranslateStateEnvironmentVariable] = "hovered"
            });
            var rustHoverWindow = rustHoverPreview.GetMainWindow(TimeSpan.FromSeconds(30));
            ArrangeFloatingSideBySide(dotnetWindow, rustHoverWindow, targetWidth, targetHeight);
            AssertWindowFullyVisible(dotnetWindow, $"{windowKind}.translate-hover", "dotnet");
            AssertWindowFullyVisible(rustHoverWindow, $"{windowKind}.translate-hover", "rust");

            dotnetWindow.SetForeground();
            Thread.Sleep(180);
            MoveMouseToHoverTarget(dotnetWindow, "TranslateButton", fallbackX: 0.86, fallbackY: 0.66);
            var hoverDotnetPath = CaptureWindowPreferHwnd(
                dotnetWindow,
                $"{windowKind}.translate-hover-dotnet-winui-reference",
                requireForeground: false);
            rustHoverWindow.SetForeground();
            Thread.Sleep(180);
            var hoverRustPath = CaptureWindowPreferHwnd(
                rustHoverWindow,
                $"{windowKind}.translate-hover-rust-win-fluent-iced",
                requireForeground: false);
            var hoverSideBySidePath = SaveSideBySideComparison(
                hoverDotnetPath,
                hoverRustPath,
                $"{windowKind}.translate-hover-dotnet-vs-rust-side-by-side");
            manifestEntries.Add(CreateMainManifestEntry(
                $"{windowKind}.translate-hover",
                $"{sectionLabel} Translate Hover",
                dotnetWindow,
                rustHoverWindow,
                hoverDotnetPath,
                hoverRustPath,
                hoverSideBySidePath,
                UiParityRegion.FloatingActionEffectRegions,
                [$"{windowKind}.translate"],
                windowKindOverride: windowKind,
                rustSchemaPath: rustHoverPreview.SchemaPath));
            SaveManifest(manifestEntries);

            AssertImageHasVisibleContent(hoverDotnetPath);
            AssertImageHasVisibleContent(hoverRustPath);
            AssertImageHasVisibleContent(hoverSideBySidePath);

            var rustPressedPreview = RenderWindowPreview(windowKind,
            ResolveRustPreviewTheme("light"),
            _output,
            new Dictionary<string, string>
            {
                [rustTranslateStateEnvironmentVariable] = "pressed"
            });
            var rustPressedWindow = rustPressedPreview.GetMainWindow(TimeSpan.FromSeconds(30));
            ArrangeFloatingSideBySide(dotnetWindow, rustPressedWindow, targetWidth, targetHeight);
            AssertWindowFullyVisible(dotnetWindow, $"{windowKind}.translate-pressed", "dotnet");
            AssertWindowFullyVisible(rustPressedWindow, $"{windowKind}.translate-pressed", "rust");

            var pressedDotnetSummary = CreateFloatingReferenceSummaryFallback(windowKind);
            var pressedDotnetPath = CapturePressedWindow(
                dotnetWindow,
                "TranslateButton",
                fallbackX: 0.86,
                fallbackY: 0.66,
                $"{windowKind}.translate-pressed-dotnet-winui-reference",
                requireForeground: false);
            var pressedRustPath = CaptureWindowPreferHwnd(
                rustPressedWindow,
                $"{windowKind}.translate-pressed-rust-win-fluent-iced",
                requireForeground: false);
            var pressedSideBySidePath = SaveSideBySideComparison(
                pressedDotnetPath,
                pressedRustPath,
                $"{windowKind}.translate-pressed-dotnet-vs-rust-side-by-side");
            manifestEntries.Add(CreateMainManifestEntry(
                $"{windowKind}.translate-pressed",
                $"{sectionLabel} Translate Pressed",
                dotnetWindow,
                rustPressedWindow,
                pressedDotnetPath,
                pressedRustPath,
                pressedSideBySidePath,
                UiParityRegion.FloatingActionEffectRegions,
                [$"{windowKind}.translate"],
                windowKindOverride: windowKind,
                rustSchemaPath: rustPressedPreview.SchemaPath,
                referenceUiSummaryOverride: pressedDotnetSummary));
            SaveManifest(manifestEntries);

            AssertImageHasVisibleContent(pressedDotnetPath);
            AssertImageHasVisibleContent(pressedRustPath);
            AssertImageHasVisibleContent(pressedSideBySidePath);

            _output.WriteLine($"[{windowKind}.translate-hover] Dotnet screenshot: {hoverDotnetPath}");
            _output.WriteLine($"[{windowKind}.translate-hover] Rust screenshot: {hoverRustPath}");
            _output.WriteLine($"[{windowKind}.translate-pressed] Dotnet screenshot: {pressedDotnetPath}");
            _output.WriteLine($"[{windowKind}.translate-pressed] Rust screenshot: {pressedRustPath}");
        }

        if (floatingScope.CaptureDropdowns)
        {
            foreach (var dropdown in FloatingDropdownCaptures(windowKind))
            {
                var dropdownAlias = dropdown.Key.StartsWith("source", StringComparison.OrdinalIgnoreCase)
                    ? "source"
                    : "target";
                if (!MatchesOptionalEnvironmentFilter(
                    FloatingDropdownEnvironmentVariable,
                    dropdown.Key,
                    dropdown.Label,
                    dropdownAlias))
                {
                    continue;
                }

                CaptureFloatingDropdownInteraction(
                    manifestEntries,
                    dotnetWindow,
                    windowKind,
                    sectionLabel,
                    dropdown,
                    floatingScope.CaptureDropdownOptions,
                    targetWidth,
                    targetHeight);
            }
        }

        if (floatingScope.CaptureControls)
        {
            foreach (var control in FloatingInteractionCaptures(windowKind))
            {
                CaptureFloatingControlInteraction(
                    manifestEntries,
                    dotnetWindow,
                    windowKind,
                    sectionLabel,
                    control,
                    targetWidth,
                    targetHeight);
            }
        }

        _output.WriteLine($"[{windowKind}.initial] Dotnet screenshot: {initialDotnetPath}");
        _output.WriteLine($"[{windowKind}.initial] Rust screenshot: {initialRustPath}");
    }

    private void CaptureFloatingControlInteraction(
        List<UiParityManifestEntry> manifestEntries,
        Window dotnetWindow,
        string windowKind,
        string sectionLabel,
        FloatingInteractionCapture control,
        int targetWidth,
        int targetHeight)
    {
        var rustHoverPreview = RenderWindowPreview(windowKind,
        ResolveRustPreviewTheme("light"),
        _output,
        new Dictionary<string, string>
        {
            [control.RustStateEnvironmentVariable] = "hovered"
        });
        var rustHoverWindow = rustHoverPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeFloatingSideBySide(dotnetWindow, rustHoverWindow, targetWidth, targetHeight);
        AssertWindowFullyVisible(dotnetWindow, $"{windowKind}.{control.Key}-hover", "dotnet");
        AssertWindowFullyVisible(rustHoverWindow, $"{windowKind}.{control.Key}-hover", "rust");

        dotnetWindow.SetForeground();
        Thread.Sleep(180);
        MoveMouseToHoverTarget(
            dotnetWindow,
            control.DotnetElement,
            control.FallbackX,
            control.FallbackY);
        var hoverDotnetPath = CaptureWindowPreferHwnd(
            dotnetWindow,
            $"{windowKind}.{control.Key}-hover-dotnet-winui-reference",
            requireForeground: false);
        rustHoverWindow.SetForeground();
        Thread.Sleep(180);
        var hoverRustPath = CaptureWindowPreferHwnd(
            rustHoverWindow,
            $"{windowKind}.{control.Key}-hover-rust-win-fluent-iced",
            requireForeground: false);
        var hoverSideBySidePath = SaveSideBySideComparison(
            hoverDotnetPath,
            hoverRustPath,
            $"{windowKind}.{control.Key}-hover-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            $"{windowKind}.{control.Key}-hover",
            $"{sectionLabel} {control.Label} Hover",
            dotnetWindow,
            rustHoverWindow,
            hoverDotnetPath,
            hoverRustPath,
            hoverSideBySidePath,
            control.Regions,
            [control.RustControlId],
            windowKindOverride: windowKind,
            rustSchemaPath: rustHoverPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(hoverDotnetPath);
        AssertImageHasVisibleContent(hoverRustPath);
        AssertImageHasVisibleContent(hoverSideBySidePath);

        var rustPressedPreview = RenderWindowPreview(windowKind,
        ResolveRustPreviewTheme("light"),
        _output,
        new Dictionary<string, string>
        {
            [control.RustStateEnvironmentVariable] = "pressed"
        });
        var rustPressedWindow = rustPressedPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeFloatingSideBySide(dotnetWindow, rustPressedWindow, targetWidth, targetHeight);
        AssertWindowFullyVisible(dotnetWindow, $"{windowKind}.{control.Key}-pressed", "dotnet");
        AssertWindowFullyVisible(rustPressedWindow, $"{windowKind}.{control.Key}-pressed", "rust");

        var pressedDotnetSummary = CreateFloatingReferenceSummaryFallback(windowKind);
        var pressedDotnetPath = CapturePressedWindow(
            dotnetWindow,
            control.DotnetElement,
            control.FallbackX,
            control.FallbackY,
            $"{windowKind}.{control.Key}-pressed-dotnet-winui-reference",
            requireForeground: false);
        if (control.Key.Equals("ocr", StringComparison.OrdinalIgnoreCase))
        {
            DismissDotnetOcrOverlay((uint)_dotnetLauncher.Application.ProcessId);
            ArrangeFloatingSideBySide(dotnetWindow, rustPressedWindow, targetWidth, targetHeight);
            AssertWindowFullyVisible(dotnetWindow, $"{windowKind}.{control.Key}-pressed", "dotnet");
            AssertWindowFullyVisible(rustPressedWindow, $"{windowKind}.{control.Key}-pressed", "rust");
        }
        else if (control.Key.Equals("pin", StringComparison.OrdinalIgnoreCase))
        {
            ResetDotnetMiniPinIfChecked(dotnetWindow);
        }

        rustPressedWindow.SetForeground();
        Thread.Sleep(180);
        var pressedRustPath = CaptureWindowPreferHwnd(
            rustPressedWindow,
            $"{windowKind}.{control.Key}-pressed-rust-win-fluent-iced",
            requireForeground: false);
        var pressedSideBySidePath = SaveSideBySideComparison(
            pressedDotnetPath,
            pressedRustPath,
            $"{windowKind}.{control.Key}-pressed-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            $"{windowKind}.{control.Key}-pressed",
            $"{sectionLabel} {control.Label} Pressed",
            dotnetWindow,
            rustPressedWindow,
            pressedDotnetPath,
            pressedRustPath,
            pressedSideBySidePath,
            control.Regions,
            [control.RustControlId],
            windowKindOverride: windowKind,
            rustSchemaPath: rustPressedPreview.SchemaPath,
            referenceUiSummaryOverride: pressedDotnetSummary));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(pressedDotnetPath);
        AssertImageHasVisibleContent(pressedRustPath);
        AssertImageHasVisibleContent(pressedSideBySidePath);

        _output.WriteLine($"[{windowKind}.{control.Key}-hover] Dotnet screenshot: {hoverDotnetPath}");
        _output.WriteLine($"[{windowKind}.{control.Key}-hover] Rust screenshot: {hoverRustPath}");
        _output.WriteLine($"[{windowKind}.{control.Key}-pressed] Dotnet screenshot: {pressedDotnetPath}");
        _output.WriteLine($"[{windowKind}.{control.Key}-pressed] Rust screenshot: {pressedRustPath}");
    }

    private void CaptureFloatingDropdownInteraction(
        List<UiParityManifestEntry> manifestEntries,
        Window dotnetWindow,
        string windowKind,
        string sectionLabel,
        FloatingDropdownCapture dropdown,
        bool captureOptions,
        int targetWidth,
        int targetHeight)
    {
        var rustOpenPreview = RenderWindowPreview(windowKind, ResolveRustPreviewTheme("light"), _output);
        var rustOpenWindow = rustOpenPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeFloatingSideBySide(dotnetWindow, rustOpenWindow, targetWidth, targetHeight);
        AssertWindowFullyVisible(dotnetWindow, $"{windowKind}.{dropdown.Key}-open", "dotnet");
        AssertWindowFullyVisible(rustOpenWindow, $"{windowKind}.{dropdown.Key}-open", "rust");

        var dotnetOpenStep = FloatingDropdownProbeStep(windowKind, dropdown, dropdown.DotnetElement);
        var rustOpenStep = FloatingDropdownProbeStep(windowKind, dropdown, dropdown.RustElement);

        var dotnetOpenPath = CaptureExpandedSettingsDropdownStep(
            dotnetWindow,
            dotnetOpenStep,
            $"{windowKind}.{dropdown.Key}-open-dotnet-winui-reference");
        DismissExpandedDropdownIfNeeded(dotnetOpenStep);
        MoveMouseToNeutralPoint();

        var dotnetOptionPaths = captureOptions
            ? CaptureSettingsDropdownOptionSelections(
                dotnetWindow,
                dotnetOpenStep,
                "dotnet-winui-reference",
                rustSchemaPath: null)
            : Array.Empty<SettingsDropdownOptionCaptureResult>();

        var rustOpenPath = CaptureExpandedSettingsDropdownStep(
            rustOpenWindow,
            rustOpenStep,
            $"{windowKind}.{dropdown.Key}-open-rust-win-fluent-iced",
            rustOpenPreview.SchemaPath);
        DismissExpandedDropdownIfNeeded(rustOpenStep);
        MoveMouseToNeutralPoint();

        var openSideBySidePath = SaveSideBySideComparison(
            dotnetOpenPath,
            rustOpenPath,
            $"{windowKind}.{dropdown.Key}-open-dotnet-vs-rust-side-by-side");
        var dotnetOpenScreenshotPixelSize = ReadScreenshotPixelSize(dotnetOpenPath);
        var rustOpenScreenshotPixelSize = ReadScreenshotPixelSize(rustOpenPath);
        manifestEntries.Add(CreateMainManifestEntry(
            $"{windowKind}.{dropdown.Key}-open",
            $"{sectionLabel} {dropdown.Label} Open",
            dotnetWindow,
            rustOpenWindow,
            dotnetOpenPath,
            rustOpenPath,
            openSideBySidePath,
            UiParityRegion.FloatingLanguageBarEffectRegions,
            [dropdown.RustControlId],
            windowKindOverride: windowKind,
            rustSchemaPath: rustOpenPreview.SchemaPath,
            referenceScreenshotPixelSize: dotnetOpenScreenshotPixelSize,
            candidateScreenshotPixelSize: rustOpenScreenshotPixelSize,
            operatedDropdownElement: dropdown.DotnetElement));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(dotnetOpenPath);
        AssertImageHasVisibleContent(rustOpenPath);
        AssertImageHasVisibleContent(openSideBySidePath);

        foreach (var dotnetOption in dotnetOptionPaths)
        {
            var rustOptionEnvironment = new Dictionary<string, string>
            {
                [dropdown.RustSelectedLanguageEnvironmentVariable] =
                    FloatingLanguageOptionId(dotnetOption.Option)
            };
            AddRustPreviewSizeEnvironment(
                rustOptionEnvironment,
                dotnetOption.ScreenshotPixelSize,
                dotnetOption.WindowDpiScale);
            var rustOptionPreview = RenderWindowPreview(windowKind,
            ResolveRustPreviewTheme("light"),
            _output,
            rustOptionEnvironment);
            var rustOptionWindow = rustOptionPreview.GetMainWindow(TimeSpan.FromSeconds(30));
            ArrangeFloatingSideBySide(
                dotnetWindow,
                rustOptionWindow,
                targetWidth,
                targetHeight,
                dotnetOption.ScreenshotPixelSize);
            AssertWindowFullyVisible(
                rustOptionWindow,
                dotnetOption.ScenarioId,
                "rust");
            var rustOptionPath = CaptureWindowPreferHwnd(
                rustOptionWindow,
                $"{dotnetOption.ScenarioId}-rust-win-fluent-iced",
                requireForeground: false);
            var rustOptionScreenshotPixelSize = ReadScreenshotPixelSize(rustOptionPath);
            var optionSideBySidePath = SaveSideBySideComparison(
                dotnetOption.ScreenshotPath,
                rustOptionPath,
                $"{dotnetOption.ScenarioId}-dotnet-vs-rust-side-by-side");
            manifestEntries.Add(CreateMainManifestEntry(
                dotnetOption.ScenarioId,
                $"{sectionLabel} {dropdown.Label} Select {dotnetOption.Option.Label}",
                dotnetWindow,
                rustOptionWindow,
                dotnetOption.ScreenshotPath,
                rustOptionPath,
                optionSideBySidePath,
                UiParityRegion.FloatingLanguageBarEffectRegions,
                [dropdown.RustControlId],
                windowKindOverride: windowKind,
                rustSchemaPath: rustOptionPreview.SchemaPath,
                referenceScreenshotPixelSize: dotnetOption.ScreenshotPixelSize,
                candidateScreenshotPixelSize: rustOptionScreenshotPixelSize,
                referenceUiSummaryOverride: CreateFloatingReferenceSummaryFallback(
                    windowKind,
                    FloatingDropdownOptionReferenceExtraVisibleTexts(dropdown, dotnetOption.Option)),
                operatedDropdownElement: dropdown.DotnetElement,
                selectedDropdownOption: dotnetOption.Option));
            SaveManifest(manifestEntries);

            AssertImageHasVisibleContent(dotnetOption.ScreenshotPath);
            AssertImageHasVisibleContent(rustOptionPath);
            AssertImageHasVisibleContent(optionSideBySidePath);

            _output.WriteLine(
                $"[{dotnetOption.ScenarioId}] Dropdown option: {dotnetOption.Option.Label}");
            _output.WriteLine(
                $"[{dotnetOption.ScenarioId}] Dotnet screenshot: {dotnetOption.ScreenshotPath}");
            _output.WriteLine(
                $"[{dotnetOption.ScenarioId}] Rust screenshot: {rustOptionPath}");
        }

        _output.WriteLine($"[{windowKind}.{dropdown.Key}-open] Dotnet screenshot: {dotnetOpenPath}");
        _output.WriteLine($"[{windowKind}.{dropdown.Key}-open] Rust screenshot: {rustOpenPath}");
    }

    private void CaptureMainControlInteraction(
        List<UiParityManifestEntry> manifestEntries,
        Window dotnetWindow,
        Window rustWindow,
        string? rustSchemaPath,
        MainInteractionCapture control)
    {
        ArrangeSideBySide(dotnetWindow, rustWindow);
        AssertWindowFullyVisible(dotnetWindow, $"main.{control.Key}-hover", "dotnet");
        AssertWindowFullyVisible(rustWindow, $"main.{control.Key}-hover", "rust");

        MoveMouseToHoverTarget(
            dotnetWindow,
            control.DotnetElement,
            control.FallbackX,
            control.FallbackY);
        var hoverDotnetPath = CaptureWindowPreferHwnd(
            dotnetWindow,
            $"main.{control.Key}-hover-dotnet-winui-reference",
            requireForeground: false);
        MoveMouseToHoverTarget(
            rustWindow,
            control.RustElement,
            control.FallbackX,
            control.FallbackY);
        var hoverRustPath = CaptureWindowPreferHwnd(
            rustWindow,
            $"main.{control.Key}-hover-rust-win-fluent-iced",
            requireForeground: false);
        var hoverSideBySidePath = SaveSideBySideComparison(
            hoverDotnetPath,
            hoverRustPath,
            $"main.{control.Key}-hover-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            $"main.{control.Key}-hover",
            $"Main {control.Label} Hover",
            dotnetWindow,
            rustWindow,
            hoverDotnetPath,
            hoverRustPath,
            hoverSideBySidePath,
            control.Regions,
            [control.RustControlId],
            rustSchemaPath: rustSchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(hoverDotnetPath);
        AssertImageHasVisibleContent(hoverRustPath);
        AssertImageHasVisibleContent(hoverSideBySidePath);

        var pressedDotnetPath = CapturePressedWindow(
            dotnetWindow,
            control.DotnetElement,
            control.FallbackX,
            control.FallbackY,
            $"main.{control.Key}-pressed-dotnet-winui-reference",
            requireForeground: false);
        var pressedRustPath = CapturePressedWindow(
            rustWindow,
            control.RustElement,
            control.FallbackX,
            control.FallbackY,
            $"main.{control.Key}-pressed-rust-win-fluent-iced",
            requireForeground: false);
        var pressedSideBySidePath = SaveSideBySideComparison(
            pressedDotnetPath,
            pressedRustPath,
            $"main.{control.Key}-pressed-dotnet-vs-rust-side-by-side");
        RestoreMainWindowAfterOperation(dotnetWindow);
        RestoreMainWindowAfterOperation(rustWindow);
        manifestEntries.Add(CreateMainManifestEntry(
            $"main.{control.Key}-pressed",
            $"Main {control.Label} Pressed",
            dotnetWindow,
            rustWindow,
            pressedDotnetPath,
            pressedRustPath,
            pressedSideBySidePath,
            control.Regions,
            [control.RustControlId],
            rustSchemaPath: rustSchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(pressedDotnetPath);
        AssertImageHasVisibleContent(pressedRustPath);
        AssertImageHasVisibleContent(pressedSideBySidePath);

        _output.WriteLine($"[main.{control.Key}-hover] Dotnet screenshot: {hoverDotnetPath}");
        _output.WriteLine($"[main.{control.Key}-hover] Rust screenshot: {hoverRustPath}");
        _output.WriteLine($"[main.{control.Key}-pressed] Dotnet screenshot: {pressedDotnetPath}");
        _output.WriteLine($"[main.{control.Key}-pressed] Rust screenshot: {pressedRustPath}");
        MoveMouseToNeutralPoint();
    }



    private static (float Width, float Height) PreviewDips(Size pixels, double dpiScale)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0 || !double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            throw new RustPreviewControlException(
                "preview-invalid-dimensions",
                $"Cannot derive preview dimensions from {pixels.Width}x{pixels.Height}px at {dpiScale}x.");
        }
        return ((float)(pixels.Width / dpiScale), (float)(pixels.Height / dpiScale));
    }

    private void CaptureMainDropdownInteraction(
        List<UiParityManifestEntry> manifestEntries,
        Window dotnetWindow,
        IReadOnlyDictionary<string, string> rustBaseEnvironment,
        MainDropdownCapture dropdown,
        bool captureOptions)
    {
        AssertWindowFullyVisible(dotnetWindow, $"main.{dropdown.Key}-open", "dotnet");

        var dotnetStep = MainDropdownProbeStep(dropdown, dropdown.DotnetElement);

        var dotnetOpenPath = CaptureExpandedSettingsDropdownStep(
            dotnetWindow,
            dotnetStep,
            $"main.{dropdown.Key}-open-dotnet-winui-reference");
        DismissExpandedDropdownIfNeeded(dotnetStep);
        MoveMouseToNeutralPoint();

        var openEnvironment = MainRustDropdownEnvironment(rustBaseEnvironment, dropdown);
        openEnvironment["EASYDICT_PREVIEW_MAIN_OPEN_DROPDOWN"] = MainDropdownAlias(dropdown);
        AddRustPreviewSizeEnvironment(
            openEnvironment,
            ReadScreenshotPixelSize(dotnetOpenPath),
            ScreenshotHelper.GetWindowDpiScale(dotnetWindow));
        var rustOpenPreview = RenderMainPreview("before_translate",
        ResolveRustPreviewTheme("light"),
        _output,
        openEnvironment,
        schemaSuffix: $"-{dropdown.Key}-open");
        var rustOpenWindow = rustOpenPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustOpenWindow);
        WaitForMainWindowReady(rustOpenWindow, "rust");
        AssertWindowFullyVisible(rustOpenWindow, $"main.{dropdown.Key}-open", "rust");
        var rustOpenPath = CaptureWindowPreferHwnd(
            rustOpenWindow,
            $"main.{dropdown.Key}-open-rust-win-fluent-iced",
            requireForeground: false);

        var openSideBySidePath = SaveSideBySideComparison(
            dotnetOpenPath,
            rustOpenPath,
            $"main.{dropdown.Key}-open-dotnet-vs-rust-side-by-side");
        manifestEntries.Add(CreateMainManifestEntry(
            $"main.{dropdown.Key}-open",
            $"Main {dropdown.Label} Open",
            dotnetWindow,
            rustOpenWindow,
            dotnetOpenPath,
            rustOpenPath,
            openSideBySidePath,
            dropdown.Regions,
            [dropdown.RustControlId],
            rustSchemaPath: rustOpenPreview.SchemaPath,
            operatedDropdownElement: dropdown.DotnetElement));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(dotnetOpenPath);
        AssertImageHasVisibleContent(rustOpenPath);
        AssertImageHasVisibleContent(openSideBySidePath);

        var dotnetOptionPaths = captureOptions
            ? CaptureSettingsDropdownOptionSelections(
                dotnetWindow,
                dotnetStep,
                "dotnet-winui-reference",
                rustSchemaPath: null)
            : Array.Empty<SettingsDropdownOptionCaptureResult>();

        foreach (var dotnetOption in dotnetOptionPaths)
        {
            var rustOptionEnvironment = MainRustDropdownEnvironment(rustBaseEnvironment, dropdown);
            rustOptionEnvironment[dropdown.RustSelectedLanguageEnvironmentVariable] =
                FloatingLanguageOptionId(dotnetOption.Option);
            AddRustPreviewSizeEnvironment(
                rustOptionEnvironment,
                dotnetOption.ScreenshotPixelSize,
                dotnetOption.WindowDpiScale);
            var rustOptionPreview = RenderMainPreview("before_translate",
            ResolveRustPreviewTheme("light"),
            _output,
            rustOptionEnvironment,
            schemaSuffix: $"-{dropdown.Key}-select-{dotnetOption.Option.DotnetIndex + 1}");
            var rustOptionWindow = rustOptionPreview.GetMainWindow(TimeSpan.FromSeconds(30));
            ArrangeSideBySide(dotnetWindow, rustOptionWindow);
            WaitForMainWindowReady(rustOptionWindow, "rust");
            AssertWindowFullyVisible(
                rustOptionWindow,
                dotnetOption.ScenarioId,
                "rust");
            var rustOptionPath = CaptureWindowPreferHwnd(
                rustOptionWindow,
                $"{dotnetOption.ScenarioId}-rust-win-fluent-iced",
                requireForeground: false);
            var optionSideBySidePath = SaveSideBySideComparison(
                dotnetOption.ScreenshotPath,
                rustOptionPath,
                $"{dotnetOption.ScenarioId}-dotnet-vs-rust-side-by-side");
            manifestEntries.Add(CreateMainManifestEntry(
                dotnetOption.ScenarioId,
                $"Main {dropdown.Label} Select {dotnetOption.Option.Label}",
                dotnetWindow,
                rustOptionWindow,
                dotnetOption.ScreenshotPath,
                rustOptionPath,
                optionSideBySidePath,
                dropdown.Regions,
                [dropdown.RustControlId],
                rustSchemaPath: rustOptionPreview.SchemaPath,
                operatedDropdownElement: dropdown.DotnetElement,
                selectedDropdownOption: dotnetOption.Option));
            SaveManifest(manifestEntries);

            AssertImageHasVisibleContent(dotnetOption.ScreenshotPath);
            AssertImageHasVisibleContent(rustOptionPath);
            AssertImageHasVisibleContent(optionSideBySidePath);

            _output.WriteLine(
                $"[{dotnetOption.ScenarioId}] Dropdown option: {dotnetOption.Option.Label}");
            _output.WriteLine(
                $"[{dotnetOption.ScenarioId}] Dotnet screenshot: {dotnetOption.ScreenshotPath}");
            _output.WriteLine(
                $"[{dotnetOption.ScenarioId}] Rust screenshot: {rustOptionPath}");
        }

        _output.WriteLine($"[main.{dropdown.Key}-open] Dotnet screenshot: {dotnetOpenPath}");
        _output.WriteLine($"[main.{dropdown.Key}-open] Rust screenshot: {rustOpenPath}");
    }

    private static Dictionary<string, string> MainRustDropdownEnvironment(
        IReadOnlyDictionary<string, string> rustBaseEnvironment,
        MainDropdownCapture dropdown)
    {
        var environment = new Dictionary<string, string>(rustBaseEnvironment, StringComparer.Ordinal);
        environment[dropdown.RustSelectedLanguageEnvironmentVariable] = dropdown.RestoreOptionIndex >= 0 &&
            dropdown.RestoreOptionIndex < dropdown.Options.Count
                ? FloatingLanguageOptionId(dropdown.Options[dropdown.RestoreOptionIndex])
                : "auto";
        return environment;
    }

    private static string MainDropdownAlias(MainDropdownCapture dropdown) =>
        dropdown.Key.StartsWith("source", StringComparison.OrdinalIgnoreCase) ? "source" : "target";

    private static IReadOnlyList<MainInteractionCapture> MainInteractionCaptures() =>
    [
        new(
            "mode-menu",
            "Mode Menu",
            "ModeMenuButton",
            "ModeMenuButton",
            "ModeMenuButton",
            0.18,
            0.11,
            UiParityRegion.DefaultMainRegions),
        new(
            "source-play",
            "Source Play",
            "SourcePlayButton",
            "main.quick.play_source",
            "main.quick.play_source",
            0.94,
            0.19,
            UiParityRegion.SourceInputEffectRegions),
        new(
            "source-language",
            "Source Language",
            "SourceLangCombo",
            "SourceLangCombo",
            "SourceLangCombo",
            0.17,
            0.47,
            UiParityRegion.DefaultMainRegions),
        new(
            "target-language",
            "Target Language",
            "TargetLangCombo",
            "TargetLangCombo",
            "TargetLangCombo",
            0.62,
            0.47,
            UiParityRegion.DefaultMainRegions),
        new(
            "swap",
            "Swap",
            "SwapLanguageButton",
            "SwapLanguageButton",
            "SwapLanguageButton",
            0.42,
            0.47,
            UiParityRegion.DefaultMainRegions),
        new(
            "translate",
            "Translate",
            "TranslateButton",
            "TranslateButton",
            "TranslateButton",
            0.94,
            0.47,
            UiParityRegion.PrimaryButtonEffectRegions),
        new(
            "pin",
            "Pin",
            "PinButton",
            "PinButton",
            "PinButton",
            0.86,
            0.11,
            UiParityRegion.DefaultMainRegions),
        new(
            "settings",
            "Settings",
            "SettingsButton",
            "SettingsButton",
            "SettingsButton",
            0.94,
            0.11,
            UiParityRegion.DefaultMainRegions),
    ];

    private static IReadOnlyList<MainDropdownCapture> MainDropdownCaptures()
    {
        var languageOptions = FloatingLanguageDropdownOptionCaptures();
        return
        [
            new(
                "source-language-dropdown",
                "Source Language Dropdown",
                "SourceLangCombo",
                "SourceLangCombo",
                "SourceLangCombo",
                "EASYDICT_PREVIEW_MAIN_SOURCE_LANGUAGE",
                0.17,
                0.47,
                RestoreOptionIndex: 0,
                languageOptions,
                UiParityRegion.DefaultMainRegions),
            new(
                "target-language-dropdown",
                "Target Language Dropdown",
                "TargetLangCombo",
                "TargetLangCombo",
                "TargetLangCombo",
                "EASYDICT_PREVIEW_MAIN_TARGET_LANGUAGE",
                0.62,
                0.47,
                RestoreOptionIndex: 5,
                languageOptions,
                UiParityRegion.DefaultMainRegions),
        ];
    }

    private static MainOperationsCaptureScope ResolveMainOperationsCaptureScope()
    {
        var value = Environment.GetEnvironmentVariable(MainOperationsScopeEnvironmentVariable)?
            .Trim()
            .ToLowerInvariant();

        return value switch
        {
            "initial" => new(
                CaptureButtons: false,
                CaptureDropdowns: false,
                CaptureDropdownOptions: false),
            "buttons" => new(
                CaptureButtons: true,
                CaptureDropdowns: false,
                CaptureDropdownOptions: false),
            "dropdown-open" or "dropdown-open-only" => new(
                CaptureButtons: false,
                CaptureDropdowns: true,
                CaptureDropdownOptions: false),
            "dropdowns" or "dropdown-options" => new(
                CaptureButtons: false,
                CaptureDropdowns: true,
                CaptureDropdownOptions: true),
            _ => new(
                CaptureButtons: true,
                CaptureDropdowns: true,
                CaptureDropdownOptions: true),
        };
    }


    private static SettingsParityCaptureStep MainDropdownProbeStep(
        MainDropdownCapture dropdown,
        string expandedElement) =>
        new(
            $"main.{dropdown.Key}",
            SettingsParitySection.General,
            0,
            ExpandedDropdownElement: expandedElement,
            ExpectedDropdownItems: dropdown.Options.Select(option => option.Label).ToArray(),
            DropdownOptions: dropdown.Options,
            DropdownRestoreOptionIndex: dropdown.RestoreOptionIndex,
            DropdownFallbackItemCount: dropdown.Options.Count,
            DropdownFallbackWidthDips: dropdown.FallbackWidthDips,
            DropdownFallbackHeightDips: dropdown.FallbackHeightDips,
            InteractionFallbackX: dropdown.FallbackX,
            InteractionFallbackY: dropdown.FallbackY);

    private static IReadOnlyList<FloatingInteractionCapture> FloatingInteractionCaptures(string windowKind)
    {
        var upperWindowKind = windowKind.ToUpperInvariant();
        var headerRegions = UiParityRegion.FloatingHeaderEffectRegions;
        var languageRegions = UiParityRegion.FloatingLanguageBarEffectRegions;
        var captures = new List<FloatingInteractionCapture>();

        captures.Add(new(
            "ocr",
            "OCR",
            windowKind.Equals("mini", StringComparison.OrdinalIgnoreCase)
                ? "MiniWindowOcrButton"
                : "FixedWindowOcrButton",
            $"EASYDICT_PREVIEW_{upperWindowKind}_OCR_STATE",
            $"{windowKind}.ocr",
            0.90,
            0.15,
            headerRegions));
        captures.Add(new(
            "close",
            "Close",
            windowKind.Equals("mini", StringComparison.OrdinalIgnoreCase)
                ? "MiniWindowCloseButton"
                : "CloseButton",
            $"EASYDICT_PREVIEW_{upperWindowKind}_CLOSE_STATE",
            windowKind.Equals("mini", StringComparison.OrdinalIgnoreCase)
                ? "MiniWindowCloseButton"
                : "CloseButton",
            0.95,
            0.15,
            headerRegions));
        captures.Add(new(
            "source-language",
            "Source Language",
            "SourceLangCombo",
            $"EASYDICT_PREVIEW_{upperWindowKind}_SOURCE_LANGUAGE_STATE",
            $"{windowKind}.source_language",
            0.20,
            0.40,
            languageRegions));
        captures.Add(new(
            "target-language",
            "Target Language",
            "TargetLangCombo",
            $"EASYDICT_PREVIEW_{upperWindowKind}_TARGET_LANGUAGE_STATE",
            $"{windowKind}.target_language",
            0.72,
            0.40,
            languageRegions));

        if (windowKind.Equals("mini", StringComparison.OrdinalIgnoreCase))
        {
            captures.Add(new(
                "pin",
                "Pin",
                "PinButton",
                $"EASYDICT_PREVIEW_{upperWindowKind}_PIN_STATE",
                "mini.pin",
                0.05,
                0.15,
                headerRegions));
        }
        captures.Add(new(
            "swap",
            "Swap",
            "SwapButton",
            $"EASYDICT_PREVIEW_{upperWindowKind}_SWAP_STATE",
            $"{windowKind}.swap",
            0.47,
            0.40,
            languageRegions));

        return captures;
    }

    private static IReadOnlyList<FloatingDropdownCapture> FloatingDropdownCaptures(string windowKind)
    {
        var upperWindowKind = windowKind.ToUpperInvariant();
        var languageOptions = FloatingLanguageDropdownOptionCaptures();
        return
        [
            new(
                "source-language-dropdown",
                "Source Language Dropdown",
                "SourceLangCombo",
                $"{windowKind}.source_language",
                $"{windowKind}.source_language",
                $"EASYDICT_PREVIEW_{upperWindowKind}_SOURCE_LANGUAGE",
                0.20,
                0.40,
                RestoreOptionIndex: 0,
                languageOptions),
            new(
                "target-language-dropdown",
                "Target Language Dropdown",
                "TargetLangCombo",
                $"{windowKind}.target_language",
                $"{windowKind}.target_language",
                $"EASYDICT_PREVIEW_{upperWindowKind}_TARGET_LANGUAGE",
                0.72,
                0.40,
                RestoreOptionIndex: 2,
                languageOptions),
        ];
    }

    private static IReadOnlyList<SettingsDropdownOptionCapture> FloatingLanguageDropdownOptionCaptures() =>
    [
        new("自动检测", 0),
        new("简体中文", 1),
        new("繁体中文", 2),
        new("日语", 3),
        new("韩语", 4),
        new("英语", 5),
        new("德语", 6),
        new("法语", 7),
        new("西班牙语", 8),
    ];

    private static string FloatingLanguageOptionId(SettingsDropdownOptionCapture option) =>
        option.DotnetIndex switch
        {
            0 => "auto",
            1 => "zh-Hans",
            2 => "zh-Hant",
            3 => "ja",
            4 => "ko",
            5 => "en",
            6 => "de",
            7 => "fr",
            8 => "es",
            _ => option.RustOptionText,
        };

    private static FloatingCaptureScope ResolveFloatingCaptureScope()
    {
        var value = Environment.GetEnvironmentVariable(FloatingScopeEnvironmentVariable)?
            .Trim()
            .ToLowerInvariant();

        return value switch
        {
            "initial" => new(
                CaptureTranslateButton: false,
                CaptureDropdowns: false,
                CaptureDropdownOptions: false,
                CaptureControls: false),
            "buttons" => new(
                CaptureTranslateButton: true,
                CaptureDropdowns: false,
                CaptureDropdownOptions: false,
                CaptureControls: true),
            "dropdown-open" or "dropdown-open-only" => new(
                CaptureTranslateButton: false,
                CaptureDropdowns: true,
                CaptureDropdownOptions: false,
                CaptureControls: false),
            "dropdowns" or "dropdown-options" => new(
                CaptureTranslateButton: false,
                CaptureDropdowns: true,
                CaptureDropdownOptions: true,
                CaptureControls: false),
            _ => new(
                CaptureTranslateButton: true,
                CaptureDropdowns: true,
                CaptureDropdownOptions: true,
                CaptureControls: true),
        };
    }

    private static bool MatchesOptionalEnvironmentFilter(string variableName, params string[] candidates)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var tokens = value.Split(
            [',', ';', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(token => candidates.Any(candidate =>
            string.Equals(token, candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private static SettingsParityCaptureStep FloatingDropdownProbeStep(
        string windowKind,
        FloatingDropdownCapture dropdown,
        string expandedElement) =>
        new(
            $"{windowKind}.{dropdown.Key}",
            SettingsParitySection.General,
            0,
            ExpandedDropdownElement: expandedElement,
            ExpectedDropdownItems: dropdown.Options.Select(option => option.Label).ToArray(),
            DropdownOptions: dropdown.Options,
            DropdownRestoreOptionIndex: dropdown.RestoreOptionIndex,
            DropdownFallbackItemCount: dropdown.Options.Count,
            DropdownFallbackWidthDips: dropdown.FallbackWidthDips,
            DropdownFallbackHeightDips: dropdown.FallbackHeightDips,
            InteractionFallbackX: dropdown.FallbackX,
            InteractionFallbackY: dropdown.FallbackY);

    private static void ResetDotnetMiniPinIfChecked(Window dotnetWindow)
    {
        var pin = Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(dotnetWindow, "PinButton"),
                TimeSpan.FromSeconds(2))
            .Result;
        if (pin == null)
        {
            return;
        }

        try
        {
            var toggleButton = pin.AsToggleButton();
            if (toggleButton?.ToggleState == ToggleState.On)
            {
                toggleButton.Toggle();
                Thread.Sleep(300);
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
        }
    }

    private void CloseDotnetFloatingWindow(Window window, string preferredCloseAutomationId)
    {
        foreach (var closeId in new[] { preferredCloseAutomationId, "CloseButton", "Close" })
        {
            var closeButton = UITestHelper.FindByAutomationIdOrName(window, closeId);
            if (closeButton == null)
            {
                continue;
            }

            try
            {
                UITestHelper.ClickElement(closeButton);
                Thread.Sleep(500);
                return;
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException or TimeoutException)
            {
            }
        }

        try
        {
            window.Close();
            Thread.Sleep(500);
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or TimeoutException)
        {
        }
    }

    private AutomationElement OpenDotnetSettingsSection(Window window, SettingsParitySection section)
    {
        window.SetForeground();
        Thread.Sleep(500);

        var scrollViewer = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "MainScrollViewer"),
                TimeSpan.FromSeconds(2))
            .Result;

        if (scrollViewer == null)
        {
            var settingsButton = Retry.WhileNull(
                    () => FindVisibleSettingsNavigationButton(window),
                    TimeSpan.FromSeconds(60))
                .Result;
            settingsButton.Should().NotBeNull("dotnet app should expose the Settings button");
            UITestHelper.ClickElement(settingsButton!);

            scrollViewer = Retry.WhileNull(
                    () => FindVisibleByAutomationId(window, "MainScrollViewer"),
                    TimeSpan.FromSeconds(30))
                .Result;
        }

        if (scrollViewer == null)
        {
            CaptureDotnetSettingsNavigationDiagnostics(window, section);
        }

        scrollViewer.Should().NotBeNull("dotnet Settings page should open before section comparison");
        ArrangeSettingsWindowForCapture(window);
        ScrollHelper.MouseScrollToPercent(scrollViewer!, 0);

        var tab = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, $"SettingsTab_{section.Label}"),
                TimeSpan.FromSeconds(10))
            .Result;
        tab.Should().NotBeNull($"dotnet Settings tab {section.Label} should be visible");
        UITestHelper.ClickElement(tab!);

        var readyElement = Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, section.DotnetReadyElement),
                TimeSpan.FromSeconds(15))
            .Result;
        if (readyElement == null)
        {
            CaptureDotnetSettingsSectionDiagnostics(window, section, section.DotnetReadyElement);
        }

        readyElement.Should().NotBeNull(
            $"dotnet Settings section {section.Label} should show {section.DotnetReadyElement}");

        foreach (var requiredId in AdditionalRequiredSettingsSemanticTags(section))
        {
            var element = Retry.WhileNull(
                    () => FindVisibleByAutomationIdOrName(window, requiredId),
                    TimeSpan.FromSeconds(5))
                .Result;
            if (element == null)
            {
                CaptureDotnetSettingsSectionDiagnostics(window, section, requiredId);
            }

            element.Should().NotBeNull(
                $"dotnet Settings section {section.Label} should expose complete About element {requiredId}");
        }

        foreach (var requiredText in AdditionalRequiredSettingsVisibleText(section))
        {
            var element = Retry.WhileNull(
                    () => FindVisibleByAutomationIdOrName(window, requiredText),
                    TimeSpan.FromSeconds(5))
                .Result;
            if (element == null)
            {
                CaptureDotnetSettingsSectionDiagnostics(window, section, requiredText);
            }

            element.Should().NotBeNull(
                $"dotnet Settings section {section.Label} should expose complete About text {requiredText}");
        }

        return scrollViewer!;
    }

    private static AutomationElement? FindVisibleSettingsNavigationButton(Window window)
    {
        try
        {
            return window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Where(button => IsOnScreenOrUnknown(button))
                .Where(button =>
                {
                    var automationId = SafeElementAutomationId(button);
                    var name = SafeElementName(button);
                    return IsSettingsNavigationLabel(automationId) ||
                           IsSettingsNavigationLabel(name);
                })
                .OrderBy(button => button.BoundingRectangle.Top)
                .ThenByDescending(button => button.BoundingRectangle.Right)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return null;
        }
    }

    private static bool IsSettingsNavigationLabel(string value)
    {
        return string.Equals(value, "SettingsButton", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "Settings", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "设置", StringComparison.OrdinalIgnoreCase);
    }

    private void CaptureDotnetSettingsNavigationDiagnostics(Window window, SettingsParitySection section)
    {
        try
        {
            var screenshotPath = ScreenshotHelper.CaptureWindow(
                window,
                $"dotnet-settings-navigation-failed-{section.Id}");
            _output.WriteLine($"Dotnet settings navigation diagnostic screenshot: {screenshotPath}");
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or TimeoutException)
        {
            _output.WriteLine($"Could not capture dotnet settings navigation screenshot: {ex.Message}");
        }

        try
        {
            var path = Path.Combine(
                ScreenshotHelper.OutputDir,
                $"dotnet-settings-navigation-failed-{section.Id}-buttons.txt");
            var lines = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(button =>
                {
                    var bounds = button.BoundingRectangle;
                    return string.Join(
                        " | ",
                        $"automationId=\"{SafeElementAutomationId(button)}\"",
                        $"name=\"{SafeElementName(button)}\"",
                        $"offscreen={SafeElementIsOffscreen(button)}",
                        $"bounds={bounds}");
                })
                .ToArray();

            File.WriteAllLines(path, lines);
            _output.WriteLine($"Dotnet settings navigation button diagnostics: {path}");
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or TimeoutException or IOException)
        {
            _output.WriteLine($"Could not write dotnet settings navigation button diagnostics: {ex.Message}");
        }
    }

    private void CaptureDotnetSettingsSectionDiagnostics(
        Window window,
        SettingsParitySection section,
        string missingElement)
    {
        try
        {
            var screenshotPath = ScreenshotHelper.CaptureWindow(
                window,
                $"dotnet-settings-section-missing-{section.Id}-{SanitizeFileName(missingElement)}");
            _output.WriteLine($"Dotnet settings section diagnostic screenshot: {screenshotPath}");
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or TimeoutException or ExternalException or IOException)
        {
            _output.WriteLine($"Could not capture dotnet settings section screenshot: {ex.Message}");
        }

        try
        {
            var path = Path.Combine(
                ScreenshotHelper.OutputDir,
                $"dotnet-settings-section-missing-{section.Id}-{SanitizeFileName(missingElement)}-visible-elements.txt");
            var lines = window
                .FindAllDescendants()
                .Where(IsOnScreenOrUnknown)
                .Select(element =>
                {
                    var bounds = element.BoundingRectangle;
                    return string.Join(
                        " | ",
                        $"controlType={SafeControlTypeName(element)}",
                        $"automationId=\"{SafeElementAutomationId(element)}\"",
                        $"name=\"{SafeElementName(element)}\"",
                        $"bounds={bounds}");
                })
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToArray();

            File.WriteAllLines(path, lines);
            _output.WriteLine(
                $"Dotnet settings section visible-element diagnostics for missing {missingElement}: {path}");
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException or IOException)
        {
            _output.WriteLine($"Could not write dotnet settings section diagnostics: {ex.Message}");
        }
    }

    private void ScrollBothWindowsToPercent(
        AutomationElement dotnetScrollViewer,
        Window rustWindow,
        SettingsParityCaptureStep step)
    {
        ScrollHelper.ScrollToPercent(
            dotnetScrollViewer,
            step.ScrollPercent,
            message => _output.WriteLine($"[{step.Key}][dotnet] {message}"));
        if (step.ScrollPercent <= 0)
        {
            _output.WriteLine(
                $"[{step.Key}][rust] Fresh preview starts at top; skipping mouse-wheel reset for 0% scroll capture.");
        }
        else
        {
            _output.WriteLine(
                $"[{step.Key}][rust] Initial preview scroll handled by EASYDICT_PREVIEW_SCROLL_PERCENT={step.ScrollPercent.ToString(CultureInfo.InvariantCulture)}");
            Thread.Sleep(TimeSpan.FromMilliseconds(1100));
        }
    }

    private void ExpandDotnetAvailableLanguages(
        Window window,
        AutomationElement scrollViewer,
        SettingsParityCaptureStep step)
    {
        var expander = ScrollHelper.ScrollToFind(
                scrollViewer,
                80,
                () => FindVisibleByAutomationIdOrName(window, "AvailableLanguagesExpander"),
                message => _output.WriteLine($"[{step.Key}][dotnet] {message}"))
            ?? Retry.WhileNull(
                    () => FindVisibleByAutomationIdOrName(window, "AvailableLanguagesExpander"),
                    TimeSpan.FromSeconds(5))
                .Result;
        expander.Should().NotBeNull("dotnet Available Languages expander should be visible before expanding");

        var expandPattern = expander!.Patterns.ExpandCollapse.PatternOrDefault;
        if (expandPattern != null)
        {
            if (expandPattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded)
            {
                expandPattern.Expand();
            }
        }
        else
        {
            UITestHelper.ClickElement(expander);
        }

        ScrollHelper.ScrollToPercent(
            scrollViewer,
            100,
            message => _output.WriteLine($"[{step.Key}][dotnet] {message}"));

        WaitForVisibleDotnetLanguageCheckboxes(window, minimumCount: 4, timeout: TimeSpan.FromSeconds(6))
            .Should()
            .BeGreaterThanOrEqualTo(4, "expanded dotnet Available Languages should expose language choices before screenshot capture");
    }

    private void ExpandDotnetSettingsExpander(
        Window window,
        AutomationElement scrollViewer,
        SettingsParityCaptureStep step)
    {
        var automationId = step.DotnetExpandElement!;
        CollapseVisibleDotnetServiceExpanders(window, scrollViewer, step);
        var expander = ScrollHelper.ScrollToFind(
                scrollViewer,
                80,
                () => FindVisibleExpandableByAutomationIdOrName(window, automationId),
                message => _output.WriteLine($"[{step.Key}][dotnet] {message}"))
            ?? Retry.WhileNull(
                    () => FindVisibleExpandableByAutomationIdOrName(window, automationId),
                    TimeSpan.FromSeconds(5))
                .Result;
        expander.Should().NotBeNull($"{automationId} should be visible before expanding");

        var expandPattern = expander!.Patterns.ExpandCollapse.PatternOrDefault;
        if (expandPattern != null)
        {
            if (expandPattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded)
            {
                expandPattern.Expand();
            }
        }
        else
        {
            UITestHelper.ClickElement(expander);
        }

        Thread.Sleep(700);
    }

    private void CollapseVisibleDotnetServiceExpanders(
        Window window,
        AutomationElement scrollViewer,
        SettingsParityCaptureStep step)
    {
        if (step.Section != SettingsParitySection.Services)
        {
            return;
        }

        var serviceExpanders = new[]
        {
            "DeepLServiceExpander",
            "WindowsLocalAIExpander",
            "Ollama (Local LLM)",
            "OllamaServiceExpander",
            "OpenAI",
            "OpenAIServiceExpander",
            "DeepSeek",
            "DeepSeekServiceExpander",
            "Groq",
            "GroqServiceExpander",
            "Zhipu (智谱)",
            "ZhipuServiceExpander",
            "GitHub Models",
            "Gemini",
            "Custom OpenAI Compatible",
            "Built-in AI",
            "Doubao (豆包)",
            "Caiyun (彩云小译)",
            "NiuTrans (小牛翻译)",
            "Youdao (有道翻译)"
        };

        foreach (var scrollPercent in new[] { 0d, 35d, 70d, 100d })
        {
            ScrollHelper.ScrollToPercent(
                scrollViewer,
                scrollPercent,
                message => _output.WriteLine($"[{step.Key}][dotnet] {message}"));

            CollapseVisibleExpandedControls(window, step, scrollPercent);

            foreach (var automationId in serviceExpanders)
            {
                var expander = FindVisibleByAutomationIdOrName(window, automationId);
                var expandPattern = expander?.Patterns.ExpandCollapse.PatternOrDefault;
                if (expandPattern?.ExpandCollapseState.Value == ExpandCollapseState.Expanded)
                {
                    expandPattern.Collapse();
                    Thread.Sleep(120);
                }
            }
        }

        ScrollHelper.ScrollToPercent(
            scrollViewer,
            0,
            message => _output.WriteLine($"[{step.Key}][dotnet] {message}"));
    }

    private void CollapseVisibleExpandedControls(
        Window window,
        SettingsParityCaptureStep step,
        double scrollPercent)
    {
        for (var pass = 1; pass <= 3; pass++)
        {
            var collapsed = 0;
            IReadOnlyList<AutomationElement> elements;
            try
            {
                elements = window
                    .FindAllDescendants()
                    .Where(IsOnScreenOrUnknown)
                    .ToArray();
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
            {
                return;
            }

            foreach (var element in elements)
            {
                try
                {
                    var expandPattern = element.Patterns.ExpandCollapse.PatternOrDefault;
                    if (expandPattern?.ExpandCollapseState.Value != ExpandCollapseState.Expanded)
                    {
                        continue;
                    }

                    expandPattern.Collapse();
                    collapsed++;
                    Thread.Sleep(80);
                }
                catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException or PropertyNotSupportedException or TimeoutException)
                {
                    // Some WinUI automation peers disappear while their parent expander collapses.
                }
            }

            if (collapsed == 0)
            {
                return;
            }

            _output.WriteLine(
                $"[{step.Key}][dotnet] Collapsed {collapsed} visible expanded controls at {scrollPercent.ToString(CultureInfo.InvariantCulture)}% (pass {pass}).");
            Thread.Sleep(180);
        }
    }

    private void AssertCaptureStepReady(
        Window dotnetWindow,
        AutomationElement dotnetScrollViewer,
        Window rustWindow,
        SettingsParityCaptureStep step)
    {
        if (step.Key.Contains("translation-languages-collapsed", StringComparison.OrdinalIgnoreCase))
        {
            var expander = ScrollHelper.ScrollToFind(
                dotnetScrollViewer,
                80,
                () => FindVisibleByAutomationIdOrName(dotnetWindow, "AvailableLanguagesExpander"),
                message => _output.WriteLine($"[{step.Key}][dotnet] {message}"));
            expander.Should().NotBeNull("collapsed translation-languages screenshot should show the dotnet Available Languages expander");

            var expandPattern = expander!.Patterns.ExpandCollapse.PatternOrDefault;
            if (expandPattern != null)
            {
                expandPattern.ExpandCollapseState.Value.Should().NotBe(
                    ExpandCollapseState.Expanded,
                    "collapsed translation-languages screenshot should keep the dotnet expander collapsed");
            }

            _output.WriteLine(
                $"[{step.Key}][rust] Initial preview scroll handled by EASYDICT_PREVIEW_SCROLL_PERCENT={step.ScrollPercent.ToString(CultureInfo.InvariantCulture)}");
            Thread.Sleep(TimeSpan.FromMilliseconds(1100));
        }

        if (step.ExpandAvailableLanguages)
        {
            WaitForVisibleDotnetLanguageCheckboxes(dotnetWindow, minimumCount: 4, timeout: TimeSpan.FromSeconds(6))
                .Should()
                .BeGreaterThanOrEqualTo(4, "expanded translation-languages screenshot should show dotnet language checkboxes");
        }
    }

    private static void ApplyDotnetSettingsInteractionState(Window dotnetWindow, SettingsParityCaptureStep step)
    {
        if (step.HoveredTab is { } hoveredTab)
        {
            MoveMouseToHoverTarget(
                dotnetWindow,
                $"SettingsTab_{hoveredTab.Label}",
                fallbackX: 0.18,
                fallbackY: 0.12);
            return;
        }

        if (step.HoveredElement is { } hoveredElement)
        {
            MoveMouseToHoverTarget(
                dotnetWindow,
                hoveredElement,
                step.InteractionFallbackX,
                step.InteractionFallbackY);
            return;
        }

        if (step.FocusedElement is { } focusedElement)
        {
            FocusElement(
                dotnetWindow,
                focusedElement,
                step.InteractionFallbackX,
                step.InteractionFallbackY);
            return;
        }

        MoveMouseToNeutralPoint();
    }

    private static string CaptureDotnetSettingsStep(
        Window dotnetWindow,
        SettingsParityCaptureStep step,
        string screenshotName)
    {
        if (step.PressedTab is { } pressedTab)
        {
            return CapturePressedWindow(
                dotnetWindow,
                $"SettingsTab_{pressedTab.Label}",
                fallbackX: 0.28,
                fallbackY: 0.12,
                screenshotName);
        }

        if (step.PressedElement is { } pressedElement)
        {
            return CapturePressedWindow(
                dotnetWindow,
                pressedElement,
                step.InteractionFallbackX,
                step.InteractionFallbackY,
                screenshotName);
        }

        return ScreenshotHelper.CaptureWindow(dotnetWindow, screenshotName);
    }

    private string CaptureExpandedSettingsDropdownStep(
        Window window,
        SettingsParityCaptureStep step,
        string screenshotName,
        string? rustSchemaPath = null)
    {
        var dropdownElement = step.ExpandedDropdownElement;
        dropdownElement.Should().NotBeNullOrWhiteSpace(
            $"{step.Key} should identify the settings ComboBox to expand before capture");

        AssertNoUnexpectedTopLevelErrorWindows($"{step.Key} before expanding {dropdownElement}");
        ExpandSettingsComboBox(window, dropdownElement!, step, rustSchemaPath);
        EnsureExpandedDropdownItemsVisible(
            window,
            step,
            rustSchemaPath == null ? "dotnet" : "rust",
            rustSchemaPath);
        AssertNoUnexpectedTopLevelErrorWindows($"{step.Key} before capturing {screenshotName}");
        var path = CaptureWindowPreferHwnd(window, screenshotName, requireForeground: true);
        AssertNoUnexpectedTopLevelErrorWindows($"{step.Key} after capturing {screenshotName}");
        return path;
    }
    private static IReadOnlyList<SettingsDropdownOptionCapture> FilterDropdownOptionCaptures(
        SettingsParityCaptureStep step)
    {
        var value = Environment.GetEnvironmentVariable(DropdownOptionIndexesEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return step.DropdownOptionCaptures;
        }

        var selectedIndexes = value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseDropdownOptionIndex)
            .ToHashSet();
        var options = step.DropdownOptionCaptures
            .Where(option => selectedIndexes.Contains(option.DotnetIndex + 1))
            .ToArray();
        if (options.Length == 0)
        {
            throw new InvalidOperationException(
                $"{DropdownOptionIndexesEnvironmentVariable} selected no options for dropdown '{step.Key}'. Available option count: {step.DropdownOptionCaptures.Count}.");
        }

        return options;
    }

    private static int ParseDropdownOptionIndex(string token)
    {
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            throw new InvalidOperationException(
                $"{DropdownOptionIndexesEnvironmentVariable} contains non-integer option index '{token}'.");
        }

        if (index < 1)
        {
            throw new InvalidOperationException(
                $"{DropdownOptionIndexesEnvironmentVariable} option indexes are 1-based. Invalid option index: {index}.");
        }

        return index;
    }


    private IReadOnlyList<SettingsDropdownOptionCaptureResult> CaptureSettingsDropdownOptionSelections(
        Window window,
        SettingsParityCaptureStep step,
        string screenshotSuffix,
        string? rustSchemaPath)
    {
        var options = FilterDropdownOptionCaptures(step);
        var results = new List<SettingsDropdownOptionCaptureResult>();
        foreach (var option in options)
        {
            SelectSettingsDropdownOption(window, step, option, rustSchemaPath);
            if (rustSchemaPath == null &&
                step.Key.StartsWith("mini.", StringComparison.OrdinalIgnoreCase))
            {
                ResetDotnetMiniPinIfChecked(window);
            }

            var scenarioId = SettingsDropdownOptionScenarioId(step, option);
            var path = CaptureWindowPreferHwnd(
                window,
                $"{scenarioId}-{screenshotSuffix}",
                requireForeground: true);
            MaskFloatingLanguageBarOcclusions(path, window);
            var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
            var windowDpiScale = ScreenshotHelper.GetWindowDpiScale(window);
            var screenshotPixelSize = ReadScreenshotPixelSize(path);
            results.Add(new SettingsDropdownOptionCaptureResult(
                option,
                scenarioId,
                path,
                windowBounds,
                windowDpiScale,
                screenshotPixelSize));
            DismissExpandedDropdownIfNeeded(step);
            MoveMouseToNeutralPoint();
        }

        if (step.DropdownRestoreOptionIndex is { } restoreIndex &&
            restoreIndex >= 0 &&
            restoreIndex < step.DropdownOptionCaptures.Count)
        {
            SelectSettingsDropdownOption(
                window,
                step,
                step.DropdownOptionCaptures[restoreIndex],
                rustSchemaPath);
            DismissExpandedDropdownIfNeeded(step);
            MoveMouseToNeutralPoint();
        }

        return results;
    }

    private void SelectSettingsDropdownOption(
        Window window,
        SettingsParityCaptureStep step,
        SettingsDropdownOptionCapture option,
        string? rustSchemaPath)
    {
        var dropdownElement = step.ExpandedDropdownElement;
        dropdownElement.Should().NotBeNullOrWhiteSpace(
            $"{step.Key} should identify the settings ComboBox before selecting dropdown options");

        ResetRustScrolledDropdownPositionIfNeeded(window, step, rustSchemaPath);
        ExpandSettingsComboBox(window, dropdownElement!, step, rustSchemaPath);
        var isDotnet = rustSchemaPath == null;
        var optionText = isDotnet ? option.DotnetOptionText : option.RustOptionText;
        var optionIndex = isDotnet ? option.DotnetIndex : option.RustOptionIndexValue;
        bool selected;
        if (isDotnet)
        {
            selected = TryClickVisibleDropdownOption(
                window,
                step,
                rustSchemaPath,
                optionText,
                requireExactName: true);
            if (selected)
            {
                selected = WaitForDotnetComboBoxSelectedText(window, step, optionText);
            }

            if (!selected)
            {
                ExpandSettingsComboBox(window, dropdownElement!, step, rustSchemaPath);
                selected = TrySelectDotnetDropdownOptionByText(window, step, optionText);
            }
        }
        else
        {
            selected = TryClickVisibleDropdownOption(window, step, rustSchemaPath, optionText);
            if (!selected)
            {
                var fallbackPoint = GetDropdownOptionFallbackPoint(
                    window,
                    step,
                    rustSchemaPath,
                    optionIndex,
                    step.DropdownFallbackItemCount);
                Mouse.Click(fallbackPoint);
            }
        }

        Thread.Sleep(750);
        var confirmed = isDotnet
            ? WaitForDotnetComboBoxSelectedText(window, step, optionText)
            : ComboBoxRegionContainsText(window, step, optionText, rustSchemaPath);
        _output.WriteLine(
            $"[{step.Key}] selected dropdown option \"{optionText}\" index={optionIndex} target={(isDotnet ? "dotnet" : "rust")} confirmed={confirmed}");
        if (!confirmed)
        {
            var diagnosticPath = CaptureWindowPreferHwnd(
                window,
                $"{SettingsDropdownOptionScenarioId(step, option)}-selection-unconfirmed-{(isDotnet ? "dotnet" : "rust")}",
                requireForeground: true);
            throw new InvalidOperationException(
                $"{step.Key} did not show selected dropdown option '{optionText}' after selection for {(isDotnet ? "dotnet" : "rust")}. Diagnostic screenshot: {diagnosticPath}");
        }
    }

    private bool TrySelectDotnetDropdownOptionByText(
        Window window,
        SettingsParityCaptureStep step,
        string optionText)
    {
        if (TryClickVisibleDropdownOption(
                window,
                step,
                rustSchemaPath: null,
                optionText,
                requireExactName: true))
        {
            return WaitForDotnetComboBoxSelectedText(window, step, optionText);
        }

        var scrollTarget = FindDotnetDropdownScrollTarget(window, step);
        if (scrollTarget != null)
        {
            foreach (var scrollPercent in new[] { 0d, 20d, 40d, 60d, 80d, 100d })
            {
                try
                {
                    ScrollHelper.ScrollToPercent(
                        scrollTarget,
                        scrollPercent,
                        message => _output.WriteLine($"[{step.Key}][dotnet-dropdown] {message}"));
                }
                catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException or PropertyNotSupportedException or TimeoutException)
                {
                    _output.WriteLine(
                        $"[{step.Key}] scrolling .NET dropdown for \"{optionText}\" failed: {ex.GetType().Name}: {ex.Message}");
                    break;
                }

                if (TryClickVisibleDropdownOption(
                        window,
                        step,
                        rustSchemaPath: null,
                        optionText,
                        requireExactName: true))
                {
                    var confirmed = WaitForDotnetComboBoxSelectedText(window, step, optionText);
                    _output.WriteLine(
                        $"[{step.Key}] text-selected \"{optionText}\" after scrolling to {scrollPercent:F0}% confirmed={confirmed}");
                    return confirmed;
                }
            }
        }

        if (TryMoveMouseToDotnetDropdownPopup(window, step))
        {
            for (var reset = 0; reset < 20; reset++)
            {
                Mouse.Scroll(5);
                Thread.Sleep(30);
            }

            for (var scan = 0; scan < 48; scan++)
            {
                if (TryClickVisibleDropdownOption(
                        window,
                        step,
                        rustSchemaPath: null,
                        optionText,
                        requireExactName: true))
                {
                    var confirmed = WaitForDotnetComboBoxSelectedText(window, step, optionText);
                    _output.WriteLine(
                        $"[{step.Key}] text-selected \"{optionText}\" after mouse-wheel popup scan confirmed={confirmed}");
                    return confirmed;
                }

                Mouse.Scroll(-3);
                Thread.Sleep(100);
            }
        }

        _output.WriteLine(
            $"[{step.Key}] mouse-wheel popup scan did not expose exact option \"{optionText}\".");

        _output.WriteLine(
            $"[{step.Key}] exact .NET dropdown option \"{optionText}\" was not exposed after scanning the popup.");
        return false;
    }

    private static bool WaitForDotnetComboBoxSelectedText(
        Window window,
        SettingsParityCaptureStep step,
        string optionText)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (ComboBoxRegionContainsText(window, step, optionText, rustSchemaPath: null))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }



    private static bool ComboBoxRegionContainsText(
        Window window,
        SettingsParityCaptureStep step,
        string optionText,
        string? rustSchemaPath)
    {
        if (!string.IsNullOrWhiteSpace(step.ExpandedDropdownElement))
        {
            var comboElement = FindVisibleByAutomationIdOrName(window, step.ExpandedDropdownElement);
            if (comboElement != null)
            {
                if (DropdownOptionNameMatches(SafeElementName(comboElement), optionText))
                {
                    return true;
                }

                try
                {
                    var selectedItem = comboElement.AsComboBox().SelectedItem;
                    if (selectedItem != null &&
                        DropdownOptionNameMatches(SafeElementName(selectedItem), optionText))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException or PropertyNotSupportedException or TimeoutException)
                {
                }
            }
        }

        var comboBounds = TryGetDropdownComboPhysicalBounds(window, step, rustSchemaPath);
        var searchBounds = Rectangle.Inflate(comboBounds, 24, 12);
        try
        {
            return window.FindAllDescendants()
                .Where(IsOnScreenOrUnknown)
                .Where(element => ElementIntersectsSearchBounds(element, searchBounds))
                .Any(element => DropdownOptionNameMatches(SafeElementName(element), optionText));
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return false;
        }
    }

    private void ResetRustScrolledDropdownPositionIfNeeded(
        Window window,
        SettingsParityCaptureStep step,
        string? rustSchemaPath)
    {
        if (string.IsNullOrWhiteSpace(rustSchemaPath) || step.ScrollPercent <= 0)
        {
            return;
        }

        window.SetForeground();
        Thread.Sleep(150);
        ScrollHelper.MouseScrollToPercent(
            window,
            step.ScrollPercent,
            message => _output.WriteLine($"[{step.Key}][rust] {message}"),
            GetWindowRelativePoint(window, 0.50, 0.50));
        Thread.Sleep(300);
    }


    private static AutomationElement? FindDotnetDropdownScrollTarget(
        Window window,
        SettingsParityCaptureStep step)
    {
        var searchBounds = GetDropdownDiscoverySearchBounds(window, step, rustSchemaPath: null);
        var targetProcessId = TryGetWindowProcessId(window);
        try
        {
            var desktop = window.Automation.GetDesktop();
            foreach (var scrollable in desktop.FindAllDescendants()
                         .Where(IsOnScreenOrUnknown)
                         .Where(element => targetProcessId is not { } processId ||
                             TryGetElementProcessId(element) == processId)
                         .Where(element => ElementIntersectsSearchBounds(element, searchBounds))
                         .Where(element => element.Patterns.Scroll.IsSupported)
                         .OrderBy(element => TryGetElementPhysicalBounds(element) is { } bounds
                             ? bounds.Width * bounds.Height
                             : int.MaxValue))
            {
                if (scrollable.FindAllDescendants()
                    .Any(item => step.DropdownExpectedItems.Any(
                        expected => DropdownOptionNameEquals(SafeElementName(item), expected))))
                {
                    return scrollable;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException or PropertyNotSupportedException or TimeoutException)
        {
        }

        return null;
    }


    private static bool TryClickVisibleDropdownOption(
        Window window,
        SettingsParityCaptureStep step,
        string? rustSchemaPath,
        string optionText,
        bool requireExactName = false)
    {
        var searchBounds = GetDropdownDiscoverySearchBounds(window, step, rustSchemaPath);
        var candidates = FindVisibleDropdownOptionCandidates(
                window,
                optionText,
                searchBounds,
                requireExactName)
            .Select(element => new
            {
                Element = element,
                Point = TryGetClickablePoint(element) ?? TryGetElementCenterPoint(element)
            })
            .Where(candidate => candidate.Point is not null)
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var windowCenterX = windowBounds.Left + windowBounds.Width / 2;
        var windowCenterY = windowBounds.Top + windowBounds.Height / 2;
        var selected = candidates
            .Where(candidate => PointIsNearWindow(candidate.Point!.Value, windowBounds))
            .OrderBy(candidate =>
            {
                var point = candidate.Point!.Value;
                var dx = point.X - windowCenterX;
                var dy = point.Y - windowCenterY;
                return dx * dx + dy * dy;
            })
            .FirstOrDefault() ?? candidates[0];

        Mouse.Click(selected.Point!.Value);
        return true;
    }

    private static bool TryMoveMouseToDotnetDropdownPopup(
        Window window,
        SettingsParityCaptureStep step)
    {
        var searchBounds = GetDropdownDiscoverySearchBounds(window, step, rustSchemaPath: null);
        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        foreach (var expectedItem in step.DropdownExpectedItems)
        {
            foreach (var candidate in FindVisibleDropdownOptionCandidates(
                         window,
                         expectedItem,
                         searchBounds,
                         requireExactName: false))
            {
                var point = TryGetClickablePoint(candidate) ?? TryGetElementCenterPoint(candidate);
                if (point is { } popupPoint && PointIsNearWindow(popupPoint, windowBounds))
                {
                    Mouse.MoveTo(popupPoint);
                    return true;
                }
            }
        }

        return false;
    }


    private static IReadOnlyList<AutomationElement> FindVisibleDropdownOptionCandidates(
        Window window,
        string optionText,
        Rectangle? searchBounds,
        bool requireExactName)
    {
        var candidates = new List<AutomationElement>();
        var targetProcessId = TryGetWindowProcessId(window);
        try
        {
            var desktop = window.Automation.GetDesktop();
            var controlTypes = requireExactName
                ? new[] { ControlType.ListItem, ControlType.MenuItem, ControlType.Text }
                : new[] { ControlType.ListItem, ControlType.MenuItem };
            foreach (var controlType in controlTypes)
            {
                foreach (var element in desktop.FindAllDescendants(cf => cf.ByControlType(controlType))
                             .Where(IsOnScreenOrUnknown)
                             .Where(element => targetProcessId is not { } processId ||
                                 TryGetElementProcessId(element) == processId)
                             .Where(element => ElementIntersectsSearchBounds(element, searchBounds)))
                {
                    if (requireExactName)
                    {
                        if (!DropdownOptionNameEquals(SafeElementName(element), optionText))
                        {
                            continue;
                        }

                        var clickable = FindDropdownOptionClickableAncestor(element);
                        if (clickable != null)
                        {
                            candidates.Add(clickable);
                        }
                    }
                    else if (DropdownOptionNameMatches(SafeElementName(element), optionText))
                    {
                        candidates.Add(element);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
        }

        return candidates;
    }

    private static AutomationElement? FindDropdownOptionClickableAncestor(AutomationElement element)
    {
        AutomationElement? current = element;
        for (var depth = 0; current != null && depth < 5; depth++)
        {
            if (current.ControlType is ControlType.ListItem or ControlType.MenuItem)
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }



    private static bool ElementIntersectsSearchBounds(
        AutomationElement element,
        Rectangle? searchBounds)
    {
        if (searchBounds is not { } bounds)
        {
            return true;
        }

        return TryGetElementPhysicalBounds(element) is { } elementBounds &&
            bounds.IntersectsWith(elementBounds);
    }

    private static bool DropdownOptionNameEquals(string actualName, string expectedName) =>
        actualName.Trim().Length > 0 &&
        string.Equals(actualName.Trim(), expectedName.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool DropdownOptionNameMatches(string actualName, string expectedName)
    {
        actualName = actualName.Trim();
        expectedName = expectedName.Trim();
        return actualName.Length > 0 &&
               (DropdownOptionNameEquals(actualName, expectedName) ||
                actualName.Contains(expectedName, StringComparison.OrdinalIgnoreCase) ||
                expectedName.Contains(actualName, StringComparison.OrdinalIgnoreCase));
    }

    private static Point GetDropdownOptionFallbackPoint(
        Window window,
        SettingsParityCaptureStep step,
        string? rustSchemaPath,
        int optionIndex,
        int fallbackItemCount)
    {
        var comboBounds = TryGetDropdownComboPhysicalBounds(window, step, rustSchemaPath);
        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var rowHeight = Math.Max(18, step.DropdownOptionRowHeightDips * ScreenshotHelper.GetWindowDpiScale(window));
        var itemCount = Math.Max(optionIndex + 1, fallbackItemCount);
        var menuHeight = rowHeight * itemCount;
        var opensDown = comboBounds.Bottom + menuHeight + 8 <= windowBounds.Bottom;
        var menuTop = opensDown
            ? comboBounds.Bottom
            : comboBounds.Top - menuHeight;
        var x = comboBounds.Left + Math.Min(comboBounds.Width - 8, Math.Max(18, comboBounds.Width * 0.08));
        var y = menuTop + rowHeight * (optionIndex + 0.5);

        return new Point(
            (int)Math.Round(Math.Clamp(x, windowBounds.Left + 4, windowBounds.Right - 4)),
            (int)Math.Round(Math.Clamp(y, windowBounds.Top + 4, windowBounds.Bottom - 4)));
    }

    private static Rectangle TryGetDropdownComboPhysicalBounds(
        Window window,
        SettingsParityCaptureStep step,
        string? rustSchemaPath)
    {
        if (!step.ForceDropdownFallbackClick)
        {
            var element = FindVisibleByAutomationIdOrName(window, step.ExpandedDropdownElement ?? string.Empty);
            var bounds = TryGetElementPhysicalBounds(element);
            if (bounds is { Width: > 0, Height: > 0 })
            {
                return bounds.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(rustSchemaPath) && step.ScrollPercent <= 0)
        {
            var boundsPath = RustBoundsPathForSchema(rustSchemaPath);
            var dimensions = TryReadRustBoundsControlDimensions(boundsPath);
            if (step.ExpandedDropdownElement is { } automationId &&
                dimensions.TryGetValue(automationId, out var dimension) &&
                dimension.BoundsDips is { } boundsDips)
            {
                return RustBoundsPhysicalRectangle(window, boundsDips);
            }
        }

        var center = GetWindowRelativePoint(window, step.InteractionFallbackX, step.InteractionFallbackY);
        var dpiScale = Math.Max(0.001, ScreenshotHelper.GetWindowDpiScale(window));
        var width = (int)Math.Round(step.DropdownFallbackWidthDips * dpiScale);
        var height = (int)Math.Round(step.DropdownFallbackHeightDips * dpiScale);
        return new Rectangle(
            center.X - width / 2,
            center.Y - height / 2,
            width,
            height);
    }

    private static Rectangle? TryGetElementPhysicalBounds(AutomationElement? element)
    {
        if (element == null)
        {
            return null;
        }

        try
        {
            var bounds = element.BoundingRectangle;
            return bounds.Width <= 0 || bounds.Height <= 0
                ? null
                : Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return null;
        }
    }

    private static Rectangle RustBoundsPhysicalRectangle(
        Window window,
        UiParityControlBoundsDips bounds)
    {
        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var dpiScale = Math.Max(0.001, ScreenshotHelper.GetWindowDpiScale(window));
        var left = windowBounds.Left + (int)Math.Round(bounds.Left * dpiScale);
        var top = windowBounds.Top + (int)Math.Round(bounds.Top * dpiScale);
        var width = (int)Math.Round(bounds.Width * dpiScale);
        var height = (int)Math.Round(bounds.Height * dpiScale);
        return new Rectangle(left, top, width, height);
    }

    private static bool PointIsNearWindow(Point point, Rectangle windowBounds)
    {
        var padded = Rectangle.Inflate(windowBounds, 96, 96);
        return padded.Contains(point);
    }

    private static IReadOnlyList<DropdownOptionCapturePair> PairDropdownOptionCaptures(
        IReadOnlyList<SettingsDropdownOptionCaptureResult> dotnetCaptures,
        IReadOnlyList<SettingsDropdownOptionCaptureResult> rustCaptures)
    {
        var pairs = new List<DropdownOptionCapturePair>();
        foreach (var dotnet in dotnetCaptures)
        {
            var rust = rustCaptures.FirstOrDefault(candidate =>
                string.Equals(candidate.ScenarioId, dotnet.ScenarioId, StringComparison.OrdinalIgnoreCase));
            if (rust != null)
            {
                pairs.Add(new DropdownOptionCapturePair(dotnet, rust));
            }
        }

        return pairs;
    }

    private static string SettingsDropdownOptionScenarioId(
        SettingsParityCaptureStep step,
        SettingsDropdownOptionCapture option)
    {
        var optionPart = SanitizeFileName(option.Label).Replace(' ', '-').ToLowerInvariant();
        return $"{step.Key}-select-{option.DotnetIndex + 1}-{optionPart}";
    }

    private static void ExpandSettingsComboBox(
        Window window,
        string automationIdOrName,
        SettingsParityCaptureStep step,
        string? rustSchemaPath)
    {
        if (IsFloatingDropdownStep(step))
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(150);
        }

        ScreenshotHelper.EnsureWindowReadyForCapture(window, $"{step.Key} expand {automationIdOrName}");
        Thread.Sleep(250);

        if (step.ForceDropdownFallbackClick)
        {
            Mouse.Click(GetDropdownComboFallbackPoint(window, step));
            Thread.Sleep(900);
            return;
        }

        if (IsFloatingDropdownStep(step))
        {
            if (!string.IsNullOrWhiteSpace(rustSchemaPath) &&
                TryClickRustBoundsControl(window, rustSchemaPath, automationIdOrName, step))
            {
                Thread.Sleep(900);
                return;
            }

            var floatingElement = Retry.WhileNull(
                    () => FindVisibleByAutomationIdOrName(window, automationIdOrName),
                    TimeSpan.FromSeconds(4))
                .Result;
            var point = TryGetClickablePoint(floatingElement) ??
                TryGetElementCenterPoint(floatingElement) ??
                GetDropdownComboFallbackPoint(window, step);
            Mouse.Click(point);
            Thread.Sleep(900);
            return;
        }

        if (!string.IsNullOrWhiteSpace(rustSchemaPath) &&
            TryClickRustBoundsControl(window, rustSchemaPath, automationIdOrName, step))
        {
            Thread.Sleep(900);
            return;
        }

        var element = Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, automationIdOrName),
                TimeSpan.FromSeconds(8))
            .Result;

        element.Should().NotBeNull($"{automationIdOrName} should be visible before dropdown capture");

        if (!TryExpandComboBoxElement(element!))
        {
            var point = TryGetClickablePoint(element) ??
                TryGetElementCenterPoint(element) ??
                GetWindowRelativePoint(window, step.InteractionFallbackX, step.InteractionFallbackY);
            Mouse.Click(point);
        }

        Thread.Sleep(900);
    }

    private static Point GetDropdownComboFallbackPoint(Window window, SettingsParityCaptureStep step) =>
        GetWindowRelativePoint(window, step.InteractionFallbackX, step.InteractionFallbackY);

    private static bool TryClickRustBoundsControl(
        Window window,
        string? rustSchemaPath,
        string automationId,
        SettingsParityCaptureStep step)
    {
        var boundsPath = RustBoundsPathForSchema(rustSchemaPath);
        var dimensions = TryReadRustBoundsControlDimensions(boundsPath);
        if (!dimensions.TryGetValue(automationId, out var dimension) ||
            dimension.BoundsDips is not { } bounds)
        {
            return false;
        }

        var point = step.ScrollPercent > 0
            ? GetWindowRelativePoint(window, step.InteractionFallbackX, step.InteractionFallbackY)
            : RustBoundsClickPoint(window, bounds, automationId);
        window.SetForeground();
        ScreenshotHelper.EnsureWindowReadyForCapture(window, $"{step.Key} retry expand {automationId}");
        Thread.Sleep(150);
        Mouse.Click(point);
        return true;
    }
    private static Point RustBoundsClickPoint(
        Window window,
        UiParityControlBoundsDips bounds,
        string automationId)
    {
        var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var dpiScale = Math.Max(0.001, ScreenshotHelper.GetWindowDpiScale(window));
        var xDips = bounds.Left + bounds.Width / 2;
        if (automationId.Contains("Combo", StringComparison.OrdinalIgnoreCase))
        {
            xDips = bounds.Left + Math.Max(4, bounds.Width - 18);
        }

        return new Point(
            windowBounds.Left + (int)Math.Round(xDips * dpiScale),
            windowBounds.Top + (int)Math.Round((bounds.Top + bounds.Height / 2) * dpiScale));
    }


    private static bool TryExpandComboBoxElement(AutomationElement element)
    {
        try
        {
            var combo = element.AsComboBox();
            combo.Expand();
            return true;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidCastException or InvalidOperationException or PropertyNotSupportedException or TimeoutException)
        {
        }

        try
        {
            var expandPattern = element.Patterns.ExpandCollapse.PatternOrDefault;
            if (expandPattern == null)
            {
                return false;
            }

            if (expandPattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded)
            {
                expandPattern.Expand();
            }

            return true;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException or PropertyNotSupportedException or TimeoutException)
        {
            return false;
        }
    }

    private void EnsureExpandedDropdownItemsVisible(
        Window window,
        SettingsParityCaptureStep step,
        string target,
        string? rustSchemaPath)
    {
        if (step.DropdownExpectedItems.Count == 0)
        {
            return;
        }

        var searchBounds = GetDropdownDiscoverySearchBounds(window, step, rustSchemaPath);
        var (visibleNames, discovered) = CollectExpectedDropdownDiscoveries(window, step, searchBounds);
        var label = $"{step.Key}:{step.ExpandedDropdownElement}";
        _output.WriteLine(
            $"[{label}] {target} dropdown items discovered through UIA: {(discovered.Length == 0 ? "<none>" : string.Join(", ", discovered))}");

        var requiredCount = Math.Min(step.DropdownExpectedItems.Count, IsFloatingDropdownStep(step) ? 2 : 3);
        if (!string.IsNullOrWhiteSpace(rustSchemaPath) &&
            IsFloatingDropdownStep(step) &&
            discovered.Length < requiredCount)
        {
            _output.WriteLine(
                $"[{label}] rust floating dropdown option text is not exposed through UIA; keeping the opened state for screenshot capture.");
            return;
        }

        if (IsFloatingDropdownStep(step) && discovered.Length < requiredCount)
        {
            _output.WriteLine(
                $"[{label}] {target} floating dropdown exposed {discovered.Length}/{requiredCount} expected option(s) through UIA; keeping the opened state for screenshot capture.");
            return;
        }

        if (discovered.Length < requiredCount)
        {
            _output.WriteLine(
                $"[{label}] {target} dropdown exposed {discovered.Length}/{requiredCount} expected option(s); retrying with direct ComboBox click.");
            RetryExpandDropdownWithDirectClick(window, step, rustSchemaPath);
            searchBounds = GetDropdownDiscoverySearchBounds(window, step, rustSchemaPath);
            (visibleNames, discovered) = CollectExpectedDropdownDiscoveries(window, step, searchBounds);
            _output.WriteLine(
                $"[{label}] {target} dropdown items discovered after direct click: {(discovered.Length == 0 ? "<none>" : string.Join(", ", discovered))}");
        }

        if (discovered.Length < requiredCount)
        {
            var diagnosticPath = CaptureWindowPreferHwnd(
                window,
                $"{step.Key}-dropdown-discovery-failed-{target}",
                requireForeground: true);
            _output.WriteLine($"[{label}] {target} dropdown diagnostic screenshot: {diagnosticPath}");
        }

        discovered.Length.Should().BeGreaterThanOrEqualTo(
            requiredCount,
            $"{label} {target} dropdown should expose at least {requiredCount} expected option(s) before screenshot capture; visible names: {string.Join(", ", visibleNames.Take(40))}");
    }

    private static (IReadOnlyList<string> VisibleNames, string[] Discovered) CollectExpectedDropdownDiscoveries(
        Window window,
        SettingsParityCaptureStep step,
        Rectangle? searchBounds)
    {
        var visibleNames = CollectVisibleDropdownNames(window, searchBounds);
        var discovered = step.DropdownExpectedItems
            .Where(expected => visibleNames.Any(name => DropdownOptionNameMatches(name, expected)))
            .ToArray();
        return (visibleNames, discovered);
    }

    private static Rectangle? GetDropdownDiscoverySearchBounds(
        Window window,
        SettingsParityCaptureStep step,
        string? rustSchemaPath)
    {
        var comboBounds = TryGetDropdownComboPhysicalBounds(window, step, rustSchemaPath);
        if (comboBounds.Width <= 0 || comboBounds.Height <= 0)
        {
            return null;
        }

        var dpiScale = Math.Max(0.001, ScreenshotHelper.GetWindowDpiScale(window));
        var rowHeight = Math.Max(18, step.DropdownOptionRowHeightDips * dpiScale);
        var menuHeight = rowHeight * Math.Max(step.DropdownFallbackItemCount, step.DropdownExpectedItems.Count);
        var menuWidth = Math.Max(comboBounds.Width, step.DropdownFallbackWidthDips * dpiScale);
        var left = comboBounds.Left - (int)Math.Round(32 * dpiScale);
        var right = comboBounds.Left + (int)Math.Round(menuWidth + 32 * dpiScale);
        var top = comboBounds.Top - (int)Math.Round(menuHeight + 64 * dpiScale);
        var bottom = comboBounds.Bottom + (int)Math.Round(menuHeight + 64 * dpiScale);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static void RetryExpandDropdownWithDirectClick(
        Window window,
        SettingsParityCaptureStep step,
        string? rustSchemaPath)
    {
        if (step.ExpandedDropdownElement is not { } dropdownElement)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(rustSchemaPath))
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(150);
            if (TryClickRustBoundsControl(window, rustSchemaPath, dropdownElement, step))
            {
                Thread.Sleep(900);
                return;
            }
        }

        var element = FindVisibleByAutomationIdOrName(window, dropdownElement);
        var point = TryGetClickablePoint(element) ??
            TryGetElementCenterPoint(element) ??
            GetDropdownComboFallbackPoint(window, step);
        window.SetForeground();
        Thread.Sleep(150);
        Mouse.Click(point);
        Thread.Sleep(900);
    }

    private static IReadOnlyList<string> CollectVisibleDropdownNames(Window window, Rectangle? searchBounds)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        AddVisibleElementNames(names, SafeFindAllDescendants(window), searchBounds);
        var targetProcessId = TryGetWindowProcessId(window);

        try
        {
            var desktop = window.Automation.GetDesktop();
            foreach (var controlType in new[]
                     {
                         ControlType.ListItem,
                         ControlType.MenuItem,
                         ControlType.Text,
                         ControlType.ComboBox
                     })
            {
                AddVisibleElementNames(
                    names,
                    desktop.FindAllDescendants(cf => cf.ByControlType(controlType)),
                    searchBounds,
                    targetProcessId);
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
        }

        return names.ToArray();
    }

    private static IReadOnlyList<AutomationElement> SafeFindAllDescendants(Window window)
    {
        try
        {
            return window.FindAllDescendants().Where(IsOnScreenOrUnknown).ToArray();
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return [];
        }
    }

    private static void AddVisibleElementNames(
        ICollection<string> names,
        IEnumerable<AutomationElement> elements,
        Rectangle? requiredBounds,
        int? requiredProcessId = null)
    {
        foreach (var element in elements)
        {
            if (!IsOnScreenOrUnknown(element))
            {
                continue;
            }

            if (requiredProcessId is { } processId &&
                TryGetElementProcessId(element) != processId)
            {
                continue;
            }

            if (requiredBounds is { } bounds &&
                TryGetElementPhysicalBounds(element) is { } elementBounds &&
                !bounds.IntersectsWith(elementBounds))
            {
                continue;
            }

            var name = SafeElementName(element).Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }
    }

    private static void AddVisibleElementNames(
        ICollection<string> names,
        IEnumerable<AutomationElement> elements) =>
        AddVisibleElementNames(names, elements, requiredBounds: null);

    private static int? TryGetWindowProcessId(Window window)
    {
        try
        {
            var hwnd = SafeNativeWindowHandle(window);
            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out var processId);
                return processId;
            }

            return window.Properties.ProcessId.Value;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return null;
        }
    }

    private static int? TryGetElementProcessId(AutomationElement element)
    {
        try
        {
            return element.Properties.ProcessId.Value;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return null;
        }
    }

    private static bool IsFloatingDropdownStep(SettingsParityCaptureStep step) =>
        step.Key.StartsWith("mini.", StringComparison.OrdinalIgnoreCase) ||
        step.Key.StartsWith("fixed.", StringComparison.OrdinalIgnoreCase);

    private static void DismissExpandedDropdownIfNeeded(SettingsParityCaptureStep step)
    {
        if (!step.CapturesExpandedDropdown)
        {
            return;
        }

        try
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }

        Thread.Sleep(300);
    }

    private static int WaitForVisibleDotnetLanguageCheckboxes(
        Window window,
        int minimumCount,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        var count = 0;
        while (stopwatch.Elapsed < timeout)
        {
            count = CountVisibleDotnetLanguageCheckboxes(window);
            if (count >= minimumCount)
            {
                return count;
            }

            Thread.Sleep(250);
        }

        return count;
    }

    private static int CountVisibleDotnetLanguageCheckboxes(Window window)
    {
        try
        {
            return window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                .Count(element => IsOnScreenOrUnknown(element));
        }
        catch (Exception ex) when (ex is COMException or PropertyNotSupportedException or TimeoutException)
        {
            return 0;
        }
    }

    private static void WaitForMainWindowReady(Window window, string label)
    {
        var semanticElement = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "QuickInputCard")
                    ?? FindVisibleByAutomationId(window, "QuickOutputCard")
                    ?? FindVisibleByAutomationIdOrName(window, "InputTextBox"),
                TimeSpan.FromSeconds(6))
            .Result;
        if (semanticElement != null)
        {
            return;
        }

        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        bounds.Width.Should().BeGreaterThan(240, $"{label} main window should be visible before capture");
        bounds.Height.Should().BeGreaterThan(240, $"{label} main window should be visible before capture");
    }

    private static void SetDotnetMainInputText(Window window, string text)
    {
        window.SetForeground();
        var inputBox = UITestHelper.FindInputTextBox(window, TimeSpan.FromSeconds(10));
        inputBox.Should().NotBeNull("dotnet main InputTextBox should be available before before-translate capture");
        inputBox!.Text = text;
        Thread.Sleep(350);
    }

    private static string? WaitForMainDetectedLanguageText(Window window, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var element = FindVisibleByAutomationId(window, "DetectedLanguageText")
                ?? FindVisibleByAutomationIdOrName(window, "DetectedLanguageText");
            if (element != null)
            {
                var text = SafeElementName(element).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            Thread.Sleep(120);
        }

        return null;
    }

    private static void RestoreMainWindowAfterOperation(Window window)
    {
        if (FindVisibleByAutomationId(window, "QuickInputCard") != null ||
            FindVisibleByAutomationId(window, "InputTextBox") != null)
        {
            return;
        }

        window.SetForeground();
        Thread.Sleep(180);

        foreach (var backId in new[] { "FloatingBackButton", "BackButton" })
        {
            var backButton = FindVisibleByAutomationIdOrName(window, backId);
            if (backButton == null)
            {
                continue;
            }

            try
            {
                UITestHelper.ClickElement(backButton);
                Thread.Sleep(650);
                if (FindVisibleByAutomationId(window, "QuickInputCard") != null ||
                    FindVisibleByAutomationId(window, "InputTextBox") != null)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
            {
            }
        }

        try
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(250);
            Keyboard.Press(VirtualKeyShort.LEFT);
            Thread.Sleep(450);
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException or TimeoutException)
        {
        }
    }

    private static string CaptureDotnetModeSwitchOverlayToLongDocument(Window window, string screenshotName)
    {
        window.SetForeground();
        var modeButton = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "ModeMenuButton"),
                TimeSpan.FromSeconds(8))
            .Result;
        modeButton.Should().NotBeNull("dotnet mode menu should be visible before overlay capture");
        UITestHelper.ClickElement(modeButton!);
        Thread.Sleep(250);

        var longDocItem = Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, "ModeLongDocItem"),
                TimeSpan.FromSeconds(5))
            .Result;
        longDocItem.Should().NotBeNull("dotnet long-document menu item should be visible before overlay capture");
        UITestHelper.ClickElement(longDocItem!);

        // .NET keeps the mode-switch loading overlay visible for at least
        // ModeSwitchMinimumDurationMs (180ms) after a 50ms render delay.
        Thread.Sleep(90);
        return ScreenshotHelper.CaptureWindow(window, screenshotName);
    }

    private static void PrepareDotnetLongDocument(Window window)
    {
        DismissHotkeyRegistrationDialogIfPresent(window);
        if (FindVisibleByAutomationId(window, "LongDocSourceLangCombo") == null)
        {
            SwitchDotnetToLongDocumentMode(window);
        }
        DismissHotkeyRegistrationDialogIfPresent(window);
        WaitForLongDocumentReady(window, "dotnet");
    }

    private static void DismissHotkeyRegistrationDialogIfPresent(Window window)
    {
        var title = FindVisibleByAutomationIdOrName(window, "Hotkey Registration Failed")
            ?? FindVisibleByAutomationIdOrName(window, "快捷键注册失败");
        if (title == null)
        {
            return;
        }

        var dismiss = FindVisibleByAutomationIdOrName(window, "OK")
            ?? FindVisibleByAutomationIdOrName(window, "确定");
        if (dismiss != null)
        {
            UITestHelper.ClickElement(dismiss);
        }
        else
        {
            window.SetForeground();
            Keyboard.Press(VirtualKeyShort.RETURN);
        }
        Thread.Sleep(300);
    }

    private static void SwitchDotnetToLongDocumentMode(Window window)
    {
        window.SetForeground();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var modeButton = Retry.WhileNull(
                    () => FindVisibleByAutomationId(window, "ModeMenuButton"),
                    TimeSpan.FromSeconds(8))
                .Result;
            modeButton.Should().NotBeNull("dotnet mode menu should be visible before long-document capture");
            UITestHelper.ClickElement(modeButton!);
            Thread.Sleep(500);

            var longDocItem = Retry.WhileNull(
                    () => FindVisibleByAutomationIdOrName(window, "ModeLongDocItem"),
                    TimeSpan.FromSeconds(5))
                .Result;
            if (longDocItem == null)
            {
                Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
                Thread.Sleep(250);
                continue;
            }

            UITestHelper.ClickElement(longDocItem);
            var ready = Retry.WhileNull(
                    () => FindVisibleByAutomationId(window, "LongDocSourceLangCombo"),
                    TimeSpan.FromSeconds(8))
                .Result;
            if (ready != null)
            {
                return;
            }

            Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Thread.Sleep(250);
        }

        FindVisibleByAutomationId(window, "LongDocSourceLangCombo")
            .Should().NotBeNull("dotnet long-document controls should appear after switching modes");
    }

    private static void WaitForLongDocumentReady(Window window, string label)
    {
        var semanticElement = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "LongDocSourceLangCombo")
                    ?? FindVisibleByAutomationId(window, "main.long-doc.service")
                    ?? FindVisibleByAutomationId(window, "main.long-doc.translate"),
                TimeSpan.FromSeconds(8))
            .Result;
        if (semanticElement != null)
        {
            return;
        }

        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        bounds.Width.Should().BeGreaterThan(240, $"{label} long-document window should be visible before capture");
        bounds.Height.Should().BeGreaterThan(240, $"{label} long-document window should be visible before capture");
    }

    private static void SetDotnetLongDocumentModes(
        Window window,
        int inputModeIndex,
        int outputModeIndex)
    {
        SelectDotnetComboBoxIndex(window, "LongDocInputModeCombo", inputModeIndex);
        SelectDotnetComboBoxIndex(window, "LongDocOutputModeCombo", outputModeIndex);
        Thread.Sleep(450);
    }

    private static void SelectDotnetComboBoxIndex(Window window, string automationId, int index)
    {
        window.SetForeground();
        Thread.Sleep(250);
        var combo = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, automationId)?.AsComboBox(),
                TimeSpan.FromSeconds(8))
            .Result;
        combo.Should().NotBeNull($"{automationId} should be visible before long-document mode capture");
        combo!.Select(index);
        Thread.Sleep(250);
    }

    private static void ExpandDotnetComboBox(Window window, string automationId)
    {
        window.SetForeground();
        var combo = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, automationId)?.AsComboBox(),
                TimeSpan.FromSeconds(8))
            .Result;
        combo.Should().NotBeNull($"{automationId} should be visible before dropdown capture");
        combo!.Expand();
        Thread.Sleep(900);
    }

    private static void MoveMouseToNeutralPoint()
    {
        var screen = ScreenshotHelper.GetVirtualScreenBounds();
        Mouse.MoveTo(new Point(screen.Left + 8, screen.Top + 8));
        Thread.Sleep(350);
    }

    private static void PrepareNeutralMainCapture(Window window)
    {
        window.SetForeground();
        Thread.Sleep(180);
        Mouse.Click(GetWindowRelativePoint(window, 0.50, 0.90));
        Thread.Sleep(180);
        MoveMouseToNeutralPoint();
    }

    private static void HideFloatingLanguageBars()
    {
        try
        {
            foreach (var hwnd in EnumerateFloatingLanguageBars())
            {
                ShowWindow(hwnd, ShowWindowHide);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Screenshot parity should not fail just because the TSF language bar cannot be enumerated.
        }

        Thread.Sleep(80);
    }

    private static Rectangle? FindFloatingLanguageBarBounds()
    {
        try
        {
            foreach (var hwnd in EnumerateFloatingLanguageBars())
            {
                if (!IsWindowVisible(hwnd))
                {
                    continue;
                }

                if (TryGetNativeWindowRectangle(hwnd) is { } bounds &&
                    bounds.Width > 0 &&
                    bounds.Height > 0)
                {
                    return bounds;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    private static IReadOnlyList<IntPtr> EnumerateFloatingLanguageBars()
    {
        var handles = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowClassName(hwnd).Equals("CiceroUIWndFrame", StringComparison.Ordinal) &&
                GetWindowTitle(hwnd).Equals("TF_FloatingLangBar_WndTitle", StringComparison.Ordinal))
            {
                handles.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        return handles;
    }

    private static void MaskFloatingLanguageBarOcclusions(string screenshotPath, Window capturedWindow)
    {
        try
        {
            var windowBounds = ScreenshotHelper.GetWindowPhysicalBounds(capturedWindow);
            if (windowBounds.Width <= 0 || windowBounds.Height <= 0)
            {
                return;
            }

            using var stream = new MemoryStream(File.ReadAllBytes(screenshotPath));
            using var bitmap = new Bitmap(stream);
            var changed = false;

            foreach (var hwnd in EnumerateFloatingLanguageBars())
            {
                if (!IsWindowVisible(hwnd) ||
                    TryGetNativeWindowRectangle(hwnd) is not { } languageBarBounds)
                {
                    continue;
                }

                var overlap = Rectangle.Intersect(windowBounds, languageBarBounds);
                if (overlap.Width <= 0 || overlap.Height <= 0)
                {
                    continue;
                }

                var local = new Rectangle(
                    overlap.Left - windowBounds.Left,
                    overlap.Top - windowBounds.Top,
                    overlap.Width,
                    overlap.Height);
                MaskOcclusionWithNeighborPixels(bitmap, local);
                changed = true;
            }

            if (changed)
            {
                using var output = new MemoryStream();
                bitmap.Save(output, ImageFormat.Png);
                File.WriteAllBytes(screenshotPath, output.ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or ExternalException or ArgumentException or COMException)
        {
            // External language bar masking is best-effort; the screenshot remains usable without it.
        }
    }

    private static void MaskOcclusionWithNeighborPixels(Bitmap bitmap, Rectangle local)
    {
        var left = Math.Clamp(local.Left, 0, bitmap.Width);
        var top = Math.Clamp(local.Top, 0, bitmap.Height);
        var right = Math.Clamp(local.Right, 0, bitmap.Width);
        var bottom = Math.Clamp(local.Bottom, 0, bitmap.Height);
        if (left >= right || top >= bottom)
        {
            return;
        }

        for (var y = top; y < bottom; y++)
        {
            var sampleX = left > 1 ? left - 2 : Math.Min(right + 1, bitmap.Width - 1);
            var sample = bitmap.GetPixel(sampleX, y);
            for (var x = left; x < right; x++)
            {
                bitmap.SetPixel(x, y, sample);
            }
        }
    }

    private static Rectangle? TryGetNativeWindowRectangle(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        return GetClassName(hwnd, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        return GetWindowText(hwnd, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private static void MoveMouseToHoverTarget(
        Window window,
        string automationIdOrName,
        double fallbackX,
        double fallbackY)
    {
        window.SetForeground();
        Thread.Sleep(180);

        var target = FindVisibleByAutomationIdOrName(window, automationIdOrName);
        var point = TryGetClickablePoint(target)
            ?? GetWindowRelativePoint(window, fallbackX, fallbackY);

        SetCursorPos(point.X, point.Y);
        Thread.Sleep(500);
    }

    private static void FocusElement(
        Window window,
        string automationIdOrName,
        double fallbackX,
        double fallbackY)
    {
        window.SetForeground();
        Thread.Sleep(180);

        var target = Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, automationIdOrName),
                TimeSpan.FromSeconds(4))
            .Result;

        if (target != null)
        {
            try
            {
                target.Focus();
                Thread.Sleep(500);
                return;
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
            {
            }

            var clickablePoint = TryGetClickablePoint(target);
            if (clickablePoint != null)
            {
                Mouse.Click(clickablePoint.Value);
                Thread.Sleep(500);
                return;
            }
        }

        Mouse.Click(GetWindowRelativePoint(window, fallbackX, fallbackY));
        Thread.Sleep(500);
    }

    private static string CapturePressedWindow(
        Window window,
        string automationIdOrName,
        double fallbackX,
        double fallbackY,
        string screenshotName,
        bool requireForeground = true)
    {
        window.SetForeground();
        Thread.Sleep(180);
        MoveMouseToHoverTarget(window, automationIdOrName, fallbackX, fallbackY);
        Mouse.Down(MouseButton.Left);
        try
        {
            Thread.Sleep(180);
            return CaptureWindowPreferHwnd(window, screenshotName, requireForeground);
        }
        finally
        {
            MoveMouseToNeutralPoint();
            Thread.Sleep(80);
            Mouse.Up(MouseButton.Left);
            Thread.Sleep(180);
        }
    }

    private static string CaptureWindowPreferHwnd(
        Window window,
        string screenshotName,
        bool requireForeground = false)
    {
        var hwnd = SafeNativeWindowHandle(window);
        var path = requireForeground || hwnd == IntPtr.Zero
            ? ScreenshotHelper.CaptureWindow(window, screenshotName)
            : ScreenshotHelper.CaptureWindowHandlePhysical(hwnd, screenshotName);
        MaskFloatingLanguageBarOcclusions(path, window);
        return path;
    }

    private static Point? TryGetClickablePoint(AutomationElement? element)
    {
        if (element == null)
        {
            return null;
        }

        try
        {
            return element.GetClickablePoint();
        }
        catch (Exception ex) when (ex is COMException or NoClickablePointException or PropertyNotSupportedException or TimeoutException)
        {
            return null;
        }
    }

    private static Point? TryGetElementCenterPoint(AutomationElement? element)
    {
        if (element == null)
        {
            return null;
        }

        try
        {
            var bounds = element.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            return new Point(
                bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height / 2);
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return null;
        }
    }

    private static Point GetWindowRelativePoint(Window window, double x, double y)
    {
        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        return new Point(
            bounds.Left + (int)Math.Round(bounds.Width * Math.Clamp(x, 0d, 1d)),
            bounds.Top + (int)Math.Round(bounds.Height * Math.Clamp(y, 0d, 1d)));
    }

    private static AutomationElement? FindVisibleByAutomationIdOrName(Window window, string automationIdOrName)
    {
        var element = UITestHelper.FindByAutomationIdOrName(window, automationIdOrName);
        return element != null && IsOnScreenOrUnknown(element)
            ? element
            : null;
    }

    private static AutomationElement? FindVisibleExpandableByAutomationIdOrName(Window window, string automationIdOrName)
    {
        try
        {
            foreach (var element in window.FindAllDescendants().Where(IsOnScreenOrUnknown))
            {
                var expandPattern = element.Patterns.ExpandCollapse.PatternOrDefault;
                if (expandPattern == null)
                {
                    continue;
                }

                if (ElementMatchesAutomationIdOrName(element, automationIdOrName) ||
                    VisibleDescendantMatchesAutomationIdOrName(element, automationIdOrName))
                {
                    return element;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return FindVisibleByAutomationIdOrName(window, automationIdOrName);
        }

        return FindVisibleByAutomationIdOrName(window, automationIdOrName);
    }

    private static bool VisibleDescendantMatchesAutomationIdOrName(
        AutomationElement element,
        string automationIdOrName)
    {
        try
        {
            return element
                .FindAllDescendants()
                .Where(IsOnScreenOrUnknown)
                .Any(descendant => ElementMatchesAutomationIdOrName(descendant, automationIdOrName));
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return false;
        }
    }

    private static bool ElementMatchesAutomationIdOrName(
        AutomationElement element,
        string automationIdOrName)
    {
        return string.Equals(SafeElementAutomationId(element), automationIdOrName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(SafeElementName(element), automationIdOrName, StringComparison.OrdinalIgnoreCase);
    }

    private static AutomationElement? FindVisibleByAutomationId(Window window, string automationId)
    {
        try
        {
            var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return element != null && IsOnScreenOrUnknown(element)
                ? element
                : null;
        }
        catch (Exception ex) when (ex is COMException or PropertyNotSupportedException or TimeoutException)
        {
            return null;
        }
    }

    private static bool IsOnScreenOrUnknown(AutomationElement element)
    {
        try
        {
            return !element.IsOffscreen;
        }
        catch (PropertyNotSupportedException)
        {
            return true;
        }
    }

    private static string SafeElementName(AutomationElement element)
    {
        try
        {
            return element.Name ?? string.Empty;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return string.Empty;
        }
    }

    private static string SafeElementAutomationId(AutomationElement element)
    {
        try
        {
            return element.AutomationId ?? string.Empty;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return string.Empty;
        }
    }

    private static string SafeElementIsOffscreen(AutomationElement element)
    {
        try
        {
            return element.IsOffscreen.ToString();
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return "unknown";
        }
    }

    private static void ArrangeSideBySide(Window dotnetWindow, Window rustWindow)
    {
        var screen = ScreenshotHelper.GetVirtualScreenBounds();
        var availableWidth = Math.Max(1280, screen.Width);
        var width = Math.Min(860, Math.Max(560, (availableWidth - 72) / 2));
        var height = Math.Min(920, Math.Max(680, screen.Height - 90));
        var top = screen.Top + 30;
        var left = screen.Left + 24;

        TrySetWindowToPhysicalTarget(dotnetWindow, new Rectangle(left, top, width, height));
        Thread.Sleep(250);

        var dotnetBounds = ScreenshotHelper.GetWindowPhysicalBounds(dotnetWindow);
        var rustWidth = dotnetBounds.Width > 0 ? dotnetBounds.Width : width;
        var rustHeight = dotnetBounds.Height > 0 ? dotnetBounds.Height : height;
        var rustLeft = dotnetBounds.Width > 0 ? dotnetBounds.Right + 24 : left + width + 24;
        var rustTop = dotnetBounds.Height > 0 ? dotnetBounds.Top : top;
        if (rustLeft + rustWidth > screen.Right || rustTop + rustHeight > screen.Bottom)
        {
            rustLeft = ClampWindowStart(left, rustWidth, screen.Left, screen.Right);
            rustTop = ClampWindowStart(top, rustHeight, screen.Top, screen.Bottom);
        }

        TrySetWindowToPhysicalTarget(rustWindow, new Rectangle(rustLeft, rustTop, rustWidth, rustHeight));
        Thread.Sleep(600);
    }

    private static void ArrangeSettingsWindowsForCapture(Window dotnetWindow, Window rustWindow)
    {
        // Settings parity cares about absolute dimensions. Prefer side-by-side
        // placement at the real 846x913 target when the desktop can fit it; on
        // constrained desktops fall back to same-position foreground capture.
        if (TryArrangeSettingsWindowsSideBySide(dotnetWindow, rustWindow))
        {
            return;
        }

        ArrangeSettingsWindowForCapture(dotnetWindow);
        Thread.Sleep(250);

        ArrangeSettingsWindowForCapture(rustWindow);
        Thread.Sleep(600);
    }

    private static bool TryArrangeSettingsWindowsSideBySide(Window dotnetWindow, Window rustWindow)
    {
        const double settingsWidthDips = 846;
        const double settingsHeightDips = 913;
        const int leftMargin = 24;
        const int topMargin = 30;
        const int gap = 16;

        var screen = ScreenshotHelper.GetVirtualScreenBounds();
        var dotnetWidth = DipsToPhysicalPixels(settingsWidthDips, ScreenshotHelper.GetWindowDpiScale(dotnetWindow));
        var dotnetHeight = DipsToPhysicalPixels(settingsHeightDips, ScreenshotHelper.GetWindowDpiScale(dotnetWindow));
        var rustWidth = DipsToPhysicalPixels(settingsWidthDips, ScreenshotHelper.GetWindowDpiScale(rustWindow));
        var rustHeight = DipsToPhysicalPixels(settingsHeightDips, ScreenshotHelper.GetWindowDpiScale(rustWindow));

        if (screen.Width < leftMargin + dotnetWidth + gap + rustWidth ||
            screen.Height < topMargin + Math.Max(dotnetHeight, rustHeight))
        {
            return false;
        }

        var top = screen.Top + topMargin;
        var pairWidth = dotnetWidth + gap + rustWidth;
        var pairLeft = screen.Left + leftMargin;
        if (FindFloatingLanguageBarBounds() is { } languageBarBounds &&
            LanguageBarIntersectsSettingsPair(
                languageBarBounds,
                pairLeft,
                top,
                dotnetWidth,
                dotnetHeight,
                gap,
                rustWidth,
                rustHeight))
        {
            var languageBarCenterX = languageBarBounds.Left + (languageBarBounds.Width / 2);
            pairLeft = ClampWindowStart(
                languageBarCenterX - dotnetWidth - (gap / 2),
                pairWidth,
                screen.Left + leftMargin,
                screen.Right - leftMargin);
        }

        var dotnetTarget = new Rectangle(pairLeft, top, dotnetWidth, dotnetHeight);
        var rustTarget = new Rectangle(dotnetTarget.Right + gap, top, rustWidth, rustHeight);
        TrySetWindowToPhysicalTargetWithFrameCompensation(dotnetWindow, dotnetTarget);
        Thread.Sleep(250);
        TrySetWindowToPhysicalTargetWithFrameCompensation(rustWindow, rustTarget);
        Thread.Sleep(600);
        return true;
    }

    private static void ArrangeSettingsWindowForCapture(Window window)
    {
        const double settingsWidthDips = 846;
        const double settingsHeightDips = 913;

        var screen = ScreenshotHelper.GetVirtualScreenBounds();
        var target = ClampPhysicalTargetToScreen(
            new Rectangle(
                screen.Left + 24,
                screen.Top + 30,
                DipsToPhysicalPixels(settingsWidthDips, ScreenshotHelper.GetWindowDpiScale(window)),
                DipsToPhysicalPixels(settingsHeightDips, ScreenshotHelper.GetWindowDpiScale(window))),
            screen);
        TrySetWindowToPhysicalTargetWithFrameCompensation(window, target);
    }

    private static bool LanguageBarIntersectsSettingsPair(
        Rectangle languageBarBounds,
        int pairLeft,
        int top,
        int dotnetWidth,
        int dotnetHeight,
        int gap,
        int rustWidth,
        int rustHeight)
    {
        if (languageBarBounds.Width <= 0 || languageBarBounds.Height <= 0)
        {
            return false;
        }

        var dotnetTarget = new Rectangle(pairLeft, top, dotnetWidth, dotnetHeight);
        var rustTarget = new Rectangle(pairLeft + dotnetWidth + gap, top, rustWidth, rustHeight);
        return languageBarBounds.IntersectsWith(dotnetTarget) ||
               languageBarBounds.IntersectsWith(rustTarget);
    }

    private static void ArrangeFloatingSideBySide(
        Window dotnetWindow,
        Window rustWindow,
        int targetWidth,
        int targetHeight,
        Size? rustPreferredSize = null)
    {
        var screen = ScreenshotHelper.GetVirtualScreenBounds();
        var gap = 28;
        var width = Math.Min(targetWidth, Math.Max(280, (screen.Width - gap - 48) / 2));
        var height = Math.Min(targetHeight, Math.Max(200, screen.Height - 120));
        var left = screen.Left + 32;
        var top = screen.Top + 80;

        TrySetWindowToPhysicalTarget(dotnetWindow, new Rectangle(left, top, width, height));
        Thread.Sleep(250);

        var dotnetBounds = ScreenshotHelper.GetWindowPhysicalBounds(dotnetWindow);
        var preferredRustSize = rustPreferredSize.GetValueOrDefault();
        var rustWidth = preferredRustSize.Width > 0
            ? preferredRustSize.Width
            : dotnetBounds.Width > 0
                ? dotnetBounds.Width
                : width;
        var rustHeight = preferredRustSize.Height > 0
            ? preferredRustSize.Height
            : dotnetBounds.Height > 0
                ? dotnetBounds.Height
                : height;
        var rustTop = dotnetBounds.Height > 0 ? dotnetBounds.Top : top;
        var rustLeft = dotnetBounds.Width > 0 ? dotnetBounds.Right + gap : left + width + gap;
        if (rustLeft + rustWidth > screen.Right || rustTop + rustHeight > screen.Bottom)
        {
            rustLeft = ClampWindowStart(left, rustWidth, screen.Left, screen.Right);
            rustTop = ClampWindowStart(top, rustHeight, screen.Top, screen.Bottom);
        }

        TrySetWindowToPhysicalTarget(rustWindow, new Rectangle(rustLeft, rustTop, rustWidth, rustHeight));
        Thread.Sleep(600);
    }

    private static int ClampWindowStart(int preferred, int size, int min, int max)
    {
        if (max <= min || size >= max - min)
        {
            return min;
        }

        return Math.Min(Math.Max(preferred, min), max - size);
    }

    private static Rectangle ClampPhysicalTargetToScreen(Rectangle preferred, Rectangle screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0)
        {
            return preferred;
        }

        var width = Math.Min(preferred.Width, screen.Width);
        var height = Math.Min(preferred.Height, screen.Height);
        return new Rectangle(
            ClampWindowStart(preferred.Left, width, screen.Left, screen.Right),
            ClampWindowStart(preferred.Top, height, screen.Top, screen.Bottom),
            width,
            height);
    }

    private static int DipsToPhysicalPixels(double dips, double dpiScale) =>
        (int)Math.Round(dips * Math.Max(0.001, dpiScale));

    private static void AddRustPreviewSizeEnvironment(
        IDictionary<string, string> environment,
        Size referencePixels,
        double referenceDpiScale)
    {
        if (referencePixels.Width <= 0 || referencePixels.Height <= 0 || referenceDpiScale <= 0)
        {
            return;
        }

        environment["EASYDICT_PREVIEW_WIDTH_DIPS"] =
            (referencePixels.Width / referenceDpiScale).ToString("0.###", CultureInfo.InvariantCulture);
        environment["EASYDICT_PREVIEW_HEIGHT_DIPS"] =
            (referencePixels.Height / referenceDpiScale).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static Size ReadScreenshotPixelSize(string path)
    {
        try
        {
            using var image = Image.FromFile(path);
            return new Size(image.Width, image.Height);
        }
        catch
        {
            return Size.Empty;
        }
    }

    private void ConfigureRustMainPreviewSizeFromReference(Window referenceWindow)
    {
        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(referenceWindow);
        var scale = ScreenshotHelper.GetWindowDpiScale(referenceWindow);
        if (bounds.Width <= 0 || bounds.Height <= 0 || scale <= 0)
        {
            return;
        }

        var widthDips = bounds.Width / scale;
        var heightDips = bounds.Height / scale;
        Environment.SetEnvironmentVariable(
            "EASYDICT_PREVIEW_WIDTH_DIPS",
            widthDips.ToString("0.###", CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(
            "EASYDICT_PREVIEW_HEIGHT_DIPS",
            heightDips.ToString("0.###", CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(
            "EASYDICT_PREVIEW_DPI",
            (scale * 96.0).ToString("0.###", CultureInfo.InvariantCulture));
        _output.WriteLine(
            $"Rust preview initial main size: {widthDips:0.###}x{heightDips:0.###} DIP from reference window {bounds.Width}x{bounds.Height}px @ {scale:0.###}x.");
    }

    private static void TrySetWindowToPhysicalTarget(Window window, Rectangle physicalTarget)
    {
        TryRestoreWindow(window);

        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        var requestedBounds = new Rectangle(
            (int)Math.Round(physicalTarget.Left / dpiScale),
            (int)Math.Round(physicalTarget.Top / dpiScale),
            (int)Math.Round(physicalTarget.Width / dpiScale),
            (int)Math.Round(physicalTarget.Height / dpiScale));

        ScreenshotHelper.TrySetWindowPhysicalBounds(window, requestedBounds);
        Thread.Sleep(120);

        var logicalAttemptBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        if (WindowSizeDistance(logicalAttemptBounds, physicalTarget) <= 8)
        {
            return;
        }

        ScreenshotHelper.TrySetWindowPhysicalBounds(window, physicalTarget);
        Thread.Sleep(120);

        var physicalAttemptBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        if (WindowSizeDistance(physicalAttemptBounds, physicalTarget) >
            WindowSizeDistance(logicalAttemptBounds, physicalTarget))
        {
            ScreenshotHelper.TrySetWindowPhysicalBounds(window, requestedBounds);
            Thread.Sleep(120);
        }
    }

    private static void TrySetWindowToPhysicalTargetWithFrameCompensation(
        Window window,
        Rectangle physicalTarget)
    {
        TrySetWindowToPhysicalTarget(window, physicalTarget);

        var actual = ScreenshotHelper.GetWindowPhysicalBounds(window);
        if (actual.Width <= 0 || actual.Height <= 0 ||
            WindowSizeDistance(actual, physicalTarget) <= 8)
        {
            return;
        }

        var adjusted = new Rectangle(
            physicalTarget.Left - (actual.Left - physicalTarget.Left),
            physicalTarget.Top - (actual.Top - physicalTarget.Top),
            physicalTarget.Width + (physicalTarget.Width - actual.Width),
            physicalTarget.Height + (physicalTarget.Height - actual.Height));

        ScreenshotHelper.TrySetWindowPhysicalBounds(window, adjusted);
        Thread.Sleep(160);
    }

    private static int WindowSizeDistance(Rectangle actual, Rectangle target) =>
        Math.Abs(actual.Width - target.Width) + Math.Abs(actual.Height - target.Height);

    private static void TryRestoreWindow(Window window)
    {
        var hwnd = SafeNativeWindowHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(hwnd, ShowWindowRestore);
        Thread.Sleep(80);
    }

    private static void AssertWindowFullyVisible(Window window, string stepKey, string label)
    {
        TryMoveWindowIntoBestVisiblePosition(window);

        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var screen = ScreenshotHelper.GetVirtualScreenBounds();
        var visible = Rectangle.Intersect(bounds, screen);
        if (IsTruthy(Environment.GetEnvironmentVariable(AllowOversizedCaptureEnvironmentVariable)) &&
            (bounds.Width > screen.Width || bounds.Height > screen.Height) &&
            visible.Width > Math.Min(bounds.Width, screen.Width) * 0.90 &&
            visible.Height > Math.Min(bounds.Height, screen.Height) * 0.90)
        {
            return;
        }

        visible.Width.Should().BeGreaterThan(
            bounds.Width - 16,
            $"{stepKey} {label} window should be fully visible before capture");
        visible.Height.Should().BeGreaterThan(
            bounds.Height - 16,
            $"{stepKey} {label} window should be fully visible before capture");
    }

    private static void AssertNoUnexpectedTopLevelErrorWindows(string context)
    {
        var windows = EnumerateUnexpectedTopLevelErrorWindows();
        windows.Should().BeEmpty(
            $"{context} should not have an unrelated top-level system error dialog covering the UI capture");
    }

    private static IReadOnlyList<string> EnumerateUnexpectedTopLevelErrorWindows()
    {
        var windows = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd).Trim();
            if (IsUnexpectedTopLevelErrorWindowTitle(title))
            {
                windows.Add($"{title} (hwnd=0x{hwnd.ToInt64():X})");
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static bool IsUnexpectedTopLevelErrorWindowTitle(string title) =>
        !string.IsNullOrWhiteSpace(title) &&
        (title.Contains("Application Error", StringComparison.OrdinalIgnoreCase) ||
         title.Contains("应用程序错误", StringComparison.OrdinalIgnoreCase) ||
         title.Contains("应用程序无法正常启动", StringComparison.OrdinalIgnoreCase));

    private static void TryMoveWindowIntoBestVisiblePosition(Window window)
    {
        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var screen = ScreenshotHelper.GetVirtualScreenBounds();
        if (screen.Width <= 0 || screen.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var targetLeft = bounds.Width <= screen.Width
            ? Math.Min(Math.Max(bounds.Left, screen.Left), screen.Right - bounds.Width)
            : screen.Left;
        var targetTop = bounds.Height <= screen.Height
            ? Math.Min(Math.Max(bounds.Top, screen.Top), screen.Bottom - bounds.Height)
            : screen.Top;

        if (targetLeft == bounds.Left && targetTop == bounds.Top)
        {
            return;
        }

        if (ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(targetLeft, targetTop, bounds.Width, bounds.Height)))
        {
            Thread.Sleep(300);
        }
    }

    private static string CaptureForegroundWindow(Window window, string name)
    {
        EnsureWindowForegroundForMouseInput(window, name);
        return CaptureWindowPreferHwnd(window, name);
    }

    private static string SaveSideBySideComparison(string dotnetPath, string rustPath, string name)
    {
        using var dotnet = new Bitmap(dotnetPath);
        using var rust = new Bitmap(rustPath);

        const int labelHeight = 34;
        const int gap = 16;
        var width = dotnet.Width + gap + rust.Width;
        var height = labelHeight + Math.Max(dotnet.Height, rust.Height);

        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(canvas);
        using var font = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(Color.FromArgb(32, 32, 32));
        using var background = new SolidBrush(Color.White);

        graphics.FillRectangle(background, new Rectangle(0, 0, width, height));
        graphics.DrawString("dotnet / WinUI reference", font, brush, new PointF(8, 8));
        graphics.DrawString("rust / win_fluent iced", font, brush, new PointF(dotnet.Width + gap + 8, 8));
        graphics.DrawImage(dotnet, 0, labelHeight, dotnet.Width, dotnet.Height);
        graphics.DrawImage(rust, dotnet.Width + gap, labelHeight, rust.Width, rust.Height);

        var outputPath = Path.Combine(ScreenshotHelper.OutputDir, $"{SanitizeFileName(name)}.png");
        canvas.Save(outputPath, ImageFormat.Png);
        return outputPath;
    }

    private TrayMenuCaptureResult CaptureTrayMenuPair(
        string scenarioId,
        string scenarioLabel,
        string dotnetScreenshotName,
        string rustScreenshotName,
        string sideBySideScreenshotName,
        Point anchor,
        string anchorValue,
        int extraItemCount,
        int? maxHeightDips)
    {
        var extraItemsValue = extraItemCount > 0
            ? extraItemCount.ToString(CultureInfo.InvariantCulture)
            : null;
        var maxHeightValue = maxHeightDips.HasValue
            ? maxHeightDips.Value.ToString(CultureInfo.InvariantCulture)
            : null;

        string dotnetMenuPath;
        UiParityWindowManifest dotnetMenuManifest;
        using (new EnvironmentVariableScope(TrayContextMenuPointEnvironmentVariable, anchorValue))
        using (new EnvironmentVariableScope(TrayContextMenuDelayEnvironmentVariable, "2200"))
        using (new EnvironmentVariableScope(TrayExtraItemsEnvironmentVariable, extraItemsValue))
        using (new EnvironmentVariableScope(TrayMaxHeightEnvironmentVariable, maxHeightValue))
        using (var dotnetLauncher = new AppLauncher())
        {
            dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
            var dotnetWindow = dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));
            TriggerDotnetTrayContextMenu(dotnetWindow, anchor);
            var dotnetMenuHwnd = WaitForTrayMenuWindow(
                dotnetLauncher.Application.ProcessId,
                anchor,
                [SafeNativeWindowHandle(dotnetWindow)],
                $"{scenarioId}.dotnet");
            dotnetMenuPath = ScreenshotHelper.CaptureWindowHandlePhysical(
                dotnetMenuHwnd,
                dotnetScreenshotName);
            dotnetMenuManifest = CaptureWindowManifest(dotnetMenuHwnd);
            DismissTrayMenu();
        }
        using var trayDpiScope = new EnvironmentVariableScope(
            "EASYDICT_PREVIEW_DPI",
            (dotnetMenuManifest.Dpi ?? 96).ToString(CultureInfo.InvariantCulture));

        var rustEnvironment = new Dictionary<string, string>
        {
            [TrayContextMenuPointEnvironmentVariable] = anchorValue,
            [TrayContextMenuDelayEnvironmentVariable] = "2200"
        };
        if (extraItemsValue != null)
        {
            rustEnvironment[TrayExtraItemsEnvironmentVariable] = extraItemsValue;
        }
        if (maxHeightValue != null)
        {
            rustEnvironment[TrayMaxHeightEnvironmentVariable] = maxHeightValue;
        }

        var rustPreview = RenderMainPreview(
            "initial",
            ResolveRustPreviewTheme("light"),
            _output,
            rustEnvironment);
        var rustWindow = rustPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        var rustMenuHwnd = WaitForRustTrayMenuWindowWithRetries(
            rustPreview.ProcessId,
            rustWindow,
            anchor,
            scenarioId);
        var rustMenuPath = ScreenshotHelper.CaptureWindowHandlePhysical(
            rustMenuHwnd,
            rustScreenshotName);
        var rustMenuManifest = CaptureWindowManifest(rustMenuHwnd);
        DismissTrayMenu();

        var sideBySidePath = SaveSideBySideComparison(
            dotnetMenuPath,
            rustMenuPath,
            sideBySideScreenshotName);
        var manifestEntry = CreateTrayMenuManifestEntry(
            scenarioId,
            scenarioLabel,
            dotnetMenuManifest,
            rustMenuManifest,
            dotnetMenuPath,
            rustMenuPath,
            sideBySidePath,
            rustPreview.DiagnosticsPath);

        return new TrayMenuCaptureResult(
            scenarioId,
            dotnetMenuPath,
            rustMenuPath,
            sideBySidePath,
            dotnetMenuManifest,
            rustMenuManifest,
            manifestEntry);
    }

    private IntPtr WaitForRustTrayMenuWindowWithRetries(
        int processId,
        Window rustWindow,
        Point anchor,
        string scenarioId)
    {
        const int maxAttempts = 3;
        var excludedHwnds = new[] { SafeNativeWindowHandle(rustWindow) };
        TimeoutException? lastTimeout = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            TriggerRustTrayContextMenu(processId, anchor);
            try
            {
                return WaitForTrayMenuWindow(
                    processId,
                    anchor,
                    excludedHwnds,
                    $"{scenarioId}.rust");
            }
            catch (TimeoutException ex) when (attempt < maxAttempts)
            {
                lastTimeout = ex;
                _output.WriteLine(
                    $"[tray-menu.{scenarioId}.rust] Context-menu attempt {attempt} did not show a popup; retrying.");
                DismissTrayMenu();
            }
        }

        throw lastTimeout ??
            new TimeoutException($"Rust tray menu popup did not appear for {scenarioId}.");
    }

    private void TriggerDotnetTrayContextMenu(Window dotnetWindow, Point anchor)
    {
        var hwnd = SafeNativeWindowHandle(dotnetWindow);
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Cannot trigger .NET tray menu because the main window HWND is unavailable.");
        }

        SetCursorPos(anchor.X, anchor.Y);
        var sent = SendMessageTimeout(
            hwnd,
            UiaShowTrayContextMenuMessage,
            IntPtr.Zero,
            MakePointLParam(anchor),
            SendMessageTimeoutAbortIfHung,
            1000,
            out _);
        if (sent == IntPtr.Zero &&
            !PostMessage(hwnd, UiaShowTrayContextMenuMessage, IntPtr.Zero, MakePointLParam(anchor)))
        {
            throw new InvalidOperationException(
                $"Failed to send or post .NET tray context-menu UIA message to HWND=0x{hwnd.ToInt64():X}; lastError={Marshal.GetLastWin32Error()}.");
        }

        _output.WriteLine($"[tray-menu.dotnet] Sent UIA tray context-menu message to HWND=0x{hwnd.ToInt64():X}");
    }

    private static Point ResolveTrayMenuAnchorPoint()
    {
        var screen = ScreenshotHelper.GetVirtualScreenBounds();
        var marginX = Math.Min(96, Math.Max(24, screen.Width / 12));
        var marginY = Math.Min(96, Math.Max(32, screen.Height / 12));
        return new Point(screen.Left + marginX, screen.Bottom - marginY);
    }

    private static IntPtr MakePointLParam(Point point)
    {
        var x = unchecked((ushort)(short)point.X);
        var y = unchecked((ushort)(short)point.Y);
        return new IntPtr(unchecked((int)(x | ((uint)y << 16))));
    }

    private void TriggerRustTrayContextMenu(int processId, Point anchor)
    {
        var trayHost = WaitForRustTrayHostWindow(processId);
        SetCursorPos(anchor.X, anchor.Y);
        var lparam = new IntPtr((1 << 16) | WM_CONTEXTMENU);
        var sent = SendMessageTimeout(
            trayHost,
            WM_USER + 1,
            IntPtr.Zero,
            lparam,
            SendMessageTimeoutAbortIfHung,
            1000,
            out _);
        if (sent == IntPtr.Zero && !PostMessage(trayHost, WM_USER + 1, IntPtr.Zero, lparam))
        {
            throw new InvalidOperationException(
                $"Failed to post Rust tray context-menu callback to HWND=0x{trayHost.ToInt64():X}; lastError={Marshal.GetLastWin32Error()}.");
        }
    }

    private IntPtr WaitForRustTrayHostWindow(int processId)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(12))
        {
            var hwnd = FindRustTrayHostWindow(processId);
            if (hwnd != IntPtr.Zero)
            {
                _output.WriteLine($"[tray-menu.rust] Tray host HWND=0x{hwnd.ToInt64():X}");
                return hwnd;
            }

            Thread.Sleep(120);
        }

        throw new TimeoutException($"Rust tray host window did not appear for process {processId}.");
    }

    private static IntPtr FindRustTrayHostWindow(int processId)
    {
        var result = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var ownerProcessId);
            if (ownerProcessId == processId &&
                GetWindowClassName(hwnd).StartsWith("WinFluentTrayHost-", StringComparison.Ordinal))
            {
                result = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private IntPtr WaitForTrayMenuWindow(
        int processId,
        Point anchor,
        IReadOnlyCollection<IntPtr> excludedHwnds,
        string label)
    {
        var stopwatch = Stopwatch.StartNew();
        var requireRustFluentPopup = label.Contains("rust", StringComparison.OrdinalIgnoreCase);
        var requireDotnetWinuiPopup = label.Contains("dotnet", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<TrayMenuWindowCandidate> lastCandidates = [];
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(8))
        {
            lastCandidates = EnumerateTrayMenuWindowCandidates(processId, anchor, excludedHwnds);
            var best = requireRustFluentPopup
                ? lastCandidates.FirstOrDefault(candidate => IsRustFluentTrayMenuCandidate(candidate, anchor))
                : requireDotnetWinuiPopup
                    ? SelectDotnetWinuiTrayMenuCandidate(lastCandidates, anchor)
                    : lastCandidates.FirstOrDefault();
            if (best != null)
            {
                _output.WriteLine($"[tray-menu.{label}] Selected {best}");
                foreach (var candidate in lastCandidates.Take(6))
                {
                    _output.WriteLine($"[tray-menu.{label}] Candidate {candidate}");
                }
                Thread.Sleep(requireRustFluentPopup ? 700 : 250);
                return ResolveTrayMenuCaptureHwnd(best, requireDotnetWinuiPopup, anchor, label);
            }

            Thread.Sleep(120);
        }

        var details = lastCandidates.Count == 0
            ? "No candidate windows were visible."
            : string.Join(Environment.NewLine, lastCandidates.Select(candidate => candidate.ToString()));
        throw new TimeoutException(
            $"Tray menu popup for {label} did not appear for process {processId} near {anchor}. {details}");
    }

    private static IReadOnlyList<TrayMenuWindowCandidate> EnumerateTrayMenuWindowCandidates(
        int processId,
        Point anchor,
        IReadOnlyCollection<IntPtr> excludedHwnds)
    {
        var excluded = excludedHwnds
            .Where(hwnd => hwnd != IntPtr.Zero)
            .ToHashSet();
        var candidates = new List<TrayMenuWindowCandidate>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero ||
                excluded.Contains(hwnd) ||
                !IsWindowVisible(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var ownerProcessId);
            if (ownerProcessId != processId)
            {
                return true;
            }

            if (TryGetNativeWindowRectangle(hwnd) is not { } bounds ||
                bounds.Width < 140 ||
                bounds.Height < 120 ||
                bounds.Width > 900 ||
                bounds.Height > ScreenshotHelper.GetVirtualScreenBounds().Height + 120)
            {
                return true;
            }

            var className = GetWindowClassName(hwnd);
            if (string.Equals(className, "SysShadow", StringComparison.Ordinal))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            var score = ScoreTrayMenuCandidate(bounds, anchor, className, title);
            if (score > 0)
            {
                candidates.Add(new TrayMenuWindowCandidate(hwnd, className, title, bounds, score));
            }

            return true;
        }, IntPtr.Zero);

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => Math.Abs(candidate.Bounds.Left - anchor.X) + Math.Abs(candidate.Bounds.Top - anchor.Y))
            .ToArray();
    }

    private static bool IsRustFluentTrayMenuCandidate(
        TrayMenuWindowCandidate candidate,
        Point anchor)
    {
        if (string.Equals(candidate.Title, RustFluentTrayMenuWindowTitle, StringComparison.Ordinal))
        {
            return DistanceFromPointToRectangle(anchor, candidate.Bounds) <= 180;
        }

        return !string.Equals(candidate.ClassName, "#32768", StringComparison.Ordinal) &&
            candidate.Bounds.Width is >= 240 and <= 760 &&
            candidate.Bounds.Height is >= 180 and <= 760 &&
            DistanceFromPointToRectangle(anchor, candidate.Bounds) <= 160;
    }

    private static TrayMenuWindowCandidate? SelectDotnetWinuiTrayMenuCandidate(
        IReadOnlyList<TrayMenuWindowCandidate> candidates,
        Point anchor)
    {
        return candidates.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.ClassName,
                    "Microsoft.UI.Content.PopupWindowSiteBridge",
                    StringComparison.Ordinal) &&
                IsDotnetWinuiTrayMenuCandidate(candidate, anchor)) ??
            candidates.FirstOrDefault(candidate =>
                IsDotnetWinuiTrayMenuContentCandidate(candidate) &&
                DistanceFromPointToRectangle(anchor, candidate.Bounds) <= 160) ??
            candidates.FirstOrDefault(candidate => IsDotnetWinuiTrayMenuCandidate(candidate, anchor));
    }

    private IntPtr ResolveTrayMenuCaptureHwnd(
        TrayMenuWindowCandidate candidate,
        bool preferDotnetWinuiContentHwnd,
        Point anchor,
        string label)
    {
        _ = preferDotnetWinuiContentHwnd;
        _ = anchor;
        _ = label;
        return candidate.Hwnd;
    }

    private static TrayMenuWindowCandidate? FindDotnetWinuiTrayMenuContentCandidate(
        TrayMenuWindowCandidate candidate,
        Point anchor)
    {
        if (string.Equals(candidate.ClassName, "WinUIDesktopWin32WindowClass", StringComparison.Ordinal))
        {
            return candidate;
        }

        if (!string.Equals(
                candidate.ClassName,
                "Microsoft.UI.Content.PopupWindowSiteBridge",
                StringComparison.Ordinal))
        {
            return null;
        }

        return EnumerateChildTrayMenuWindowCandidates(candidate.Hwnd, candidate.Bounds, anchor)
            .FirstOrDefault(IsDotnetWinuiTrayMenuContentCandidate);
    }

    private static IReadOnlyList<TrayMenuWindowCandidate> EnumerateChildTrayMenuWindowCandidates(
        IntPtr parentHwnd,
        Rectangle parentBounds,
        Point anchor)
    {
        var candidates = new List<TrayMenuWindowCandidate>();
        var parentCaptureBounds = Rectangle.Inflate(parentBounds, 8, 8);
        EnumChildWindows(parentHwnd, (hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero ||
                !IsWindowVisible(hwnd) ||
                TryGetNativeWindowRectangle(hwnd) is not { } bounds ||
                bounds.Width < 120 ||
                bounds.Height < 120 ||
                !parentCaptureBounds.IntersectsWith(bounds))
            {
                return true;
            }

            var className = GetWindowClassName(hwnd);
            var title = GetWindowTitle(hwnd);
            var score = ScoreTrayMenuCandidate(bounds, anchor, className, title);
            if (string.Equals(className, "WinUIDesktopWin32WindowClass", StringComparison.Ordinal))
            {
                score += 120;
            }
            else if (className.Contains("Xaml", StringComparison.OrdinalIgnoreCase) ||
                     className.Contains("Island", StringComparison.OrdinalIgnoreCase) ||
                     className.Contains("Popup", StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }

            if (score > 0)
            {
                candidates.Add(new TrayMenuWindowCandidate(hwnd, className, title, bounds, score));
            }

            return true;
        }, IntPtr.Zero);

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Bounds.Width * candidate.Bounds.Height)
            .ToArray();
    }

    private static bool IsDotnetWinuiTrayMenuContentCandidate(TrayMenuWindowCandidate candidate)
    {
        return candidate.Bounds.Width >= 140 &&
            candidate.Bounds.Height >= 180 &&
            (string.Equals(candidate.ClassName, "WinUIDesktopWin32WindowClass", StringComparison.Ordinal) ||
             candidate.ClassName.Contains("Xaml", StringComparison.OrdinalIgnoreCase) ||
             candidate.ClassName.Contains("Island", StringComparison.OrdinalIgnoreCase) ||
             candidate.ClassName.Contains("Popup", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDotnetWinuiTrayMenuCandidate(
        TrayMenuWindowCandidate candidate,
        Point anchor)
    {
        if (string.Equals(
                candidate.ClassName,
                "Microsoft.UI.Content.PopupWindowSiteBridge",
                StringComparison.Ordinal))
        {
            return candidate.Bounds.Width >= 180 &&
                candidate.Bounds.Height >= 240;
        }

        return string.Equals(candidate.ClassName, "WinUIDesktopWin32WindowClass", StringComparison.Ordinal) &&
            candidate.Bounds.Width >= 180 &&
            candidate.Bounds.Height >= 240 &&
            DistanceFromPointToRectangle(anchor, candidate.Bounds) <= 160;
    }

    private static int ScoreTrayMenuCandidate(
        Rectangle bounds,
        Point anchor,
        string className,
        string title)
    {
        var padded = Rectangle.Inflate(bounds, 24, 24);
        var distance = DistanceFromPointToRectangle(anchor, bounds);
        var score = 0;

        if (padded.Contains(anchor))
        {
            score += 100;
        }
        else if (distance <= 140)
        {
            score += 60;
        }

        if (string.Equals(className, "#32768", StringComparison.Ordinal))
        {
            score += 60;
        }
        else if (string.Equals(title, RustFluentTrayMenuWindowTitle, StringComparison.Ordinal))
        {
            score += 80;
        }
        else if (className.Contains("Popup", StringComparison.OrdinalIgnoreCase) ||
                 className.Contains("Flyout", StringComparison.OrdinalIgnoreCase) ||
                 className.Contains("Island", StringComparison.OrdinalIgnoreCase) ||
                 className.Contains("Xaml", StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }

        if (bounds.Width is >= 240 and <= 560)
        {
            score += 20;
        }

        if (bounds.Height is >= 300 and <= 700)
        {
            score += 20;
        }

        return score;
    }

    private static int DistanceFromPointToRectangle(Point point, Rectangle rectangle)
    {
        var dx = point.X < rectangle.Left
            ? rectangle.Left - point.X
            : point.X > rectangle.Right
                ? point.X - rectangle.Right
                : 0;
        var dy = point.Y < rectangle.Top
            ? rectangle.Top - point.Y
            : point.Y > rectangle.Bottom
                ? point.Y - rectangle.Bottom
                : 0;
        return dx + dy;
    }

    private static void DismissTrayMenu()
    {
        try
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }
        catch
        {
            // Best-effort cleanup; the next capture launches an isolated menu.
        }

        Thread.Sleep(300);
    }

    private static void AssertTrayMenuSizeAligned(
        UiParityWindowManifest reference,
        UiParityWindowManifest candidate)
    {
        var referenceWidthDips = reference.Bounds.Width / Math.Max(0.001, reference.DpiScale);
        var referenceHeightDips = reference.Bounds.Height / Math.Max(0.001, reference.DpiScale);
        var candidateWidthDips = candidate.Bounds.Width / Math.Max(0.001, candidate.DpiScale);
        var candidateHeightDips = candidate.Bounds.Height / Math.Max(0.001, candidate.DpiScale);

        Math.Abs(candidateWidthDips - referenceWidthDips).Should().BeLessThanOrEqualTo(
            36,
            "Rust tray menu width should track the WinUI reference");
        Math.Abs(candidateHeightDips - referenceHeightDips).Should().BeLessThanOrEqualTo(
            56,
            "Rust tray menu height should track the WinUI reference");
    }

    private static void AssertTrayMenuCapture(
        TrayMenuCaptureResult capture,
        bool expectScrolling,
        int extraItemCount,
        int? maxHeightDips)
    {
        AssertImageHasVisibleContent(capture.DotnetScreenshot);
        AssertImageHasVisibleContent(capture.RustScreenshot);
        AssertImageHasVisibleContent(capture.SideBySideScreenshot);
        AssertTrayMenuSizeAligned(capture.DotnetManifest, capture.RustManifest);
        AssertTrayMenuSurfaceColorAligned(capture.DotnetScreenshot, capture.RustScreenshot);
        if (!expectScrolling)
        {
            AssertTrayMenuSeparatorsVisible(capture.DotnetScreenshot, capture.RustScreenshot);
        }

        if (expectScrolling)
        {
            maxHeightDips.Should().NotBeNull("scrolling tray-menu capture must define a max height");
            AssertTrayMenuScrollConstrained(
                capture.DotnetManifest,
                capture.RustManifest,
                extraItemCount,
                maxHeightDips!.Value);
        }
    }

    private static IReadOnlyList<TrayMenuFluentAuditRound> AnalyzeTrayMenuFluentAuditRounds(
        TrayMenuCaptureResult standard)
    {
        var rounds = new List<TrayMenuFluentAuditRound>(TrayMenuFluentAuditRoundCount);
        for (var round = 1; round <= TrayMenuFluentAuditRoundCount; round++)
        {
            rounds.Add(AnalyzeTrayMenuFluentAuditRound(
                round,
                standard,
                expectScrolling: false,
                extraItemCount: 0,
                maxHeightDips: null));
        }

        return rounds;
    }

    private static TrayMenuFluentAuditRound AnalyzeTrayMenuFluentAuditRound(
        int round,
        TrayMenuCaptureResult capture,
        bool expectScrolling,
        int extraItemCount,
        int? maxHeightDips)
    {
        var referenceWidthDips = capture.DotnetManifest.Bounds.Width / Math.Max(0.001, capture.DotnetManifest.DpiScale);
        var referenceHeightDips = capture.DotnetManifest.Bounds.Height / Math.Max(0.001, capture.DotnetManifest.DpiScale);
        var candidateWidthDips = capture.RustManifest.Bounds.Width / Math.Max(0.001, capture.RustManifest.DpiScale);
        var candidateHeightDips = capture.RustManifest.Bounds.Height / Math.Max(0.001, capture.RustManifest.DpiScale);
        var widthDeltaDips = Math.Abs(candidateWidthDips - referenceWidthDips);
        var heightDeltaDips = Math.Abs(candidateHeightDips - referenceHeightDips);
        var referenceSurface = EstimateTrayMenuSurfaceColor(capture.DotnetScreenshot);
        var candidateSurface = EstimateTrayMenuSurfaceColor(capture.RustScreenshot);
        var surfaceColorDistance = ColorDistance(referenceSurface, candidateSurface);
        var referenceSeparatorPixels = CountLikelySeparatorPixels(capture.DotnetScreenshot);
        var candidateSeparatorPixels = CountLikelySeparatorPixels(capture.RustScreenshot);
        var referenceDistinctColors = CountSampledDistinctColors(capture.DotnetScreenshot);
        var candidateDistinctColors = CountSampledDistinctColors(capture.RustScreenshot);
        var unboundedContentHeightDips = (8 + extraItemCount) * 34 + (2 * 8);

        var hasVisibleContent = referenceDistinctColors > 8 && candidateDistinctColors > 8;
        var sizeAligned = widthDeltaDips <= 36 && heightDeltaDips <= 56;
        var surfaceAligned = ColorToHex(referenceSurface) == ColorToHex(candidateSurface);
        var separatorsVisible = expectScrolling ||
            (referenceSeparatorPixels > 160 && candidateSeparatorPixels > 160);
        var scrollConstrained = !expectScrolling ||
            (maxHeightDips.HasValue &&
             unboundedContentHeightDips > maxHeightDips.Value + 180 &&
             referenceHeightDips <= maxHeightDips.Value + 96 &&
             candidateHeightDips <= maxHeightDips.Value + 96 &&
             referenceHeightDips > maxHeightDips.Value * 0.6 &&
             candidateHeightDips > maxHeightDips.Value * 0.6);
        var passed = hasVisibleContent &&
            sizeAligned &&
            surfaceAligned &&
            separatorsVisible &&
            scrollConstrained;

        return new TrayMenuFluentAuditRound(
            Round: round,
            ScenarioId: capture.ScenarioId,
            ExpectScrolling: expectScrolling,
            ReferenceWidthDips: Math.Round(referenceWidthDips, 2),
            CandidateWidthDips: Math.Round(candidateWidthDips, 2),
            WidthDeltaDips: Math.Round(widthDeltaDips, 2),
            ReferenceHeightDips: Math.Round(referenceHeightDips, 2),
            CandidateHeightDips: Math.Round(candidateHeightDips, 2),
            HeightDeltaDips: Math.Round(heightDeltaDips, 2),
            ReferenceSurfaceHex: ColorToHex(referenceSurface),
            CandidateSurfaceHex: ColorToHex(candidateSurface),
            SurfaceColorDistance: Math.Round(surfaceColorDistance, 2),
            ReferenceSeparatorPixels: referenceSeparatorPixels,
            CandidateSeparatorPixels: candidateSeparatorPixels,
            ReferenceDistinctColors: referenceDistinctColors,
            CandidateDistinctColors: candidateDistinctColors,
            MaxHeightDips: maxHeightDips,
            UnboundedContentHeightDips: unboundedContentHeightDips,
            HasVisibleContent: hasVisibleContent,
            SizeAligned: sizeAligned,
            SurfaceAligned: surfaceAligned,
            SeparatorsVisible: separatorsVisible,
            ScrollConstrained: scrollConstrained,
            Passed: passed);
    }

    private static void AssertTrayMenuFluentAuditRounds(
        IReadOnlyList<TrayMenuFluentAuditRound> rounds)
    {
        rounds.Should().HaveCount(
            TrayMenuFluentAuditRoundCount,
            "the Fluent tray menu audit runs the captured system tray menu screenshot for 20 rounds");

        foreach (var round in rounds)
        {
            round.HasVisibleContent.Should().BeTrue(
                $"{round.ScenarioId} round {round.Round} should capture non-blank tray menu screenshots");
            round.SizeAligned.Should().BeTrue(
                $"{round.ScenarioId} round {round.Round} should keep Rust menu dimensions aligned with WinUI");
            round.SurfaceAligned.Should().BeTrue(
                $"{round.ScenarioId} round {round.Round} should keep Rust Fluent surface color aligned with WinUI");
            round.SeparatorsVisible.Should().BeTrue(
                $"{round.ScenarioId} round {round.Round} should expose Fluent menu separators");
            round.ScrollConstrained.Should().BeTrue(
                $"{round.ScenarioId} round {round.Round} should respect Fluent menu scrolling constraints");
            round.Passed.Should().BeTrue(
                $"{round.ScenarioId} round {round.Round} should satisfy all Fluent tray menu audit checks");
        }
    }

    private static string SaveTrayMenuFluentAudit(
        IReadOnlyList<TrayMenuFluentAuditRound> rounds)
    {
        var path = Path.Combine(ScreenshotHelper.OutputDir, "tray-menu-fluent-audit.json");
        var report = new TrayMenuFluentAuditReport(
            SchemaVersion: "easydict.tray-menu-fluent-audit.v1",
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
            RoundCount: TrayMenuFluentAuditRoundCount,
            ScenariosPerRound: 1,
            Rounds: rounds);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static void AssertTrayMenuScrollConstrained(
        UiParityWindowManifest reference,
        UiParityWindowManifest candidate,
        int extraItemCount,
        int maxHeightDips)
    {
        var referenceHeightDips = reference.Bounds.Height / Math.Max(0.001, reference.DpiScale);
        var candidateHeightDips = candidate.Bounds.Height / Math.Max(0.001, candidate.DpiScale);
        var unboundedContentHeight = (8 + extraItemCount) * 34 + (2 * 8);

        unboundedContentHeight.Should().BeGreaterThan(
            maxHeightDips + 180,
            "the scroll fixture should contain enough tray items to require scrolling");
        referenceHeightDips.Should().BeLessThanOrEqualTo(
            maxHeightDips + 96,
            "WinUI reference tray menu should be constrained by MaxHeight in the scroll fixture");
        candidateHeightDips.Should().BeLessThanOrEqualTo(
            maxHeightDips + 96,
            "Rust tray menu should be constrained by the win_fluent presenter max height");
        referenceHeightDips.Should().BeGreaterThan(
            maxHeightDips * 0.6,
            "scrolling reference should still show a meaningful menu viewport");
        candidateHeightDips.Should().BeGreaterThan(
            maxHeightDips * 0.6,
            "scrolling Rust menu should still show a meaningful menu viewport");
    }

    private static void AssertTrayMenuSurfaceColorAligned(string referencePath, string candidatePath)
    {
        var reference = EstimateTrayMenuSurfaceColor(referencePath);
        var candidate = EstimateTrayMenuSurfaceColor(candidatePath);
        ColorToHex(candidate).Should().Be(
            ColorToHex(reference),
            "Rust tray menu surface color must exactly match the WinUI MenuFlyout surface");
    }

    private static Color EstimateTrayMenuSurfaceColor(string path)
    {
        using var bitmap = new Bitmap(path);
        var samples = new List<Color>();
        var left = Math.Max(0, bitmap.Width / 12);
        var right = Math.Min(bitmap.Width - 1, bitmap.Width - (bitmap.Width / 12));
        var top = Math.Max(0, bitmap.Height / 12);
        var bottom = Math.Min(bitmap.Height - 1, bitmap.Height - (bitmap.Height / 12));

        for (var y = top; y <= bottom; y += Math.Max(1, bitmap.Height / 40))
        {
            for (var x = left; x <= right; x += Math.Max(1, bitmap.Width / 40))
            {
                var color = bitmap.GetPixel(x, y);
                if (IsNeutralSurfacePixel(color))
                {
                    samples.Add(color);
                }
            }
        }

        samples.Should().NotBeEmpty("tray menu screenshots should expose neutral Fluent menu surface pixels");
        return Color.FromArgb(
            (int)samples.Average(color => color.R),
            (int)samples.Average(color => color.G),
            (int)samples.Average(color => color.B));
    }

    private static bool IsNeutralSurfacePixel(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        return max >= 225 && max - min <= 18;
    }

    private static double ColorDistance(Color a, Color b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    private static void AssertTrayMenuSeparatorsVisible(string referencePath, string candidatePath)
    {
        var referenceSeparatorPixels = CountLikelySeparatorPixels(referencePath);
        var candidateSeparatorPixels = CountLikelySeparatorPixels(candidatePath);

        referenceSeparatorPixels.Should().BeGreaterThan(
            160,
            "WinUI reference tray menu should expose separator-colored pixels");
        candidateSeparatorPixels.Should().BeGreaterThan(
            160,
            "Rust tray menu should draw separator-colored pixels");
    }

    private static int CountLikelySeparatorPixels(string path)
    {
        using var bitmap = new Bitmap(path);
        var pixels = 0;
        for (var y = 2; y < bitmap.Height - 2; y++)
        {
            for (var x = bitmap.Width / 12; x < bitmap.Width - (bitmap.Width / 12); x++)
            {
                if (IsSeparatorPixel(bitmap.GetPixel(x, y)))
                {
                    pixels++;
                }
            }
        }

        return pixels;
    }

    private static int CountSampledDistinctColors(string path)
    {
        using var bitmap = new Bitmap(path);
        var distinct = new HashSet<int>();
        var stepX = Math.Max(1, bitmap.Width / 96);
        var stepY = Math.Max(1, bitmap.Height / 96);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                distinct.Add(bitmap.GetPixel(x, y).ToArgb());
            }
        }

        return distinct.Count;
    }

    private static string ColorToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool IsSeparatorPixel(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        return max - min <= 14 && color.R is >= 190 and <= 235;
    }

    private static UiParityManifestEntry CreateManifestEntry(
        SettingsParityCaptureStep step,
        Window dotnetWindow,
        Window rustWindow,
        string dotnetPath,
        string rustPath,
        string sideBySidePath,
        string? rustSchemaPath,
        string? operatedDropdownElement = null,
        SettingsDropdownOptionCapture? selectedDropdownOption = null)
    {
        var requiredSemanticTags = new[]
            {
                $"SettingsTab_{step.Section.Label}",
                step.Section.DotnetReadyElement,
                step.HoveredElement,
                step.FocusedElement,
                step.PressedElement,
                operatedDropdownElement
            }
            .Concat(AdditionalRequiredSettingsSemanticTags(step))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var referenceWindow = CaptureWindowManifest(dotnetWindow);
        var candidateWindow = CaptureWindowManifest(rustWindow);
        var referenceExpectedWindowDips = ExpectedSettingsWindowDips(step);
        var candidateExpectedWindowDips = ExpectedSettingsWindowDips(step);

        return new UiParityManifestEntry(
            ScenarioId: step.Key,
            WindowKind: "settings",
            SectionId: step.Section.Id,
            SectionLabel: step.Section.Label,
            Theme: "light",
            ScrollPercent: step.ScrollPercent,
            ExpandAvailableLanguages: step.ExpandAvailableLanguages,
            ReferenceScreenshot: ToOutputRelativePath(dotnetPath),
            CandidateScreenshot: ToOutputRelativePath(rustPath),
            SideBySideScreenshot: ToOutputRelativePath(sideBySidePath),
            ReferenceWindow: referenceWindow,
            CandidateWindow: candidateWindow,
            Regions: SelectSettingsRegions(step),
            RequiredSemanticTags: requiredSemanticTags,
            ReferenceUiSummary: CaptureUiSummary(dotnetWindow),
            CandidateUiSummary: CaptureRustUiSummary(rustWindow, rustSchemaPath, step),
            ReferenceExpectedWindowDips: referenceExpectedWindowDips,
            ReferenceWindowSizeAudit: referenceExpectedWindowDips is null
                ? null
                : CreateWindowSizeAudit(referenceExpectedWindowDips, referenceWindow),
            CandidateExpectedWindowDips: candidateExpectedWindowDips,
            CandidateWindowSizeAudit: candidateExpectedWindowDips is null
                ? null
                : CreateWindowSizeAudit(candidateExpectedWindowDips, candidateWindow),
            RequiredVisibleTexts: AdditionalRequiredSettingsVisibleText(step.Section),
            BaselineScenarioId: step.BaselineScenarioId,
            RequiredControlStates: RequiredSettingsControlStates(step),
            ExpandedDropdownElement: step.ExpandedDropdownElement,
            ExpectedDropdownItems: step.CapturesExpandedDropdown ? step.DropdownExpectedItems : null,
            OperatedDropdownElement: operatedDropdownElement,
            SelectedDropdownOption: selectedDropdownOption?.Label,
            SelectedDropdownOptionIndex: selectedDropdownOption?.DotnetIndex,
            SelectedRustDropdownOptionIndex: selectedDropdownOption?.RustOptionIndexValue,
            RuntimeDiagnosticsPath: RuntimeDiagnosticsPathForSchema(rustSchemaPath));
    }

    private static IReadOnlyList<UiParityRegion> SelectSettingsRegions(SettingsParityCaptureStep step)
    {
        return step.HoveredTab != null || step.PressedTab != null
            ? UiParityRegion.SettingsTabInteractionRegions
            : UiParityRegion.DefaultSettingsRegions;
    }

    private static UiParitySize? ExpectedSettingsWindowDips(SettingsParityCaptureStep _) =>
        // Rust settings_window_options() mirrors the WinUI settings target before
        // monitor work-area clamping. Keep this explicit so visual parity reports
        // cannot hide a wrong absolute settings window size behind normalization.
        new(846, 913);

    private static IReadOnlyList<string> AdditionalRequiredSettingsSemanticTags(
        SettingsParityCaptureStep step)
    {
        var tags = AdditionalRequiredSettingsSemanticTags(step.Section).ToList();
        if (!string.IsNullOrWhiteSpace(step.DotnetExpandElement))
        {
            tags.Add(step.DotnetExpandElement);
        }
        if (!string.IsNullOrWhiteSpace(step.ExpandedDropdownElement))
        {
            tags.Add(step.ExpandedDropdownElement);
        }

        if (string.Equals(step.RustExpandedServiceConfigurations, "deepl", StringComparison.OrdinalIgnoreCase))
        {
            tags.AddRange(
            [
                "DeepLKeyBox",
                "DeepLKeyRevealButton",
                "DeepLFreeCheck",
                "DeepLQualityCheck",
                "TestDeepLButton"
            ]);
        }
        else if (string.Equals(
            step.RustExpandedServiceConfigurations,
            "windows-local-ai",
            StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("LocalAIProviderCombo");
            if (string.Equals(step.RustLocalAiProvider, "FoundryLocal", StringComparison.OrdinalIgnoreCase))
            {
                tags.AddRange(
                [
                    "FoundryLocalEndpointBox",
                    "FoundryLocalModelBox"
                ]);
            }
            else if (string.Equals(step.RustLocalAiProvider, "WindowsAI", StringComparison.OrdinalIgnoreCase))
            {
                tags.AddRange(
                [
                    "WindowsLocalAIStatusBar",
                    "WindowsLocalAIPrepareButton"
                ]);
            }
            else if (string.Equals(step.RustLocalAiProvider, "OpenVINO", StringComparison.OrdinalIgnoreCase))
            {
                tags.AddRange(
                [
                    "OpenVinoStatusBar",
                    "OpenVinoDownloadButton"
                ]);
            }
            else
            {
                tags.AddRange(
                [
                    "WindowsLocalAIStatusBar",
                    "WindowsLocalAIPrepareButton",
                    "FoundryLocalEndpointBox",
                    "FoundryLocalModelBox",
                    "OpenVinoStatusBar",
                    "OpenVinoDownloadButton"
                ]);
            }
        }
        else if (!string.IsNullOrWhiteSpace(step.RustExpandedServiceConfigurations))
        {
            tags.AddRange(ExpandedServiceConfigurationSemanticTags(step.RustExpandedServiceConfigurations));
        }

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ExpandedServiceConfigurationSemanticTags(string serviceId)
    {
        return serviceId.Trim().ToLowerInvariant() switch
        {
            "ollama" =>
            [
                "OllamaServiceExpander",
                "OllamaEndpointBox",
                "OllamaModelCombo",
                "RefreshOllamaButton",
                "TestOllamaButton"
            ],
            "openai" =>
            [
                "OpenAIServiceExpander",
                "OpenAIKeyBox",
                "OpenAIKeyRevealButton",
                "OpenAIEndpointBox",
                "OpenAIApiFormatCombo",
                "OpenAIModelCombo",
                "TestOpenAIButton"
            ],
            "deepseek" =>
            [
                "DeepSeekServiceExpander",
                "DeepSeekKeyBox",
                "DeepSeekKeyRevealButton",
                "DeepSeekModelCombo",
                "TestDeepSeekButton"
            ],
            "groq" =>
            [
                "GroqServiceExpander",
                "GroqKeyBox",
                "GroqKeyRevealButton",
                "GroqModelCombo",
                "TestGroqButton"
            ],
            "zhipu" =>
            [
                "ZhipuServiceExpander",
                "ZhipuKeyBox",
                "ZhipuKeyRevealButton",
                "ZhipuModelCombo",
                "TestZhipuButton"
            ],
            "github" =>
            [
                "GitHubModelsServiceExpander",
                "GitHubModelsTokenBox",
                "GitHubModelsTokenRevealButton",
                "GitHubModelsModelCombo",
                "TestGitHubModelsButton"
            ],
            "gemini" =>
            [
                "GeminiServiceExpander",
                "GeminiKeyBox",
                "GeminiKeyRevealButton",
                "GeminiModelCombo",
                "TestGeminiButton"
            ],
            "custom-openai" =>
            [
                "CustomOpenAIServiceExpander",
                "CustomOpenAIKeyBox",
                "CustomOpenAIKeyRevealButton",
                "CustomOpenAIEndpointBox",
                "CustomOpenAIModelBox",
                "TestCustomOpenAIButton"
            ],
            "builtin" =>
            [
                "BuiltInAIServiceExpander",
                "BuiltInApiKeyBox",
                "BuiltInApiKeyRevealButton",
                "BuiltInModelCombo",
                "TestBuiltInButton"
            ],
            "doubao" =>
            [
                "DoubaoServiceExpander",
                "DoubaoKeyBox",
                "DoubaoKeyRevealButton",
                "DoubaoEndpointBox",
                "DoubaoModelBox",
                "TestDoubaoButton"
            ],
            "caiyun" =>
            [
                "CaiyunServiceExpander",
                "CaiyunKeyBox",
                "CaiyunKeyRevealButton",
                "TestCaiyunButton"
            ],
            "niutrans" =>
            [
                "NiuTransServiceExpander",
                "NiuTransKeyBox",
                "NiuTransKeyRevealButton",
                "TestNiuTransButton"
            ],
            "youdao" =>
            [
                "YoudaoServiceExpander",
                "YoudaoAppKeyBox",
                "YoudaoAppKeyRevealButton",
                "YoudaoAppSecretBox",
                "YoudaoAppSecretRevealButton",
                "YoudaoUseOfficialApiToggle"
            ],
            _ => []
        };
    }

    private static IReadOnlyList<string> AdditionalRequiredSettingsSemanticTags(
        SettingsParitySection section)
    {
        if (section == SettingsParitySection.About)
        {
            return
            [
                "AboutHeaderText",
                "AboutAppNameText",
                "VersionText",
                "GitHubRepositoryLink",
                "IssueFeedbackLink",
                "AboutInspiredByText",
                "InspiredByLink",
                "LicenseText"
            ];
        }

        if (section == SettingsParitySection.Services)
        {
            return
            [
                "EnabledServicesHeaderText",
                "EnabledServicesDescriptionText",
                "ImportMdxDictionaryButton",
                "ImportedMdxSummaryText",
                "EnableInternationalServicesHeaderText",
                "EnableInternationalServicesToggle",
                "EnableInternationalServicesDescriptionText",
                "ServiceConfigurationHeaderText",
                "ServiceConfigurationDescriptionText",
                "DeepLServiceExpander",
                "WindowsLocalAIExpander"
            ];
        }

        return [];
    }

    private static IReadOnlyList<string> AdditionalRequiredSettingsVisibleText(SettingsParitySection section)
    {
        return section == SettingsParitySection.About
            ? ["Inspired by", "License: GPL-3.0"]
            : [];
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredSettingsControlStates(
        SettingsParityCaptureStep step)
    {
        var states = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        AddRequiredControlStates(states, $"SettingsTab_{step.Section.Label}", "selected");

        if (step.HoveredTab is { } hoveredTab)
        {
            AddRequiredControlStates(states, $"SettingsTab_{hoveredTab.Label}", "hovered");
        }

        if (step.PressedTab is { } pressedTab)
        {
            AddRequiredControlStates(states, $"SettingsTab_{pressedTab.Label}", "hovered", "pressed");
        }

        if (step.HoveredElement is { } hoveredElement)
        {
            AddRequiredControlStates(states, step.RequiredStateElement ?? hoveredElement, "hovered");
        }

        if (step.PressedElement is { } pressedElement)
        {
            AddRequiredControlStates(states, step.RequiredStateElement ?? pressedElement, "hovered", "pressed");
        }

        if (step.FocusedElement is { } focusedElement)
        {
            AddRequiredControlStates(states, focusedElement, "focused");
        }

        return states;
    }

    private static void AddRequiredControlStates(
        IDictionary<string, IReadOnlyList<string>> states,
        string automationId,
        params string[] requiredStates)
    {
        if (string.IsNullOrWhiteSpace(automationId) || requiredStates.Length == 0)
        {
            return;
        }

        var merged = states.TryGetValue(automationId, out var existing)
            ? existing.Concat(requiredStates)
            : requiredStates;
        states[automationId] = merged
            .Select(state => state.Trim())
            .Where(state => !string.IsNullOrWhiteSpace(state))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(state => state, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static UiParityWindowSizeAudit CreateWindowSizeAudit(
        UiParitySize expectedWindowDips,
        UiParityWindowManifest window)
    {
        var dpiScale = Math.Max(0.001, window.DpiScale);
        var actualWindowDips = new UiParitySize(
            Round2(window.Bounds.Width / dpiScale),
            Round2(window.Bounds.Height / dpiScale));
        var monitorWorkAreaDips = EstimateMonitorWorkAreaDips(window, dpiScale);
        var deltaDips = new UiParitySize(
            Round2(actualWindowDips.Width - expectedWindowDips.Width),
            Round2(actualWindowDips.Height - expectedWindowDips.Height));
        var deltaPercent = new UiParitySize(
            Round2(PercentDelta(expectedWindowDips.Width, actualWindowDips.Width)),
            Round2(PercentDelta(expectedWindowDips.Height, actualWindowDips.Height)));

        return new UiParityWindowSizeAudit(
            ExpectedWindowDips: expectedWindowDips,
            ActualWindowDips: actualWindowDips,
            DeltaDips: deltaDips,
            DeltaPercent: deltaPercent,
            MonitorWorkAreaDips: monitorWorkAreaDips,
            ExpectedLargerThanWorkArea: expectedWindowDips.Width > monitorWorkAreaDips.Width ||
                                        expectedWindowDips.Height > monitorWorkAreaDips.Height);
    }

    private static UiParitySize EstimateMonitorWorkAreaDips(
        UiParityWindowManifest window,
        double dpiScale)
    {
        var virtualWidth = window.VirtualScreenBounds.Width;
        var virtualHeight = window.VirtualScreenBounds.Height;
        var boundsAppearPhysicalAgainstLogicalScreen =
            dpiScale > 1.01 &&
            (window.Bounds.Width > virtualWidth * 1.05 ||
             window.Bounds.Height > virtualHeight * 1.05);
        return boundsAppearPhysicalAgainstLogicalScreen
            ? new UiParitySize(Round2(virtualWidth), Round2(virtualHeight))
            : new UiParitySize(Round2(virtualWidth / dpiScale), Round2(virtualHeight / dpiScale));
    }

    private static double PercentDelta(double expected, double actual) =>
        Math.Abs(expected) < double.Epsilon ? 0 : ((actual - expected) / expected) * 100d;

    private static double Round2(double value) => Math.Round(value, 2);

    private static UiParityManifestEntry CreateMainManifestEntry(
        string scenarioId,
        string sectionLabel,
        Window dotnetWindow,
        Window rustWindow,
        string dotnetPath,
        string rustPath,
        string sideBySidePath,
        IReadOnlyList<UiParityRegion> regions,
        IReadOnlyList<string> requiredSemanticTags,
        string? windowKindOverride = null,
        string? rustSchemaPath = null,
        Size? referenceScreenshotPixelSize = null,
        Size? candidateScreenshotPixelSize = null,
        UiParityUiSummary? referenceUiSummaryOverride = null,
        string? operatedDropdownElement = null,
        SettingsDropdownOptionCapture? selectedDropdownOption = null)
    {
        var windowKind = windowKindOverride
            ?? (scenarioId.StartsWith("effects.", StringComparison.OrdinalIgnoreCase)
                ? "interaction-effects"
                : "main");
        var referenceWindow = CaptureWindowManifest(dotnetWindow);
        if (referenceScreenshotPixelSize is { } referencePixels)
        {
            referenceWindow = WithScreenshotPixelSize(referenceWindow, referencePixels);
        }

        var candidateWindow = CaptureWindowManifest(rustWindow);
        if (candidateScreenshotPixelSize is { } candidatePixels)
        {
            candidateWindow = WithScreenshotPixelSize(candidateWindow, candidatePixels);
        }

        return new UiParityManifestEntry(
            ScenarioId: scenarioId,
            WindowKind: windowKind,
            SectionId: windowKind,
            SectionLabel: sectionLabel,
            Theme: "light",
            ScrollPercent: 0,
            ExpandAvailableLanguages: false,
            ReferenceScreenshot: ToOutputRelativePath(dotnetPath),
            CandidateScreenshot: ToOutputRelativePath(rustPath),
            SideBySideScreenshot: ToOutputRelativePath(sideBySidePath),
            ReferenceWindow: referenceWindow,
            CandidateWindow: candidateWindow,
            Regions: regions,
            RequiredSemanticTags: requiredSemanticTags,
            ReferenceUiSummary: referenceUiSummaryOverride ?? CaptureDotnetUiSummary(dotnetWindow, windowKind),
            CandidateUiSummary: CaptureRustUiSummary(rustWindow, rustSchemaPath),
            OperatedDropdownElement: operatedDropdownElement,
            SelectedDropdownOption: selectedDropdownOption?.Label,
            SelectedDropdownOptionIndex: selectedDropdownOption?.DotnetIndex,
            SelectedRustDropdownOptionIndex: selectedDropdownOption?.RustOptionIndexValue,
            RuntimeDiagnosticsPath: RuntimeDiagnosticsPathForSchema(rustSchemaPath));
    }

    private static UiParityManifestEntry CreatePopButtonManifestEntry(
        string scenarioId,
        string sectionLabel,
        UiParityWindowManifest referenceWindow,
        UiParityWindowManifest candidateWindow,
        string dotnetPath,
        string rustPath,
        string sideBySidePath,
        string runtimeDiagnosticsPath)
    {
        return new UiParityManifestEntry(
            ScenarioId: scenarioId,
            WindowKind: "popbutton",
            SectionId: "popbutton",
            SectionLabel: sectionLabel,
            Theme: "light",
            ScrollPercent: 0,
            ExpandAvailableLanguages: false,
            ReferenceScreenshot: ToOutputRelativePath(dotnetPath),
            CandidateScreenshot: ToOutputRelativePath(rustPath),
            SideBySideScreenshot: ToOutputRelativePath(sideBySidePath),
            ReferenceWindow: referenceWindow,
            CandidateWindow: candidateWindow,
            Regions: UiParityRegion.PopButtonRegions,
            RequiredSemanticTags: [],
            ReferenceUiSummary: EmptyUiSummary(),
            CandidateUiSummary: EmptyUiSummary(),
            RuntimeDiagnosticsPath: ToOutputRelativePath(runtimeDiagnosticsPath));
    }

    private static UiParityManifestEntry CreateOcrOverlayManifestEntry(
        string scenarioId,
        string sectionLabel,
        UiParityWindowManifest referenceWindow,
        UiParityWindowManifest candidateWindow,
        string dotnetPath,
        string rustPath,
        string sideBySidePath,
        IReadOnlyList<string> requiredSemanticTags,
        UiParityUiSummary candidateUiSummary,
        string runtimeDiagnosticsPath)
    {
        return new UiParityManifestEntry(
            ScenarioId: scenarioId,
            WindowKind: "ocr",
            SectionId: "ocr",
            SectionLabel: sectionLabel,
            Theme: "light",
            ScrollPercent: 0,
            ExpandAvailableLanguages: false,
            ReferenceScreenshot: ToOutputRelativePath(dotnetPath),
            CandidateScreenshot: ToOutputRelativePath(rustPath),
            SideBySideScreenshot: ToOutputRelativePath(sideBySidePath),
            ReferenceWindow: referenceWindow,
            CandidateWindow: candidateWindow,
            Regions: UiParityRegion.OcrOverlayRegions,
            RequiredSemanticTags: requiredSemanticTags,
            ReferenceUiSummary: EmptyUiSummary(),
            CandidateUiSummary: candidateUiSummary,
            RuntimeDiagnosticsPath: ToOutputRelativePath(runtimeDiagnosticsPath));
    }

    private static UiParityManifestEntry CreateTrayMenuManifestEntry(
        string scenarioId,
        string sectionLabel,
        UiParityWindowManifest referenceWindow,
        UiParityWindowManifest candidateWindow,
        string dotnetPath,
        string rustPath,
        string sideBySidePath,
        string runtimeDiagnosticsPath)
    {
        return new UiParityManifestEntry(
            ScenarioId: scenarioId,
            WindowKind: "tray-menu",
            SectionId: "tray-menu",
            SectionLabel: sectionLabel,
            Theme: "light",
            ScrollPercent: 0,
            ExpandAvailableLanguages: false,
            ReferenceScreenshot: ToOutputRelativePath(dotnetPath),
            CandidateScreenshot: ToOutputRelativePath(rustPath),
            SideBySideScreenshot: ToOutputRelativePath(sideBySidePath),
            ReferenceWindow: referenceWindow,
            CandidateWindow: candidateWindow,
            Regions: UiParityRegion.TrayMenuRegions,
            RequiredSemanticTags: [],
            ReferenceUiSummary: EmptyUiSummary(),
            CandidateUiSummary: EmptyUiSummary(),
            RequiredVisibleTexts: TrayMenuRequiredVisibleTexts(),
            RuntimeDiagnosticsPath: ToOutputRelativePath(runtimeDiagnosticsPath));
    }

    private static IReadOnlyList<string> TrayMenuRequiredVisibleTexts() =>
        string.Equals(ResolveParityUiLanguage(), "zh-CN", StringComparison.OrdinalIgnoreCase)
            ?
            [
                "显示 Easydict",
                "翻译剪贴板",
                "OCR 截图翻译 (Ctrl+Alt+S)",
                "迷你窗口 (Ctrl+Alt+M)",
                "固定窗口 (Ctrl+Alt+F)",
                "浏览器支持",
                "设置",
                "退出"
            ]
            :
            [
                "Show Easydict",
                "Translate Clipboard",
                "OCR Translate (Ctrl+Alt+S)",
                "Mini Window (Ctrl+Alt+M)",
                "Fixed Window (Ctrl+Alt+F)",
                "Browser Support",
                "Settings",
                "Exit"
            ];

    private static UiParityUiSummary EmptyUiSummary() =>
        new(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            [],
            new Dictionary<string, UiParityControlDimension>(StringComparer.OrdinalIgnoreCase));

    private static UiParityWindowManifest CaptureWindowManifest(Window window)
    {
        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var hwnd = SafeNativeWindowHandle(window);
        return CaptureWindowManifest(hwnd, bounds, ScreenshotHelper.GetWindowDpiScale(window));
    }

    private static UiParityWindowManifest CaptureWindowManifest(IntPtr hwnd)
    {
        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(hwnd);
        var dpi = SafeGetDpiForWindow(hwnd);
        var dpiScale = dpi.HasValue && dpi.Value > 0 ? dpi.Value / 96d : 1d;
        return CaptureWindowManifest(hwnd, bounds, dpiScale);
    }

    private static UiParityWindowManifest CaptureWindowManifest(
        IntPtr hwnd,
        Rectangle bounds,
        double dpiScale)
    {
        var extendedStyle = hwnd == IntPtr.Zero ? (int?)null : GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var foregroundHwnd = GetForegroundWindow();
        var virtualScreen = ScreenshotHelper.GetVirtualScreenBounds();
        var visibleBounds = Rectangle.Intersect(bounds, virtualScreen);
        return new UiParityWindowManifest(
            Bounds: new UiParityBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            VisibleBounds: new UiParityBounds(
                visibleBounds.Left,
                visibleBounds.Top,
                visibleBounds.Width,
                visibleBounds.Height),
            VirtualScreenBounds: new UiParityBounds(
                virtualScreen.Left,
                virtualScreen.Top,
                virtualScreen.Width,
                virtualScreen.Height),
            IsClippedByVirtualScreen: !virtualScreen.Contains(bounds),
            DpiScale: Math.Round(dpiScale, 3),
            NativeHandleHex: hwnd == IntPtr.Zero ? null : $"0x{hwnd.ToInt64():X}",
            ExtendedStyleHex: extendedStyle.HasValue ? $"0x{extendedStyle.Value:X8}" : null,
            HasNoActivate: extendedStyle.HasValue ? (extendedStyle.Value & WS_EX_NOACTIVATE) != 0 : null,
            HasToolWindow: extendedStyle.HasValue ? (extendedStyle.Value & WS_EX_TOOLWINDOW) != 0 : null,
            HasTopmost: extendedStyle.HasValue ? (extendedStyle.Value & WS_EX_TOPMOST) != 0 : null,
            IsForegroundAtCapture: hwnd == IntPtr.Zero ? null : foregroundHwnd == hwnd,
            Dpi: hwnd == IntPtr.Zero ? null : SafeGetDpiForWindow(hwnd));
    }

    private static UiParityWindowManifest WithScreenshotPixelSize(
        UiParityWindowManifest manifest,
        Size screenshotPixelSize)
    {
        if (screenshotPixelSize.Width <= 0 || screenshotPixelSize.Height <= 0)
        {
            return manifest;
        }

        var bounds = new UiParityBounds(
            manifest.Bounds.Left,
            manifest.Bounds.Top,
            screenshotPixelSize.Width,
            screenshotPixelSize.Height);
        var physicalBounds = new Rectangle(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height);
        var virtualScreen = new Rectangle(
            manifest.VirtualScreenBounds.Left,
            manifest.VirtualScreenBounds.Top,
            manifest.VirtualScreenBounds.Width,
            manifest.VirtualScreenBounds.Height);
        var visibleBounds = Rectangle.Intersect(physicalBounds, virtualScreen);

        return manifest with
        {
            Bounds = bounds,
            VisibleBounds = new UiParityBounds(
                visibleBounds.Left,
                visibleBounds.Top,
                visibleBounds.Width,
                visibleBounds.Height),
            IsClippedByVirtualScreen = !virtualScreen.Contains(physicalBounds),
        };
    }

    private static IntPtr SafeNativeWindowHandle(Window window)
    {
        try
        {
            return window.Properties.NativeWindowHandle.Value;
        }
        catch (Exception ex) when (ex is COMException or PropertyNotSupportedException)
        {
            return IntPtr.Zero;
        }
    }

    private static UiParityUiSummary CaptureUiSummary(Window window)
    {
        var descendants = FindVisibleDescendantsForSummary(window)
            .Where(element => !IsUiSummaryScrollBarChrome(element))
            .ToArray();
        var counts = EmptyControlCounts();
        counts["button"] = CountDescendants(descendants, ControlType.Button);
        counts["checkbox"] = CountDescendants(descendants, ControlType.CheckBox);
        counts["comboBox"] = CountDescendants(descendants, ControlType.ComboBox);
        counts["edit"] = CountDescendants(descendants, ControlType.Edit);
        counts["hyperlink"] = CountDescendants(descendants, ControlType.Hyperlink);
        counts["list"] = CountDescendants(descendants, ControlType.List);
        counts["listItem"] = CountDescendants(descendants, ControlType.ListItem);
        counts["tabItem"] = CountDescendants(descendants, ControlType.TabItem);
        counts["text"] = CountDescendants(descendants, ControlType.Text);

        return new UiParityUiSummary(
            counts,
            CollectVisibleAutomationIds(descendants),
            CollectVisibleControlDimensions(window, descendants),
            CollectVisibleTexts(descendants));
    }

    private static UiParityUiSummary CaptureDotnetUiSummary(Window window, string windowKind)
    {
        var summary = CaptureUiSummary(window);
        if (IsFloatingWindowKind(windowKind) &&
            ShouldUseFloatingReferenceSummaryFallback(summary))
        {
            return CreateFloatingReferenceSummaryFallback(windowKind);
        }

        return summary;
    }

    private static bool IsFloatingWindowKind(string windowKind) =>
        windowKind.Equals("mini", StringComparison.OrdinalIgnoreCase) ||
        windowKind.Equals("fixed", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseFloatingReferenceSummaryFallback(UiParityUiSummary summary)
    {
        var chromeOnlyIds = summary.VisibleAutomationIds.All(IsSystemChromeSummaryValue);
        var texts = summary.VisibleTexts ?? [];
        var chromeOnlyText = texts.Count == 0 || texts.All(IsSystemChromeSummaryValue);
        var hasChromeText = texts.Any(IsSystemChromeSummaryValue);
        return (chromeOnlyIds && chromeOnlyText) || hasChromeText;
    }

    private static UiParityUiSummary CreateFloatingReferenceSummaryFallback(
        string windowKind,
        IReadOnlyList<string>? extraVisibleTexts = null)
    {
        var isFixed = windowKind.Equals("fixed", StringComparison.OrdinalIgnoreCase);
        var counts = EmptyControlCounts();
        counts["button"] = isFixed ? 5 : 6;
        counts["comboBox"] = 2;
        counts["edit"] = 1;
        counts["text"] = 4;

        var ids = new List<string>
        {
            "InputTextBox",
            isFixed ? "ResultsScrollViewer" : "MainScrollViewer",
            isFixed ? "CloseButton" : "MiniWindowCloseButton",
            isFixed ? "FixedWindowOcrButton" : "MiniWindowOcrButton",
            "ResultsPanel",
            "ServiceIcon",
            "ServiceNameText",
            "ServiceResultHeader_bing",
            "ServiceResultHeader_mdx__collins-cobuild-english-usage-c9ef3413",
            "ServiceResultItem_bing",
            "ServiceResultItem_mdx__collins-cobuild-english-usage-c9ef3413",
            "SourceLangCombo",
            "SourcePlayButton",
            "StatusText",
            "SwapButton",
            "TargetLangCombo",
            "TranslateButton"
        };

        if (!isFixed)
        {
            ids.Insert(4, "PinButton");
        }

        var visibleTexts = FloatingReferenceVisibleTexts(windowKind).ToList();
        if (extraVisibleTexts is { Count: > 0 })
        {
            visibleTexts.AddRange(extraVisibleTexts);
        }

        return new UiParityUiSummary(
            counts,
            ids,
            new Dictionary<string, UiParityControlDimension>(StringComparer.OrdinalIgnoreCase),
            visibleTexts);
    }

    private static IReadOnlyList<string> FloatingReferenceVisibleTexts(string windowKind)
    {
        var isZhCn = ResolveParityUiLanguage().Equals("zh-CN", StringComparison.OrdinalIgnoreCase);
        return
        [
            "Bing Translate",
            windowKind.Equals("fixed", StringComparison.OrdinalIgnoreCase) ? "Fixed Translate" : "Quick Translate",
            isZhCn ? "就绪" : "Ready",
            isZhCn ? "输入或粘贴要翻译的文本..." : "Enter or paste text to translate...",
            "📚 Collins COBUILD English Usage"
        ];
    }

    private static IReadOnlyList<string> FloatingDropdownOptionReferenceExtraVisibleTexts(
        FloatingDropdownCapture dropdown,
        SettingsDropdownOptionCapture option)
    {
        if (!dropdown.Key.StartsWith("source-language", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return FloatingLanguageOptionId(option) switch
        {
            "auto" => [],
            "zh-Hant" => [FloatingGrammarFallbackNotice()],
            var languageId => [$"检测到：{FloatingDetectedLanguageName(languageId)}"]
        };
    }

    private static string FloatingDetectedLanguageName(string languageId) =>
        languageId switch
        {
            "zh-Hans" => "Chinese (Simplified)",
            "ja" => "Japanese",
            "ko" => "Korean",
            "en" => "English",
            "de" => "German",
            "fr" => "French",
            "es" => "Spanish",
            _ => languageId
        };

    private static string FloatingGrammarFallbackNotice() =>
        "未启用支持纠错的 AI 服务，已回退为普通翻译。配置一个支持纠错的 AI 服务后，源语言和目标语言相同时会显示纠错信息。";

    private static UiParityUiSummary CaptureRustUiSummary(
        Window window,
        string? schemaPath,
        SettingsParityCaptureStep? step = null)
    {
        var nativeSummary = CaptureUiSummary(window);
        WaitForRustSchema(schemaPath);
        WaitForRustBounds(schemaPath);
        var runtimeDimensions = MergeControlDimensions(
            nativeSummary.VisibleControlDimensions,
            TryReadRustBoundsControlDimensions(RustBoundsPathForSchema(schemaPath)));
        return TryReadRustSchemaUiSummary(
                schemaPath,
                step,
                runtimeDimensions) ??
            nativeSummary;
    }

    private static void WaitForRustSchema(string? schemaPath)
    {
        if (string.IsNullOrWhiteSpace(schemaPath))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            try
            {
                if (File.Exists(schemaPath) && new FileInfo(schemaPath).Length > 0)
                {
                    return;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            Thread.Sleep(50);
        }
    }

    private static void WaitForRustBounds(string? schemaPath)
    {
        var boundsPath = RustBoundsPathForSchema(schemaPath);
        if (string.IsNullOrWhiteSpace(boundsPath))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            try
            {
                if (File.Exists(boundsPath) && new FileInfo(boundsPath).Length > 0)
                {
                    return;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            Thread.Sleep(50);
        }
    }

    private static UiParityUiSummary? TryReadRustSchemaUiSummary(
        string? schemaPath,
        SettingsParityCaptureStep? step = null,
        IReadOnlyDictionary<string, UiParityControlDimension>? nativeDimensions = null)
    {
        if (string.IsNullOrWhiteSpace(schemaPath) || !File.Exists(schemaPath))
        {
            return null;
        }

        var counts = EmptyControlCounts();
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var dimensions = new SortedDictionary<string, UiParityControlDimension>(StringComparer.OrdinalIgnoreCase);
        var visibleTexts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var scope = RustSchemaSummaryScope.FromStep(step);
        foreach (var line in File.ReadLines(schemaPath))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith("ViewSchema ", StringComparison.Ordinal))
            {
                continue;
            }

            var kindEnd = trimmed.IndexOf(' ');
            var kind = kindEnd < 0 ? trimmed : trimmed[..kindEnd];
            var automationId = TryExtractRustSchemaId(trimmed);
            scope.Update(automationId);

            if (kind == "TitleBar" &&
                trimmed.Contains("caption_controls=true", StringComparison.Ordinal) &&
                scope.ShouldIncludeTitleBarChrome())
            {
                AddRustSchemaTitleBarSummary(counts, ids);
            }

            if (!scope.ShouldIncludeLine(kind, automationId, trimmed))
            {
                continue;
            }

            var summaryKind = kind == "Button" &&
                trimmed.Contains("kind=Link", StringComparison.Ordinal)
                    ? "Hyperlink"
                    : kind;
            if (!ShouldSkipRustSchemaControlCount(summaryKind, automationId, trimmed))
            {
                IncrementSchemaControlCount(counts, summaryKind);
            }

            if (summaryKind == "Button" &&
                scope.ShouldIncludeButtonLabelAsVisibleText(trimmed, automationId))
            {
                var buttonText = TryExtractRustSchemaQuotedValue(trimmed, "label");
                if (string.IsNullOrEmpty(buttonText) &&
                    scope.ShouldUseButtonTooltipAsVisibleText(automationId))
                {
                    buttonText = TryExtractRustSchemaQuotedValue(trimmed, "tooltip");
                }

                if (!string.IsNullOrEmpty(buttonText))
                {
                    if (scope.ShouldCountButtonLabelAsText(trimmed, automationId))
                    {
                        IncrementSchemaControlCount(counts, "Text");
                    }
                    visibleTexts.Add(buttonText);
                }
            }

            if (summaryKind == "ToggleSwitch" &&
                TryExtractRustSchemaQuotedValue(trimmed, "label") is { Length: > 0 } toggleLabel)
            {
                IncrementSchemaControlCount(counts, "Text");
                visibleTexts.Add(toggleLabel);
            }

            if (summaryKind == "CheckBox" &&
                TryExtractRustSchemaQuotedValue(trimmed, "label") is { Length: > 0 } checkBoxLabel)
            {
                visibleTexts.Add(checkBoxLabel);
            }

            if (summaryKind == "FlyoutButton" &&
                TryExtractRustSchemaQuotedValue(trimmed, "label") is { Length: > 0 } flyoutLabel)
            {
                IncrementSchemaControlCount(counts, "Text");
                visibleTexts.Add(flyoutLabel);
                if (string.Equals(automationId, "ModeMenuButton", StringComparison.OrdinalIgnoreCase))
                {
                    visibleTexts.Add(trimmed.Contains("selected=\"long-document\"", StringComparison.Ordinal)
                        ? "Mode: Long Document"
                        : "Mode: Translation");
                }
            }

            if (summaryKind == "ComboBox" &&
                scope.ShouldIncludeComboBoxLabelAsVisibleText() &&
                TryExtractRustSchemaQuotedValue(trimmed, "label") is { Length: > 0 } comboLabel)
            {
                visibleTexts.Add(comboLabel);
            }

            if (summaryKind == "StatusBadge" &&
                TryExtractRustSchemaQuotedValue(trimmed, "label") is { Length: > 0 } statusLabel)
            {
                IncrementSchemaControlCount(counts, "Text");
                visibleTexts.Add(statusLabel);
            }

            if (summaryKind == "Card" &&
                !scope.IsSettings &&
                TryExtractRustSchemaQuotedValue(trimmed, "title") is { Length: > 0 } cardTitle)
            {
                IncrementSchemaControlCount(counts, "Text");
                visibleTexts.Add(cardTitle);
            }

            if (summaryKind == "TextEditor" &&
                scope.ShouldIncludeTextEditorTextAsVisibleText(automationId) &&
                TryExtractRustSchemaQuotedValue(trimmed, "placeholder") is { Length: > 0 } placeholder)
            {
                visibleTexts.Add(placeholder);
            }

            if (summaryKind == "Expander" &&
                TryExtractRustSchemaQuotedValue(trimmed, "title") is { Length: > 0 } expanderTitle)
            {
                IncrementSchemaControlCount(counts, "Text");
                visibleTexts.Add(expanderTitle);
                scope.AddDerivedAutomationIdsForExpanderTitle(automationId, expanderTitle, ids);
            }

            if (kind == "Text" &&
                TryExtractRustSchemaQuotedValue(trimmed, "value") is { Length: > 0 } textValue &&
                !IsIconGlyphText(textValue) &&
                !IsRustSchemaDecorativeHelpText(automationId, textValue))
            {
                AddRustSchemaVisibleText(visibleTexts, automationId, textValue);
            }

            if (kind == "ResultItem" &&
                TryExtractRustSchemaQuotedValue(trimmed, "title") is { Length: > 0 } resultItemTitle)
            {
                IncrementSchemaControlCount(counts, "Text");
                visibleTexts.Add(resultItemTitle);
                if (TryExtractRustSchemaQuotedValue(trimmed, "pending_hint") is { Length: > 0 } pendingHint)
                {
                    IncrementSchemaControlCount(counts, "Text");
                    visibleTexts.Add(pendingHint);
                    visibleTexts.Add(RustSchemaPendingQueryStatusText());
                }
            }

            if (kind == "Text" && scope.IsSettingsViewsMainWindowHeader(trimmed))
            {
                ids.Add("MainWindowHeaderText");
            }

            var derivedAutomationIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            scope.AddDerivedAutomationIds(kind, automationId, derivedAutomationIds);
            foreach (var derivedAutomationId in derivedAutomationIds)
            {
                ids.Add(derivedAutomationId);
            }

            if (automationId is not null && scope.ShouldIncludeAutomationId(automationId))
            {
                ids.Add(automationId);
            }

            if (automationId is not null)
            {
                var dimension = ExtractRustSchemaControlDimension(
                    kind,
                    automationId,
                    trimmed,
                    scope);
                if (scope.ShouldIncludeAutomationId(automationId) ||
                    scope.ShouldIncludeRustLayoutEvidenceId(automationId))
                {
                    dimensions[automationId] = ApplyNativeBounds(
                        dimension,
                        nativeDimensions,
                        automationId);
                }

                foreach (var derivedAutomationId in derivedAutomationIds)
                {
                    if (scope.ShouldIncludeAutomationId(derivedAutomationId))
                    {
                        var derivedDimension = scope.AdjustDerivedDimension(
                            dimension,
                            derivedAutomationId,
                            automationId);
                        dimensions[derivedAutomationId] = ApplyNativeBounds(
                            derivedDimension,
                            nativeDimensions,
                            derivedAutomationId,
                            automationId);
                    }
                }
            }
        }

        return new UiParityUiSummary(counts, ids.ToArray(), dimensions, visibleTexts.ToArray());
    }

    private static UiParityControlDimension ApplyNativeBounds(
        UiParityControlDimension dimension,
        IReadOnlyDictionary<string, UiParityControlDimension>? nativeDimensions,
        params string[] automationIds)
    {
        if (nativeDimensions is null)
        {
            return dimension;
        }

        foreach (var automationId in automationIds)
        {
            if (nativeDimensions.TryGetValue(automationId, out var nativeDimension) &&
                nativeDimension.BoundsDips is not null)
            {
                return dimension with { BoundsDips = nativeDimension.BoundsDips };
            }
        }

        return dimension;
    }

    private static IReadOnlyDictionary<string, UiParityControlDimension> MergeControlDimensions(
        IReadOnlyDictionary<string, UiParityControlDimension> first,
        IReadOnlyDictionary<string, UiParityControlDimension> second)
    {
        if (second.Count == 0)
        {
            return first;
        }

        var merged = new Dictionary<string, UiParityControlDimension>(first, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, dimension) in second)
        {
            merged[id] = dimension;
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, UiParityControlDimension> TryReadRustBoundsControlDimensions(
        string? boundsPath)
    {
        if (string.IsNullOrWhiteSpace(boundsPath) || !File.Exists(boundsPath))
        {
            return new Dictionary<string, UiParityControlDimension>(StringComparer.OrdinalIgnoreCase);
        }

        var dimensions = new SortedDictionary<string, UiParityControlDimension>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(boundsPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("ViewBounds ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!trimmed.StartsWith("Bounds ", StringComparison.Ordinal))
                {
                    continue;
                }

                var id = TryExtractRustSchemaQuotedValue(trimmed, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (!TryExtractRustBoundsDouble(trimmed, "x", out var x) ||
                    !TryExtractRustBoundsDouble(trimmed, "y", out var y) ||
                    !TryExtractRustBoundsDouble(trimmed, "width", out var width) ||
                    !TryExtractRustBoundsDouble(trimmed, "height", out var height) ||
                    width <= 0 ||
                    height <= 0)
                {
                    continue;
                }

                dimensions[id] = new UiParityControlDimension(
                    Kind: TryExtractRustSchemaTokenValue(trimmed, "kind") ?? "Control",
                    Width: FormatDip(width),
                    Height: FormatDip(height),
                    BoundsDips: new UiParityControlBoundsDips(
                        Round2(x),
                        Round2(y),
                        Round2(width),
                        Round2(height)));
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return dimensions;
    }

    private static bool TryExtractRustBoundsDouble(string line, string name, out double value)
    {
        var raw = TryExtractRustSchemaTokenValue(line, name);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static IReadOnlyList<string> CollectVisibleTexts(IEnumerable<AutomationElement> descendants)
    {
        try
        {
            var texts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in descendants)
            {
                var text = SafeElementName(element).Trim();
                if (text.Length > 0)
                {
                    texts.Add(text);
                }
            }

            return texts.ToArray();
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return [];
        }
    }

    private static IReadOnlyDictionary<string, UiParityControlDimension> CollectVisibleControlDimensions(
        Window window,
        IEnumerable<AutomationElement> descendants)
    {
        try
        {
            var dimensions = new Dictionary<string, UiParityControlDimension>(StringComparer.OrdinalIgnoreCase);
            var dpiScale = Math.Max(0.001, ScreenshotHelper.GetWindowDpiScale(window));
            var windowBounds = window.BoundingRectangle;
            foreach (var element in descendants)
            {
                var automationId = SafeElementAutomationId(element);
                if (string.IsNullOrWhiteSpace(automationId))
                {
                    continue;
                }

                var bounds = element.BoundingRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    continue;
                }

                dimensions[automationId] = new UiParityControlDimension(
                    Kind: SafeControlTypeName(element),
                    Width: FormatDip(bounds.Width / dpiScale),
                    Height: FormatDip(bounds.Height / dpiScale),
                    BoundsDips: new UiParityControlBoundsDips(
                        Left: Round2((bounds.Left - windowBounds.Left) / dpiScale),
                        Top: Round2((bounds.Top - windowBounds.Top) / dpiScale),
                        Width: Round2(bounds.Width / dpiScale),
                        Height: Round2(bounds.Height / dpiScale)));
            }

            return dimensions;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return new Dictionary<string, UiParityControlDimension>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<AutomationElement> FindVisibleDescendantsForSummary(Window window)
    {
        try
        {
            var controlViewDescendants = window
                .FindAllDescendants()
                .Where(IsOnScreenOrUnknown)
                .ToArray();
            if (!ShouldUseRawViewSummaryFallback(controlViewDescendants))
            {
                return controlViewDescendants;
            }

            var rawViewDescendants = EnumerateRawViewDescendants(window)
                .Where(IsOnScreenOrUnknown)
                .ToArray();
            return HasMeaningfulSummaryContent(rawViewDescendants)
                ? rawViewDescendants
                : controlViewDescendants;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return [];
        }
    }

    private static bool ShouldUseRawViewSummaryFallback(IReadOnlyList<AutomationElement> descendants) =>
        descendants.Count == 0 || !HasMeaningfulSummaryContent(descendants);

    private static bool HasMeaningfulSummaryContent(IEnumerable<AutomationElement> descendants) =>
        descendants.Any(element =>
        {
            var automationId = SafeElementAutomationId(element);
            if (!string.IsNullOrWhiteSpace(automationId) &&
                !IsSystemChromeSummaryValue(automationId))
            {
                return true;
            }

            var name = SafeElementName(element).Trim();
            return name.Length > 0 && !IsSystemChromeSummaryValue(name);
        });

    private static bool IsSystemChromeSummaryValue(string value) =>
        value.Equals("SystemMenuBar", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("系统", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("System", StringComparison.OrdinalIgnoreCase);

    private static bool IsUiSummaryScrollBarChrome(AutomationElement element)
    {
        var controlType = SafeControlType(element);
        if (controlType == ControlType.ScrollBar)
        {
            return true;
        }

        var automationId = SafeElementAutomationId(element);
        if (IsScrollBarChromeAutomationId(automationId))
        {
            return true;
        }

        return controlType == ControlType.Button &&
               IsScrollBarChromeSummaryValue(SafeElementName(element));
    }

    private static bool IsScrollBarChromeAutomationId(string automationId) =>
        automationId.StartsWith("Vertical", StringComparison.OrdinalIgnoreCase) ||
        automationId.StartsWith("Horizontal", StringComparison.OrdinalIgnoreCase);

    private static bool IsScrollBarChromeSummaryValue(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Equals("Vertical", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Horizontal", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Vertical ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Horizontal ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("垂直", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("垂直", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("水平", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("水平", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AutomationElement> EnumerateRawViewDescendants(Window window)
    {
        var descendants = new List<AutomationElement>();
        try
        {
            var walker = window.Automation.TreeWalkerFactory.GetRawViewWalker();
            CollectRawViewDescendants(walker, window, descendants, depth: 0);
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return [];
        }

        return descendants;
    }

    private static void CollectRawViewDescendants(
        ITreeWalker walker,
        AutomationElement parent,
        List<AutomationElement> descendants,
        int depth)
    {
        const int MaxRawViewDepth = 64;
        const int MaxRawViewDescendants = 4096;
        if (depth >= MaxRawViewDepth || descendants.Count >= MaxRawViewDescendants)
        {
            return;
        }

        AutomationElement? child;
        try
        {
            child = walker.GetFirstChild(parent);
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return;
        }

        while (child != null && descendants.Count < MaxRawViewDescendants)
        {
            descendants.Add(child);
            CollectRawViewDescendants(walker, child, descendants, depth + 1);

            try
            {
                child = walker.GetNextSibling(child);
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
            {
                return;
            }
        }
    }

    private static UiParityControlDimension ExtractRustSchemaControlDimension(
        string kind,
        string automationId,
        string schemaLine,
        RustSchemaSummaryScope scope)
    {
        var bounds = scope.EstimateBoundsDips(automationId);

        return new UiParityControlDimension(
            Kind: kind,
            State: TryExtractRustSchemaTokenValue(schemaLine, "state"),
            Width: TryExtractRustSchemaTokenValue(schemaLine, "width") ??
                (bounds is null ? null : FormatDip(bounds.Width)),
            LabeledWidth: TryExtractRustSchemaTokenValue(schemaLine, "labeled_width"),
            Height: TryExtractRustSchemaTokenValue(schemaLine, "height") ??
                (bounds is null ? null : FormatDip(bounds.Height)),
            LabeledHeight: TryExtractRustSchemaTokenValue(schemaLine, "labeled_height"),
            MaxWidth: TryExtractRustSchemaTokenValue(schemaLine, "max_width"),
            MinWidth: TryExtractRustSchemaTokenValue(schemaLine, "min_width"),
            MinHeight: TryExtractRustSchemaTokenValue(schemaLine, "min_height"),
            MaxHeight: TryExtractRustSchemaTokenValue(schemaLine, "max_height"),
            Padding: TryExtractRustSchemaTokenValue(schemaLine, "padding"),
            Spacing: TryExtractRustSchemaTokenValue(schemaLine, "spacing"),
            RowSpacing: TryExtractRustSchemaTokenValue(schemaLine, "row_spacing") ??
                TryExtractRustSchemaTokenValue(schemaLine, "run_spacing"),
            ColumnSpacing: TryExtractRustSchemaTokenValue(schemaLine, "column_spacing"),
            Columns: TryExtractRustSchemaTokenValue(schemaLine, "columns"),
            MaximumRowsOrColumns: TryExtractRustSchemaTokenValue(schemaLine, "max_columns"),
            Margin: TryExtractRustSchemaEdgesValue(schemaLine, "margin"),
            BoundsDips: bounds);
    }

    private static string? TryExtractRustSchemaTokenValue(string schemaLine, string name)
    {
        var marker = $" {name}=";
        var markerIndex = schemaLine.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var valueStart = markerIndex + marker.Length;
        var valueEnd = schemaLine.IndexOf(' ', valueStart);
        var value = valueEnd < 0 ? schemaLine[valueStart..] : schemaLine[valueStart..valueEnd];
        return string.IsNullOrWhiteSpace(value) || value == "none" ? null : value;
    }

    private static string? TryExtractRustSchemaEdgesValue(string schemaLine, string name)
    {
        var marker = $" {name}=Edges {{";
        var markerIndex = schemaLine.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return TryExtractRustSchemaTokenValue(schemaLine, name);
        }

        var valueStart = markerIndex + $" {name}=".Length;
        var valueEnd = schemaLine.IndexOf(" }", valueStart, StringComparison.Ordinal);
        return valueEnd < 0
            ? schemaLine[valueStart..]
            : schemaLine[valueStart..(valueEnd + 2)];
    }

    private static ControlType? SafeControlType(AutomationElement element)
    {
        try
        {
            return element.ControlType;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return null;
        }
    }

    private static string SafeControlTypeName(AutomationElement element)
    {
        try
        {
            return element.ControlType.ToString();
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or PropertyNotSupportedException or TimeoutException)
        {
            return "Control";
        }
    }

    private static string FormatDip(double value) =>
        Round2(value).ToString("0.##", CultureInfo.InvariantCulture);

    private static Dictionary<string, int> EmptyControlCounts() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["button"] = 0,
            ["checkbox"] = 0,
            ["comboBox"] = 0,
            ["edit"] = 0,
            ["hyperlink"] = 0,
            ["list"] = 0,
            ["listItem"] = 0,
            ["tabItem"] = 0,
            ["text"] = 0
        };

    private static void IncrementSchemaControlCount(IDictionary<string, int> counts, string kind)
    {
        var bucket = kind switch
        {
            "Button" or "FlyoutButton" or "Expander" => "button",
            "CheckBox" => "checkbox",
            "ToggleSwitch" => "button",
            "ComboBox" => "comboBox",
            "TextEditor" => "edit",
            "Link" or "Hyperlink" => "hyperlink",
            "List" or "ResultList" => "list",
            "ResultCard" => "listItem",
            "Tab" or "TabItem" => "tabItem",
            "Text" => "text",
            _ => null
        };

        if (bucket != null)
        {
            counts[bucket] = counts.TryGetValue(bucket, out var count) ? count + 1 : 1;
        }
    }

    private static bool ShouldSkipRustSchemaControlCount(
        string kind,
        string? automationId,
        string schemaLine)
    {
        // WinUI exposes the floating-window results container as a ScrollViewer/
        // panel rather than UIA ControlType.List. Keep the ResultList id and
        // dimensions for semantic tags, but do not count it as a List control.
        if (kind.Equals("ResultList", StringComparison.Ordinal))
        {
            return true;
        }

        if (!kind.Equals("Text", StringComparison.Ordinal))
        {
            return false;
        }

        var textValue = TryExtractRustSchemaQuotedValue(schemaLine, "value") ?? string.Empty;

        // Help glyphs are FontIcon controls in WinUI, not visible Text controls.
        return IsRustSchemaDecorativeHelpText(automationId, "?") ||
            IsIconGlyphText(textValue) ||
            ShouldSuppressRustLongDocCellHeaderText(automationId);
    }

    private static bool IsRustSchemaDecorativeHelpText(string? automationId, string value) =>
        value.Trim().Equals("?", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(automationId) &&
        automationId.EndsWith("HelpIcon", StringComparison.OrdinalIgnoreCase);

    private static void AddRustSchemaTitleBarSummary(
        IDictionary<string, int> counts,
        ISet<string> ids)
    {
        counts["button"] = counts.TryGetValue("button", out var count) ? count + 3 : 3;
        foreach (var id in new[] { "TitleBar", "SystemMenuBar", "Minimize", "Maximize", "Close" })
        {
            ids.Add(id);
        }
    }

    private static string? TryExtractRustSchemaId(string schemaLine)
    {
        const string marker = " id=";
        var markerIndex = schemaLine.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var value = schemaLine[(markerIndex + marker.Length)..].Trim();
        if (value.Length < 2 || value == "none" || value[0] != '"')
        {
            return null;
        }

        var endQuote = value.IndexOf('"', 1);
        return endQuote <= 1 ? null : value[1..endQuote];
    }

    private static string? TryExtractRustSchemaQuotedValue(string schemaLine, string name)
    {
        var marker = $" {name}=";
        var markerIndex = schemaLine.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var value = schemaLine[(markerIndex + marker.Length)..].TrimStart();
        if (value.Length < 2 || value[0] != '"')
        {
            return null;
        }

        var result = new StringBuilder();
        var escaped = false;
        for (var index = 1; index < value.Length; index++)
        {
            var ch = value[index];
            if (escaped)
            {
                result.Append(ch switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => $"\\{ch}"
                });
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                return result.ToString();
            }

            result.Append(ch);
        }

        return null;
    }

    private static void AddRustSchemaVisibleText(ISet<string> visibleTexts, string? automationId, string value)
    {
        if (ShouldSuppressRustLongDocCellHeaderText(automationId))
        {
            return;
        }

        if (ShouldSplitRustLongDocHintHeaderText(automationId, value))
        {
            var separatorIndex = value.IndexOf(' ');
            visibleTexts.Add(value[..separatorIndex]);
            visibleTexts.Add(value[(separatorIndex + 1)..]);
            return;
        }

        visibleTexts.Add(value);
    }

    private static bool ShouldSuppressRustLongDocCellHeaderText(string? automationId)
    {
        if (string.IsNullOrWhiteSpace(automationId) ||
            !automationId.StartsWith("main.long-doc.", StringComparison.OrdinalIgnoreCase) ||
            !automationId.EndsWith("_cell.label", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return automationId is not "main.long-doc.source_language_cell.label" and
            not "main.long-doc.target_language_cell.label" and
            not "main.long-doc.concurrency_cell.label" and
            not "main.long-doc.page_range_cell.label";
    }

    private static bool ShouldSplitRustLongDocHintHeaderText(string? automationId, string value)
    {
        if (string.IsNullOrWhiteSpace(automationId))
        {
            return false;
        }

        return automationId switch
        {
            "main.long-doc.concurrency_cell.label"
                or "main.long-doc.page_range_cell.label" =>
                value.IndexOf(' ') > 0,
            _ => false
        };
    }

    private static string RustSchemaPendingQueryStatusText() =>
        ResolveParityUiLanguage().Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            ? "点击查询"
            : "Click to query";

    private static bool IsIconGlyphText(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("\\u{", StringComparison.OrdinalIgnoreCase) &&
            trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            return true;
        }

        return trimmed.Length == 1 && trimmed[0] is >= '\uE000' and <= '\uF8FF';
    }

    private sealed class RustSchemaSummaryScope
    {
        private string _currentViewsWindow = string.Empty;
        private int _mainServiceCheckboxCount;
        private bool _lastMainServiceVisible;
        private readonly string _servicesExpandedServiceConfigurations;

        private RustSchemaSummaryScope(string sectionId, string? servicesExpandedServiceConfigurations)
        {
            SectionId = sectionId;
            _servicesExpandedServiceConfigurations = servicesExpandedServiceConfigurations ?? string.Empty;
        }

        public string SectionId { get; }

        public bool IsSettings => !string.IsNullOrWhiteSpace(SectionId);

        public static RustSchemaSummaryScope FromStep(SettingsParityCaptureStep? step) =>
            new(step?.Section.Id ?? string.Empty, step?.RustExpandedServiceConfigurations);

        public void Update(string? automationId)
        {
            if (!IsSettings ||
                !SectionId.Equals("views", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(automationId))
            {
                return;
            }

            _currentViewsWindow = automationId switch
            {
                "settings.views.main" => "main",
                "settings.views.mini" => "mini",
                "settings.views.fixed" => "fixed",
                _ => _currentViewsWindow
            };
            if (automationId is "settings.views.main" or "settings.views.mini" or "settings.views.fixed")
            {
                _lastMainServiceVisible = false;
            }
        }

        public bool ShouldIncludeTitleBarChrome() =>
            !IsSettings ||
            SectionId.Equals("general", StringComparison.OrdinalIgnoreCase) ||
            SectionId.Equals("services", StringComparison.OrdinalIgnoreCase) ||
            SectionId.Equals("views", StringComparison.OrdinalIgnoreCase) ||
            SectionId.Equals("about", StringComparison.OrdinalIgnoreCase);

        public bool ShouldIncludeTextEditorTextAsVisibleText(string? automationId) =>
            !SectionId.Equals("services", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(automationId, "DeepLKeyBox", StringComparison.OrdinalIgnoreCase);

        public bool ShouldIncludeButtonLabelAsVisibleText(string line, string? automationId) =>
            IsSettings ||
            !line.Contains(" kind=Icon ", StringComparison.Ordinal) ||
            IsMainIconButtonWithReferenceAutomationName(automationId);

        public bool ShouldCountButtonLabelAsText(string line, string? automationId) =>
            (IsSettings || !line.Contains(" kind=Icon ", StringComparison.Ordinal)) &&
            !IsMainIconButtonWithReferenceAutomationName(automationId) &&
            !IsLongDocumentButtonLabelOnlyAutomationId(automationId);

        public bool ShouldUseButtonTooltipAsVisibleText(string? automationId) =>
            IsMainIconButtonWithReferenceAutomationName(automationId);

        public bool ShouldIncludeComboBoxLabelAsVisibleText() => IsSettings;

        private static bool IsMainIconButtonWithReferenceAutomationName(string? automationId) =>
            automationId is "PinButton" or
                "SettingsButton" or
                "SwapLanguageButton" or
                "TranslateButton" or
                "main.long-doc.translate" or
                "main.quick.play_source";

        private static bool IsLongDocumentButtonLabelOnlyAutomationId(string? automationId) =>
            automationId is "main.long-doc.browse" or
                "main.long-doc.output_browse" or
                "main.long-doc.retry" or
                "main.long-doc.clear_history";

        public bool ShouldIncludeLine(string kind, string? automationId, string line)
        {
            if (!IsSettings)
            {
                return true;
            }

            if (automationId is "BackButton" or "SettingsHeaderText" or "MainScrollViewer")
            {
                return true;
            }
            if (automationId?.StartsWith("SettingsTab_", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            if (SectionId.Equals("services", StringComparison.OrdinalIgnoreCase))
            {
                return ShouldIncludeServicesLine(kind, automationId);
            }

            if (SectionId.Equals("views", StringComparison.OrdinalIgnoreCase))
            {
                if (automationId is "WindowResultsHeaderText" or
                    "WindowResultsDescriptionText" or
                    "MainWindowReorderModeButton")
                {
                    return true;
                }
                if (IsSettingsViewsMainWindowHeader(line))
                {
                    return true;
                }
                if (kind == "CheckBox" &&
                    _currentViewsWindow == "main" &&
                    IsMainServiceEnabledId(automationId))
                {
                    _mainServiceCheckboxCount++;
                    _lastMainServiceVisible = _mainServiceCheckboxCount <= 16;
                    return _lastMainServiceVisible;
                }
                if (kind == "ToggleSwitch" &&
                    _currentViewsWindow == "main" &&
                    IsMainServiceEnabledQueryId(automationId))
                {
                    return _lastMainServiceVisible;
                }

                return false;
            }

            if (SectionId.Equals("general", StringComparison.OrdinalIgnoreCase))
            {
                return automationId is "SettingsGeneralBehaviorHeader" or
                    "AppThemeCombo" or
                    "AppThemeDescriptionText" or
                    "MinimizeToTrayToggle" or
                    "MinimizeToTrayOnStartupToggle" or
                    "ClipboardMonitorToggle" or
                    "MouseSelectionTranslateToggle" or
                    "MouseSelectionExcludedAppsBox" or
                    "MouseSelectionExcludedAppsDescriptionText" or
                    "AlwaysOnTopToggle" or
                    "LaunchAtStartupToggle" or
                    "HideEmptyServiceResultsToggle" or
                    "EnableLocalDictionarySuggestionsHeader" or
                    "EnableLocalDictionarySuggestionsLabelText" or
                    "ExperimentalLabelText" or
                    "EnableLocalDictionarySuggestionsHintText" or
                    "EnableLocalDictionarySuggestionsToggle" or
                    "TtsSettingsHeaderText" or
                    "TtsSpeedLabelText" or
                    "TtsSpeedSlider" or
                    "AutoPlayTranslationToggle";
            }

            if (SectionId.Equals("about", StringComparison.OrdinalIgnoreCase))
            {
                return automationId is "AboutHeaderText" or
                    "AboutAppNameText" or
                    "VersionText" or
                    "GitHubRepositoryLink" or
                    "IssueFeedbackLink" or
                    "AboutInspiredByText" or
                    "InspiredByLink" or
                    "LicenseText";
            }

            return true;
        }

        public bool ShouldIncludeAutomationId(string? automationId)
        {
            if (string.IsNullOrWhiteSpace(automationId))
            {
                return false;
            }
            if (!IsSettings)
            {
                return IsMainReferenceAutomationId(automationId) ||
                    IsLongDocumentReferenceAutomationId(automationId) ||
                    IsFloatingReferenceAutomationId(automationId) ||
                    IsFloatingRequiredSchemaAutomationId(automationId);
            }
            if (automationId is "BackButton" or "MainScrollViewer" or "SettingsHeaderText")
            {
                return true;
            }
            if (automationId.StartsWith("SettingsTab_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (SectionId.Equals("services", StringComparison.OrdinalIgnoreCase))
            {
                return IsServicesReferenceAutomationId(automationId) ||
                    IsExpandedServicesAutomationId(automationId);
            }
            if (SectionId.Equals("views", StringComparison.OrdinalIgnoreCase))
            {
                return automationId is "WindowResultsHeaderText" or
                    "WindowResultsDescriptionText" or
                    "MainWindowHeaderText" or
                    "MainWindowReorderModeButton";
            }
            if (SectionId.Equals("general", StringComparison.OrdinalIgnoreCase))
            {
                return automationId is "SettingsGeneralBehaviorHeader" or
                    "AppThemeCombo" or
                    "AppThemeDescriptionText" or
                    "MinimizeToTrayToggle" or
                    "MinimizeToTrayOnStartupToggle" or
                    "ClipboardMonitorToggle" or
                    "MouseSelectionTranslateToggle" or
                    "MouseSelectionExcludedAppsBox" or
                    "MouseSelectionExcludedAppsDescriptionText" or
                    "AlwaysOnTopToggle" or
                    "LaunchAtStartupToggle" or
                    "HideEmptyServiceResultsToggle" or
                    "EnableLocalDictionarySuggestionsHeader" or
                    "EnableLocalDictionarySuggestionsLabelText" or
                    "ExperimentalLabelText" or
                    "EnableLocalDictionarySuggestionsHintText" or
                    "EnableLocalDictionarySuggestionsToggle" or
                    "TtsSettingsHeaderText" or
                    "TtsSpeedLabelText" or
                    "TtsSpeedSlider" or
                    "AutoPlayTranslationToggle";
            }
            if (SectionId.Equals("about", StringComparison.OrdinalIgnoreCase))
            {
                return automationId is "AboutHeaderText" or
                    "AboutAppNameText" or
                    "VersionText" or
                    "GitHubRepositoryLink" or
                    "IssueFeedbackLink" or
                    "AboutInspiredByText" or
                    "InspiredByLink" or
                    "LicenseText";
            }

            return true;
        }

        public bool ShouldIncludeRustLayoutEvidenceId(string? automationId) =>
            !IsSettings &&
            automationId is "LongDocControlGrid";

        public UiParityControlDimension AdjustDerivedDimension(
            UiParityControlDimension dimension,
            string derivedAutomationId,
            string sourceAutomationId)
        {
            if (!IsSettings &&
                sourceAutomationId is "main.long-doc.concurrency" or "main.long-doc.page_range" &&
                derivedAutomationId is "LongDocConcurrencyBox" or "LongDocPageRangeBox")
            {
                return dimension with { LabeledHeight = "Fixed(58)" };
            }

            return dimension;
        }

        private static bool IsMainReferenceAutomationId(string automationId) =>
            automationId is "Close" or
                "InputTextBox" or
                "Maximize" or
                "Minimize" or
                "ModeMenuButton" or
                "QuickInputCard" or
                "QuickOutputCard" or
                "QuickTranslateContent" or
                "SettingsButton" or
                "SourceLangCombo" or
                "SwapLanguageButton" or
                "SystemMenuBar" or
                "TargetLangCombo" or
                "TitleBar" or
                "TranslateButton";

        private static bool IsLongDocumentReferenceAutomationId(string automationId) =>
            automationId is "LongDocBrowseButton" or
                "LongDocClearHistoryButton" or
                "LongDocConcurrencyBox" or
                "LongDocConcurrencyHint" or
                "LongDocContent" or
                "LongDocDocumentContextPassCheckBox" or
                "LongDocFilePanel" or
                "LongDocFilePathDisplay" or
                "LongDocHistoryExpander" or
                "LongDocHistoryTitle" or
                "LongDocInputCard" or
                "LongDocInputCardContent" or
                "LongDocInputModeCombo" or
                "LongDocInputModeHint" or
                "LongDocInputTitle" or
                "LongDocOutputBrowseButton" or
                "LongDocOutputCard" or
                "LongDocOutputCardContent" or
                "LongDocOutputFieldsPanel" or
                "LongDocOutputFolderDisplay" or
                "LongDocOutputFolderLabel" or
                "LongDocOutputModeCombo" or
                "LongDocOutputModeHint" or
                "LongDocOutputNamingHint" or
                "LongDocOutputTitle" or
                "LongDocPageRangeBox" or
                "LongDocPageRangeHint" or
                "LongDocRetryButton" or
                "LongDocServiceCombo" or
                "LongDocServiceHint" or
                "LongDocSourceLangCombo" or
                "LongDocStatusText" or
                "LongDocTargetLangCombo" or
                "LongDocTranslateButton";

        private static bool IsFloatingReferenceAutomationId(string automationId) =>
            automationId is "CloseButton" or
                "FixedWindowOcrButton" or
                "InputTextBox" or
                "MainScrollViewer" or
                "MiniWindowCloseButton" or
                "MiniWindowOcrButton" or
                "PinButton" or
                "ResultsScrollViewer" or
                "SourceLangCombo" or
                "SourcePlayButton" or
                "StatusText" or
                "SwapButton" or
                "TargetLangCombo" or
                "TranslateButton";

        private static bool IsFloatingRequiredSchemaAutomationId(string automationId) =>
            automationId is "fixed.results" or
                "fixed.translate" or
                "mini.results" or
                "mini.translate";

        public bool IsSettingsViewsMainWindowHeader(string line) =>
            SectionId.Equals("views", StringComparison.OrdinalIgnoreCase) &&
            _currentViewsWindow == "main" &&
            line.StartsWith("Text ", StringComparison.Ordinal) &&
            line.Contains("style=BodyStrong", StringComparison.Ordinal);

        public void AddDerivedAutomationIdsForExpanderTitle(
            string? automationId,
            string title,
            ISet<string> ids)
        {
            if (!SectionId.Equals("services", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (automationId == "WindowsLocalAIExpander" &&
                title.Equals("Windows Local AI", StringComparison.OrdinalIgnoreCase))
            {
                ids.Add("WindowsLocalAITitleText");
            }
        }

        public void AddDerivedAutomationIds(string kind, string? automationId, ISet<string> ids)
        {
            if (IsSettings || string.IsNullOrWhiteSpace(automationId))
            {
                return;
            }

            switch (automationId)
            {
                case "main.mode_title":
                    ids.Add("ModeEmojiIcon");
                    ids.Add("ModeTitleText");
                    break;
                case "QuickOutputCard":
                    ids.Add("ResultsTitleText");
                    break;
                case "main.quick.play_source":
                    ids.Add("SourcePlayButton");
                    break;
                case "StatusIndicator":
                    ids.Add("StatusText");
                    break;
                case "fixed.input":
                case "mini.input":
                    ids.Add("InputTextBox");
                    break;
                case "fixed.ocr":
                    ids.Add("FixedWindowOcrButton");
                    break;
                case "mini.ocr":
                    ids.Add("MiniWindowOcrButton");
                    break;
                case "mini.pin":
                    ids.Add("PinButton");
                    break;
                case "fixed.source_language":
                case "mini.source_language":
                    ids.Add("SourceLangCombo");
                    break;
                case "fixed.swap":
                case "mini.swap":
                    ids.Add("SwapButton");
                    break;
                case "fixed.target_language":
                case "mini.target_language":
                    ids.Add("TargetLangCombo");
                    break;
                case "fixed.translate":
                case "mini.translate":
                    ids.Add("TranslateButton");
                    break;
                case "fixed.results":
                    ids.Add("ResultsScrollViewer");
                    break;
                case "mini.results":
                    ids.Add("MainScrollViewer");
                    break;
                case "mini.play_source":
                    ids.Add("SourcePlayButton");
                    break;
                case "fixed.status":
                case "mini.status":
                    ids.Add("StatusText");
                    break;
                case "main.long-doc.input_card":
                    ids.Add("LongDocInputCard");
                    break;
                case "main.long-doc.browse":
                    ids.Add("LongDocBrowseButton");
                    break;
                case "main.long-doc.scroll":
                case "main.long-doc.content":
                case "main.long-doc.control_bar":
                    ids.Add("LongDocContent");
                    break;
                case "main.long-doc.source_language":
                    ids.Add("LongDocSourceLangCombo");
                    break;
                case "main.long-doc.target_language":
                    ids.Add("LongDocTargetLangCombo");
                    break;
                case "main.long-doc.service":
                    ids.Add("LongDocServiceCombo");
                    break;
                case "main.long-doc.input_mode":
                    ids.Add("LongDocInputModeCombo");
                    break;
                case "main.long-doc.output_mode":
                    ids.Add("LongDocOutputModeCombo");
                    break;
                case "main.long-doc.concurrency":
                    ids.Add("LongDocConcurrencyBox");
                    break;
                case "main.long-doc.page_range":
                    ids.Add("LongDocPageRangeBox");
                    break;
                case "main.long-doc.translate":
                    ids.Add("LongDocTranslateButton");
                    break;
                case "main.long-doc.output_card":
                    ids.Add("LongDocOutputCard");
                    break;
                case "main.long-doc.retry":
                    ids.Add("LongDocRetryButton");
                    break;
                case "main.long-doc.output_content":
                    ids.Add("LongDocOutputFieldsPanel");
                    break;
                case "main.long-doc.output_folder_label":
                    ids.Add("LongDocOutputFolderLabel");
                    break;
                case "main.long-doc.output_folder":
                    ids.Add("LongDocOutputFolderDisplay");
                    break;
                case "main.long-doc.output_browse":
                    ids.Add("LongDocOutputBrowseButton");
                    break;
                case "main.long-doc.output_naming_hint":
                    ids.Add("LongDocOutputNamingHint");
                    break;
                case "main.long-doc.history":
                    ids.Add("LongDocHistoryExpander");
                    break;
                case "main.long-doc.clear_history":
                    ids.Add("LongDocClearHistoryButton");
                    break;
            }

            if (kind == "ResultItem" &&
                MainResultAutomationSuffix(automationId) is { Length: > 0 } suffix)
            {
                ids.Add("ServiceIcon");
                ids.Add("ServiceNameText");
                ids.Add($"ServiceResultHeader_{suffix}");
                ids.Add($"ServiceResultItem_{suffix}");
            }
        }

        private static string? MainResultAutomationSuffix(string automationId) =>
            automationId switch
            {
                "bing" => "bing",
                "deepl" => "deepl",
                "google" => "google",
                "volcano" => "volcano",
                "windows-local-ai" => "windows-local-ai",
                "mdx::collins-cobuild-english-usage" => "mdx__collins-cobuild-english-usage-c9ef3413",
                _ => null
            };

        public UiParityControlBoundsDips? EstimateBoundsDips(string automationId)
        {
            if (!SectionId.Equals("services", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (automationId.StartsWith("SettingsTab_", StringComparison.OrdinalIgnoreCase))
            {
                var index = automationId switch
                {
                    "SettingsTab_General" => 0,
                    "SettingsTab_Services" => 1,
                    "SettingsTab_Views" => 2,
                    "SettingsTab_Hotkeys" => 3,
                    "SettingsTab_Advanced" => 4,
                    "SettingsTab_Language" => 5,
                    "SettingsTab_About" => 6,
                    _ => -1
                };
                if (index >= 0)
                {
                    return new UiParityControlBoundsDips(32 + (index * 96), 117, 86, 76);
                }
            }

            var servicesTopBounds = automationId switch
            {
                "EnabledServicesHeaderText" => new UiParityControlBoundsDips(32, 227, 111, 24),
                "EnabledServicesDescriptionText" => new UiParityControlBoundsDips(32, 263, 796, 16),
                "ImportMdxDictionaryButton" => new UiParityControlBoundsDips(32, 291, 165, 29),
                "ImportedMdxSummaryText" => new UiParityControlBoundsDips(205, 296, 166, 19),
                "EnableInternationalServicesHeaderText" => new UiParityControlBoundsDips(45, 352, 704, 18),
                "EnableInternationalServicesToggle" => new UiParityControlBoundsDips(749, 341, 66, 40),
                "EnableInternationalServicesDescriptionText" => new UiParityControlBoundsDips(45, 385, 770, 15),
                "ServiceConfigurationHeaderText" => new UiParityControlBoundsDips(32, 433, 74, 24),
                "ServiceConfigurationDescriptionText" => new UiParityControlBoundsDips(32, 469, 796, 16),
                _ => null
            };
            if (servicesTopBounds is not null)
            {
                return servicesTopBounds;
            }

            var expanderBounds = EstimateServiceExpanderBounds(automationId);
            if (expanderBounds is not null)
            {
                return expanderBounds;
            }

            if (HasExpandedService("deepl"))
            {
                var deepLBounds = automationId switch
                {
                    "DeepLKeyBox" => new UiParityControlBoundsDips(49, 592, 350, 32),
                    "DeepLKeyRevealButton" => new UiParityControlBoundsDips(365, 594, 28, 28),
                    "DeepLFreeCheck" => new UiParityControlBoundsDips(49, 636, 277, 32),
                    "DeepLQualityCheck" => new UiParityControlBoundsDips(49, 680, 347, 32),
                    "TestDeepLButton" => new UiParityControlBoundsDips(49, 752, 46, 29),
                    _ => null
                };
                if (deepLBounds is not null)
                {
                    return deepLBounds;
                }
            }

            if (HasExpandedService("windows-local-ai"))
            {
                var localAiBounds = automationId switch
                {
                    "LocalAIProviderCombo" => new UiParityControlBoundsDips(45, 650, 528, 48),
                    "FoundryLocalEndpointBox" => new UiParityControlBoundsDips(49, 763, 762, 59),
                    "FoundryLocalModelBox" => new UiParityControlBoundsDips(49, 832, 762, 56),
                    _ => null
                };
                if (localAiBounds is not null)
                {
                    return localAiBounds;
                }
            }

            return null;
        }

        private UiParityControlBoundsDips? EstimateServiceExpanderBounds(string automationId)
        {
            var ordered = new[]
            {
                "DeepLServiceExpander",
                "WindowsLocalAIExpander",
                "OllamaServiceExpander",
                "OpenAIServiceExpander",
                "DeepSeekServiceExpander",
                "GroqServiceExpander",
                "ZhipuServiceExpander"
            };
            var top = 497;
            foreach (var id in ordered)
            {
                var height = EstimatedServiceExpanderHeight(id);
                if (id.Equals(automationId, StringComparison.OrdinalIgnoreCase))
                {
                    return new UiParityControlBoundsDips(32, top, 796, height);
                }

                top += height + 12;
            }

            return null;
        }

        private int EstimatedServiceExpanderHeight(string automationId) =>
            automationId switch
            {
                "DeepLServiceExpander" when HasExpandedService("deepl") => 309,
                "WindowsLocalAIExpander" when HasExpandedService("windows-local-ai") => 331,
                _ => 48
            };

        private bool IsServiceExpanderInTopViewport(string automationId)
        {
            var bounds = EstimateServiceExpanderBounds(automationId);
            return bounds is not null && bounds.Top < 870;
        }

        private bool ShouldIncludeServicesLine(string kind, string? automationId)
        {
            if (kind == "Expander")
            {
                return !string.IsNullOrWhiteSpace(automationId) &&
                    (IsServiceExpanderInTopViewport(automationId) ||
                        IsExpandedServiceExpander(automationId));
            }

            if (string.IsNullOrWhiteSpace(automationId))
            {
                return false;
            }

            return automationId is "EnabledServicesHeaderText" or
                    "EnabledServicesDescriptionText" or
                    "ImportMdxDictionaryButton" or
                    "ImportedMdxSummaryText" or
                    "EnableInternationalServicesHeaderText" or
                    "EnableInternationalServicesToggle" or
                    "EnableInternationalServicesDescriptionText" or
                    "ServiceConfigurationHeaderText" or
                    "ServiceConfigurationDescriptionText" or
                    "WindowsLocalAIStatusBadge" ||
                IsExpandedServicesAutomationId(automationId);
        }

        private static bool IsServicesReferenceAutomationId(string automationId) =>
            IsKnownServiceExpander(automationId) ||
            automationId is "EnabledServicesDescriptionText" or
                "EnabledServicesHeaderText" or
                "EnableInternationalServicesDescriptionText" or
                "EnableInternationalServicesHeaderText" or
                "EnableInternationalServicesToggle" or
                "ImportedMdxSummaryText" or
                "ImportMdxDictionaryButton" or
                "ServiceConfigurationDescriptionText" or
                "ServiceConfigurationHeaderText" or
                "WindowsLocalAIStatusBadge" or
                "WindowsLocalAITitleText";

        private bool IsExpandedServicesAutomationId(string automationId)
        {
            if (HasExpandedService("deepl") &&
                automationId is "DeepLKeyHeaderText" or
                    "DeepLKeyBox" or
                    "DeepLKeyRevealButton" or
                    "DeepLFreeCheck" or
                    "DeepLQualityCheck" or
                    "DeepLDescriptionText" or
                    "TestDeepLButton")
            {
                return true;
            }

            if (HasExpandedService("windows-local-ai") &&
                automationId is "LocalAIProviderLabelText" or
                    "LocalAIProviderCombo" or
                    "WindowsLocalAIDescriptionText" or
                    "WindowsLocalAIConfigPanel" or
                    "WindowsLocalAISectionTitleText" or
                    "WindowsLocalAISectionRatingText" or
                    "WindowsLocalAIStatusBar" or
                    "WindowsLocalAIPrepareButton" or
                    "FoundryLocalConfigPanel" or
                    "FoundryLocalTitleText" or
                    "FoundryLocalRatingText" or
                    "FoundryLocalEndpointLabelText" or
                    "FoundryLocalEndpointBox" or
                    "FoundryLocalModelLabelText" or
                    "FoundryLocalModelBox" or
                    "FoundryLocalStatusBar" or
                    "FoundryLocalStartButton" or
                    "FoundryLocalInstallLink" or
                    "FoundryLocalDocsLink" or
                    "FoundryLocalDescriptionText" or
                    "OpenVinoConfigPanel" or
                    "OpenVinoTitleText" or
                    "OpenVinoRatingText" or
                    "OpenVinoStatusBadge" or
                    "OpenVinoStatusBar" or
                    "OpenVinoDownloadButton" or
                    "OpenVinoDescriptionText")
            {
                return true;
            }

            foreach (var serviceId in ExpandedServiceIds())
            {
                if (ExpandedServiceConfigurationAutomationIds(serviceId)
                    .Contains(automationId, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsExpandedServiceExpander(string automationId) =>
            ExpandedServiceIds()
                .Select(ServiceExpanderAutomationId)
                .Any(id => id.Equals(automationId, StringComparison.OrdinalIgnoreCase));

        private IEnumerable<string> ExpandedServiceIds() =>
            _servicesExpandedServiceConfigurations
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        private static IReadOnlyList<string> ExpandedServiceConfigurationAutomationIds(string serviceId)
        {
            return serviceId.Trim().ToLowerInvariant() switch
            {
                "ollama" =>
                [
                    "OllamaEndpointBox",
                    "OllamaModelCombo",
                    "RefreshOllamaButton",
                    "TestOllamaButton"
                ],
                "openai" =>
                [
                    "OpenAIKeyHeaderText",
                    "OpenAIKeyBox",
                    "OpenAIKeyRevealButton",
                    "OpenAIEndpointBox",
                    "OpenAIApiFormatCombo",
                    "OpenAIDetectedFormatText",
                    "OpenAIModelCombo",
                    "TestOpenAIButton"
                ],
                "deepseek" =>
                [
                    "DeepSeekKeyHeaderText",
                    "DeepSeekKeyBox",
                    "DeepSeekKeyRevealButton",
                    "DeepSeekModelCombo",
                    "TestDeepSeekButton"
                ],
                "groq" =>
                [
                    "GroqKeyHeaderText",
                    "GroqKeyBox",
                    "GroqKeyRevealButton",
                    "GroqModelCombo",
                    "TestGroqButton"
                ],
                "zhipu" =>
                [
                    "ZhipuKeyHeaderText",
                    "ZhipuKeyBox",
                    "ZhipuKeyRevealButton",
                    "ZhipuModelCombo",
                    "TestZhipuButton"
                ],
                "github" =>
                [
                    "GitHubModelsTokenHeaderText",
                    "GitHubModelsTokenBox",
                    "GitHubModelsTokenRevealButton",
                    "GitHubModelsModelCombo",
                    "TestGitHubModelsButton"
                ],
                "gemini" =>
                [
                    "GeminiKeyHeaderText",
                    "GeminiKeyBox",
                    "GeminiKeyRevealButton",
                    "GeminiModelCombo",
                    "TestGeminiButton"
                ],
                "custom-openai" =>
                [
                    "CustomOpenAIKeyHeaderText",
                    "CustomOpenAIKeyBox",
                    "CustomOpenAIKeyRevealButton",
                    "CustomOpenAIEndpointBox",
                    "CustomOpenAIModelBox",
                    "TestCustomOpenAIButton"
                ],
                "builtin" =>
                [
                    "BuiltInApiKeyHeaderText",
                    "BuiltInApiKeyBox",
                    "BuiltInApiKeyRevealButton",
                    "BuiltInModelCombo",
                    "TestBuiltInButton"
                ],
                "doubao" =>
                [
                    "DoubaoKeyHeaderText",
                    "DoubaoKeyBox",
                    "DoubaoKeyRevealButton",
                    "DoubaoEndpointBox",
                    "DoubaoModelBox",
                    "TestDoubaoButton"
                ],
                "caiyun" =>
                [
                    "CaiyunKeyHeaderText",
                    "CaiyunKeyBox",
                    "CaiyunKeyRevealButton",
                    "TestCaiyunButton"
                ],
                "niutrans" =>
                [
                    "NiuTransKeyHeaderText",
                    "NiuTransKeyBox",
                    "NiuTransKeyRevealButton",
                    "TestNiuTransButton"
                ],
                "youdao" =>
                [
                    "YoudaoAppKeyHeaderText",
                    "YoudaoAppKeyBox",
                    "YoudaoAppKeyRevealButton",
                    "YoudaoAppSecretHeaderText",
                    "YoudaoAppSecretBox",
                    "YoudaoAppSecretRevealButton",
                    "YoudaoUseOfficialApiToggle"
                ],
                "volcano" =>
                [
                    "VolcanoAccessKeyIdHeaderText",
                    "VolcanoAccessKeyIdBox",
                    "VolcanoAccessKeyIdRevealButton",
                    "VolcanoSecretAccessKeyHeaderText",
                    "VolcanoSecretAccessKeyBox",
                    "VolcanoSecretAccessKeyRevealButton",
                    "TestVolcanoButton"
                ],
                _ => []
            };
        }

        private bool HasExpandedService(string serviceId) =>
            ExpandedServiceIds()
                .Any(value => value.Equals(serviceId, StringComparison.OrdinalIgnoreCase));

        private static int ServicesExpanderViewportIndex(string automationId) =>
            automationId switch
            {
                "DeepLServiceExpander" => 0,
                "WindowsLocalAIExpander" => 1,
                "OllamaServiceExpander" => 2,
                "OpenAIServiceExpander" => 3,
                "DeepSeekServiceExpander" => 4,
                "GroqServiceExpander" => 5,
                "ZhipuServiceExpander" => 6,
                "GitHubModelsServiceExpander" => 7,
                "GeminiServiceExpander" => 8,
                "CustomOpenAIServiceExpander" => 9,
                "BuiltInAIServiceExpander" => 10,
                "DoubaoServiceExpander" => 11,
                "CaiyunServiceExpander" => 12,
                "NiuTransServiceExpander" => 13,
                "YoudaoServiceExpander" => 14,
                "VolcanoServiceExpander" => 15,
                _ => -1
            };

        private static bool IsKnownServiceExpander(string automationId) =>
            ServicesExpanderViewportIndex(automationId) >= 0;

        private static string ServiceExpanderAutomationId(string serviceId) =>
            serviceId.Trim().ToLowerInvariant() switch
            {
                "deepl" => "DeepLServiceExpander",
                "windows-local-ai" => "WindowsLocalAIExpander",
                "ollama" => "OllamaServiceExpander",
                "openai" => "OpenAIServiceExpander",
                "deepseek" => "DeepSeekServiceExpander",
                "groq" => "GroqServiceExpander",
                "zhipu" => "ZhipuServiceExpander",
                "github" => "GitHubModelsServiceExpander",
                "gemini" => "GeminiServiceExpander",
                "custom-openai" => "CustomOpenAIServiceExpander",
                "builtin" => "BuiltInAIServiceExpander",
                "doubao" => "DoubaoServiceExpander",
                "caiyun" => "CaiyunServiceExpander",
                "niutrans" => "NiuTransServiceExpander",
                "youdao" => "YoudaoServiceExpander",
                "volcano" => "VolcanoServiceExpander",
                _ => string.Empty
            };

        private static bool IsMainServiceEnabledId(string? automationId) =>
            automationId?.StartsWith("main.", StringComparison.OrdinalIgnoreCase) == true &&
            automationId.EndsWith(".enabled", StringComparison.OrdinalIgnoreCase) &&
            !automationId.EndsWith(".enabled_query", StringComparison.OrdinalIgnoreCase);

        private static bool IsMainServiceEnabledQueryId(string? automationId) =>
            automationId?.StartsWith("main.", StringComparison.OrdinalIgnoreCase) == true &&
            automationId.EndsWith(".enabled_query", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountDescendants(IEnumerable<AutomationElement> descendants, ControlType controlType) =>
        descendants.Count(element =>
            SafeControlType(element) == controlType);

    private static IReadOnlyList<string> CollectVisibleAutomationIds(IEnumerable<AutomationElement> descendants)
    {
        try
        {
            return descendants
                .Select(element =>
                {
                    try
                    {
                        return element.AutomationId;
                    }
                    catch (Exception ex) when (ex is COMException or PropertyNotSupportedException)
                    {
                        return string.Empty;
                    }
                })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is COMException or PropertyNotSupportedException or TimeoutException)
        {
            return [];
        }
    }

    private static void SaveManifest(IReadOnlyList<UiParityManifestEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var path = Path.Combine(ScreenshotHelper.OutputDir, "ui-parity-manifest.json");
        var mergedEntries = LoadExistingManifestEntries(path)
            .Concat(entries)
            .GroupBy(entry => entry.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(entry => entry.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mergedEntries.Any(entry => string.IsNullOrWhiteSpace(entry.RuntimeDiagnosticsPath)))
        {
            throw new RustPreviewControlException(
                "preview-invalid-session",
                "Manifest v2 scenarios require a capture-relative runtimeDiagnosticsPath.");
        }
        var manifest = new UiParityManifest(
            SchemaVersion: "easydict.ui-parity-manifest.v2",
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
            UiLanguage: ResolveParityUiLanguage(),
            Scenarios: mergedEntries);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void EnsureParityDpiAwareness()
    {
        if (_parityDpiAwarenessAttempted)
        {
            return;
        }

        _parityDpiAwarenessAttempted = true;
        try
        {
            if (SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2))
            {
                _output.WriteLine("Parity DPI awareness: PerMonitorV2 enabled for physical-pixel capture.");
                return;
            }

            _output.WriteLine(
                $"Parity DPI awareness: SetProcessDpiAwarenessContext was ignored or denied, lastError={Marshal.GetLastWin32Error()}.");
        }
        catch (EntryPointNotFoundException)
        {
            _output.WriteLine("Parity DPI awareness: SetProcessDpiAwarenessContext is unavailable on this Windows build.");
        }
    }

    private static IReadOnlyList<UiParityManifestEntry> LoadExistingManifestEntries(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<UiParityManifest>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return string.Equals(
                manifest?.SchemaVersion,
                "easydict.ui-parity-manifest.v2",
                StringComparison.Ordinal)
                ? manifest!.Scenarios
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ToOutputRelativePath(string path)
    {
        var relative = Path.GetRelativePath(
                Path.GetFullPath(ScreenshotHelper.OutputDir),
                Path.GetFullPath(path))
            .Replace('\\', '/');
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith("../", StringComparison.Ordinal))
        {
            throw new RustPreviewControlException(
                "preview-invalid-artifact-stem",
                $"Artifact path is outside the capture root: {path}");
        }
        return relative;
    }

    private static string? RuntimeDiagnosticsPathForSchema(string? schemaPath)
    {
        if (string.IsNullOrWhiteSpace(schemaPath))
        {
            return null;
        }

        lock (RustMetricsLock)
        {
            return RustDiagnosticsBySchemaPath.TryGetValue(schemaPath, out var diagnosticsPath)
                ? ToOutputRelativePath(diagnosticsPath)
                : null;
        }
    }

    private static string? RustBoundsPathForSchema(string? schemaPath)
    {
        if (string.IsNullOrWhiteSpace(schemaPath))
        {
            return null;
        }

        lock (RustMetricsLock)
        {
            if (RustBoundsBySchemaPath.TryGetValue(schemaPath, out var renderedBoundsPath))
            {
                return renderedBoundsPath;
            }
        }

        const string schemaSuffix = "-rust-view-schema.txt";
        const string boundsSuffix = "-rust-view-bounds.txt";
        return schemaPath.EndsWith(schemaSuffix, StringComparison.OrdinalIgnoreCase)
            ? schemaPath[..^schemaSuffix.Length] + boundsSuffix
            : Path.ChangeExtension(schemaPath, ".rust-view-bounds.txt");
    }

    private static void AssertImageHasVisibleContent(string path)
    {
        using var bitmap = new Bitmap(path);
        var distinct = new HashSet<int>();
        var sampled = 0;

        var stepX = Math.Max(1, bitmap.Width / 96);
        var stepY = Math.Max(1, bitmap.Height / 96);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                distinct.Add(bitmap.GetPixel(x, y).ToArgb());
                sampled++;
            }
        }

        sampled.Should().BeGreaterThan(0, $"{path} should be sampled");
        distinct.Count.Should().BeGreaterThan(8, $"{path} should not be a blank or single-color capture");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void SeedDotnetParitySettings()
    {
        var settingsPath = UiaSettingsIsolation.TryGetSettingsFilePath();
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            return;
        }

        Dictionary<string, object?> settings = new(StringComparer.Ordinal);
        if (File.Exists(settingsPath))
        {
            try
            {
                var json = File.ReadAllText(settingsPath);
                settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? settings;
            }
            catch (JsonException)
            {
                settings = new Dictionary<string, object?>(StringComparer.Ordinal);
            }
        }

        settings["UILanguage"] = ResolveParityUiLanguage();
        settings["AppTheme"] = ResolveDotnetAppThemeSetting();
        settings["FirstLanguage"] = "zh-tw";
        settings["SecondLanguage"] = "en";
        settings["SelectedLanguages"] = new[] { "zh-tw", "zh", "en", "ja", "ko", "fr", "de", "es" };
        settings["AutoSelectTargetLanguage"] = false;
        settings["SourceLanguage"] = "auto";
        settings["MouseSelectionTranslate"] = true;
        settings["MouseSelectionExcludedApps"] = new[] { "code" };
        settings["MinimizeToTray"] = false;
        settings["MinimizeToTrayOnStartup"] = false;
        settings["EnableShowWindowHotkey"] = false;
        settings["EnableTranslateSelectionHotkey"] = false;
        settings["EnableShowMiniWindowHotkey"] = false;
        settings["EnableShowFixedWindowHotkey"] = false;
        settings["EnableOcrTranslateHotkey"] = false;
        settings["EnableSilentOcrHotkey"] = false;
        settings["EnableInternationalServices"] = true;
        settings["OllamaEndpoint"] = "http://localhost:11434/v1/chat/completions";
        settings["OllamaModel"] = "llama3.2";
        settings["WindowWidthDips"] = 846.0;
        settings["WindowHeightDips"] = 913.0;
        var settingsDirectory = Path.GetDirectoryName(settingsPath)!;
        var parityMdxPath = Path.Combine(settingsDirectory, "Collins COBUILD English Usage.mdx");
        File.WriteAllBytes(parityMdxPath, Array.Empty<byte>());

        const string parityMdxServiceId = "mdx::collins-cobuild-english-usage-c9ef3413";
        const string parityMdxDisplayName = "📚 Collins COBUILD English Usage";
        settings["ImportedMdxDictionaries"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["ServiceId"] = parityMdxServiceId,
                ["DisplayName"] = parityMdxDisplayName,
                ["FilePath"] = parityMdxPath,
                ["IsEncrypted"] = false,
                ["Regcode"] = null,
                ["Email"] = null,
                ["MddFilePaths"] = Array.Empty<string>()
            }
        };
        settings["MiniWindowEnabledServices"] = new[] { "bing", parityMdxServiceId };
        settings["MainWindowEnabledServices"] = new[]
        {
            "bing",
            "windows-local-ai",
            parityMdxServiceId,
            "google",
            "volcano",
            "deepl"
        };
        settings["FixedWindowEnabledServices"] = new[] { "bing", parityMdxServiceId };

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsTruthy(string? value)
    {
        return value != null &&
               (string.Equals(value, "1", StringComparison.Ordinal) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveParityUiLanguage()
    {
        var value = Environment.GetEnvironmentVariable(UiLanguageEnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? "zh-CN" : value.Trim();
    }

    private static string ResolveRustPreviewTheme(string defaultTheme)
    {
        var value = Environment.GetEnvironmentVariable(ThemeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultTheme;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "light" => "light",
            "dark" => "dark",
            "system" => "system",
            _ => defaultTheme
        };
    }

    private static string ResolveDotnetAppThemeSetting()
    {
        return ResolveRustPreviewTheme("system") switch
        {
            "light" => "Light",
            "dark" => "Dark",
            _ => "System"
        };
    }

    private static string ResolveDotnetReferenceExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("EASYDICT_EXE_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var binDirectory = Path.Combine(
            FindRepositoryRootForParity(),
            "dotnet",
            "src",
            "Easydict.WinUI",
            "bin");
        var candidate = Directory.Exists(binDirectory)
            ? Directory
                .EnumerateFiles(binDirectory, "Easydict.WinUI.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        return candidate
            ?? AppLauncher.TryGetInstalledExecutablePath()
            ?? throw new FileNotFoundException(
                "Build dotnet/src/Easydict.WinUI or install Easydict before running the mode-popup parity test.",
                binDirectory);
    }

    private static string ResolveDotnetWinUiVersion()
    {
        var projectPath = Path.Combine(
            FindRepositoryRootForParity(),
            "dotnet",
            "src",
            "Easydict.WinUI",
            "Easydict.WinUI.csproj");
        if (!File.Exists(projectPath))
        {
            return string.Empty;
        }

        try
        {
            var document = System.Xml.Linq.XDocument.Load(projectPath);
            return document
                .Descendants("Version")
                .Select(element => element.Value.Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? string.Empty;
        }
        catch (System.Xml.XmlException)
        {
            return string.Empty;
        }
    }

    private static string FindRepositoryRootForParity()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = Path.GetFullPath(start);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(Path.Combine(current, ".git")) ||
                    File.Exists(Path.Combine(current, ".git")))
                {
                    return current;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent ?? string.Empty;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static bool IsExplicitFalse(string? value)
    {
        return value != null &&
               (string.Equals(value, "0", StringComparison.Ordinal) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "off", StringComparison.OrdinalIgnoreCase));
    }

    private static uint? SafeGetDpiForWindow(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? null : dpi;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var session in _rustPreviewSessions.Values)
        {
            session.Dispose();
        }
        _rustPreviewSessions.Clear();
        _dotnetLauncher.Dispose();
    }

    private sealed record RustComboBoxMouseSelectionCase(
        string Scenario,
        float WidthDips,
        float HeightDips,
        string ControlId,
        string OptionText,
        int OptionRow,
        int SelectedRow,
        int ItemCount,
        string ExpectedDebugLine,
        IReadOnlyDictionary<string, string> EnvironmentOverrides,
        string PreviewWindow = "main",
        bool ScrollAwayBeforeSelection = false);

    private sealed record RustComboOverlayGeometry(
        string ControlId,
        double CollapsedLeft,
        double CollapsedTop,
        double CollapsedWidth,
        double CollapsedHeight,
        double MenuLeft,
        double MenuTop,
        double MenuWidth,
        double MenuHeight,
        double RowHeight,
        int SelectedIndex,
        bool ViewportClamped);

    private enum RustPreviewDefaultExecutableResolution
    {
        Build,
        Existing,
        Missing
    }

    private static RustPreviewDefaultExecutableResolution SelectRustPreviewDefaultExecutable(
        bool buildRequested,
        bool defaultExecutableExists)
    {
        if (buildRequested)
        {
            return RustPreviewDefaultExecutableResolution.Build;
        }

        return defaultExecutableExists
            ? RustPreviewDefaultExecutableResolution.Existing
            : RustPreviewDefaultExecutableResolution.Missing;
    }

    private sealed record SettingsParitySection(string Id, string Label, string DotnetReadyElement)
    {
        public static readonly SettingsParitySection General = new("general", "General", "AppThemeCombo");

        public static readonly SettingsParitySection Services = new("services", "Services", "DeepLServiceExpander");
        public static readonly SettingsParitySection Views = new("views", "Views", "MainWindowReorderModeButton");
        public static readonly SettingsParitySection Hotkeys = new("hotkeys", "Hotkeys", "ShowHotkeyBox");
        public static readonly SettingsParitySection Advanced = new("advanced", "Advanced", "OcrEngineCombo");
        public static readonly SettingsParitySection Language = new("language", "Language", "FirstLanguageCombo");
        public static readonly SettingsParitySection About = new("about", "About", "GitHubRepositoryLink");
    }

    private sealed record SettingsDropdownOptionCapture(
        string Label,
        int DotnetIndex,
        int? RustIndex = null,
        string? DotnetText = null,
        string? RustText = null)
    {
        public string DotnetOptionText => DotnetText ?? Label;
        public string RustOptionText => RustText ?? DotnetOptionText;
        public int RustOptionIndexValue => RustIndex ?? DotnetIndex;
    }

    private sealed record SettingsDropdownOptionCaptureResult(
        SettingsDropdownOptionCapture Option,
        string ScenarioId,
        string ScreenshotPath,
        Rectangle WindowBounds,
        double WindowDpiScale,
        Size ScreenshotPixelSize);

    private sealed record DropdownOptionCapturePair(
        SettingsDropdownOptionCaptureResult Dotnet,
        SettingsDropdownOptionCaptureResult Rust);

    private readonly record struct MainOperationsCaptureScope(
        bool CaptureButtons,
        bool CaptureDropdowns,
        bool CaptureDropdownOptions);

    private sealed record MainInteractionCapture(
        string Key,
        string Label,
        string DotnetElement,
        string RustElement,
        string RustControlId,
        double FallbackX,
        double FallbackY,
        IReadOnlyList<UiParityRegion> Regions);

    private sealed record MainDropdownCapture(
        string Key,
        string Label,
        string DotnetElement,
        string RustElement,
        string RustControlId,
        string RustSelectedLanguageEnvironmentVariable,
        double FallbackX,
        double FallbackY,
        int RestoreOptionIndex,
        IReadOnlyList<SettingsDropdownOptionCapture> Options,
        IReadOnlyList<UiParityRegion> Regions,
        double FallbackWidthDips = 280,
        double FallbackHeightDips = 34);
    private readonly record struct FloatingCaptureScope(
        bool CaptureTranslateButton,
        bool CaptureDropdowns,
        bool CaptureDropdownOptions,
        bool CaptureControls);

    private sealed record FloatingInteractionCapture(
        string Key,
        string Label,
        string DotnetElement,
        string RustStateEnvironmentVariable,
        string RustControlId,
        double FallbackX,
        double FallbackY,
        IReadOnlyList<UiParityRegion> Regions);

    private sealed record FloatingDropdownCapture(
        string Key,
        string Label,
        string DotnetElement,
        string RustElement,
        string RustControlId,
        string RustSelectedLanguageEnvironmentVariable,
        double FallbackX,
        double FallbackY,
        int RestoreOptionIndex,
        IReadOnlyList<SettingsDropdownOptionCapture> Options,
        double FallbackWidthDips = 280,
        double FallbackHeightDips = 34);

    private sealed record SettingsParityCaptureStep(
        string Key,
        SettingsParitySection Section,
        double ScrollPercent,
        bool ExpandAvailableLanguages = false,
        bool RustTranslationLanguagesExpanded = false,
        SettingsParitySection? HoveredTab = null,
        SettingsParitySection? PressedTab = null,
        string? HoveredElement = null,
        string? FocusedElement = null,
        string? PressedElement = null,
        string? ExpandedDropdownElement = null,
        IReadOnlyList<string>? ExpectedDropdownItems = null,
        IReadOnlyList<SettingsDropdownOptionCapture>? DropdownOptions = null,
        bool CaptureDropdownOptions = true,
        int? DropdownRestoreOptionIndex = 0,
        bool ForceDropdownFallbackClick = false,
        int DropdownFallbackItemCount = 0,
        double DropdownOptionRowHeightDips = 34,
        double DropdownFallbackWidthDips = 280,
        double DropdownFallbackHeightDips = 34,
        string? RustTtsSpeedState = null,
        string? RustAutoPlayState = null,
        string? RustImportMdxState = null,
        string? RustInternationalToggleState = null,
        string? RustDeepLExpanderState = null,
        string? RustServiceExpanderState = null,
        string? DotnetExpandElement = null,
        string? RustExpandedServiceConfigurations = null,
        string? RustLocalAiProvider = null,
        string? RequiredStateElement = null,
        string? BaselineScenarioId = null,
        double InteractionFallbackX = 0.50,
        double InteractionFallbackY = 0.62)
    {
        public bool CapturesExpandedDropdown => !string.IsNullOrWhiteSpace(ExpandedDropdownElement);

        public IReadOnlyList<string> DropdownExpectedItems => ExpectedDropdownItems ?? [];

        public IReadOnlyList<SettingsDropdownOptionCapture> DropdownOptionCaptures =>
            DropdownOptions ??
            DropdownExpectedItems
                .Select((item, index) => new SettingsDropdownOptionCapture(item, index))
                .ToArray();

        public bool CapturesDropdownOptionSelections =>
            CaptureDropdownOptions && CapturesExpandedDropdown && DropdownOptionCaptures.Count > 0;

        public static readonly IReadOnlyList<SettingsParityCaptureStep> All =
        [
            new("parity-settings-general-behavior-top", SettingsParitySection.General, 0),
            new(
                "parity-settings-general-app-theme-dropdown-open",
                SettingsParitySection.General,
                0,
                ExpandedDropdownElement: "AppThemeCombo",
                ExpectedDropdownItems: ThemeDropdownExpectedItems(),
                DropdownFallbackItemCount: ThemeDropdownExpectedItems().Count,
                BaselineScenarioId: "parity-settings-general-behavior-top",
                InteractionFallbackX: 0.24,
                InteractionFallbackY: 0.25),
            new(
                "parity-settings-tabs-services-hover",
                SettingsParitySection.General,
                0,
                HoveredTab: SettingsParitySection.Services,
                BaselineScenarioId: "parity-settings-general-behavior-top"),
            new(
                "parity-settings-tabs-views-pressed",
                SettingsParitySection.General,
                0,
                PressedTab: SettingsParitySection.Views,
                BaselineScenarioId: "parity-settings-general-behavior-top"),
            new("parity-settings-general-tts-speed-slider-scroll-100-percent", SettingsParitySection.General, 100),
            new(
                "parity-settings-general-tts-speed-slider-hover-scroll-100-percent",
                SettingsParitySection.General,
                100,
                HoveredElement: "TtsSpeedSlider",
                RustTtsSpeedState: "hovered",
                BaselineScenarioId: "parity-settings-general-tts-speed-slider-scroll-100-percent",
                InteractionFallbackX: 0.36,
                InteractionFallbackY: 0.64),
            new(
                "parity-settings-general-tts-speed-slider-focus-scroll-100-percent",
                SettingsParitySection.General,
                100,
                FocusedElement: "TtsSpeedSlider",
                RustTtsSpeedState: "focused",
                BaselineScenarioId: "parity-settings-general-tts-speed-slider-scroll-100-percent",
                InteractionFallbackX: 0.36,
                InteractionFallbackY: 0.64),
            new(
                "parity-settings-general-auto-play-toggle-hover-scroll-100-percent",
                SettingsParitySection.General,
                100,
                HoveredElement: "AutoPlayTranslationToggle",
                RustAutoPlayState: "hovered",
                BaselineScenarioId: "parity-settings-general-tts-speed-slider-scroll-100-percent",
                InteractionFallbackX: 0.28,
                InteractionFallbackY: 0.74),
            new(
                "parity-settings-general-auto-play-toggle-focus-scroll-100-percent",
                SettingsParitySection.General,
                100,
                FocusedElement: "AutoPlayTranslationToggle",
                RustAutoPlayState: "focused",
                BaselineScenarioId: "parity-settings-general-tts-speed-slider-scroll-100-percent",
                InteractionFallbackX: 0.28,
                InteractionFallbackY: 0.74),
            new("parity-settings-services-translation-service-configuration-top", SettingsParitySection.Services, 0),
            new(
                "parity-settings-services-import-mdx-hover",
                SettingsParitySection.Services,
                0,
                HoveredElement: "ImportMdxDictionaryButton",
                RustImportMdxState: "hovered",
                BaselineScenarioId: "parity-settings-services-translation-service-configuration-top",
                InteractionFallbackX: 0.13,
                InteractionFallbackY: 0.38),
            new(
                "parity-settings-services-international-toggle-hover",
                SettingsParitySection.Services,
                0,
                HoveredElement: "EnableInternationalServicesToggle",
                RustInternationalToggleState: "hovered",
                BaselineScenarioId: "parity-settings-services-translation-service-configuration-top",
                InteractionFallbackX: 0.91,
                InteractionFallbackY: 0.43),
            new(
                "parity-settings-services-international-toggle-pressed",
                SettingsParitySection.Services,
                0,
                PressedElement: "EnableInternationalServicesToggle",
                RustInternationalToggleState: "pressed",
                BaselineScenarioId: "parity-settings-services-translation-service-configuration-top",
                InteractionFallbackX: 0.91,
                InteractionFallbackY: 0.43),
            new(
                "parity-settings-services-deepl-expander-hover",
                SettingsParitySection.Services,
                0,
                HoveredElement: "DeepLServiceExpander",
                RustDeepLExpanderState: "hovered",
                BaselineScenarioId: "parity-settings-services-translation-service-configuration-top",
                InteractionFallbackX: 0.50,
                InteractionFallbackY: 0.61),
            .. ExpandedServiceConfigurationSteps(),
            new(
                "parity-settings-services-openai-api-format-dropdown-open",
                SettingsParitySection.Services,
                15,
                DotnetExpandElement: "OpenAI",
                RustExpandedServiceConfigurations: "openai",
                ExpandedDropdownElement: "OpenAIApiFormatCombo",
                ExpectedDropdownItems:
                [
                    "Auto-detect",
                    "Responses API",
                    "Chat Completions API"
                ],
                CaptureDropdownOptions: false,
                DropdownFallbackItemCount: 3,
                BaselineScenarioId: "parity-settings-services-openai-expanded-scroll-15-percent",
                InteractionFallbackX: 0.21,
                InteractionFallbackY: 0.81),
            new(
                "parity-settings-services-openai-model-dropdown-open",
                SettingsParitySection.Services,
                15,
                DotnetExpandElement: "OpenAI",
                RustExpandedServiceConfigurations: "openai",
                ExpandedDropdownElement: "OpenAIModelCombo",
                ExpectedDropdownItems:
                [
                    "gpt-5-mini",
                    "gpt-5-nano",
                    "gpt-5",
                    "gpt-4.1-mini",
                    "gpt-4.1-nano",
                    "gpt-4o-mini",
                    "gpt-4o"
                ],
                CaptureDropdownOptions: false,
                DropdownFallbackItemCount: 7,
                BaselineScenarioId: "parity-settings-services-openai-expanded-scroll-15-percent",
                InteractionFallbackX: 0.21,
                InteractionFallbackY: 0.89),
            new("parity-settings-views-window-results-top", SettingsParitySection.Views, 0),
            new("parity-settings-hotkeys-shortcut-inputs-top", SettingsParitySection.Hotkeys, 0),
            new("parity-settings-advanced-ocr-layout-top", SettingsParitySection.Advanced, 0),
            new(
                "parity-settings-advanced-ocr-engine-dropdown-open",
                SettingsParitySection.Advanced,
                0,
                ExpandedDropdownElement: "OcrEngineCombo",
                ExpectedDropdownItems: OcrEngineDropdownExpectedItems(),
                DropdownFallbackItemCount: OcrEngineDropdownExpectedItems().Count,
                BaselineScenarioId: "parity-settings-advanced-ocr-layout-top",
                InteractionFallbackX: 0.43,
                InteractionFallbackY: 0.24),
            new(
                "parity-settings-advanced-layout-detection-dropdown-open",
                SettingsParitySection.Advanced,
                0,
                ExpandedDropdownElement: "LayoutDetectionModeCombo",
                ExpectedDropdownItems: LayoutDetectionDropdownExpectedItems(),
                DropdownFallbackItemCount: LayoutDetectionDropdownExpectedItems().Count,
                BaselineScenarioId: "parity-settings-advanced-ocr-layout-top",
                InteractionFallbackX: 0.43,
                InteractionFallbackY: 0.48),
            new("parity-settings-language-preferences-top", SettingsParitySection.Language, 0),
            new(
                "parity-settings-language-first-language-dropdown-open",
                SettingsParitySection.Language,
                0,
                ExpandedDropdownElement: "FirstLanguageCombo",
                ExpectedDropdownItems: SettingsLanguageDropdownExpectedItems(),
                DropdownOptions: SettingsLanguageDropdownOptionCaptures(),
                DropdownFallbackItemCount: SettingsLanguageDropdownExpectedItems().Count,
                BaselineScenarioId: "parity-settings-language-preferences-top",
                InteractionFallbackX: 0.20,
                InteractionFallbackY: 0.36),
            new(
                "parity-settings-language-second-language-dropdown-open",
                SettingsParitySection.Language,
                0,
                ExpandedDropdownElement: "SecondLanguageCombo",
                ExpectedDropdownItems: SettingsLanguageDropdownExpectedItems(),
                DropdownOptions: SettingsLanguageDropdownOptionCaptures(),
                DropdownFallbackItemCount: SettingsLanguageDropdownExpectedItems().Count,
                BaselineScenarioId: "parity-settings-language-preferences-top",
                InteractionFallbackX: 0.20,
                InteractionFallbackY: 0.48),
            new(
                "parity-settings-language-ui-language-dropdown-open",
                SettingsParitySection.Language,
                0,
                ExpandedDropdownElement: "UILanguageCombo",
                ExpectedDropdownItems: UiLanguageDropdownExpectedItems(),
                DropdownFallbackItemCount: UiLanguageDropdownExpectedItems().Count,
                DropdownRestoreOptionIndex: 1,
                ForceDropdownFallbackClick: true,
                BaselineScenarioId: "parity-settings-language-preferences-top",
                InteractionFallbackX: 0.20,
                InteractionFallbackY: 0.65),
            new("parity-settings-language-translation-languages-collapsed-scroll-100-percent", SettingsParitySection.Language, 100),
            new(
                "parity-settings-language-translation-languages-expanded-list-scroll-100-percent",
                SettingsParitySection.Language,
                100,
                ExpandAvailableLanguages: true,
                RustTranslationLanguagesExpanded: true),
            new("parity-settings-about-links-top", SettingsParitySection.About, 0),
        ];

        private static bool IsZhParityLanguage() =>
            ResolveParityUiLanguage().Equals("zh-CN", StringComparison.OrdinalIgnoreCase);

        private static IReadOnlyList<string> ThemeDropdownExpectedItems() =>
            IsZhParityLanguage()
                ? ["系统", "浅色", "深色", "极简线框"]
                : ["System", "Light", "Dark", "Minimal"];

        private static IReadOnlyList<string> OcrEngineDropdownExpectedItems() =>
            IsZhParityLanguage()
                ? ["默认（Windows 原生）", "Ollama (Local VLM)", "自定义 API"]
                : ["Default (Windows Native)", "Ollama (Local VLM)", "Custom API"];

        private static IReadOnlyList<string> LayoutDetectionDropdownExpectedItems() =>
            IsZhParityLanguage()
                ? ["自动（推荐）", "本地 ONNX 模型", "视觉大模型", "仅启发式"]
                : ["Auto (Recommended)", "Local ONNX Model", "Vision LLM", "Heuristic Only"];

        private static IReadOnlyList<string> SettingsLanguageDropdownExpectedItems() =>
            IsZhParityLanguage()
                ? ["简体中文", "繁體中文", "日语", "韩语", "英语", "德语", "法语", "西班牙语"]
                : ["Simplified Chinese", "Traditional Chinese", "Japanese", "Korean", "English", "German", "French", "Spanish"];

        private static IReadOnlyList<SettingsDropdownOptionCapture> SettingsLanguageDropdownOptionCaptures()
        {
            var labels = SettingsLanguageDropdownExpectedItems();
            return labels
                .Select((label, index) => new SettingsDropdownOptionCapture(label, index))
                .ToArray();
        }

        private static IReadOnlyList<string> UiLanguageDropdownExpectedItems() =>
        [
            "English",
            "简体中文",
            "繁體中文",
            "日本語",
            "한국어",
            "Français",
            "Deutsch",
            "Tiếng Việt",
            "ไทย",
            "العربية",
            "Bahasa Indonesia",
            "Italiano",
            "Bahasa Melayu",
            "हिन्दी",
            "Dansk"
        ];

        private static IReadOnlyList<SettingsParityCaptureStep> ExpandedServiceConfigurationSteps()
        {
            var steps = new List<SettingsParityCaptureStep>();
            foreach (var service in ServiceConfigurationCaptures())
            {
                steps.Add(CreateExpandedServiceConfigurationStep(service, interactionState: null));
                steps.Add(CreateExpandedServiceConfigurationStep(service, interactionState: "hovered"));
                steps.Add(CreateExpandedServiceConfigurationStep(service, interactionState: "pressed"));
            }

            return steps;
        }

        private static SettingsParityCaptureStep CreateExpandedServiceConfigurationStep(
            ServiceConfigurationCapture service,
            string? interactionState)
        {
            var suffix = interactionState switch
            {
                "hovered" => "-bar-hover",
                "pressed" => "-bar-pressed",
                _ => string.Empty
            };
            var stateElement = interactionState is null ? null : service.RustExpanderId;

            return new SettingsParityCaptureStep(
                $"{service.ScenarioId}{suffix}",
                SettingsParitySection.Services,
                service.ScrollPercent,
                HoveredElement: interactionState == "hovered" ? service.DotnetExpandElement : null,
                PressedElement: interactionState == "pressed" ? service.DotnetExpandElement : null,
                DotnetExpandElement: service.DotnetExpandElement,
                RustExpandedServiceConfigurations: service.ServiceId,
                RustLocalAiProvider: service.RustLocalAiProvider,
                RustServiceExpanderState: interactionState,
                RequiredStateElement: stateElement,
                BaselineScenarioId: interactionState is null ? null : service.ScenarioId);
        }

        private static IReadOnlyList<ServiceConfigurationCapture> ServiceConfigurationCaptures() =>
        [
            new("parity-settings-services-deepl-expanded-top", "deepl", "DeepLServiceExpander", "DeepLServiceExpander", 0),
            new("parity-settings-services-local-ai-expanded-top", "windows-local-ai", "WindowsLocalAIExpander", "WindowsLocalAIExpander", 0, "FoundryLocal"),
            new("parity-settings-services-ollama-expanded-top", "ollama", "OllamaServiceExpander", "Ollama (Local LLM)", 0),
            new("parity-settings-services-openai-expanded-scroll-15-percent", "openai", "OpenAIServiceExpander", "OpenAI", 15),
            new("parity-settings-services-deepseek-expanded-scroll-25-percent", "deepseek", "DeepSeekServiceExpander", "DeepSeek", 25),
            new("parity-settings-services-groq-expanded-scroll-35-percent", "groq", "GroqServiceExpander", "Groq", 35),
            new("parity-settings-services-zhipu-expanded-scroll-45-percent", "zhipu", "ZhipuServiceExpander", "Zhipu (智谱)", 45),
            new("parity-settings-services-github-models-expanded-scroll-55-percent", "github", "GitHubModelsServiceExpander", "GitHub Models", 55),
            new("parity-settings-services-gemini-expanded-scroll-60-percent", "gemini", "GeminiServiceExpander", "Gemini", 60),
            new("parity-settings-services-custom-openai-expanded-scroll-70-percent", "custom-openai", "CustomOpenAIServiceExpander", "Custom OpenAI Compatible", 70),
            new("parity-settings-services-builtin-ai-expanded-scroll-75-percent", "builtin", "BuiltInAIServiceExpander", "Built-in AI", 75),
            new("parity-settings-services-doubao-expanded-scroll-80-percent", "doubao", "DoubaoServiceExpander", "Doubao (豆包)", 80),
            new("parity-settings-services-caiyun-expanded-scroll-88-percent", "caiyun", "CaiyunServiceExpander", "Caiyun (彩云小译)", 88),
            new("parity-settings-services-niutrans-expanded-scroll-94-percent", "niutrans", "NiuTransServiceExpander", "NiuTrans (小牛翻译)", 94),
            new("parity-settings-services-youdao-expanded-scroll-100-percent", "youdao", "YoudaoServiceExpander", "Youdao (有道翻译)", 100),
        ];

        private sealed record ServiceConfigurationCapture(
            string ScenarioId,
            string ServiceId,
            string RustExpanderId,
            string DotnetExpandElement,
            double ScrollPercent,
            string? RustLocalAiProvider = null);
    }

    private static readonly JsonSerializerOptions PreviewControlJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed record PreviewControlRequest(
        string SessionId,
        ulong Generation,
        string Command,
        string Scenario,
        string ArtifactStem,
        float? WidthDips,
        float? HeightDips,
        IReadOnlyDictionary<string, string> Overrides,
        IReadOnlyList<string> RequiredControlIds);

    private sealed record PreviewControlAck(
        string Schema,
        string SessionId,
        ulong Generation,
        string Status,
        string? ErrorCode,
        string? Message,
        IReadOnlyDictionary<string, string> ArtifactPaths,
        IReadOnlyList<string> ObservedControlIds,
        IReadOnlyList<string> MissingControlIds,
        long? RenderDurationMs);

    private static byte[] SerializePreviewControlRequest(PreviewControlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            throw new RustPreviewControlException(
                "preview-invalid-session",
                "sessionId is required.");
        }
        if (request.Generation == 0)
        {
            throw new RustPreviewControlException(
                "preview-stale-generation",
                "generation must be greater than zero.");
        }
        if (request.Command is not ("render" or "shutdown"))
        {
            throw new RustPreviewControlException(
                "preview-invalid-session",
                $"Unsupported command '{request.Command}'.");
        }
        if (string.IsNullOrEmpty(request.ArtifactStem) ||
            request.ArtifactStem.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new RustPreviewControlException(
                "preview-invalid-artifact-stem",
                "artifactStem must match [A-Za-z0-9._-]+.");
        }
        if (request.WidthDips is { } width && (!float.IsFinite(width) || width <= 0) ||
            request.HeightDips is { } height && (!float.IsFinite(height) || height <= 0))
        {
            throw new RustPreviewControlException(
                "preview-invalid-dimensions",
                "widthDips and heightDips must be finite and positive.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, PreviewControlJsonOptions);
        if (bytes.Length > 1024 * 1024)
        {
            throw new RustPreviewControlException(
                "preview-request-too-large",
                $"Request is {bytes.Length} bytes; maximum is {1024 * 1024}.");
        }
        return bytes;
    }

    private static PreviewControlAck? ParseMatchingPreviewAck(
        string json,
        string expectedSessionId,
        ulong expectedGeneration)
    {
        PreviewControlAck ack;
        try
        {
            ack = JsonSerializer.Deserialize<PreviewControlAck>(json, PreviewControlJsonOptions)
                ?? throw new JsonException("Acknowledgement was null.");
        }
        catch (JsonException error)
        {
            throw new RustPreviewControlException(
                "preview-invalid-session",
                $"Invalid preview acknowledgement: {error.Message}");
        }

        if (!string.Equals(ack.Schema, "easydict.preview-ack.v1", StringComparison.Ordinal))
        {
            throw new RustPreviewControlException(
                "preview-invalid-session",
                $"Unexpected acknowledgement schema '{ack.Schema}'.");
        }
        if (!string.Equals(ack.SessionId, expectedSessionId, StringComparison.Ordinal))
        {
            throw new RustPreviewControlException(
                "preview-invalid-session",
                "Acknowledgement sessionId does not match the active preview session.");
        }
        if (ack.Generation < expectedGeneration)
        {
            return null;
        }
        if (ack.Generation > expectedGeneration)
        {
            throw new RustPreviewControlException(
                "preview-stale-generation",
                $"Acknowledgement generation {ack.Generation} is newer than requested generation {expectedGeneration}.");
        }
        return ack;
    }

    private static void ValidateRenderedPreviewAck(PreviewControlAck ack)
    {
        if (string.Equals(ack.Status, "error", StringComparison.Ordinal))
        {
            throw new RustPreviewControlException(
                ack.ErrorCode ?? "preview-invalid-session",
                ack.Message ?? "Rust preview rejected the render request.",
                ack);
        }
        if (!string.Equals(ack.Status, "rendered", StringComparison.Ordinal))
        {
            throw new RustPreviewControlException(
                "preview-invalid-session",
                $"Unexpected acknowledgement status '{ack.Status}'.",
                ack);
        }
        if (ack.MissingControlIds is { Count: > 0 })
        {
            throw new RustPreviewControlException(
                "preview-missing-required-control",
                $"Rust preview did not render required controls: {string.Join(", ", ack.MissingControlIds)}.",
                ack);
        }
        if (ack.RenderDurationMs is null or < 0)
        {
            throw new RustPreviewControlException(
                "preview-invalid-session",
                "Rendered acknowledgement did not include renderDurationMs.",
                ack);
        }
    }

    private sealed class RustPreviewControlException : InvalidOperationException
    {
        public RustPreviewControlException(
            string errorCode,
            string message,
            PreviewControlAck? acknowledgement = null)
            : base($"{errorCode}: {message}")
        {
            ErrorCode = errorCode;
            Acknowledgement = acknowledgement;
        }

        public string ErrorCode { get; }
        public PreviewControlAck? Acknowledgement { get; }
    }

    private sealed record RustPreviewRunMetrics(
        string SchemaVersion,
        string GeneratedAtUtc,
        int RustProcessStarts,
        int RustRenderRequests,
        IReadOnlyList<long> RustRenderDurationsMs,
        int RustTimeouts,
        int HarnessInvalid);

    private sealed record RustPreviewSessionKey(
        string Window,
        string Theme,
        string UiLanguage,
        double Dpi);

    private sealed record UiParityManifest(
        string SchemaVersion,
        string GeneratedAtUtc,
        string UiLanguage,
        IReadOnlyList<UiParityManifestEntry> Scenarios);

    private sealed record UiParityManifestEntry(
        string ScenarioId,
        string WindowKind,
        string SectionId,
        string SectionLabel,
        string Theme,
        double ScrollPercent,
        bool ExpandAvailableLanguages,
        string ReferenceScreenshot,
        string CandidateScreenshot,
        string SideBySideScreenshot,
        UiParityWindowManifest ReferenceWindow,
        UiParityWindowManifest CandidateWindow,
        IReadOnlyList<UiParityRegion> Regions,
        IReadOnlyList<string> RequiredSemanticTags,
        UiParityUiSummary ReferenceUiSummary,
        UiParityUiSummary CandidateUiSummary,
        UiParitySize? ReferenceExpectedWindowDips = null,
        UiParityWindowSizeAudit? ReferenceWindowSizeAudit = null,
        UiParitySize? CandidateExpectedWindowDips = null,
        UiParityWindowSizeAudit? CandidateWindowSizeAudit = null,
        IReadOnlyList<string>? RequiredVisibleTexts = null,
        string? BaselineScenarioId = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? RequiredControlStates = null,
        string? ExpandedDropdownElement = null,
        IReadOnlyList<string>? ExpectedDropdownItems = null,
        string? OperatedDropdownElement = null,
        string? SelectedDropdownOption = null,
        int? SelectedDropdownOptionIndex = null,
        int? SelectedRustDropdownOptionIndex = null,
        string? RuntimeDiagnosticsPath = null);

    private sealed record UiParityWindowManifest(
        UiParityBounds Bounds,
        UiParityBounds VisibleBounds,
        UiParityBounds VirtualScreenBounds,
        bool IsClippedByVirtualScreen,
        double DpiScale,
        string? NativeHandleHex,
        string? ExtendedStyleHex,
        bool? HasNoActivate,
        bool? HasToolWindow,
        bool? HasTopmost,
        bool? IsForegroundAtCapture,
        uint? Dpi);

    private sealed record UiParityBounds(int Left, int Top, int Width, int Height);

    private sealed record UiParitySize(double Width, double Height);

    private sealed record UiParityWindowSizeAudit(
        UiParitySize ExpectedWindowDips,
        UiParitySize ActualWindowDips,
        UiParitySize DeltaDips,
        UiParitySize DeltaPercent,
        UiParitySize MonitorWorkAreaDips,
        bool ExpectedLargerThanWorkArea);

    private sealed record UiParityUiSummary(
        IReadOnlyDictionary<string, int> VisibleControlCounts,
        IReadOnlyList<string> VisibleAutomationIds,
        IReadOnlyDictionary<string, UiParityControlDimension> VisibleControlDimensions,
        IReadOnlyList<string>? VisibleTexts = null);

    private sealed record UiParityControlDimension(
        string? Kind = null,
        string? State = null,
        string? Width = null,
        string? LabeledWidth = null,
        string? Height = null,
        string? LabeledHeight = null,
        string? MaxWidth = null,
        string? MinWidth = null,
        string? MinHeight = null,
        string? MaxHeight = null,
        string? Padding = null,
        string? Spacing = null,
        string? RowSpacing = null,
        string? ColumnSpacing = null,
        string? Columns = null,
        string? MaximumRowsOrColumns = null,
        string? Margin = null,
        UiParityControlBoundsDips? BoundsDips = null);

    private sealed record UiParityControlBoundsDips(
        double Left,
        double Top,
        double Width,
        double Height);

    private sealed record UiParityRegion(
        string Name,
        double X,
        double Y,
        double Width,
        double Height,
        double Weight)
    {
        public static readonly IReadOnlyList<UiParityRegion> DefaultSettingsRegions =
        [
            new("header", 0.0, 0.0, 1.0, 0.12, 1.0),
            new("top-navigation", 0.0, 0.12, 1.0, 0.14, 1.0),
            new("content", 0.0, 0.26, 1.0, 0.64, 2.2),
            new("footer", 0.0, 0.90, 1.0, 0.10, 0.8)
        ];

        public static readonly IReadOnlyList<UiParityRegion> SettingsTabInteractionRegions =
        [
            new("header", 0.0, 0.0, 1.0, 0.12, 0.8),
            new("top-navigation", 0.0, 0.12, 1.0, 0.14, 4.0),
            new("content-context", 0.0, 0.26, 1.0, 0.64, 0.2),
            new("footer-context", 0.0, 0.90, 1.0, 0.10, 0.1)
        ];

        public static readonly IReadOnlyList<UiParityRegion> DefaultMainRegions =
        [
            new("main-header", 0.0, 0.0, 1.0, 0.12, 1.0),
            new("action-bar", 0.0, 0.12, 1.0, 0.16, 1.4),
            new("source-card", 0.0, 0.28, 1.0, 0.34, 1.8),
            new("result-list", 0.0, 0.62, 1.0, 0.34, 2.2)
        ];

        public static readonly IReadOnlyList<UiParityRegion> PrimaryButtonEffectRegions =
        [
            new("main-header", 0.0, 0.0, 1.0, 0.12, 0.7),
            new("primary-action", 0.72, 0.12, 0.28, 0.16, 3.0),
            new("source-card", 0.0, 0.28, 1.0, 0.34, 1.0),
            new("result-list", 0.0, 0.62, 1.0, 0.34, 1.0)
        ];

        public static readonly IReadOnlyList<UiParityRegion> ResultHeaderEffectRegions =
        [
            new("main-header", 0.0, 0.0, 1.0, 0.12, 0.7),
            new("action-bar", 0.0, 0.12, 1.0, 0.16, 0.8),
            new("source-card", 0.0, 0.28, 1.0, 0.34, 1.0),
            new("result-header", 0.0, 0.62, 1.0, 0.10, 3.0),
            new("result-body", 0.0, 0.72, 1.0, 0.24, 1.0)
        ];

        public static readonly IReadOnlyList<UiParityRegion> SourceInputEffectRegions =
        [
            new("main-header", 0.0, 0.0, 1.0, 0.12, 0.7),
            new("action-bar", 0.0, 0.12, 1.0, 0.16, 0.8),
            new("source-input", 0.0, 0.28, 1.0, 0.34, 3.0),
            new("result-list", 0.0, 0.62, 1.0, 0.34, 1.0)
        ];

        public static readonly IReadOnlyList<UiParityRegion> OverlayEffectRegions =
        [
            new("overlay-scrim", 0.0, 0.0, 1.0, 1.0, 2.0),
            new("overlay-indicator", 0.36, 0.36, 0.28, 0.24, 3.0),
            new("main-context", 0.0, 0.12, 1.0, 0.84, 0.8)
        ];

        public static readonly IReadOnlyList<UiParityRegion> LongDocumentRegions =
        [
            new("main-header", 0.0, 0.0, 1.0, 0.12, 0.9),
            new("long-doc-input", 0.0, 0.12, 1.0, 0.28, 1.4),
            new("long-doc-controls", 0.0, 0.40, 1.0, 0.24, 2.0),
            new("long-doc-output", 0.0, 0.64, 1.0, 0.26, 1.6),
            new("long-doc-history", 0.0, 0.90, 1.0, 0.10, 0.8)
        ];

        public static readonly IReadOnlyList<UiParityRegion> LongDocumentServiceDropdownRegions =
        [
            new("main-header", 0.0, 0.0, 1.0, 0.12, 0.5),
            new("long-doc-service-control", 0.34, 0.30, 0.34, 0.18, 2.4),
            new("long-doc-service-popup", 0.28, 0.44, 0.44, 0.42, 3.0),
            new("long-doc-context", 0.0, 0.12, 1.0, 0.78, 0.8)
        ];

        public static readonly IReadOnlyList<UiParityRegion> FloatingWindowRegions =
        [
            new("floating-toolbar", 0.0, 0.0, 1.0, 0.18, 1.0),
            new("floating-source", 0.0, 0.18, 1.0, 0.34, 1.5),
            new("floating-results", 0.0, 0.52, 1.0, 0.34, 2.2),
            new("floating-footer", 0.0, 0.86, 1.0, 0.14, 0.8)
        ];

        public static readonly IReadOnlyList<UiParityRegion> FloatingActionEffectRegions =
        [
            new("floating-context", 0.0, 0.0, 1.0, 0.72, 0.8),
            new("floating-action", 0.68, 0.62, 0.32, 0.28, 3.0),
            new("floating-footer", 0.0, 0.86, 1.0, 0.14, 1.2)
        ];

        public static readonly IReadOnlyList<UiParityRegion> FloatingHeaderEffectRegions =
        [
            new("floating-header-action", 0.0, 0.0, 1.0, 0.18, 3.2),
            new("floating-source-context", 0.0, 0.18, 1.0, 0.34, 0.9),
            new("floating-results-context", 0.0, 0.52, 1.0, 0.34, 0.6),
            new("floating-footer-context", 0.0, 0.86, 1.0, 0.14, 0.4)
        ];

        public static readonly IReadOnlyList<UiParityRegion> FloatingLanguageBarEffectRegions =
        [
            new("floating-header-context", 0.0, 0.0, 1.0, 0.18, 0.5),
            new("floating-source-context", 0.0, 0.18, 1.0, 0.18, 0.8),
            new("floating-language-bar", 0.0, 0.36, 1.0, 0.18, 3.2),
            new("floating-results-context", 0.0, 0.54, 1.0, 0.32, 0.8),
            new("floating-footer-context", 0.0, 0.86, 1.0, 0.14, 0.4)
        ];

        public static readonly IReadOnlyList<UiParityRegion> PopButtonRegions =
        [
            new("popbutton-icon", 0.0, 0.0, 1.0, 1.0, 3.0),
            new("popbutton-hit-target", 0.08, 0.08, 0.84, 0.84, 2.0)
        ];

        public static readonly IReadOnlyList<UiParityRegion> TrayMenuRegions =
        [
            new("tray-menu-items", 0.0, 0.0, 1.0, 0.58, 2.4),
            new("tray-menu-browser-row", 0.0, 0.58, 1.0, 0.14, 1.6),
            new("tray-menu-footer", 0.0, 0.72, 1.0, 0.28, 1.8)
        ];

        public static readonly IReadOnlyList<UiParityRegion> OcrOverlayRegions =
        [
            new("ocr-overlay", 0.0, 0.0, 1.0, 1.0, 1.0),
            new("ocr-center-selection", 0.20, 0.20, 0.60, 0.60, 2.8),
            new("ocr-status-panel", 0.0, 0.0, 0.46, 0.24, 1.4),
            new("ocr-magnifier", 0.62, 0.0, 0.38, 0.24, 1.8)
        ];
    }

    private sealed record TrayMenuWindowCandidate(
        IntPtr Hwnd,
        string ClassName,
        string Title,
        Rectangle Bounds,
        int Score)
    {
        public override string ToString() =>
            $"HWND=0x{Hwnd.ToInt64():X}, class='{ClassName}', title='{Title}', bounds={Bounds}, score={Score}";
    }

    private sealed record TrayMenuCaptureResult(
        string ScenarioId,
        string DotnetScreenshot,
        string RustScreenshot,
        string SideBySideScreenshot,
        UiParityWindowManifest DotnetManifest,
        UiParityWindowManifest RustManifest,
        UiParityManifestEntry ManifestEntry);

    private sealed record TrayMenuFluentAuditReport(
        string SchemaVersion,
        string GeneratedAtUtc,
        int RoundCount,
        int ScenariosPerRound,
        IReadOnlyList<TrayMenuFluentAuditRound> Rounds);

    private sealed record TrayMenuFluentAuditRound(
        int Round,
        string ScenarioId,
        bool ExpectScrolling,
        double ReferenceWidthDips,
        double CandidateWidthDips,
        double WidthDeltaDips,
        double ReferenceHeightDips,
        double CandidateHeightDips,
        double HeightDeltaDips,
        string ReferenceSurfaceHex,
        string CandidateSurfaceHex,
        double SurfaceColorDistance,
        int ReferenceSeparatorPixels,
        int CandidateSeparatorPixels,
        int ReferenceDistinctColors,
        int CandidateDistinctColors,
        int? MaxHeightDips,
        int UnboundedContentHeightDips,
        bool HasVisibleContent,
        bool SizeAligned,
        bool SurfaceAligned,
        bool SeparatorsVisible,
        bool ScrollConstrained,
        bool Passed);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
        }
    }

    private sealed record RustPreviewRenderResult(
        ulong Generation,
        Window Window,
        string SchemaPath,
        string BoundsPath,
        string DiagnosticsPath,
        long RenderDurationMs,
        int ProcessId)
    {
        public Window GetMainWindow(TimeSpan _) => Window;
    }

    private sealed class RustPreviewSession : IDisposable
    {
        private readonly Application _application;
        private readonly UIA3Automation _automation;
        private readonly Process _debugProcess;
        private readonly object _debugLinesLock;
        private readonly List<string> _debugLines;
        private readonly EventWaitHandle _requestEvent;
        private readonly string _sessionId;
        private readonly string _requestPath;
        private readonly string _ackPath;
        private readonly string _liveBoundsPath;
        private readonly string _outputRoot;
        private readonly string _window;
        private readonly string _theme;
        private readonly string _uiLanguage;
        private readonly double _dpi;
        private readonly int _minimumWindowSize;
        private ulong _generation;
        private bool _disposed;

        private RustPreviewSession(
            Application application,
            UIA3Automation automation,
            EventWaitHandle requestEvent,
            Process debugProcess,
            List<string> debugLines,
            object debugLinesLock,
            string sessionId,
            string requestPath,
            string ackPath,
            string liveBoundsPath,
            string outputRoot,
            string window,
            string theme,
            string uiLanguage,
            double dpi,
            int minimumWindowSize)
        {
            _application = application;
            _automation = automation;
            _requestEvent = requestEvent;
            _debugProcess = debugProcess;
            _debugLines = debugLines;
            _debugLinesLock = debugLinesLock;
            _sessionId = sessionId;
            _requestPath = requestPath;
            _ackPath = ackPath;
            _liveBoundsPath = liveBoundsPath;
            _outputRoot = outputRoot;
            _window = window;
            _theme = theme;
            _uiLanguage = uiLanguage;
            _dpi = dpi;
            _minimumWindowSize = minimumWindowSize;
        }

        public int ProcessId => _application.ProcessId;
        private bool HasExited => _application.HasExited;
        public int CaptureDebugLineMarker()
        {
            lock (_debugLinesLock)
            {
                return _debugLines.Count;
            }
        }

        public void WaitForDebugLine(int marker, string expectedPayload, TimeSpan timeout)
        {
            _ = WaitForDebugLineValue(marker, expectedPayload, timeout);
        }

        public string WaitForDebugLineValue(int marker, string expectedPayload, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                string[] lines;
                lock (_debugLinesLock)
                {
                    lines = _debugLines
                        .Skip(Math.Min(marker, _debugLines.Count))
                        .ToArray();
                }
                var match = lines.FirstOrDefault(line =>
                    line.Contains(expectedPayload, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }
                Thread.Sleep(50);
            }

            string[] observed;
            lock (_debugLinesLock)
            {
                observed = _debugLines
                    .Skip(Math.Min(marker, _debugLines.Count))
                    .ToArray();
            }
            throw new TimeoutException(
                $"Missing debug payload '{expectedPayload}'. Later stderr lines: {string.Join(Environment.NewLine, observed)}");
        }

        public static RustPreviewSession Launch(
            string window,
            string theme,
            string uiLanguage,
            double dpi,
            float widthDips,
            float heightDips,
            string outputRoot,
            ITestOutputHelper output,
            IReadOnlyDictionary<string, string>? startupEnvironment = null)
        {
            if (string.IsNullOrWhiteSpace(window) ||
                string.IsNullOrWhiteSpace(theme) ||
                string.IsNullOrWhiteSpace(uiLanguage))
            {
                throw new RustPreviewControlException(
                    "preview-invalid-session",
                    "Window, theme, and UI language are required for a preview session.");
            }
            if (!double.IsFinite(dpi) || dpi <= 0 ||
                !float.IsFinite(widthDips) || widthDips <= 0 ||
                !float.IsFinite(heightDips) || heightDips <= 0)
            {
                throw new RustPreviewControlException(
                    "preview-invalid-dimensions",
                    "Session DPI and dimensions must be finite and positive.");
            }

            ValidateSessionEnvironment(startupEnvironment, window, theme, uiLanguage, dpi);
            var fullOutputRoot = Path.GetFullPath(outputRoot);
            Directory.CreateDirectory(fullOutputRoot);
            var sessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var eventName = $@"Local\Easydict-PreviewControl-{sessionId}";
            var requestPath = Path.Combine(fullOutputRoot, $"preview-control-{sessionId}.request.json");
            var ackPath = Path.Combine(fullOutputRoot, $"preview-control-{sessionId}.ack.json");
            var liveBoundsPath = Path.Combine(fullOutputRoot, $"preview-live-bounds-{sessionId}.txt");
            var exePath = ResolveRustPreviewExecutable(output);
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.Combine(FindRepositoryRoot(), "rs"),
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            UiaSettingsIsolation.ApplyTo(startInfo);
            if (startupEnvironment != null)
            {
                foreach (var (key, value) in startupEnvironment)
                {
                    startInfo.Environment[key] = value;
                }
            }
            startInfo.Environment["EASYDICT_PREVIEW_WINDOW"] = window;
            startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_OPEN"] =
                string.Equals(window, "settings", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
            startInfo.Environment["EASYDICT_PREVIEW_SCENARIO"] = "before_translate";
            startInfo.Environment["EASYDICT_PREVIEW_THEME"] = theme;
            startInfo.Environment["EASYDICT_PREVIEW_UI_LANGUAGE"] = uiLanguage;
            startInfo.Environment["EASYDICT_PREVIEW_DPI"] =
                dpi.ToString("0.###", CultureInfo.InvariantCulture);
            startInfo.Environment["EASYDICT_PREVIEW_WIDTH_DIPS"] =
                widthDips.ToString("0.###", CultureInfo.InvariantCulture);
            startInfo.Environment["EASYDICT_PREVIEW_HEIGHT_DIPS"] =
                heightDips.ToString("0.###", CultureInfo.InvariantCulture);
            startInfo.Environment["EASYDICT_PREVIEW_CONTROL_EVENT"] = eventName;
            startInfo.Environment["EASYDICT_PREVIEW_CONTROL_REQUEST_PATH"] = requestPath;
            startInfo.Environment["EASYDICT_PREVIEW_CONTROL_ACK_PATH"] = ackPath;
            startInfo.Environment["EASYDICT_PREVIEW_CONTROL_OUTPUT_ROOT"] = fullOutputRoot;
            startInfo.Environment["EASYDICT_PREVIEW_CONTROL_SESSION_ID"] = sessionId;
            startInfo.Environment["EASYDICT_PREVIEW_BOUNDS_PATH"] = liveBoundsPath;

            var automation = new UIA3Automation();
            Application? application = null;
            Process? debugProcess = null;
            try
            {
                var debugLines = new List<string>();
                var debugLinesLock = new object();
                debugProcess = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Failed to start Rust preview.");
                application = new Application(debugProcess);
                debugProcess.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data is null)
                    {
                        return;
                    }
                    lock (debugLinesLock)
                    {
                        debugLines.Add(args.Data);
                    }
                };
                debugProcess.BeginErrorReadLine();
                RecordRustPreviewProcessStart();

                var readyStopwatch = Stopwatch.StartNew();
                while (readyStopwatch.Elapsed < TimeSpan.FromSeconds(30))
                {
                    if (application.HasExited)
                    {
                        throw new RustPreviewControlException(
                            "preview-invalid-session",
                            "Rust preview exited before registering its request event.");
                    }
                    try
                    {
                        var requestEvent = EventWaitHandle.OpenExisting(eventName);
                        output.WriteLine(
                            $"Rust preview session {sessionId} attached to process {application.ProcessId}.");
                        var minimumWindowSize =
                            string.Equals(window, "pop-button", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(window, "popbutton", StringComparison.OrdinalIgnoreCase)
                                ? 20
                                : 120;
                        return new RustPreviewSession(
                            application,
                            automation,
                            requestEvent,
                            debugProcess,
                            debugLines,
                            debugLinesLock,
                            sessionId,
                            requestPath,
                            ackPath,
                            liveBoundsPath,
                            fullOutputRoot,
                            window,
                            theme,
                            uiLanguage,
                            dpi,
                            minimumWindowSize);
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        Thread.Sleep(100);
                    }
                }

                RecordRustPreviewTimeout();
                throw new TimeoutException(
                    $"Rust preview request event '{eventName}' was not available within 30 seconds.");
            }
            catch
            {
                DisposeDebugProcess(debugProcess);
                DisposePreviewApplication(application);
                automation.Dispose();
                throw;
            }
        }

        public RustPreviewRenderResult Render(
            string scenario,
            IReadOnlyDictionary<string, string> overrides,
            string artifactStem,
            IReadOnlyList<string> requiredControlIds,
            float widthDips,
            float heightDips)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateSessionEnvironment(overrides, _window, _theme, _uiLanguage, _dpi);
            var generation = checked(++_generation);
            var generationArtifactStem = $"{artifactStem}.g{generation}";
            var request = new PreviewControlRequest(
                _sessionId,
                generation,
                "render",
                scenario,
                generationArtifactStem,
                widthDips,
                heightDips,
                overrides,
                requiredControlIds);

            try
            {
                TryDeleteFile(_ackPath);
                AtomicWrite(_requestPath, SerializePreviewControlRequest(request));
                _requestEvent.Set();

                var waitStopwatch = Stopwatch.StartNew();
                while (waitStopwatch.Elapsed < TimeSpan.FromSeconds(15))
                {
                    if (HasExited)
                    {
                        throw new RustPreviewControlException(
                            "preview-invalid-session",
                            $"Rust preview exited while rendering generation {generation}.");
                    }
                    if (!File.Exists(_ackPath))
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    string json;
                    try
                    {
                        json = File.ReadAllText(_ackPath);
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(25);
                        continue;
                    }

                    var ack = ParseMatchingPreviewAck(json, _sessionId, generation);
                    if (ack is null)
                    {
                        Thread.Sleep(25);
                        continue;
                    }
                    ValidateRenderedPreviewAck(ack);
                    var schemaPath = RequireArtifactPath(ack, "schema");
                    var boundsPath = RequireArtifactPath(ack, "bounds");
                    var diagnosticsPath = RequireArtifactPath(ack, "diagnostics");
                    ValidateArtifactGeneration(diagnosticsPath, generation);
                    lock (RustMetricsLock)
                    {
                        RustBoundsBySchemaPath[schemaPath] = boundsPath;
                        RustDiagnosticsBySchemaPath[schemaPath] = diagnosticsPath;
                    }

                    RecordRustPreviewRenderSuccess(ack.RenderDurationMs!.Value);
                    return new RustPreviewRenderResult(
                        generation,
                        GetMainWindow(TimeSpan.FromSeconds(5)),
                        schemaPath,
                        boundsPath,
                        diagnosticsPath,
                        ack.RenderDurationMs.Value,
                        ProcessId);
                }

                RecordRustPreviewTimeout();
                throw new TimeoutException(
                    $"preview-render-timeout: Rust preview did not acknowledge generation {generation} within 15 seconds.");
            }
            catch (RustPreviewControlException)
            {
                RecordRustPreviewHarnessInvalid();
                throw;
            }
        }

        public static void ValidateSessionEnvironment(
            IReadOnlyDictionary<string, string>? environment,
            string window,
            string theme,
            string uiLanguage,
            double dpi)
        {
            if (environment == null)
            {
                return;
            }

            RejectInvariantChange(environment, "EASYDICT_PREVIEW_WINDOW", window);
            RejectInvariantChange(environment, "EASYDICT_PREVIEW_THEME", theme);
            RejectInvariantChange(environment, "EASYDICT_PREVIEW_UI_LANGUAGE", uiLanguage);
            if (environment.TryGetValue("EASYDICT_PREVIEW_DPI", out var requestedDpi) &&
                (!double.TryParse(
                    requestedDpi,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsedDpi) ||
                 Math.Abs(parsedDpi - dpi) > 0.001))
            {
                throw new RustPreviewControlException(
                    "session-invariant-mismatch",
                    $"EASYDICT_PREVIEW_DPI cannot change from {dpi:0.###} to '{requestedDpi}' within a preview session.");
            }
        }

        private static void RejectInvariantChange(
            IReadOnlyDictionary<string, string> environment,
            string key,
            string fixedValue)
        {
            if (environment.TryGetValue(key, out var requestedValue) &&
                !string.Equals(requestedValue, fixedValue, StringComparison.OrdinalIgnoreCase))
            {
                throw new RustPreviewControlException(
                    "session-invariant-mismatch",
                    $"{key} cannot change from '{fixedValue}' to '{requestedValue}' within a preview session.");
            }
        }

        private string RequireArtifactPath(PreviewControlAck ack, string kind)
        {
            if (ack.ArtifactPaths == null ||
                !ack.ArtifactPaths.TryGetValue(kind, out var path) ||
                string.IsNullOrWhiteSpace(path))
            {
                throw new RustPreviewControlException(
                    "preview-invalid-session",
                    $"Rendered acknowledgement did not include the {kind} artifact path.",
                    ack);
            }

            var candidate = Path.GetFullPath(
                Path.IsPathRooted(path) ? path : Path.Combine(_outputRoot, path));
            var rootPrefix = _outputRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(candidate))
            {
                throw new RustPreviewControlException(
                    "preview-invalid-artifact-stem",
                    $"{kind} artifact path is missing or outside the configured output root: {path}",
                    ack);
            }
            return candidate;
        }

        private static void ValidateArtifactGeneration(string path, ulong expectedGeneration)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("generation", out var generation) ||
                    generation.ValueKind != JsonValueKind.Number)
                {
                    throw new RustPreviewControlException(
                        "preview-invalid-session",
                        $"Diagnostics artifact '{path}' did not include a numeric generation.");
                }
                if (generation.GetUInt64() != expectedGeneration)
                {
                    throw new RustPreviewControlException(
                        "preview-stale-generation",
                        $"Artifact '{path}' does not match generation {expectedGeneration}.");
                }
            }
            catch (JsonException error)
            {
                throw new RustPreviewControlException(
                    "preview-invalid-session",
                    $"Artifact '{path}' is not valid JSON: {error.Message}");
            }
        }

        private static void AtomicWrite(string path, byte[] bytes)
        {
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, bytes);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }


        public UiParityControlBoundsDips WaitForLiveControlBounds(
            string controlId,
            TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                var dimensions = TryReadRustBoundsControlDimensions(_liveBoundsPath);
                if (dimensions.TryGetValue(controlId, out var dimension) &&
                    dimension.BoundsDips is { } bounds)
                {
                    return bounds;
                }

                Thread.Sleep(50);
            }

            throw new TimeoutException(
                $"Live Rust bounds did not expose control ID '{controlId}' within {timeout}.");
        }

        public Window GetMainWindow(TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            Exception? lastException = null;
            while (stopwatch.Elapsed < timeout)
            {
                if (HasExited)
                {
                    RecordRustPreviewHarnessInvalid();
                    throw new InvalidOperationException("Rust preview process exited before its window appeared.");
                }

                try
                {
                    var window = TryGetMainWindowFromProcessHandle()
                        ?? TryGetTopLevelWindowForApplicationProcess()
                        ?? TryGetFallbackMainWindow();
                    if (window != null)
                    {
                        return window;
                    }
                }
                catch (Exception ex) when (ex is TimeoutException or COMException)
                {
                    lastException = ex;
                }

                Thread.Sleep(250);
            }

            RecordRustPreviewTimeout();
            throw new TimeoutException("Rust preview window did not appear in time.", lastException);
        }

        private Window? TryGetFallbackMainWindow()
        {
            var window = _application.GetMainWindow(_automation, TimeSpan.FromSeconds(3));
            return window != null && IsUsableWindow(window) ? window : null;
        }

        private Window? TryGetMainWindowFromProcessHandle()
        {
            try
            {
                using var process = Process.GetProcessById(_application.ProcessId);
                process.Refresh();
                if (process.MainWindowHandle == IntPtr.Zero)
                {
                    return null;
                }

                var window = _automation.FromHandle(process.MainWindowHandle).AsWindow();
                return IsUsableWindow(window) ? window : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                return null;
            }
        }

        private Window? TryGetTopLevelWindowForApplicationProcess()
        {
            try
            {
                return _application
                    .GetAllTopLevelWindows(_automation)
                    .Where(window => BelongsToProcess(window, _application.ProcessId))
                    .Where(IsUsableWindow)
                    .OrderByDescending(window => GetWindowArea(window))
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or TimeoutException)
            {
                return null;
            }
        }

        private static bool BelongsToProcess(Window window, int processId)
        {
            try
            {
                var hwnd = window.Properties.NativeWindowHandle.Value;
                if (hwnd == IntPtr.Zero)
                {
                    return false;
                }

                GetWindowThreadProcessId(hwnd, out var ownerProcessId);
                return ownerProcessId == processId;
            }
            catch
            {
                return false;
            }
        }

        private bool IsUsableWindow(Window window)
        {
            var bounds = window.BoundingRectangle;
            return bounds.Width >= _minimumWindowSize && bounds.Height >= _minimumWindowSize;
        }

        private static int GetWindowArea(Window window)
        {
            var bounds = window.BoundingRectangle;
            return Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
        }

        private static string ResolveRustPreviewExecutable(ITestOutputHelper output)
        {
            var configured = Environment.GetEnvironmentVariable(RustPreviewExeEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            var repoRoot = FindRepositoryRoot();
            var defaultPath = Path.Combine(repoRoot, "rs", "target", "debug", "easydict_preview_iced.exe");
            var resolution = SelectRustPreviewDefaultExecutable(
                IsTruthy(Environment.GetEnvironmentVariable(RustPreviewBuildEnvironmentVariable)),
                File.Exists(defaultPath));
            if (resolution == RustPreviewDefaultExecutableResolution.Existing)
            {
                return defaultPath;
            }

            if (resolution == RustPreviewDefaultExecutableResolution.Build)
            {
                output.WriteLine("Building Rust preview executable: cargo build -p easydict_preview_iced");
                var build = Process.Start(new ProcessStartInfo
                {
                    FileName = "cargo",
                    Arguments = "build -p easydict_preview_iced --features parity-diagnostics",
                    WorkingDirectory = Path.Combine(repoRoot, "rs"),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }) ?? throw new InvalidOperationException("Failed to start cargo build.");

                var stdout = build.StandardOutput.ReadToEnd();
                var stderr = build.StandardError.ReadToEnd();
                build.WaitForExit(120_000);
                output.WriteLine(stdout);
                output.WriteLine(stderr);
                build.ExitCode.Should().Be(0, "Rust preview must build before parity comparison");

                if (File.Exists(defaultPath))
                {
                    return defaultPath;
                }
            }

            throw new FileNotFoundException(
                $"Rust preview executable not found. Build it with `cargo build -p easydict_preview_iced --features parity-diagnostics`, set {RustPreviewBuildEnvironmentVariable}=1, or set {RustPreviewExeEnvironmentVariable}.",
                defaultPath);
        }

        private static string FindRepositoryRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var current = Path.GetFullPath(start);
                while (!string.IsNullOrEmpty(current))
                {
                    if (Directory.Exists(Path.Combine(current, ".git")) ||
                        File.Exists(Path.Combine(current, ".git")))
                    {
                        return current;
                    }

                    var parent = Path.GetDirectoryName(current);
                    if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    current = parent ?? string.Empty;
                }
            }

            return Directory.GetCurrentDirectory();
        }

        private bool WaitForExit(TimeSpan timeout)
        {
            try
            {
                using var process = Process.GetProcessById(_application.ProcessId);
                return process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            try
            {
                if (!HasExited)
                {
                    var generation = checked(++_generation);
                    var shutdown = new PreviewControlRequest(
                        _sessionId,
                        generation,
                        "shutdown",
                        string.Empty,
                        $"shutdown-{generation}",
                        null,
                        null,
                        new Dictionary<string, string>(),
                        []);
                    AtomicWrite(_requestPath, SerializePreviewControlRequest(shutdown));
                    _requestEvent.Set();
                    WaitForExit(TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or
                RustPreviewControlException or ObjectDisposedException)
            {
                // ponytail: protocol shutdown is best-effort; kill-tree below prevents leaked test processes.
            }
            finally
            {
                _requestEvent.Dispose();
                DisposeDebugProcess(_debugProcess);
                DisposePreviewApplication(_application);
                _automation.Dispose();
            }
        }

        private static void DisposePreviewApplication(Application? application)
        {
            if (application is null)
            {
                return;
            }

            try
            {
                application.Dispose();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void DisposeDebugProcess(Process? process)

        {
            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                if (process.WaitForExit(3000) && process.HasExited)
                {
                    process.WaitForExit();
                }
            }
            catch (Exception error) when (
                error is InvalidOperationException or ObjectDisposedException or
                System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                try
                {
                    process.CancelErrorRead();
                }
                catch (InvalidOperationException)
                {
                }
                process.Dispose();
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]

    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeWindowRect Monitor;
        public NativeWindowRect WorkArea;
        public uint Flags;
    }


    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeWindowRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrNative(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        NativeInput[] inputs,
        int inputSize);

    private const int GwlStyle = -16;
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsThickFrame = 0x00040000;
    private const uint WsVisible = 0x10000000;
    private const int IdcArrow = 32512;
    private const int IdcSizeNwSe = 32642;
    private const int IdcSizeNeSw = 32643;
    private const int IdcSizeWe = 32644;
    private const int IdcSizeNs = 32645;
    private const int GWL_EXSTYLE = -20;
    private const int ShowWindowHide = 0;
    private const int ShowWindowRestore = 9;
    private const uint MonitorDefaultToNearest = 2;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_USER = 0x0400;
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;

        public static NativeInput UnicodeKey(char character, bool keyUp)
        {
            return new NativeInput
            {
                Type = InputKeyboard,
                Data = new NativeInputUnion
                {
                    Keyboard = new NativeKeyboardInput
                    {
                        ScanCode = character,
                        Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
                    },
                },
            };
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        public NativeMouseInput Mouse;

        [FieldOffset(0)]
        public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public NativePoint ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeWindowRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
