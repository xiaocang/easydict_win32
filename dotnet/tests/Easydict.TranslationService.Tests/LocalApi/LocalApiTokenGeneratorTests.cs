using Easydict.TranslationService.LocalApi;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.LocalApi;

public class LocalApiTokenGeneratorTests
{
    [Fact]
    public void Generate_has_expected_prefix()
    {
        LocalApiTokenGenerator.Generate().Should().StartWith("sk-edt-");
    }

    [Fact]
    public void Generate_has_expected_length()
    {
        // 20 random bytes → 32 base32 chars + "sk-edt-" prefix (7) = 39
        LocalApiTokenGenerator.Generate().Length.Should().Be(39);
    }

    [Fact]
    public void Generate_consecutive_calls_differ()
    {
        var a = LocalApiTokenGenerator.Generate();
        var b = LocalApiTokenGenerator.Generate();
        a.Should().NotBe(b);
    }
}
