using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Trait("Category", "DirectXaml")]
[Collection("UIAutomation")]
public sealed class DirectRendererTests : IDisposable
{
    private const string DirectResultText = "Copied direct result";
    private readonly ITestOutputHelper _output;
    private readonly AppLauncher _launcher = new();


    private readonly record struct CardCapture(
        string ScreenshotPath,
        Size PhysicalSize,
        double DpiScale,
        int TextPeerCount)
    {
        public double WidthDips => PhysicalSize.Width / DpiScale;
        public double HeightDips => PhysicalSize.Height / DpiScale;
    }
    public DirectRendererTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MinimalTheme_UsesCompiledWin2DResultCards()
    {
        var window = StartQuery(directRenderer: true);

        var card = Retry.WhileNull(
            () => window.FindFirstDescendant(
                condition => condition.ByAutomationId("ServiceResultItem_bing")),
            TimeSpan.FromSeconds(20)).Result;
        var header = Retry.WhileNull(
            () => window.FindFirstDescendant(
                condition => condition.ByAutomationId("ServiceResultHeader_bing")),
            TimeSpan.FromSeconds(20)).Result;

        card.Should().NotBeNull("the configured Bing result card must be present");
        header.Should().NotBeNull("the direct canvas is the card header compatibility surface");
        header!.FindAllDescendants()
            .Where(element => element.ControlType == FlaUI.Core.Definitions.ControlType.Text)
            .Should()
            .BeEmpty("the Direct backend paints card text instead of creating TextBlock peers");
        ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(40, 40, 800, 700))
            .Should()
            .BeTrue("the resize regression needs deterministic on-screen window bounds");
        Thread.Sleep(500);
        var initialHeader = Retry.WhileNull(
            () => window.FindFirstDescendant(
                condition => condition.ByAutomationId("ServiceResultHeader_bing")),
            TimeSpan.FromSeconds(5)).Result;
        initialHeader.Should().NotBeNull();
        var initialScreenshotPath = ScreenshotHelper.CaptureElementsPhysical(
            window,
            "direct-renderer-result-card-initial",
            padding: 0,
            initialHeader!);
        int initialHeaderWidth;
        using (var initialBitmap = new Bitmap(initialScreenshotPath))
        {
            initialHeaderWidth = initialBitmap.Width;
        }
        ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(40, 40, 1000, 700))
            .Should()
            .BeTrue();
        Thread.Sleep(1000);
        window.FindFirstDescendant(condition => condition.ByName("Unexpected Error"))
            .Should()
            .BeNull("resizing the Win2D surface must not raise an app-level error");
        var resizedHeader = Retry.WhileNull(
            () => window.FindFirstDescendant(
                condition => condition.ByAutomationId("ServiceResultHeader_bing")),
            TimeSpan.FromSeconds(5)).Result;
        resizedHeader.Should().NotBeNull();
        resizedHeader!.Name.Should().Be("Bing Translate");
        string? elementScreenshotPath = Retry.WhileNull(
            () =>
            {
                string candidate = ScreenshotHelper.CaptureElementsPhysical(
                    window,
                    "direct-renderer-result-card-resized",
                    padding: 0,
                    resizedHeader);
                return IsRightCardBorderPainted(candidate) ? candidate : null;
            },
            TimeSpan.FromSeconds(5)).Result;
        elementScreenshotPath.Should().NotBeNull(
            "the resized display list must reach the right edge of its own surface");
        using (var resizedBitmap = new Bitmap(elementScreenshotPath!))
        {
            resizedBitmap.Width.Should().BeGreaterThan(initialHeaderWidth + 150);
        }
        AssertCopyButtonIsPainted(elementScreenshotPath!);
        _output.WriteLine(elementScreenshotPath!);
        _output.WriteLine(
            ScreenshotHelper.CaptureWindow(window, "direct-renderer-result-cards-resized"));
    }


    [Fact]
    public async Task CopyButton_InvokesCommandFromPointerCoordinates()
    {
        var window = StartQuery(directRenderer: true);
        ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(40, 40, 1000, 700))
            .Should()
            .BeTrue("the pointer test needs a fully on-screen Direct card");
        Thread.Sleep(500);
        var header = Retry.WhileNull(
            () =>
            {
                var element = window.FindFirstDescendant(
                    condition => condition.ByAutomationId("ServiceResultHeader_bing"));
                return element?.BoundingRectangle.Height > 60 ? element : null;
            },
            TimeSpan.FromSeconds(20)).Result;
        header.Should().NotBeNull("the deterministic result must lay out the Copy hit region");

        uint sequenceBefore = GetClipboardSequenceNumber();
        var bounds = header!.BoundingRectangle;
        // The Copy button is right-aligned; keep the point inside its narrow physical interior.
        var copyPoint = new Point(
            (int)Math.Round(bounds.Left + (bounds.Width * 0.975)),
            (int)Math.Round(bounds.Top + (bounds.Height * 0.75)));
        Mouse.Click(copyPoint);

        var copied = await WaitForClipboardTextAsync(
            DirectResultText,
            sequenceBefore,
            TimeSpan.FromSeconds(5));
        copied.Should().Be(DirectResultText);
    }

    [Fact]
    public void MinimalTheme_StockXamlResultCardProvidesBenchmarkBaseline()
    {
        var window = StartQuery(directRenderer: false);
        var card = Retry.WhileNull(
            () => window.FindFirstDescendant(
                condition => condition.ByAutomationId("ServiceResultItem_bing")),
            TimeSpan.FromSeconds(20)).Result;
        var header = Retry.WhileNull(
            () => window.FindFirstDescendant(
                condition => condition.ByAutomationId("ServiceResultHeader_bing")),
            TimeSpan.FromSeconds(20)).Result;

        header.Should().NotBeNull("the stock XAML comparison card must be present");
        card.Should().NotBeNull("the stock XAML comparison card must be present");
        ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(40, 40, 1000, 700))
            .Should()
            .BeTrue("paired renderer screenshots require identical window bounds");
        var expandedCard = WaitForExpandedCard(window, directRenderer: false);
        expandedCard.Should().NotBeNull(
            "the XAML baseline screenshot must wait for the same expanded result-card state");
        _output.WriteLine(
            ScreenshotHelper.CaptureElementsPhysical(
                window,
                "xaml-renderer-result-card-resized",
                padding: 0,
                expandedCard!));
        header!.FindAllDescendants()
            .Where(element => element.ControlType == FlaUI.Core.Definitions.ControlType.Text)
            .Should()
            .NotBeEmpty("the XAML backend materializes its TextBlock peers");
    }

    [Fact]
    public void MinimalTheme_DirectAndXamlResultCards_HaveMatchingExpandedBounds()
    {
        CardCapture directCapture = CaptureExpandedCardMetrics(
            directRenderer: true,
            "direct-renderer-result-card-parity",
            expectsDirectCard: true);
        CardCapture xamlCapture = CaptureExpandedCardMetrics(
            directRenderer: false,
            "xaml-renderer-result-card-parity",
            expectsDirectCard: false);

        AssertExpandedCardParity(
            directCapture,
            xamlCapture,
            "direct-xaml-result-card-parity");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void FluentThemes_DirectRendererSetting_PreservesXamlResultCards(string appTheme)
    {
        string themeName = appTheme.ToLowerInvariant();
        CardCapture directSettingCapture = CaptureExpandedCardMetrics(
            directRenderer: true,
            screenshotName: $"direct-setting-fluent-{themeName}-result-card-parity",
            expectsDirectCard: false,
            appTheme: appTheme);
        CardCapture xamlCapture = CaptureExpandedCardMetrics(
            directRenderer: false,
            screenshotName: $"xaml-renderer-fluent-{themeName}-result-card-parity",
            expectsDirectCard: false,
            appTheme: appTheme);

        directSettingCapture.TextPeerCount.Should().BeGreaterThan(
            0,
            "Fluent result cards must retain their stock XAML content until a Fluent Direct IR exists");
        AssertExpandedCardParity(
            directSettingCapture,
            xamlCapture,
            $"direct-setting-xaml-fluent-{themeName}-result-card-parity");
    }

    private void AssertExpandedCardParity(
        CardCapture actualCapture,
        CardCapture expectedCapture,
        string comparisonName,
        double thresholdPercent = VisualRegressionHelper.ThresholdText)
    {
        actualCapture.DpiScale.Should().BeApproximately(
            expectedCapture.DpiScale,
            0.01,
            "the paired cards must be captured on the same DPI scale");
        actualCapture.WidthDips.Should().BeApproximately(
            expectedCapture.WidthDips,
            1,
            "the paired backends must use the same card width within the Direct XAML 1 DIP tolerance");
        actualCapture.HeightDips.Should().BeApproximately(
            expectedCapture.HeightDips,
            1,
            "the paired backends must use the same card height within the Direct XAML 1 DIP tolerance");

        int allowedSizeDeltaPixels = (int)Math.Ceiling(
            Math.Max(actualCapture.DpiScale, expectedCapture.DpiScale));
        var visualComparison = VisualRegressionHelper.ComparePairedScreenshots(
            actualCapture.ScreenshotPath,
            expectedCapture.ScreenshotPath,
            comparisonName,
            thresholdPercent,
            allowedSizeDeltaPixels);
        _output.WriteLine(visualComparison.ToString());
        if (visualComparison.DiffImagePath is not null)
        {
            _output.WriteLine($"Visual diff saved: {visualComparison.DiffImagePath}");
        }

        visualComparison.Passed.Should().BeTrue(
            $"the paired cards must remain within the {visualComparison.ThresholdPercent:F0}% text-rendering visual-diff threshold; observed {visualComparison.PixelErrorPercent:F2}%");
    }

    [Fact]
    public void DirectLoadingIndicator_AnimatesInHeader()
    {
        var window = StartQuery(directRenderer: true, loading: true);
        ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(40, 40, 800, 600))
            .Should()
            .BeTrue("the loading animation test needs a deterministic on-screen Direct card");

        var header = Retry.WhileNull(
            () => window.FindFirstDescendant(
                condition => condition.ByAutomationId("ServiceResultHeader_bing")),
            TimeSpan.FromSeconds(20)).Result;
        header.Should().NotBeNull();

        var framePaths = new List<string>();
        for (int frame = 0; frame < 4; frame++)
        {
            Thread.Sleep(150);
            framePaths.Add(ScreenshotHelper.CaptureElementsPhysical(
                window,
                $"direct-renderer-loading-frame-{frame}",
                padding: 0,
                header!));
        }

        AssertLoadingSpinnerFramesDiffer(framePaths);
        foreach (string framePath in framePaths)
        {
            _output.WriteLine(framePath);
        }
    }

    [Fact]
    public void DirectCards_RemainScrollableAcrossViewportResize()
    {
        var window = StartQuery(directRenderer: true, cardCount: 20);
        ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(40, 40, 800, 600))
            .Should()
            .BeTrue("the scroll regression needs deterministic on-screen window bounds");

        var scrollViewer = Retry.WhileNull(
            () => window.FindFirstDescendant(
                condition => condition.ByAutomationId("QuickTranslateContent")),
            TimeSpan.FromSeconds(20)).Result;
        scrollViewer.Should().NotBeNull();

        ScrollHelper.ScrollToPercent(scrollViewer!, 60, _output.WriteLine);
        ScrollHelper.TryGetVerticalScrollPercent(scrollViewer!, out double beforeResize)
            .Should()
            .BeTrue();
        beforeResize.Should().BeGreaterThan(0);

        ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(40, 40, 1000, 700))
            .Should()
            .BeTrue();
        Thread.Sleep(1000);

        ScrollHelper.TryGetVerticalScrollPercent(scrollViewer!, out double afterResize)
            .Should()
            .BeTrue();
        afterResize.Should().BeGreaterThan(
            0,
            "resizing Direct cards must not reset the outer results scroll position");

        ScrollHelper.ScrollToPercent(scrollViewer!, 100, _output.WriteLine);
        var lastHeader = Retry.WhileNull(
            () =>
            {
                var element = window.FindFirstDescendant(
                    condition => condition.ByAutomationId("ServiceResultHeader_benchmark-19"));
                return element is { IsOffscreen: false } ? element : null;
            },
            TimeSpan.FromSeconds(10)).Result;
        lastHeader.Should().NotBeNull("the last Direct card must remain reachable after resize");
        window.FindFirstDescendant(condition => condition.ByName("Unexpected Error"))
            .Should()
            .BeNull();
    }

    private CardCapture CaptureExpandedCardMetrics(
        bool directRenderer,
        string screenshotName,
        bool expectsDirectCard,
        string appTheme = "Minimal")
    {
        using var launcher = new AppLauncher();
        var window = StartQuery(launcher, directRenderer, appTheme: appTheme);
        ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(40, 40, 1000, 700))
            .Should()
            .BeTrue("paired renderer screenshots require identical window bounds");

        var card = WaitForExpandedCard(window, expectsDirectCard);
        card.Should().NotBeNull("the deterministic result must finish expanding before capture");

        string screenshotPath = ScreenshotHelper.CaptureElementsPhysical(
            window,
            screenshotName,
            padding: 0,
            card!);
        _output.WriteLine(screenshotPath);
        double dpiScale = ScreenshotHelper.GetWindowDpiScale(window);
        int textPeerCount = card!.FindAllDescendants()
            .Count(element => element.ControlType == FlaUI.Core.Definitions.ControlType.Text);
        using var bitmap = new Bitmap(screenshotPath);
        return new CardCapture(screenshotPath, bitmap.Size, dpiScale, textPeerCount);
    }

    private static AutomationElement? WaitForExpandedCard(
        Window window,
        bool directRenderer) =>
        Retry.WhileNull(
            () =>
            {
                var card = window.FindFirstDescendant(
                    condition => condition.ByAutomationId(
                        directRenderer
                            ? "ServiceResultHeader_bing"
                            : "ServiceResultItem_bing"));
                return card?.BoundingRectangle.Height > 60
                    ? card
                    : null;
            },
            TimeSpan.FromSeconds(20)).Result;

    private Window StartQuery(bool directRenderer, int cardCount = 1, bool loading = false) =>
        StartQuery(_launcher, directRenderer, cardCount, loading);

    private static Window StartQuery(
        AppLauncher launcher,
        bool directRenderer,
        int cardCount = 1,
        bool loading = false,
        string appTheme = "Minimal")
    {
        var settingsPath = UiaSettingsIsolation.TryGetSettingsFilePath();
        settingsPath.Should().NotBeNull("UI automation must use an isolated settings directory");
        ArgumentOutOfRangeException.ThrowIfLessThan(cardCount, 1);
        string[] services = new[] { "bing" }
            .Concat(Enumerable.Range(1, cardCount - 1).Select(index => $"benchmark-{index}"))
            .ToArray();
        File.WriteAllText(settingsPath!, JsonSerializer.Serialize(new
        {
            UILanguage = "en-US",
            HasUserConfiguredServices = true,
            AppTheme = appTheme,
            DirectRenderer = directRenderer,
            MainWindowEnabledServices = services,
            HideEmptyServiceResults = false,
        }));

        Environment.SetEnvironmentVariable("EASYDICT_UIA_DIRECT_RESULT_TEXT", DirectResultText);
        Environment.SetEnvironmentVariable(
            "EASYDICT_UIA_DIRECT_LOADING",
            loading ? "1" : null);

        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        var input = UITestHelper.FindInputTextBox(window, TimeSpan.FromSeconds(15));
        input.Should().NotBeNull();
        input!.Click();
        input.Text = "Direct renderer smoke test";
        Keyboard.Type(VirtualKeyShort.ENTER);
        return window;
    }


    private static bool IsRightCardBorderPainted(string screenshotPath)
    {
        using var bitmap = new Bitmap(screenshotPath);
        int borderPixels = 0;
        for (int x = Math.Max(0, bitmap.Width - 24); x < bitmap.Width; x++)
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.R < 225 && color.G < 225 && color.B < 225)
                {
                    borderPixels++;
                }
            }
        }

        return borderPixels > 12;
    }

    private static void AssertCopyButtonIsPainted(string screenshotPath)
    {
        using var bitmap = new Bitmap(screenshotPath);
        int darkPixels = 0;
        int left = (int)(bitmap.Width * 0.78);
        int top = (int)(bitmap.Height * 0.55);
        int right = Math.Max(left, bitmap.Width - 8);
        int bottom = Math.Max(top, bitmap.Height - 8);
        for (int x = left; x < right; x++)
        {
            for (int y = top; y < bottom; y++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.R < 200 && color.G < 200 && color.B < 200)
                {
                    darkPixels++;
                }
            }
        }

        darkPixels.Should().BeGreaterThan(
            30,
            $"the compiled Copy button must be visible in the lower-right card interior " +
            $"(bitmap={bitmap.Width}x{bitmap.Height})");
    }
    private static void AssertLoadingSpinnerFramesDiffer(IReadOnlyList<string> framePaths)
    {
        framePaths.Should().HaveCountGreaterThan(1);
        int greatestChangedPixels = 0;
        for (int index = 1; index < framePaths.Count; index++)
        {
            using var first = new Bitmap(framePaths[index - 1]);
            using var next = new Bitmap(framePaths[index]);
            next.Size.Should().Be(first.Size);

            greatestChangedPixels = Math.Max(
                greatestChangedPixels,
                CountSpinnerPixelChanges(first, next));
        }

        greatestChangedPixels.Should().BeGreaterThan(
            8,
            $"the Direct loading spinner must advance between sampled frames " +
            $"(samples={framePaths.Count})");
    }

    private static int CountSpinnerPixelChanges(Bitmap first, Bitmap next)
    {
        int changedPixels = 0;
        int left = Math.Max(0, first.Width - 160);
        int right = Math.Max(left, first.Width - 8);
        int bottom = Math.Min(first.Height, 32);
        for (int x = left; x < right; x++)
        {
            for (int y = 0; y < bottom; y++)
            {
                var before = first.GetPixel(x, y);
                var after = next.GetPixel(x, y);
                if (Math.Abs(before.R - after.R) > 12
                    || Math.Abs(before.G - after.G) > 12
                    || Math.Abs(before.B - after.B) > 12)
                {
                    changedPixels++;
                }
            }
        }

        return changedPixels;
    }

    private static async Task<string?> WaitForClipboardTextAsync(
        string expected,
        uint sequenceBefore,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (GetClipboardSequenceNumber() != sequenceBefore &&
                TryReadClipboardText() is { } text &&
                text == expected)
            {
                return text;
            }

            await Task.Delay(50);
        }

        return null;
    }

    private static string? TryReadClipboardText()
    {
        const uint UnicodeTextFormat = 13;
        if (!OpenClipboard(nint.Zero))
        {
            return null;
        }

        try
        {
            nint handle = GetClipboardData(UnicodeTextFormat);
            if (handle == nint.Zero)
            {
                return null;
            }

            nint pointer = GlobalLock(handle);
            if (pointer == nint.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint newOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EASYDICT_UIA_DIRECT_RESULT_TEXT", null);
        Environment.SetEnvironmentVariable("EASYDICT_UIA_DIRECT_LOADING", null);
        _launcher.Dispose();
    }

}
