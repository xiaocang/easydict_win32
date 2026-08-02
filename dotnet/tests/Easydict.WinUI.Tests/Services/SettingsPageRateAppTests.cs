using System.Xml.Linq;
using Easydict.WinUI.Views;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class SettingsPageRateAppTests
{
    private const string StoreReviewUri = "ms-windows-store://review/?ProductId=9P7NQVXF9DZJ";
    private const string StoreProductPageUri = "https://apps.microsoft.com/detail/9P7NQVXF9DZJ";
    private static readonly string _settingsPageXamlPath = Path.Combine(
        FindProjectRoot(),
        "src",
        "Easydict.WinUI",
        "Views",
        "SettingsPage.xaml");

    [Fact]
    public async Task LaunchStoreRatingAsync_DoesNotFallback_WhenStoreReviewLaunches()
    {
        var launchedUris = new List<string>();

        var launched = await SettingsPage.LaunchStoreRatingAsync(uri =>
        {
            launchedUris.Add(uri.AbsoluteUri);
            return Task.FromResult(true);
        });

        launched.Should().BeTrue();
        launchedUris.Should().Equal(StoreReviewUri);
    }

    [Fact]
    public async Task LaunchStoreRatingAsync_UsesProductPage_WhenStoreReviewReturnsFalse()
    {
        var launchedUris = new List<string>();
        var outcomes = new Queue<bool>(new[] { false, true });

        var launched = await SettingsPage.LaunchStoreRatingAsync(uri =>
        {
            launchedUris.Add(uri.AbsoluteUri);
            return Task.FromResult(outcomes.Dequeue());
        });

        launched.Should().BeTrue();
        launchedUris.Should().Equal(StoreReviewUri, StoreProductPageUri);
    }

    [Fact]
    public async Task LaunchStoreRatingAsync_UsesProductPage_WhenStoreReviewThrows()
    {
        var launchedUris = new List<string>();

        var launched = await SettingsPage.LaunchStoreRatingAsync(uri =>
        {
            launchedUris.Add(uri.AbsoluteUri);
            return launchedUris.Count == 1
                ? Task.FromException<bool>(new InvalidOperationException("Store unavailable"))
                : Task.FromResult(true);
        });

        launched.Should().BeTrue();
        launchedUris.Should().Equal(StoreReviewUri, StoreProductPageUri);
    }

    [Fact]
    public void RateAppLink_BindsExplicitLaunchHandler()
    {
        var document = XDocument.Load(_settingsPageXamlPath);
        var xamlNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var rateAppLink = document.Descendants().Single(element =>
            (string?)element.Attribute(xamlNamespace + "Name") == "RateAppLink");

        rateAppLink.Attribute("AutomationProperties.AutomationId")!.Value
            .Should().Be("MicrosoftStoreRatingLink");
        rateAppLink.Attribute("Click")!.Value.Should().Be("OnRateAppLinkClick");
        rateAppLink.Attribute("NavigateUri").Should().BeNull();
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Easydict.Win32.sln")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }
}
