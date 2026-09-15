using Easydict.BobPlugin.Mapping;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Mapping;

public class BobErrorMapperTests
{
    private static readonly TranslationRequest Request = new()
    {
        Text = "word",
        FromLanguage = Language.English,
        ToLanguage = Language.SimplifiedChinese
    };

    [Theory]
    [InlineData("notFound", true)]
    [InlineData("NOTFOUND", true)]
    [InlineData("network", false)]
    [InlineData(null, false)]
    public void IsNotFound_RecognisesTheLookupMiss(string? type, bool expected)
        => BobErrorMapper.IsNotFound(new BobError { Type = type }).Should().Be(expected);

    [Fact]
    public void TryMapToNoResult_TurnsNotFoundIntoANeutralOutcome()
    {
        var result = BobErrorMapper.TryMapToNoResult(
            new BobError { Type = "notFound", Message = "No entry for word." },
            Request,
            "Example");

        result.Should().NotBeNull();
        result!.ResultKind.Should().Be(TranslationResultKind.NoResult);
        result.InfoMessage.Should().Be("No entry for word.");
        result.TranslatedText.Should().BeEmpty();
        result.ServiceName.Should().Be("Example");
    }

    [Fact]
    public void TryMapToNoResult_SuppliesAMessageWhenThePluginDoesNot()
    {
        var result = BobErrorMapper.TryMapToNoResult(new BobError { Type = "notFound" }, Request, "Example");

        result!.InfoMessage.Should().Be("No result");
    }

    [Fact]
    public void TryMapToNoResult_LeavesRealFailuresAlone()
        => BobErrorMapper.TryMapToNoResult(new BobError { Type = "network" }, Request, "Example").Should().BeNull();

    [Theory]
    [InlineData("unsupportLanguage", TranslationErrorCode.UnsupportedLanguage)]
    [InlineData("unsupportedLanguage", TranslationErrorCode.UnsupportedLanguage)]
    [InlineData("secretKey", TranslationErrorCode.InvalidApiKey)]
    [InlineData("network", TranslationErrorCode.NetworkError)]
    [InlineData("api", TranslationErrorCode.ServiceUnavailable)]
    [InlineData("param", TranslationErrorCode.InvalidResponse)]
    [InlineData("unknown", TranslationErrorCode.Unknown)]
    [InlineData("something-new", TranslationErrorCode.Unknown)]
    [InlineData(null, TranslationErrorCode.Unknown)]
    public void MapToException_MapsBobsErrorTypes(string? type, TranslationErrorCode expected)
    {
        var exception = BobErrorMapper.MapToException(new BobError { Type = type }, "bob:x:test");

        exception.ErrorCode.Should().Be(expected);
        exception.ServiceId.Should().Be("bob:x:test");
    }

    [Fact]
    public void MapToException_KeepsTheMessageAdditionAndLink()
    {
        var exception = BobErrorMapper.MapToException(
            new BobError
            {
                Type = "secretKey",
                Message = "The API key is missing.",
                Addition = "Set it in the plugin options.",
                TroubleshootingLink = "https://example.invalid/help"
            },
            "bob:x:test");

        exception.Message.Should().Be("The API key is missing. (Set it in the plugin options.)");
        exception.DocumentationUrl.Should().Be("https://example.invalid/help");
    }

    [Fact]
    public void MapToException_DescribesTheErrorWhenThePluginSaysNothing()
    {
        var exception = BobErrorMapper.MapToException(new BobError { Type = "network" }, "bob:x:test");

        exception.Message.Should().Contain("NetworkError");
        exception.DocumentationUrl.Should().BeNull();
    }

    [Fact]
    public void MapToException_HandlesAMissingErrorObject()
    {
        var exception = BobErrorMapper.MapToException(null, "bob:x:test");

        exception.ErrorCode.Should().Be(TranslationErrorCode.Unknown);
    }
}
