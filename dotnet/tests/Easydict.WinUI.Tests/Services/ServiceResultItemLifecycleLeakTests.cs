using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Static regression checks for ServiceResultItem cleanup paths that can retain handlers or WebView2 resources.
/// </summary>
[Trait("Category", "Configuration")]
public class ServiceResultItemLifecycleLeakTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string ServiceResultItemPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml.cs");

    [Fact]
    public void ServiceResultItem_HasCleanupMethod()
    {
        var content = File.ReadAllText(ServiceResultItemPath);
        content.Should().Contain("public void Cleanup()",
            "MainPage should be able to explicitly release result-control resources before rebuilding the panel");
    }

    [Fact]
    public void ServiceResultItem_CleanupDetachesServiceResultPropertyChanged()
    {
        var content = File.ReadAllText(ServiceResultItemPath);
        content.Should().Contain("_serviceResult.PropertyChanged -= OnServiceResultPropertyChanged;",
            "Cleanup should break the ServiceQueryResult -> control event chain");
        content.Should().Contain("_serviceResult = null;",
            "Cleanup should clear the tracked result reference after detaching handlers");
    }

    [Fact]
    public void ServiceResultItem_CleanupDetachesWebView2Events()
    {
        var content = File.ReadAllText(ServiceResultItemPath);
        content.Should().Contain("webView.NavigationCompleted -= OnDictWebViewNavigationCompleted;",
            "Cleanup should detach WebView2 navigation handlers");
        content.Should().Contain("webView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;",
            "Cleanup should detach WebView2 resource handlers");
        content.Should().Contain("webView.CoreWebView2.WebMessageReceived -= OnDictWebViewWebMessageReceived;",
            "Cleanup should detach WebView2 message handlers");
    }

    [Fact]
    public void ServiceResultItem_CleanupClearsHeavyUiReferences()
    {
        var content = File.ReadAllText(ServiceResultItemPath);
        content.Should().Contain("ServiceIcon.Source = null;",
            "Cleanup should release cached icon image references");
        content.Should().Contain("_currentMdxService = null;",
            "Cleanup should release the current MDX service reference");
        content.Should().Contain("DictionaryPanel.Children.Clear();",
            "Cleanup should release dynamically generated MDX result visuals before the control is discarded");
        content.Should().Contain("PhoneticPanel.Children.Clear();",
            "Cleanup should release dynamically generated phonetic badges before the control is discarded");
        content.Should().Contain("webView.NavigateToString(\"<html><body></body></html>\");",
            "Cleanup should reset WebView2 content before the control is discarded");
        content.Should().Contain("webView.Close();",
            "Cleanup should close the lazily-created WebView2 control to release browser resources");
        content.Should().Contain("DictWebViewHost.Children.Clear();",
            "Cleanup should detach the lazily-created WebView2 from the visual tree");
        content.Should().Contain("_dictWebView = null;",
            "Cleanup should clear the WebView2 field after releasing it");
    }

    [Fact]
    public void ServiceResultItem_CreatesDictionaryWebViewLazily()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml"));
        var content = File.ReadAllText(ServiceResultItemPath);

        xaml.Should().Contain("x:Name=\"DictWebViewHost\"",
            "XAML should keep only a lightweight host in the normal result item tree");
        xaml.Should().NotContain("<WebView2",
            "WebView2 should not be constructed with every result item at InitializeComponent time");
        content.Should().Contain("private WebView2 EnsureDictionaryWebView()",
            "HTML dictionary results should create WebView2 only on demand");
        content.Should().Contain("ScheduleDictionaryWebViewRelease();",
            "non-HTML transitions should release WebView2 after a short idle delay");
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var solutionPath = Path.Combine(current, "Easydict.Win32.sln");
            if (File.Exists(solutionPath))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }
}
