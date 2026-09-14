using Easydict.BobPlugin.Adapter;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Adapter;

/// <summary>
/// Drives real fixture plugins through the JavaScript engine, one fixture per behavior. These are
/// the tests that prove the host bridges, the prelude and the result mapping line up.
/// </summary>
public class BobTranslationServiceRuntimeTests
{
    private static async Task<(List<TranslationStreamUpdate> Updates, TranslationResult Result)> RunAsync(
        BobTranslationService service,
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<TranslationStreamUpdate>();
        await foreach (var update in service.TranslateStreamUpdatesAsync(request, cancellationToken))
        {
            updates.Add(update);
        }

        var completed = updates.OfType<TranslationStreamUpdate.Completed>().Should().ContainSingle().Which;
        return (updates, completed.Result);
    }

    [Fact]
    public async Task ATopLevelTranslateFunctionIsFound()
    {
        using var fixture = PluginFixture.Create("echo-global");

        var result = await fixture.Service().TranslateAsync(PluginFixture.Request("hello"));

        result.TranslatedText.Should().Be("ECHO(1): hello");
        result.ResultKind.Should().Be(TranslationResultKind.Success);
    }

    [Fact]
    public async Task AnExportedTranslateFunctionIsFound()
    {
        using var fixture = PluginFixture.Create("echo-exports");

        var result = await fixture.Service().TranslateAsync(PluginFixture.Request("hello"));

        result.TranslatedText.Should().Be("EXPORTS: hello\nsecond line");
    }

    [Fact]
    public async Task TranslateIsCalledExactlyOncePerQuery()
    {
        using var fixture = PluginFixture.Create("echo-global");
        var service = fixture.Service();

        var first = await service.TranslateAsync(PluginFixture.Request("one"));
        var second = await service.TranslateAsync(PluginFixture.Request("two"));

        // The fixture counts its own invocations, so a second call for dictionary data would show.
        first.TranslatedText.Should().Be("ECHO(1): one");
        second.TranslatedText.Should().Be("ECHO(2): two");
    }

    [Fact]
    public async Task TheQueryCarriesEveryFieldBobDefines()
    {
        using var fixture = PluginFixture.Create("query-shape");
        var request = PluginFixture.Request(
            "processed",
            Language.Auto,
            Language.Japanese,
            originalText: "original",
            detectedFrom: Language.SimplifiedChinese);

        var result = await fixture.Service().TranslateAsync(request);

        result.TranslatedText.Split('\n').Should().Equal(
            "text=processed",
            "originalText=original",
            "from=auto",
            "to=ja",
            "detectFrom=zh-Hans",
            "detectTo=ja");
    }

    [Fact]
    public async Task DetectFromFallsBackToTheScriptOfTheText()
    {
        using var fixture = PluginFixture.Create("query-shape");

        var result = await fixture.Service().TranslateAsync(
            PluginFixture.Request("こんにちは", Language.Auto, Language.English));

        result.TranslatedText.Should().Contain("detectFrom=ja");
    }

    [Fact]
    public async Task DictionaryDataArrivesInTheSameCall()
    {
        using var fixture = PluginFixture.Create("dict");

        var (_, result) = await RunAsync(fixture.Service(), PluginFixture.Request("word"));

        result.TranslatedText.Should().Be("单词");
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Phonetics.Should().HaveCount(2);
        result.WordResult.Definitions.Should().HaveCount(3);
        result.WordResult.WordForms.Should().HaveCount(2);
        result.WordResult.Synonyms.Should().HaveCount(1);
        result.RawHtml.Should().BeNull();
    }

    [Fact]
    public async Task StreamedPartialsArriveBeforeTheStructuredResult()
    {
        using var fixture = PluginFixture.Create("stream");

        var (updates, result) = await RunAsync(fixture.Service(), PluginFixture.Request("hello world"));

        updates.OfType<TranslationStreamUpdate.TextSnapshot>().Select(s => s.Text)
            .Should().Equal("Hel", "Hello", "Hello wor");
        updates.Last().Should().BeOfType<TranslationStreamUpdate.Completed>();
        result.TranslatedText.Should().Be("Hello world");
        result.WordResult.Should().NotBeNull("the dictionary must arrive without a second translate() call");
    }

    [Fact]
    public async Task TranslateStreamAsyncYieldsTheDeltasOfThoseSnapshots()
    {
        using var fixture = PluginFixture.Create("stream");

        var deltas = new List<string>();
        await foreach (var delta in fixture.Service().TranslateStreamAsync(PluginFixture.Request("hello world")))
        {
            deltas.Add(delta);
        }

        string.Concat(deltas).Should().Be("Hello world");
    }

    [Fact]
    public async Task NotFoundIsANeutralOutcomeRatherThanAFailure()
    {
        using var fixture = PluginFixture.Create("error-notfound");

        var result = await fixture.Service().TranslateAsync(PluginFixture.Request("qwertyuiop"));

        result.ResultKind.Should().Be(TranslationResultKind.NoResult);
        result.InfoMessage.Should().Be("No entry for qwertyuiop");
        result.TranslatedText.Should().BeEmpty();
    }

    [Fact]
    public async Task AConfigurationErrorSurfacesAsAnApiKeyFailure()
    {
        using var fixture = PluginFixture.Create("error-secretkey", secureOptionIds: ["apiKey"]);

        var act = async () => await fixture.Service().TranslateAsync(PluginFixture.Request());

        var exception = (await act.Should().ThrowAsync<TranslationException>()).Which;
        exception.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
        exception.DocumentationUrl.Should().Be("https://example.invalid/help");
    }

    [Fact]
    public async Task AThrowingPluginFailsTheQueryInsteadOfHangingIt()
    {
        using var fixture = PluginFixture.Create("throws");

        var act = async () => await fixture.Service().TranslateAsync(PluginFixture.Request());

        (await act.Should().ThrowAsync<TranslationException>()).Which.Message.Should().Contain("plugin exploded");
    }

    [Fact]
    public async Task APluginWithoutATranslateFunctionFailsClearly()
    {
        using var fixture = PluginFixture.Create("no-entry-point");

        var act = async () => await fixture.Service().TranslateAsync(PluginFixture.Request());

        (await act.Should().ThrowAsync<TranslationException>()).Which.Message.Should().Contain("translate");
    }

    [Fact]
    public async Task ATimerCallbackCanCompleteTheQuery()
    {
        using var fixture = PluginFixture.Create("timer");

        var result = await fixture.Service().TranslateAsync(PluginFixture.Request("later"));

        result.TranslatedText.Should().Be("delayed: later");
    }

    [Fact]
    public async Task RequireLoadsASiblingModule()
    {
        using var fixture = PluginFixture.Create("require-module");

        var result = await fixture.Service().TranslateAsync(PluginFixture.Request("shout"));

        result.TranslatedText.Should().Be("SHOUT!");
    }

    [Fact]
    public async Task TheBundledCryptoShimMatchesKnownVectors()
    {
        using var fixture = PluginFixture.Create("crypto");

        var result = await fixture.Service().TranslateAsync(PluginFixture.Request("anything"));

        var lines = result.TranslatedText.Split('\n');
        lines[0].Should().Be("900150983cd24fb0d6963f7d28e17f72", "MD5('abc')");
        lines[1].Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "SHA256('abc')");
        lines[2].Should().Be("6e9ef29b75fffc5b7abae527d58fdadb2fe42e7219011976917343065f58ed4a", "HmacSHA256('message','key')");
        lines[3].Should().Be("aGVsbG8=", "base64('hello')");
    }

    [Fact]
    public async Task OptionsAndPluginInfoReachThePlugin()
    {
        using var fixture = PluginFixture.Create(
            "options",
            options: new Dictionary<string, string> { ["apiKey"] = "sk-test", ["model"] = "accurate" },
            secureOptionIds: ["apiKey"]);

        var result = await fixture.Service().TranslateAsync(PluginFixture.Request());

        result.TranslatedText.Split('\n').Should().Equal(
            "apiKey=sk-test",
            "model=accurate",
            "identifier=test.options",
            "version=3.4.5",
            "platform=windows");
    }

    [Fact]
    public async Task FileAccessIsConfinedToTheSandbox()
    {
        using var fixture = PluginFixture.Create("file-sandbox");

        var result = await fixture.Service().TranslateAsync(PluginFixture.Request("this"));

        result.TranslatedText.Split('\n').Should().Equal(
            "wrote=true",
            "readBack=remembered: this",
            "packaged=packaged",
            "escaped=false");
        File.Exists(Path.Combine(fixture.SandboxDirectory, "note.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task SupportedLanguagesAreReadFromThePlugin()
    {
        using var fixture = PluginFixture.Create("languages");

        var codes = await fixture.Service().GetSupportedLanguageCodesAsync();

        codes.Should().Equal("auto", "en", "zh-Hans", "ja", "not-a-language");
    }

    [Fact]
    public async Task APluginWithoutSupportLanguagesReportsNone()
    {
        using var fixture = PluginFixture.Create("dict");

        (await fixture.Service().GetSupportedLanguageCodesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateReportsAHealthyPlugin()
    {
        using var fixture = PluginFixture.Create("validate-ok");

        var outcome = await fixture.Service().ValidateAsync();

        outcome.Status.Should().Be(BobValidationStatus.Valid);
    }

    [Fact]
    public async Task ValidateReportsWhatThePluginComplainedAbout()
    {
        using var fixture = PluginFixture.Create("validate-fail");

        var outcome = await fixture.Service().ValidateAsync();

        outcome.Status.Should().Be(BobValidationStatus.Invalid);
        outcome.Message.Should().Contain("The key was rejected.");
    }

    [Fact]
    public async Task ValidateSaysSoWhenThePluginHasNoSelfCheck()
    {
        using var fixture = PluginFixture.Create("echo-global");

        (await fixture.Service().ValidateAsync()).Status.Should().Be(BobValidationStatus.NotSupported);
    }

    [Fact]
    public async Task CancellingTheQueryStopsThePlugin()
    {
        using var fixture = PluginFixture.Create("cancel");
        var service = fixture.Service();
        using var cts = new CancellationTokenSource();

        var act = async () =>
        {
            var task = service.TranslateAsync(PluginFixture.Request(), cts.Token);
            await PluginFixture.WaitForLogAsync(service, "translate running");
            cts.Cancel();
            await task;
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        // The plugin's cancelSignal subscriber ran, which its self-check reports.
        var outcome = await service.ValidateAsync();
        outcome.Status.Should().Be(BobValidationStatus.Valid);
    }

    [Fact]
    public async Task APluginThatNeverCompletesTimesOut()
    {
        using var fixture = PluginFixture.Create("timeout-interval");

        var act = async () => await fixture.Service().TranslateAsync(PluginFixture.Request());

        // The fixture declares a one-second timeout, which wins over the request's.
        var exception = (await act.Should().ThrowAsync<TranslationException>()).Which;
        exception.ErrorCode.Should().Be(TranslationErrorCode.Timeout);
    }

    [Fact]
    public async Task ARunawayScriptIsCutOffByTheEngine()
    {
        using var fixture = PluginFixture.Create("hang", sliceTimeoutSeconds: 1);

        var act = async () => await fixture.Service().TranslateAsync(
            PluginFixture.Request(timeoutMs: 10_000));

        var exception = (await act.Should().ThrowAsync<TranslationException>()).Which;
        exception.ErrorCode.Should().Be(TranslationErrorCode.Timeout);
    }
}
