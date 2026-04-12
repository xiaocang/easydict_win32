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

    private const string DictionaryQuery = "draft";
    private const int TranslationWaitMs = 10000;

    public DictionaryWebViewRenderingTests(ITestOutputHelper output)
    {
        _output = output;
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void MainWindow_DictionaryWebView_CapturesScreenshotForManualReview()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var inputBox = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("InputTextBox"))?.AsTextBox(),
            TimeSpan.FromSeconds(10)).Result;

        inputBox.Should().NotBeNull("InputTextBox must exist on main window");

        inputBox!.Click();
        Thread.Sleep(300);
        inputBox.Text = DictionaryQuery;
        Thread.Sleep(500);

        var pathBeforeTranslate = ScreenshotHelper.CaptureWindow(window, "50_dictionary_webview_before_query");
        _output.WriteLine($"Screenshot saved: {pathBeforeTranslate}");
        File.Exists(pathBeforeTranslate).Should().BeTrue("the pre-query screenshot should be written for manual review");

        Keyboard.Type(VirtualKeyShort.ENTER);

        _output.WriteLine($"Waiting {TranslationWaitMs}ms for dictionary results...");
        Thread.Sleep(TranslationWaitMs);

        _output.WriteLine($"App has exited after dictionary query: {_launcher.Application.HasExited}");
        _launcher.Application.HasExited.Should().BeFalse(
            "triggering a dictionary query should not freeze the UI hard enough to terminate the app");

        var pathAfterTranslate = ScreenshotHelper.CaptureWindow(window, "51_dictionary_webview_after_query");
        _output.WriteLine($"Screenshot saved: {pathAfterTranslate}");
        File.Exists(pathAfterTranslate).Should().BeTrue("the post-query screenshot should be written for manual review");

        var dictWebView = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("DictWebView")),
            TimeSpan.FromSeconds(5)).Result;

        if (dictWebView == null)
        {
            _output.WriteLine("Dictionary WebView not detected in this environment. Window screenshots were still captured for manual review.");
        }
        else
        {
            dictWebView.IsOffscreen.Should().BeFalse("the dictionary WebView should be visible once the word query completes");

            var pathElement = ScreenshotHelper.CaptureElement(dictWebView, "52_dictionary_webview_element");
            _output.WriteLine($"Element screenshot saved: {pathElement}");
            File.Exists(pathElement).Should().BeTrue("the dictionary WebView element screenshot should be written when the WebView is present");
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
        _launcher.Dispose();
    }
}
