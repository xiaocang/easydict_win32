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
    private const string AllowOversizedCaptureEnvironmentVariable = "EASYDICT_UIA_ALLOW_OVERSIZED_CAPTURE";
    private const string UiLanguageEnvironmentVariable = "EASYDICT_UIA_PARITY_UI_LANGUAGE";

    private readonly ITestOutputHelper _output;
    private readonly AppLauncher _dotnetLauncher = new();
    private static bool _parityDpiAwarenessAttempted;

    public DotnetRustParityTests(ITestOutputHelper output)
    {
        _output = output;
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

        foreach (var step in steps)
        {
            using var rustPreview = RustPreviewApp.Launch(step, _output);
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
            var dotnetPath = CaptureDotnetSettingsStep(
                dotnetWindow,
                step,
                $"{step.Key}-dotnet-winui-reference");
            MaskFloatingLanguageBarOcclusions(dotnetPath, dotnetWindow);
            MoveMouseToNeutralPoint();
            rustWindow.SetForeground();
            Thread.Sleep(150);
            MoveMouseToNeutralPoint();
            HideFloatingLanguageBars();
            var rustPath = CaptureWindowPreferHwnd(
                rustWindow,
                $"{step.Key}-rust-win-fluent-iced");
            MaskFloatingLanguageBarOcclusions(rustPath, rustWindow);
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

        using var rustPreview = RustPreviewApp.LaunchMainPreview("initial", "light", _output);
        var rustWindow = rustPreview.GetMainWindow(TimeSpan.FromSeconds(30));

        ArrangeSideBySide(dotnetWindow, rustWindow);
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        WaitForMainWindowReady(rustWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "main.initial", "dotnet");
        AssertWindowFullyVisible(rustWindow, "main.initial", "rust");

        MoveMouseToNeutralPoint();
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

        rustPreview.Dispose();
        using var rustResultHeaderPreview =
            RustPreviewApp.LaunchMainPreview("result_header_hover", "light", _output);
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

        rustResultHeaderPreview.Dispose();
        SetDotnetMainInputText(dotnetWindow, "Hello from the Rust main window preview");
        using var rustBeforeTranslatePreview =
            RustPreviewApp.LaunchMainPreview("before_translate", "light", _output);
        var rustBeforeTranslateWindow = rustBeforeTranslatePreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustBeforeTranslateWindow);
        WaitForMainWindowReady(dotnetWindow, "dotnet");
        WaitForMainWindowReady(rustBeforeTranslateWindow, "rust");
        AssertWindowFullyVisible(dotnetWindow, "main.before-translate", "dotnet");
        AssertWindowFullyVisible(rustBeforeTranslateWindow, "main.before-translate", "rust");

        MoveMouseToNeutralPoint();
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

        using var rustSourceInputHoverPreview =
            RustPreviewApp.LaunchMainPreview(
                "before_translate",
                "light",
                _output,
                new Dictionary<string, string>
                {
                    ["EASYDICT_PREVIEW_SOURCE_TEXT_STATE"] = "hovered"
                });
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
        rustSourceInputHoverPreview.Dispose();

        using var rustSourceInputFocusPreview =
            RustPreviewApp.LaunchMainPreview(
                "before_translate",
                "light",
                _output,
                new Dictionary<string, string>
                {
                    ["EASYDICT_PREVIEW_SOURCE_TEXT_STATE"] = "focused"
                });
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
        rustSourceInputFocusPreview.Dispose();

        using var rustModeOverlayPreview =
            RustPreviewApp.LaunchMainPreview("mode_overlay", "light", _output);
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
        rustModeOverlayPreview.Dispose();

        if (IsTruthy(Environment.GetEnvironmentVariable(MainEffectsOnlyEnvironmentVariable)))
        {
            _output.WriteLine(
                $"Dotnet/Rust main/effects parity run stopped before long-document captures because {MainEffectsOnlyEnvironmentVariable}=1.");
            SaveManifest(manifestEntries);
            return;
        }

        WaitForLongDocumentReady(dotnetWindow, "dotnet");
        using var rustLongDocumentPreview =
            RustPreviewApp.LaunchMainPreview("long_document", "light", _output);
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
            ["main.long-doc.source_language", "main.long-doc.target_language", "main.long-doc.service", "main.long-doc.translate"],
            windowKindOverride: "long-document"));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(longDocDotnetPath);
        AssertImageHasVisibleContent(longDocRustPath);
        AssertImageHasVisibleContent(longDocSideBySidePath);

        SetDotnetLongDocumentModes(dotnetWindow, inputModeIndex: 0, outputModeIndex: 1);
        using var rustLongDocumentModesPreview =
            RustPreviewApp.LaunchMainPreview(
                "long_document",
                "light",
                _output,
                new Dictionary<string, string>
                {
                    ["EASYDICT_PREVIEW_LONG_DOC_INPUT_MODE"] = "plaintext",
                    ["EASYDICT_PREVIEW_LONG_DOC_OUTPUT_MODE"] = "bilingual"
                });
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
            ["main.long-doc.input_mode", "main.long-doc.output_mode", "main.long-doc.translate"],
            windowKindOverride: "long-document"));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(longDocModesDotnetPath);
        AssertImageHasVisibleContent(longDocModesRustPath);
        AssertImageHasVisibleContent(longDocModesSideBySidePath);

        rustLongDocumentPreview.Dispose();
        rustLongDocumentModesPreview.Dispose();

        ExpandDotnetComboBox(dotnetWindow, "LongDocServiceCombo");
        var longDocServiceDropdownDotnetPath = ScreenshotHelper.CaptureScreen(
            "long-doc.service-dropdown-dotnet-winui-reference");
        Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);

        using var rustLongDocumentServiceDropdownPreview =
            RustPreviewApp.LaunchMainPreview(
                "long_document",
                "light",
                _output,
                new Dictionary<string, string>
                {
                    ["EASYDICT_PREVIEW_LONG_DOC_INPUT_MODE"] = "plaintext",
                    ["EASYDICT_PREVIEW_LONG_DOC_OUTPUT_MODE"] = "bilingual",
                    ["EASYDICT_PREVIEW_LONG_DOC_SERVICE_STATE"] = "hovered"
                });
        var rustLongDocumentServiceDropdownWindow =
            rustLongDocumentServiceDropdownPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeSideBySide(dotnetWindow, rustLongDocumentServiceDropdownWindow);
        WaitForLongDocumentReady(rustLongDocumentServiceDropdownWindow, "rust");
        AssertWindowFullyVisible(
            rustLongDocumentServiceDropdownWindow,
            "long-doc.service-dropdown",
            "rust");
        MoveMouseToHoverTarget(
            rustLongDocumentServiceDropdownWindow,
            "main.long-doc.service",
            fallbackX: 0.55,
            fallbackY: 0.42);
        rustLongDocumentServiceDropdownWindow.SetForeground();
        Thread.Sleep(250);
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
            ["main.long-doc.service"],
            windowKindOverride: "long-document"));
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

        using var rustHoverPreview = RustPreviewApp.LaunchWindowPreview(
            "pop-button",
            "light",
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
            hoverSideBySidePath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(dotnetHoverPath);
        AssertImageHasVisibleContent(rustHoverPath);
        AssertImageHasVisibleContent(hoverSideBySidePath);

        using var rustPressedPreview = RustPreviewApp.LaunchWindowPreview(
            "pop-button",
            "light",
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
            pressedSideBySidePath));
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

        using var rustWindowDetectPreview = RustPreviewApp.LaunchWindowPreview(
            "capture-overlay",
            "light",
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
            CaptureUiSummary(rustWindowDetectWindow)));
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

        using var rustDragPreview = RustPreviewApp.LaunchWindowPreview(
            "capture-overlay",
            "light",
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
            CaptureUiSummary(rustDragWindow)));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(dotnetDragPath);
        AssertImageHasVisibleContent(rustDragPath);
        AssertImageHasVisibleContent(dragSideBySidePath);

        _output.WriteLine($"[ocr.window-detect] Dotnet screenshot: {dotnetWindowDetectPath}");
        _output.WriteLine($"[ocr.window-detect] Rust screenshot: {rustWindowDetectPath}");
        _output.WriteLine($"[ocr.drag-selection] Dotnet screenshot: {dotnetDragPath}");
        _output.WriteLine($"[ocr.drag-selection] Rust screenshot: {rustDragPath}");
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

        _output.WriteLine($"Opening {windowType} window with Ctrl+Alt+{key}");
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, key);
        Thread.Sleep(3000);

        var floatingWindow = UITestHelper.FindSecondaryWindow(
            _dotnetLauncher.Application,
            _dotnetLauncher.Automation,
            windowType,
            _output);
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
        using var rustInitialPreview = RustPreviewApp.LaunchWindowPreview(windowKind, "light", _output);
        var rustInitialWindow = rustInitialPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeFloatingSideBySide(dotnetWindow, rustInitialWindow, targetWidth, targetHeight);
        AssertWindowFullyVisible(dotnetWindow, $"{windowKind}.initial", "dotnet");
        AssertWindowFullyVisible(rustInitialWindow, $"{windowKind}.initial", "rust");

        MoveMouseToNeutralPoint();
        var initialDotnetPath = ScreenshotHelper.CaptureWindow(
            dotnetWindow,
            $"{windowKind}.initial-dotnet-winui-reference");
        var initialRustPath = ScreenshotHelper.CaptureWindow(
            rustInitialWindow,
            $"{windowKind}.initial-rust-win-fluent-iced");
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

        using var rustHoverPreview = RustPreviewApp.LaunchWindowPreview(
            windowKind,
            "light",
            _output,
            new Dictionary<string, string>
            {
                [rustTranslateStateEnvironmentVariable] = "hovered"
            });
        var rustHoverWindow = rustHoverPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeFloatingSideBySide(dotnetWindow, rustHoverWindow, targetWidth, targetHeight);
        AssertWindowFullyVisible(dotnetWindow, $"{windowKind}.translate-hover", "dotnet");
        AssertWindowFullyVisible(rustHoverWindow, $"{windowKind}.translate-hover", "rust");

        MoveMouseToHoverTarget(dotnetWindow, "TranslateButton", fallbackX: 0.86, fallbackY: 0.66);
        var hoverDotnetPath = ScreenshotHelper.CaptureWindow(
            dotnetWindow,
            $"{windowKind}.translate-hover-dotnet-winui-reference");
        var hoverRustPath = ScreenshotHelper.CaptureWindow(
            rustHoverWindow,
            $"{windowKind}.translate-hover-rust-win-fluent-iced");
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

        using var rustPressedPreview = RustPreviewApp.LaunchWindowPreview(
            windowKind,
            "light",
            _output,
            new Dictionary<string, string>
            {
                [rustTranslateStateEnvironmentVariable] = "pressed"
            });
        var rustPressedWindow = rustPressedPreview.GetMainWindow(TimeSpan.FromSeconds(30));
        ArrangeFloatingSideBySide(dotnetWindow, rustPressedWindow, targetWidth, targetHeight);
        AssertWindowFullyVisible(dotnetWindow, $"{windowKind}.translate-pressed", "dotnet");
        AssertWindowFullyVisible(rustPressedWindow, $"{windowKind}.translate-pressed", "rust");

        var pressedDotnetPath = CapturePressedWindow(
            dotnetWindow,
            "TranslateButton",
            fallbackX: 0.86,
            fallbackY: 0.66,
            $"{windowKind}.translate-pressed-dotnet-winui-reference");
        var pressedRustPath = ScreenshotHelper.CaptureWindow(
            rustPressedWindow,
            $"{windowKind}.translate-pressed-rust-win-fluent-iced");
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
            rustSchemaPath: rustPressedPreview.SchemaPath));
        SaveManifest(manifestEntries);

        AssertImageHasVisibleContent(pressedDotnetPath);
        AssertImageHasVisibleContent(pressedRustPath);
        AssertImageHasVisibleContent(pressedSideBySidePath);

        _output.WriteLine($"[{windowKind}.initial] Dotnet screenshot: {initialDotnetPath}");
        _output.WriteLine($"[{windowKind}.initial] Rust screenshot: {initialRustPath}");
        _output.WriteLine($"[{windowKind}.translate-hover] Dotnet screenshot: {hoverDotnetPath}");
        _output.WriteLine($"[{windowKind}.translate-hover] Rust screenshot: {hoverRustPath}");
        _output.WriteLine($"[{windowKind}.translate-pressed] Dotnet screenshot: {pressedDotnetPath}");
        _output.WriteLine($"[{windowKind}.translate-pressed] Rust screenshot: {pressedRustPath}");
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
                () => FindVisibleByAutomationIdOrName(window, automationId),
                message => _output.WriteLine($"[{step.Key}][dotnet] {message}"))
            ?? Retry.WhileNull(
                    () => FindVisibleByAutomationIdOrName(window, automationId),
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

        ScrollHelper.ScrollToPercent(
            scrollViewer,
            0,
            message => _output.WriteLine($"[{step.Key}][dotnet] {message}"));

        foreach (var automationId in new[]
                 {
                     "DeepLServiceExpander",
                     "WindowsLocalAIExpander",
                     "OllamaServiceExpander",
                     "OpenAIServiceExpander",
                     "DeepSeekServiceExpander",
                     "GroqServiceExpander"
                 })
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

        Mouse.MoveTo(point);
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
        string screenshotName)
    {
        window.SetForeground();
        Thread.Sleep(180);
        MoveMouseToHoverTarget(window, automationIdOrName, fallbackX, fallbackY);
        Mouse.Down(MouseButton.Left);
        try
        {
            Thread.Sleep(180);
            return CaptureWindowPreferHwnd(window, screenshotName);
        }
        finally
        {
            Mouse.MoveTo(GetWindowRelativePoint(window, 0.03, 0.03));
            Thread.Sleep(80);
            Mouse.Up(MouseButton.Left);
            Thread.Sleep(180);
        }
    }

    private static string CaptureWindowPreferHwnd(Window window, string screenshotName)
    {
        var hwnd = SafeNativeWindowHandle(window);
        var path = hwnd == IntPtr.Zero
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
        int targetHeight)
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
        var rustWidth = dotnetBounds.Width > 0 ? dotnetBounds.Width : width;
        var rustHeight = dotnetBounds.Height > 0 ? dotnetBounds.Height : height;
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
        window.SetForeground();
        Thread.Sleep(250);
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

    private static UiParityManifestEntry CreateManifestEntry(
        SettingsParityCaptureStep step,
        Window dotnetWindow,
        Window rustWindow,
        string dotnetPath,
        string rustPath,
        string sideBySidePath,
        string? rustSchemaPath)
    {
        var requiredSemanticTags = new[]
            {
                $"SettingsTab_{step.Section.Label}",
                step.Section.DotnetReadyElement,
                step.HoveredElement,
                step.FocusedElement,
                step.PressedElement
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
            RequiredControlStates: RequiredSettingsControlStates(step));
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

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
            AddRequiredControlStates(states, hoveredElement, "hovered");
        }

        if (step.PressedElement is { } pressedElement)
        {
            AddRequiredControlStates(states, pressedElement, "hovered", "pressed");
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
        string? rustSchemaPath = null)
    {
        var windowKind = windowKindOverride
            ?? (scenarioId.StartsWith("effects.", StringComparison.OrdinalIgnoreCase)
                ? "interaction-effects"
                : "main");
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
            ReferenceWindow: CaptureWindowManifest(dotnetWindow),
            CandidateWindow: CaptureWindowManifest(rustWindow),
            Regions: regions,
            RequiredSemanticTags: requiredSemanticTags,
            ReferenceUiSummary: CaptureUiSummary(dotnetWindow),
            CandidateUiSummary: CaptureRustUiSummary(rustWindow, rustSchemaPath));
    }

    private static UiParityManifestEntry CreatePopButtonManifestEntry(
        string scenarioId,
        string sectionLabel,
        UiParityWindowManifest referenceWindow,
        UiParityWindowManifest candidateWindow,
        string dotnetPath,
        string rustPath,
        string sideBySidePath)
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
            CandidateUiSummary: EmptyUiSummary());
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
        UiParityUiSummary candidateUiSummary)
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
            CandidateUiSummary: candidateUiSummary);
    }

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
        var counts = EmptyControlCounts();
        counts["button"] = CountDescendants(window, ControlType.Button);
        counts["checkbox"] = CountDescendants(window, ControlType.CheckBox);
        counts["comboBox"] = CountDescendants(window, ControlType.ComboBox);
        counts["edit"] = CountDescendants(window, ControlType.Edit);
        counts["hyperlink"] = CountDescendants(window, ControlType.Hyperlink);
        counts["list"] = CountDescendants(window, ControlType.List);
        counts["listItem"] = CountDescendants(window, ControlType.ListItem);
        counts["tabItem"] = CountDescendants(window, ControlType.TabItem);
        counts["text"] = CountDescendants(window, ControlType.Text);

        return new UiParityUiSummary(
            counts,
            CollectVisibleAutomationIds(window),
            CollectVisibleControlDimensions(window),
            CollectVisibleTexts(window));
    }

    private static UiParityUiSummary CaptureRustUiSummary(
        Window window,
        string? schemaPath,
        SettingsParityCaptureStep? step = null)
    {
        var nativeSummary = CaptureUiSummary(window);
        if (nativeSummary.VisibleAutomationIds.Count > 0)
        {
            return nativeSummary;
        }

        return TryReadRustSchemaUiSummary(schemaPath, step) ?? nativeSummary;
    }

    private static UiParityUiSummary? TryReadRustSchemaUiSummary(
        string? schemaPath,
        SettingsParityCaptureStep? step = null)
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
            IncrementSchemaControlCount(counts, summaryKind);

            if (summaryKind == "Button" &&
                TryExtractRustSchemaQuotedValue(trimmed, "label") is { Length: > 0 } buttonLabel)
            {
                IncrementSchemaControlCount(counts, "Text");
                visibleTexts.Add(buttonLabel);
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

            if (summaryKind == "ComboBox" &&
                TryExtractRustSchemaQuotedValue(trimmed, "label") is { Length: > 0 } comboLabel)
            {
                visibleTexts.Add(comboLabel);
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
                !IsIconGlyphText(textValue))
            {
                visibleTexts.Add(textValue);
            }

            if (kind == "Text" && scope.IsSettingsViewsMainWindowHeader(trimmed))
            {
                ids.Add("MainWindowHeaderText");
            }

            if (automationId is not null && scope.ShouldIncludeAutomationId(automationId))
            {
                ids.Add(automationId);
            }

            if (automationId is not null)
            {
                dimensions[automationId] = ExtractRustSchemaControlDimension(
                    kind,
                    automationId,
                    trimmed,
                    scope);
            }
        }

        return new UiParityUiSummary(counts, ids.ToArray(), dimensions, visibleTexts.ToArray());
    }

    private static IReadOnlyList<string> CollectVisibleTexts(Window window)
    {
        try
        {
            var texts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in window.FindAllDescendants().Where(IsOnScreenOrUnknown))
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

    private static IReadOnlyDictionary<string, UiParityControlDimension> CollectVisibleControlDimensions(Window window)
    {
        try
        {
            var dimensions = new Dictionary<string, UiParityControlDimension>(StringComparer.OrdinalIgnoreCase);
            var dpiScale = Math.Max(0.001, ScreenshotHelper.GetWindowDpiScale(window));
            var windowBounds = window.BoundingRectangle;
            foreach (var element in window.FindAllDescendants().Where(IsOnScreenOrUnknown))
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
            RowSpacing: TryExtractRustSchemaTokenValue(schemaLine, "run_spacing"),
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

        var endQuote = value.IndexOf('"', 1);
        return endQuote <= 1 ? null : value[1..endQuote];
    }

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
                return true;
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
                    IsServiceExpanderInTopViewport(automationId);
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
            automationId is "DeepLServiceExpander" or
                "OllamaServiceExpander" or
                "OpenAIServiceExpander" or
                "DeepSeekServiceExpander" or
                "GroqServiceExpander" or
                "ZhipuServiceExpander" or
                "EnabledServicesDescriptionText" or
                "EnabledServicesHeaderText" or
                "EnableInternationalServicesDescriptionText" or
                "EnableInternationalServicesHeaderText" or
                "EnableInternationalServicesToggle" or
                "ImportedMdxSummaryText" or
                "ImportMdxDictionaryButton" or
                "ServiceConfigurationDescriptionText" or
                "ServiceConfigurationHeaderText" or
                "WindowsLocalAIExpander" or
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

            return false;
        }

        private bool HasExpandedService(string serviceId) =>
            _servicesExpandedServiceConfigurations
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
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
                _ => -1
            };

        private static bool IsMainServiceEnabledId(string? automationId) =>
            automationId?.StartsWith("main.", StringComparison.OrdinalIgnoreCase) == true &&
            automationId.EndsWith(".enabled", StringComparison.OrdinalIgnoreCase) &&
            !automationId.EndsWith(".enabled_query", StringComparison.OrdinalIgnoreCase);

        private static bool IsMainServiceEnabledQueryId(string? automationId) =>
            automationId?.StartsWith("main.", StringComparison.OrdinalIgnoreCase) == true &&
            automationId.EndsWith(".enabled_query", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountDescendants(Window window, ControlType controlType)
    {
        try
        {
            return window
                .FindAllDescendants(cf => cf.ByControlType(controlType))
                .Count(IsOnScreenOrUnknown);
        }
        catch (Exception ex) when (ex is COMException or PropertyNotSupportedException or TimeoutException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> CollectVisibleAutomationIds(Window window)
    {
        try
        {
            return window
                .FindAllDescendants()
                .Where(IsOnScreenOrUnknown)
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
        var manifest = new UiParityManifest(
            SchemaVersion: "easydict.ui-parity.manifest.v1",
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
            return manifest?.Scenarios ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ToOutputRelativePath(string path)
    {
        return Path.GetRelativePath(ScreenshotHelper.OutputDir, path).Replace('\\', '/');
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
        settings["AppTheme"] = "System";
        settings["FirstLanguage"] = "zh";
        settings["SecondLanguage"] = "en";
        settings["SelectedLanguages"] = new[] { "zh", "en", "ja", "ko", "fr", "de", "es" };
        settings["AutoSelectTargetLanguage"] = true;
        settings["SourceLanguage"] = "auto";
        settings["MouseSelectionTranslate"] = true;
        settings["MouseSelectionExcludedApps"] = new[] { "code" };
        settings["EnableInternationalServices"] = true;
        settings["WindowWidthDips"] = 846.0;
        settings["WindowHeightDips"] = 913.0;

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
        return string.IsNullOrWhiteSpace(value) ? "en-US" : value.Trim();
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
        _dotnetLauncher.Dispose();
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
        string? RustTtsSpeedState = null,
        string? RustAutoPlayState = null,
        string? RustImportMdxState = null,
        string? RustInternationalToggleState = null,
        string? RustDeepLExpanderState = null,
        string? DotnetExpandElement = null,
        string? RustExpandedServiceConfigurations = null,
        string? RustLocalAiProvider = null,
        string? BaselineScenarioId = null,
        double InteractionFallbackX = 0.50,
        double InteractionFallbackY = 0.62)
    {
        public static readonly IReadOnlyList<SettingsParityCaptureStep> All =
        [
            new("parity-settings-general-behavior-top", SettingsParitySection.General, 0),
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
            new(
                "parity-settings-services-deepl-expanded-top",
                SettingsParitySection.Services,
                0,
                DotnetExpandElement: "DeepLServiceExpander",
                RustExpandedServiceConfigurations: "deepl"),
            new(
                "parity-settings-services-local-ai-expanded-top",
                SettingsParitySection.Services,
                0,
                DotnetExpandElement: "WindowsLocalAIExpander",
                RustExpandedServiceConfigurations: "windows-local-ai",
                RustLocalAiProvider: "FoundryLocal"),
            new("parity-settings-views-window-results-top", SettingsParitySection.Views, 0),
            new("parity-settings-hotkeys-shortcut-inputs-top", SettingsParitySection.Hotkeys, 0),
            new("parity-settings-advanced-ocr-layout-top", SettingsParitySection.Advanced, 0),
            new("parity-settings-language-preferences-top", SettingsParitySection.Language, 0),
            new("parity-settings-language-translation-languages-collapsed-scroll-100-percent", SettingsParitySection.Language, 100),
            new(
                "parity-settings-language-translation-languages-expanded-list-scroll-100-percent",
                SettingsParitySection.Language,
                100,
                ExpandAvailableLanguages: true,
                RustTranslationLanguagesExpanded: true),
            new("parity-settings-about-links-top", SettingsParitySection.About, 0),
        ];
    }

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
        IReadOnlyDictionary<string, IReadOnlyList<string>>? RequiredControlStates = null);

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

        public static readonly IReadOnlyList<UiParityRegion> PopButtonRegions =
        [
            new("popbutton-icon", 0.0, 0.0, 1.0, 1.0, 3.0),
            new("popbutton-hit-target", 0.08, 0.08, 0.84, 0.84, 2.0)
        ];

        public static readonly IReadOnlyList<UiParityRegion> OcrOverlayRegions =
        [
            new("ocr-overlay", 0.0, 0.0, 1.0, 1.0, 1.0),
            new("ocr-center-selection", 0.20, 0.20, 0.60, 0.60, 2.8),
            new("ocr-status-panel", 0.0, 0.0, 0.46, 0.24, 1.4),
            new("ocr-magnifier", 0.62, 0.0, 0.38, 0.24, 1.8)
        ];
    }

    private sealed class RustPreviewApp : IDisposable
    {
        private readonly Application _application;
        private readonly UIA3Automation _automation;
        private readonly int _minimumWindowWidth;
        private readonly int _minimumWindowHeight;
        private bool _disposed;

        private RustPreviewApp(
            Application application,
            UIA3Automation automation,
            string schemaPath,
            int minimumWindowWidth = 120,
            int minimumWindowHeight = 120)
        {
            _application = application;
            _automation = automation;
            _minimumWindowWidth = minimumWindowWidth;
            _minimumWindowHeight = minimumWindowHeight;
            SchemaPath = schemaPath;
        }

        public string SchemaPath { get; }

        public static RustPreviewApp Launch(SettingsParityCaptureStep step, ITestOutputHelper output)
        {
            var exePath = ResolveRustPreviewExecutable(output);
            var schemaPath = CreateSchemaPath(step.Key);
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.Combine(FindRepositoryRoot(), "rs"),
                UseShellExecute = false
            };
            UiaSettingsIsolation.ApplyTo(startInfo);
            ClearRustPreviewSettingsStepEnvironment(startInfo);
            startInfo.Environment["EASYDICT_PREVIEW_WINDOW"] = "settings";
            startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_OPEN"] = "1";
            startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_SECTION"] = step.Section.Id;
            startInfo.Environment["EASYDICT_PREVIEW_THEME"] = "system";
            startInfo.Environment["EASYDICT_PREVIEW_UI_LANGUAGE"] = ResolveParityUiLanguage();
            startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_MOUSE_SELECTION_TRANSLATE"] = "1";
            startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_FIXED_ALWAYS_ON_TOP"] = "0";
            startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_HIDE_EMPTY_SERVICE_RESULTS"] = "1";
            startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_IMPORTED_MDX"] = "1";
            if (step.Section == SettingsParitySection.Views)
            {
                startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_VIEW_SERVICE_PROFILE"] = "dotnet-reference";
            }
            var dotnetVersion = ResolveDotnetWinUiVersion();
            if (!string.IsNullOrWhiteSpace(dotnetVersion))
            {
                startInfo.Environment["EASYDICT_PREVIEW_APP_VERSION"] = dotnetVersion;
            }
            startInfo.Environment["EASYDICT_PREVIEW_SCHEMA_PATH"] = schemaPath;
            if (step.RustTranslationLanguagesExpanded)
            {
                startInfo.Environment["EASYDICT_PREVIEW_TRANSLATION_LANGUAGES_EXPANDED"] = "1";
            }
            if (step.HoveredTab is { } hoveredTab)
            {
                startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_HOVERED_SECTION"] = hoveredTab.Id;
            }
            if (step.PressedTab is { } pressedTab)
            {
                startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_PRESSED_SECTION"] = pressedTab.Id;
            }
            if (!string.IsNullOrWhiteSpace(step.RustTtsSpeedState))
            {
                startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_TTS_SPEED_STATE"] = step.RustTtsSpeedState;
            }
            if (!string.IsNullOrWhiteSpace(step.RustAutoPlayState))
            {
                startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_AUTO_PLAY_STATE"] = step.RustAutoPlayState;
            }
            if (!string.IsNullOrWhiteSpace(step.RustImportMdxState))
            {
                startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_IMPORT_MDX_STATE"] = step.RustImportMdxState;
            }
            if (!string.IsNullOrWhiteSpace(step.RustInternationalToggleState))
            {
                startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_INTERNATIONAL_TOGGLE_STATE"] =
                    step.RustInternationalToggleState;
            }
            if (!string.IsNullOrWhiteSpace(step.RustDeepLExpanderState))
            {
                startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_DEEPL_EXPANDER_STATE"] =
                    step.RustDeepLExpanderState;
            }
            if (!string.IsNullOrWhiteSpace(step.RustExpandedServiceConfigurations))
            {
                startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_EXPANDED_SERVICE_CONFIGURATIONS"] =
                    step.RustExpandedServiceConfigurations;
            }
            if (!string.IsNullOrWhiteSpace(step.RustLocalAiProvider))
            {
                startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_LOCAL_AI_PROVIDER"] =
                    step.RustLocalAiProvider;
            }
            if (step.ScrollPercent > 0)
            {
                startInfo.Environment["EASYDICT_PREVIEW_SCROLL_PERCENT"] =
                    step.ScrollPercent.ToString(CultureInfo.InvariantCulture);
                startInfo.Environment["EASYDICT_PREVIEW_SCROLL_TARGET"] = "MainScrollViewer";
            }

            var automation = new UIA3Automation();
            try
            {
                var application = Application.Launch(startInfo);
                return new RustPreviewApp(application, automation, schemaPath);
            }
            catch
            {
                automation.Dispose();
                throw;
            }
        }

        private static void ClearRustPreviewSettingsStepEnvironment(ProcessStartInfo startInfo)
        {
            foreach (var key in new[]
            {
                "EASYDICT_PREVIEW_SETTINGS_SECTION",
                "EASYDICT_PREVIEW_SETTINGS_HOVERED_SECTION",
                "EASYDICT_PREVIEW_SETTINGS_PRESSED_SECTION",
                "EASYDICT_PREVIEW_SETTINGS_TAB_SWITCHING",
                "EASYDICT_PREVIEW_SETTINGS_VIEW_SERVICE_PROFILE",
                "EASYDICT_PREVIEW_SETTINGS_TTS_SPEED_STATE",
                "EASYDICT_PREVIEW_SETTINGS_AUTO_PLAY_STATE",
                "EASYDICT_PREVIEW_SETTINGS_IMPORT_MDX_STATE",
                "EASYDICT_PREVIEW_SETTINGS_INTERNATIONAL_TOGGLE_STATE",
                "EASYDICT_PREVIEW_SETTINGS_DEEPL_EXPANDER_STATE",
                "EASYDICT_PREVIEW_SETTINGS_EXPANDED_SERVICE_CONFIGURATIONS",
                "EASYDICT_PREVIEW_SETTINGS_LOCAL_AI_PROVIDER",
                "EASYDICT_PREVIEW_TRANSLATION_LANGUAGES_EXPANDED",
                "EASYDICT_PREVIEW_SCROLL_PERCENT",
                "EASYDICT_PREVIEW_SCROLL_TARGET",
                "EASYDICT_PREVIEW_SCROLL_DELAY_MS"
            })
            {
                startInfo.Environment.Remove(key);
            }
        }

        public static RustPreviewApp LaunchMainPreview(
            string scenario,
            string theme,
            ITestOutputHelper output,
            IReadOnlyDictionary<string, string>? extraEnvironment = null,
            int minimumWindowWidth = 120,
            int minimumWindowHeight = 120)
        {
            var exePath = ResolveRustPreviewExecutable(output);
            var schemaPath = CreateSchemaPath($"main-{scenario}");
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.Combine(FindRepositoryRoot(), "rs"),
                UseShellExecute = false
            };
            UiaSettingsIsolation.ApplyTo(startInfo);
            startInfo.Environment["EASYDICT_PREVIEW_WINDOW"] = "main";
            startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_OPEN"] = "0";
            startInfo.Environment["EASYDICT_PREVIEW_SCENARIO"] = scenario;
            startInfo.Environment["EASYDICT_PREVIEW_THEME"] = theme;
            startInfo.Environment["EASYDICT_PREVIEW_UI_LANGUAGE"] = ResolveParityUiLanguage();
            startInfo.Environment["EASYDICT_PREVIEW_SCHEMA_PATH"] = schemaPath;
            if (extraEnvironment != null)
            {
                foreach (var (key, value) in extraEnvironment)
                {
                    startInfo.Environment[key] = value;
                }
            }

            var automation = new UIA3Automation();
            try
            {
                var application = Application.Launch(startInfo);
                return new RustPreviewApp(
                    application,
                    automation,
                    schemaPath,
                    minimumWindowWidth,
                    minimumWindowHeight);
            }
            catch
            {
                automation.Dispose();
                throw;
            }
        }

        public static RustPreviewApp LaunchWindowPreview(
            string windowKind,
            string theme,
            ITestOutputHelper output,
            IReadOnlyDictionary<string, string>? extraEnvironment = null)
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["EASYDICT_PREVIEW_WINDOW"] = windowKind
            };
            if (string.Equals(windowKind, "mini", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(windowKind, "fixed", StringComparison.OrdinalIgnoreCase))
            {
                environment["EASYDICT_PREVIEW_FLOATING_CONTENT"] = "empty";
            }
            if (extraEnvironment != null)
            {
                foreach (var (key, value) in extraEnvironment)
                {
                    environment[key] = value;
                }
            }

            var minimumSize = string.Equals(windowKind, "pop-button", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(windowKind, "popbutton", StringComparison.OrdinalIgnoreCase)
                ? 20
                : 120;

            return LaunchMainPreview(
                "initial",
                theme,
                output,
                environment,
                minimumWindowWidth: minimumSize,
                minimumWindowHeight: minimumSize);
        }

        private static string CreateSchemaPath(string scenarioId)
        {
            Directory.CreateDirectory(ScreenshotHelper.OutputDir);
            return Path.Combine(
                ScreenshotHelper.OutputDir,
                $"{SanitizeFileName(scenarioId)}-rust-view-schema.txt");
        }

        public Window GetMainWindow(TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            Exception? lastException = null;
            while (stopwatch.Elapsed < timeout)
            {
                if (_application.HasExited)
                {
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
                var processId = _application.ProcessId;
                return _application
                    .GetAllTopLevelWindows(_automation)
                    .Where(window => BelongsToProcess(window, processId))
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
            return bounds.Width >= _minimumWindowWidth && bounds.Height >= _minimumWindowHeight;
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
            if (File.Exists(defaultPath))
            {
                return defaultPath;
            }

            if (IsTruthy(Environment.GetEnvironmentVariable(RustPreviewBuildEnvironmentVariable)))
            {
                output.WriteLine("Building Rust preview executable: cargo build -p easydict_preview_iced");
                var build = Process.Start(new ProcessStartInfo
                {
                    FileName = "cargo",
                    Arguments = "build -p easydict_preview_iced",
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
                $"Rust preview executable not found. Build it with `cargo build -p easydict_preview_iced`, set {RustPreviewBuildEnvironmentVariable}=1, or set {RustPreviewExeEnvironmentVariable}.",
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

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _application.Close();
                if (!_application.HasExited)
                {
                    Thread.Sleep(800);
                }

                if (!_application.HasExited)
                {
                    _application.Kill();
                }
            }
            catch
            {
                // Best-effort cleanup; the UIA suite runs isolated processes.
            }
            finally
            {
                _automation.Dispose();
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeWindowRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const int GWL_EXSTYLE = -20;
    private const int ShowWindowHide = 0;
    private const int ShowWindowRestore = 9;
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeWindowRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
