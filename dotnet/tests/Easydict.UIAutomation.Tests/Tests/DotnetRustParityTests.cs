using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Tools;
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

    private readonly ITestOutputHelper _output;
    private readonly AppLauncher _dotnetLauncher = new();

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

        _dotnetLauncher.LaunchAuto(TimeSpan.FromSeconds(45));
        var dotnetWindow = _dotnetLauncher.GetMainWindow(TimeSpan.FromSeconds(20));

        foreach (var step in steps)
        {
            using var rustPreview = RustPreviewApp.Launch(step, _output);
            var rustWindow = rustPreview.GetMainWindow(TimeSpan.FromSeconds(30));
            var dotnetScrollViewer = OpenDotnetSettingsSection(dotnetWindow, step.Section);

            ArrangeSideBySide(dotnetWindow, rustWindow);

            if (step.ExpandAvailableLanguages)
            {
                ExpandDotnetAvailableLanguages(dotnetWindow, dotnetScrollViewer, step);
            }

            ScrollBothWindowsToPercent(dotnetScrollViewer, rustWindow, step);
            AssertCaptureStepReady(dotnetWindow, dotnetScrollViewer, rustWindow, step);
            AssertWindowFullyVisible(dotnetWindow, step.Key, "dotnet");
            AssertWindowFullyVisible(rustWindow, step.Key, "rust");

            var dotnetPath = ScreenshotHelper.CaptureWindow(
                dotnetWindow,
                $"{step.Key}-dotnet-winui-reference");
            var rustPath = ScreenshotHelper.CaptureWindow(
                rustWindow,
                $"{step.Key}-rust-win-fluent-iced");
            var sideBySidePath = SaveSideBySideComparison(
                dotnetPath,
                rustPath,
                $"{step.Key}-dotnet-vs-rust-side-by-side");

            AssertImageHasVisibleContent(dotnetPath);
            AssertImageHasVisibleContent(rustPath);
            AssertImageHasVisibleContent(sideBySidePath);

            _output.WriteLine($"[{step.Key}] Dotnet screenshot: {dotnetPath}");
            _output.WriteLine($"[{step.Key}] Rust screenshot: {rustPath}");
            _output.WriteLine($"[{step.Key}] Side-by-side comparison: {sideBySidePath}");
        }
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
            var settingsButton = UITestHelper.WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
            settingsButton.Should().NotBeNull("dotnet app should expose the Settings button");
            UITestHelper.ClickElement(settingsButton!);

            scrollViewer = Retry.WhileNull(
                    () => FindVisibleByAutomationId(window, "MainScrollViewer"),
                    TimeSpan.FromSeconds(15))
                .Result;
        }

        scrollViewer.Should().NotBeNull("dotnet Settings page should open before section comparison");
        ScrollHelper.MouseScrollToPercent(scrollViewer!, 0);

        var tab = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, $"SettingsTab_{section.Label}"),
                TimeSpan.FromSeconds(10))
            .Result;
        tab.Should().NotBeNull($"dotnet Settings tab {section.Label} should be visible");
        UITestHelper.ClickElement(tab!);

        Retry.WhileNull(
                () => FindVisibleByAutomationIdOrName(window, section.DotnetReadyElement),
                TimeSpan.FromSeconds(15))
            .Result
            .Should()
            .NotBeNull($"dotnet Settings section {section.Label} should show {section.DotnetReadyElement}");

        return scrollViewer!;
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
        ScrollHelper.MouseScrollToPercent(
            rustWindow,
            step.ScrollPercent,
            message => _output.WriteLine($"[{step.Key}][rust] {message}"),
            GetRustPreviewScrollPoint(rustWindow));
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

            ScrollHelper.MouseScrollToPercent(
                rustWindow,
                step.ScrollPercent,
                message => _output.WriteLine($"[{step.Key}][rust] {message}"),
                GetRustPreviewScrollPoint(rustWindow));
        }

        if (step.ExpandAvailableLanguages)
        {
            WaitForVisibleDotnetLanguageCheckboxes(dotnetWindow, minimumCount: 4, timeout: TimeSpan.FromSeconds(6))
                .Should()
                .BeGreaterThanOrEqualTo(4, "expanded translation-languages screenshot should show dotnet language checkboxes");
        }
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

    private static Point GetRustPreviewScrollPoint(Window rustWindow)
    {
        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(rustWindow);
        return new Point(
            bounds.Left + Math.Max(80, (int)Math.Round(bounds.Width * 0.62)),
            bounds.Top + Math.Max(140, (int)Math.Round(bounds.Height * 0.54)));
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

    private static void ArrangeSideBySide(Window dotnetWindow, Window rustWindow)
    {
        var screen = ScreenshotHelper.GetVirtualScreenBounds();
        var availableWidth = Math.Max(1280, screen.Width);
        var width = Math.Min(860, Math.Max(560, (availableWidth - 72) / 2));
        var height = Math.Min(920, Math.Max(680, screen.Height - 90));
        var top = screen.Top + 30;
        var left = screen.Left + 24;

        TrySetWindowToPhysicalTarget(dotnetWindow, new Rectangle(left, top, width, height));
        TrySetWindowToPhysicalTarget(rustWindow, new Rectangle(left + width + 24, top, width, height));
        Thread.Sleep(600);
    }

    private static void TrySetWindowToPhysicalTarget(Window window, Rectangle physicalTarget)
    {
        var dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        var requestedBounds = new Rectangle(
            (int)Math.Round(physicalTarget.Left / dpiScale),
            (int)Math.Round(physicalTarget.Top / dpiScale),
            (int)Math.Round(physicalTarget.Width / dpiScale),
            (int)Math.Round(physicalTarget.Height / dpiScale));

        ScreenshotHelper.TrySetWindowPhysicalBounds(window, requestedBounds);
    }

    private static void AssertWindowFullyVisible(Window window, string stepKey, string label)
    {
        var bounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        var visible = Rectangle.Intersect(bounds, ScreenshotHelper.GetVirtualScreenBounds());
        visible.Width.Should().BeGreaterThan(
            bounds.Width - 16,
            $"{stepKey} {label} window should be fully visible before capture");
        visible.Height.Should().BeGreaterThan(
            bounds.Height - 16,
            $"{stepKey} {label} window should be fully visible before capture");
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

    private static bool IsTruthy(string? value)
    {
        return value != null &&
               (string.Equals(value, "1", StringComparison.Ordinal) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _dotnetLauncher.Dispose();
    }

    private sealed record SettingsParitySection(string Id, string Label, string DotnetReadyElement)
    {
        public static readonly SettingsParitySection General = new("general", "General", "AppThemeCombo");

        public static readonly SettingsParitySection Views = new("views", "Views", "MainWindowReorderModeButton");
        public static readonly SettingsParitySection Hotkeys = new("hotkeys", "Hotkeys", "ShowHotkeyBox");
        public static readonly SettingsParitySection Language = new("language", "Language", "FirstLanguageCombo");
        public static readonly SettingsParitySection About = new("about", "About", "GitHub Repository");
    }

    private sealed record SettingsParityCaptureStep(
        string Key,
        SettingsParitySection Section,
        double ScrollPercent,
        bool ExpandAvailableLanguages = false,
        bool RustTranslationLanguagesExpanded = false)
    {
        public static readonly IReadOnlyList<SettingsParityCaptureStep> All =
        [
            new("parity-settings-general-behavior-top", SettingsParitySection.General, 0),
            new("parity-settings-general-tts-speed-slider-scroll-100-percent", SettingsParitySection.General, 100),
            new("parity-settings-views-window-results-top", SettingsParitySection.Views, 0),
            new("parity-settings-hotkeys-shortcut-inputs-top", SettingsParitySection.Hotkeys, 0),
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

    private sealed class RustPreviewApp : IDisposable
    {
        private readonly Application _application;
        private readonly UIA3Automation _automation;
        private bool _disposed;

        private RustPreviewApp(Application application, UIA3Automation automation)
        {
            _application = application;
            _automation = automation;
        }

        public static RustPreviewApp Launch(SettingsParityCaptureStep step, ITestOutputHelper output)
        {
            var exePath = ResolveRustPreviewExecutable(output);
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.Combine(FindRepositoryRoot(), "rs"),
                UseShellExecute = false
            };
            UiaSettingsIsolation.ApplyTo(startInfo);
            startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_OPEN"] = "1";
            startInfo.Environment["EASYDICT_PREVIEW_SETTINGS_SECTION"] = step.Section.Id;
            startInfo.Environment["EASYDICT_PREVIEW_THEME"] = "light";
            if (step.RustTranslationLanguagesExpanded)
            {
                startInfo.Environment["EASYDICT_PREVIEW_TRANSLATION_LANGUAGES_EXPANDED"] = "1";
            }

            var automation = new UIA3Automation();
            try
            {
                var application = Application.Launch(startInfo);
                return new RustPreviewApp(application, automation);
            }
            catch
            {
                automation.Dispose();
                throw;
            }
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
                        ?? _application.GetMainWindow(_automation, TimeSpan.FromSeconds(3));
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

                return _automation.FromHandle(process.MainWindowHandle).AsWindow();
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

        private static bool IsUsableWindow(Window window)
        {
            var bounds = window.BoundingRectangle;
            return bounds.Width >= 200 && bounds.Height >= 200;
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
}
