using System.Runtime.InteropServices;
using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class DictionaryWebViewRenderingTests : IDisposable
{
    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;
    private readonly DictionaryRenderingFixture _fixture;

    public DictionaryWebViewRenderingTests(ITestOutputHelper output)
    {
        _output = output;
        _fixture = new DictionaryRenderingFixture();
        _launcher = new AppLauncher();
        try
        {
            _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        }
        catch
        {
            _launcher.Dispose();
            _fixture.Dispose();
            throw;
        }
    }

    [Fact]
    public void MainWindow_DictionaryWebView_CapturesScreenshotForManualReview()
    {
        var window = _launcher.GetMainWindow();
        Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByName(DictionaryRenderingFixture.ServiceName)),
            TimeSpan.FromSeconds(10)).Result.Should().NotBeNull("the isolated local dictionary must be configured before querying");

        var inputBox = UITestHelper.FindInputTextBox(window);

        inputBox.Should().NotBeNull("InputTextBox must exist on main window");

        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = DictionaryRenderingFixture.Query;
        Thread.Sleep(500);

        var pathBeforeTranslate = ScreenshotHelper.CaptureWindow(window, "50_dictionary_webview_before_query");
        _output.WriteLine($"Screenshot saved: {pathBeforeTranslate}");
        File.Exists(pathBeforeTranslate).Should().BeTrue("the pre-query screenshot should be written for manual review");

        Keyboard.Type(VirtualKeyShort.ENTER);

        var visibleResult = Retry.WhileNull(
            () => TryFindVisibleDescendant(window, "DictWebView")
                ?? TryFindVisibleDescendant(window, "ResultText"),
            TimeSpan.FromSeconds(20)).Result;

        _output.WriteLine($"App has exited after dictionary query: {_launcher.Application.HasExited}");
        _launcher.Application.HasExited.Should().BeFalse(
            "triggering a dictionary query should not freeze the UI hard enough to terminate the app");

        var pathAfterTranslate = ScreenshotHelper.CaptureWindow(window, "51_dictionary_webview_after_query");
        _output.WriteLine($"Screenshot saved: {pathAfterTranslate}");
        File.Exists(pathAfterTranslate).Should().BeTrue("the post-query screenshot should be written for manual review");

        visibleResult.Should().NotBeNull("the local MDX fixture must render HTML or its plain-text fallback without an online service");

        var visibleDictWebView = Retry.WhileNull(
            () => TryFindVisibleDescendant(window, "DictWebView"),
            TimeSpan.FromSeconds(5)).Result;

        if (visibleDictWebView != null)
        {
            Retry.WhileFalse(() => TryFindVisibleDescendant(window, "DictWebView")?.BoundingRectangle.Height > 20,
                TimeSpan.FromSeconds(10)).Result.Should().BeTrue("the WebView must finish laying out its dictionary content");
            visibleDictWebView = TryFindVisibleDescendant(window, "DictWebView")!;
            var pathElement = ScreenshotHelper.CaptureElementPhysical(window, visibleDictWebView, "52_dictionary_webview_element");
            _output.WriteLine($"Element screenshot saved: {pathElement}");
            File.Exists(pathElement).Should().BeTrue("the dictionary WebView element screenshot should be written when the WebView is present");
        }
        else
        {
            var visibleResultText = Retry.WhileNull(
                () => TryFindVisibleDescendant(window, "ResultText"),
                TimeSpan.FromSeconds(5)).Result;

            visibleResultText.Should().NotBeNull(
                "when WebView2 cannot render the MDX HTML, the plain-text fallback should still be visible for dictionary entries that exist");
            visibleResultText!.Name.Should().Contain(DictionaryRenderingFixture.Definition);
            _output.WriteLine("Dictionary WebView not visible; plain-text fallback is visible instead.");
        }

        var comparison = VisualRegressionHelper.CompareWithBaseline(
            pathAfterTranslate,
            "dictionary_webview_main_window",
            VisualRegressionHelper.ThresholdText);

        if (comparison == null)
        {
            _output.WriteLine("No baseline found - screenshot saved as baseline candidate for manual review.");
        }
        else
        {
            _output.WriteLine(comparison.ToString());
        }
    }

    public void Dispose()
    {
        try { _launcher.Dispose(); }
        finally { _fixture.Dispose(); }
    }

    private static AutomationElement? TryFindVisibleDescendant(AutomationElement root, string automationId)
    {
        try
        {
            return root.FindAllDescendants(cf => cf.ByAutomationId(automationId))
                .FirstOrDefault(candidate => !candidate.IsOffscreen);
        }
        catch (COMException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
