using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Foundation;

public class ServiceOriginTests
{
    [Fact]
    public void BuiltIn_IsNative()
    {
        ServiceOrigin.BuiltIn.Kind.Should().Be(ServiceOriginKind.BuiltIn);
        ServiceOrigin.BuiltIn.IsNative.Should().BeTrue();
        ServiceOrigin.BuiltIn.Label.Should().BeNull();
    }

    [Fact]
    public void Plugin_IsNotNative_AndCarriesLabel()
    {
        var origin = new ServiceOrigin(ServiceOriginKind.Plugin, "Bob", "com.example.plugin v1.2.0");

        origin.IsNative.Should().BeFalse();
        origin.Label.Should().Be("Bob");
        origin.Should().Be(new ServiceOrigin(ServiceOriginKind.Plugin, "Bob", "com.example.plugin v1.2.0"));
    }

    [Fact]
    public void ServiceQueryResult_DefaultsToBuiltIn()
    {
        var row = new ServiceQueryResult { ServiceId = "google" };

        row.Origin.Should().Be(ServiceOrigin.BuiltIn);
        row.IsPluginService.Should().BeFalse();
        new ServiceQueryResult { ServiceId = "bob:x:y", Origin = new ServiceOrigin(ServiceOriginKind.Plugin, "Bob") }
            .IsPluginService.Should().BeTrue();
    }

    [Fact]
    public void TranslationRequest_NewOptionalFields_DefaultToNull()
    {
        var request = new TranslationRequest { Text = "x", ToLanguage = Language.English };

        request.OriginalText.Should().BeNull();
        request.DetectedFromLanguage.Should().BeNull();
    }
}
