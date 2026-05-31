using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services.Workers;

/// <summary>
/// Locks the facade protocol that Rust uses to call the temporary .NET Compat Host.
/// Worker-specific DTOs are intentionally reused where the facade delegates to an
/// existing worker capability.
/// </summary>
[Trait("Category", "WinUI")]
public sealed class CompatHostProtocolSerializationTests
{
    [Fact]
    public void CompatHostMethods_MatchRustMigrationContract()
    {
        CompatHostMethods.Translate.Should().Be("translate");
        CompatHostMethods.TranslateStream.Should().Be("translate_stream");
        CompatHostMethods.GrammarCorrect.Should().Be("grammar_correct");
        CompatHostMethods.OcrRecognize.Should().Be("ocr_recognize");
        CompatHostMethods.LongDocTranslate.Should().Be("longdoc_translate");
        CompatHostMethods.LocalAiPrepare.Should().Be("local_ai_prepare");
        CompatHostMethods.LocalAiTranslate.Should().Be("local_ai_translate");
        CompatHostMethods.MdxLookup.Should().Be("mdx_lookup");
        CompatHostMethods.SettingsMigrate.Should().Be("settings_migrate");
    }

    [Fact]
    public void TranslateRequestAndResult_RoundTrip_WithFacadeMethod()
    {
        var request = new IpcRequest
        {
            Id = "req-translate",
            Method = CompatHostMethods.Translate,
            Params = new TranslateParams
            {
                Text = "Hello",
                From = "en",
                To = "zh-Hans",
                Services = ["google", "openai"],
            },
        };

        var json = JsonLineSerializer.Serialize(request);
        json.Should().Contain("\"method\":\"translate\"");
        json.Should().Contain("\"services\":[\"google\",\"openai\"]");
        json.Should().NotEndWith("\n");

        var line = JsonLineSerializer.SerializeLine(request);
        line.Should().EndWith("\n");

        var response = new IpcResponse
        {
            Id = "req-translate",
            Result = JsonLineSerializer.ToElement(new TranslationResultDto
            {
                TranslatedText = "你好",
                ServiceId = "google",
                ServiceName = "Google Translate",
                DetectedLanguage = "English",
                ResultKind = "Success",
                TimingMs = 42,
            }),
        };

        var responseJson = JsonLineSerializer.Serialize(response);
        responseJson.Should().NotContain("isSuccess");
        responseJson.Should().NotContain("isError");
        responseJson.Should().Contain("\"resultKind\":\"Success\"");
        responseJson.Should().Contain("\"timingMs\":42");

        var back = JsonLineSerializer.Deserialize<IpcResponse>(responseJson);
        back.Should().NotBeNull();
        back!.Result.Should().NotBeNull();

        var result = back.Result!.Value.Deserialize<TranslationResultDto>();
        result.Should().NotBeNull();
        result!.TranslatedText.Should().Be("你好");
        result.ServiceId.Should().Be("google");
        result.ResultKind.Should().Be("Success");
    }

    [Fact]
    public void TranslateStreamEvents_RoundTrip_WithChunkAndDonePayloads()
    {
        var chunk = new IpcEvent
        {
            Id = "req-stream",
            Event = IpcEventTypes.TranslateChunk,
            Data = JsonLineSerializer.ToElement(new TranslateChunkEventData
            {
                Text = "你",
            }),
        };

        var chunkJson = JsonLineSerializer.Serialize(chunk);
        chunkJson.Should().Contain("\"event\":\"translate_chunk\"");

        var chunkBack = JsonLineSerializer.Deserialize<IpcEvent>(chunkJson);
        chunkBack.Should().NotBeNull();
        chunkBack!.Data!.Value.Deserialize<TranslateChunkEventData>()!.Text.Should().Be("你");

        var done = new IpcEvent
        {
            Id = "req-stream",
            Event = IpcEventTypes.TranslateDone,
            Data = JsonLineSerializer.ToElement(new TranslationResultDto
            {
                TranslatedText = "你好",
                ServiceId = "openai",
                ServiceName = "OpenAI",
                ResultKind = "Success",
                TimingMs = 99,
            }),
        };

        var doneJson = JsonLineSerializer.Serialize(done);
        doneJson.Should().Contain("\"event\":\"translate_done\"");

        var doneBack = JsonLineSerializer.Deserialize<IpcEvent>(doneJson);
        doneBack.Should().NotBeNull();
        var doneResult = doneBack!.Data!.Value.Deserialize<TranslationResultDto>();
        doneResult.Should().NotBeNull();
        doneResult!.TranslatedText.Should().Be("你好");
        doneResult.ServiceId.Should().Be("openai");
        doneResult.ResultKind.Should().Be("Success");
    }

    [Fact]
    public void GrammarCorrectRequestAndEvents_RoundTrip_WithParsedResultPayload()
    {
        var request = new IpcRequest
        {
            Id = "req-grammar",
            Method = CompatHostMethods.GrammarCorrect,
            Params = new GrammarCorrectParams
            {
                Text = "I has a apple.",
                Language = "en",
                Services = ["openai"],
                IncludeExplanations = true,
            },
        };

        var requestJson = JsonLineSerializer.Serialize(request);
        requestJson.Should().Contain("\"method\":\"grammar_correct\"");
        requestJson.Should().Contain("\"language\":\"en\"");
        requestJson.Should().Contain("\"includeExplanations\":true");

        var chunk = new IpcEvent
        {
            Id = "req-grammar",
            Event = IpcEventTypes.GrammarChunk,
            Data = JsonLineSerializer.ToElement(new GrammarChunkEventData
            {
                Text = "[CORRECTED]I have an apple.",
            }),
        };

        var chunkJson = JsonLineSerializer.Serialize(chunk);
        chunkJson.Should().Contain("\"event\":\"grammar_chunk\"");
        JsonLineSerializer.Deserialize<IpcEvent>(chunkJson)!
            .Data!.Value.Deserialize<GrammarChunkEventData>()!.Text
            .Should()
            .Contain("I have");

        var done = new IpcEvent
        {
            Id = "req-grammar",
            Event = IpcEventTypes.GrammarDone,
            Data = JsonLineSerializer.ToElement(new GrammarCorrectResultDto
            {
                OriginalText = "I has a apple.",
                CorrectedText = "I have an apple.",
                Explanation = "Subject-verb agreement and article.",
                RawText = "[CORRECTED]I have an apple.[/CORRECTED]",
                ServiceId = "openai",
                ServiceName = "OpenAI",
                Language = "en",
                TimingMs = 42,
                HasCorrections = true,
            }),
        };

        var doneJson = JsonLineSerializer.Serialize(done);
        doneJson.Should().Contain("\"event\":\"grammar_done\"");
        doneJson.Should().Contain("\"correctedText\":\"I have an apple.\"");

        var result = JsonLineSerializer.Deserialize<IpcEvent>(doneJson)!
            .Data!.Value.Deserialize<GrammarCorrectResultDto>();
        result.Should().NotBeNull();
        result!.HasCorrections.Should().BeTrue();
        result.ServiceId.Should().Be("openai");
    }

    [Fact]
    public void FacadeMethods_CanReuseWorkerParams_ForHeavyDelegatedCapabilities()
    {
        var longDocRequest = new IpcRequest
        {
            Id = "req-longdoc",
            Method = CompatHostMethods.LongDocTranslate,
            Params = new TranslateDocumentParams
            {
                InputPath = @"C:\docs\paper.pdf",
                OutputPath = @"C:\docs\paper_zh.pdf",
                InputMode = "Pdf",
                From = "English",
                To = "ChineseSimplified",
                ServiceId = "openai",
                OutputMode = "Bilingual",
                PdfExportMode = "ContentStreamReplacement",
                LayoutDetection = "OnnxLocal",
                PageRange = "1-10",
                ResultJsonPath = @"C:\Temp\easydict-result.json",
            },
        };

        var longDocJson = JsonLineSerializer.Serialize(longDocRequest);
        longDocJson.Should().Contain("\"method\":\"longdoc_translate\"");
        longDocJson.Should().Contain("\"inputPath\"");
        longDocJson.Should().Contain("\"resultJsonPath\"");

        var ocrRequest = new IpcRequest
        {
            Id = "req-ocr",
            Method = CompatHostMethods.OcrRecognize,
            Params = new OcrRecognizeParams
            {
                PixelDataPath = @"C:\Temp\capture.bgra",
                PixelWidth = 320,
                PixelHeight = 200,
                PreferredLanguageTag = "en-US",
            },
        };

        var ocrJson = JsonLineSerializer.Serialize(ocrRequest);
        ocrJson.Should().Contain("\"method\":\"ocr_recognize\"");
        ocrJson.Should().Contain("\"pixelDataPath\"");
        ocrJson.Should().Contain("\"preferredLanguageTag\":\"en-US\"");

        var prepareRequest = new IpcRequest
        {
            Id = "req-local-ai-prepare",
            Method = CompatHostMethods.LocalAiPrepare,
            Params = new PrepareModelParams
            {
                Provider = LocalAiProviderModes.FoundryLocal,
                Endpoint = "http://127.0.0.1:5273",
                Model = "qwen2.5",
            },
        };

        var prepareJson = JsonLineSerializer.Serialize(prepareRequest);
        prepareJson.Should().Contain("\"method\":\"local_ai_prepare\"");
        prepareJson.Should().Contain("\"provider\":\"FoundryLocal\"");

        var localAiRequest = new IpcRequest
        {
            Id = "req-local-ai-translate",
            Method = CompatHostMethods.LocalAiTranslate,
            Params = new LocalAiTranslateParams
            {
                Text = "Hello",
                FromLanguage = "English",
                ToLanguage = "ChineseSimplified",
                ProviderMode = LocalAiProviderModes.Auto,
                CustomPrompt = null,
            },
        };

        var localAiJson = JsonLineSerializer.Serialize(localAiRequest);
        localAiJson.Should().Contain("\"method\":\"local_ai_translate\"");
        localAiJson.Should().Contain("\"providerMode\":\"Auto\"");
        localAiJson.Should().NotContain("customPrompt");
    }

    [Fact]
    public void MdxLookup_RoundTrips_WithDictionaryEntries()
    {
        var request = new IpcRequest
        {
            Id = "req-mdx",
            Method = CompatHostMethods.MdxLookup,
            Params = new MdxLookupParams
            {
                DictionaryId = "dict-1",
                Query = "apple",
                Fuzzy = false,
            },
        };

        var requestJson = JsonLineSerializer.Serialize(request);
        requestJson.Should().Contain("\"method\":\"mdx_lookup\"");
        requestJson.Should().Contain("\"dictionaryId\":\"dict-1\"");

        var result = new MdxLookupResult
        {
            Entries =
            [
                new MdxLookupEntry
                {
                    Key = "apple",
                    Html = "<p>fruit</p>",
                    DictionaryName = "Demo",
                },
            ],
        };

        var resultJson = JsonLineSerializer.Serialize(result);
        var back = JsonLineSerializer.Deserialize<MdxLookupResult>(resultJson);

        back.Should().NotBeNull();
        back!.Entries.Should().ContainSingle();
        back.Entries[0].DictionaryName.Should().Be("Demo");
        resultJson.Should().Contain("\"dictionaryName\":\"Demo\"");
    }

    [Fact]
    public void SettingsMigrate_RoundTrips_WithWarnings()
    {
        var request = new IpcRequest
        {
            Id = "req-settings",
            Method = CompatHostMethods.SettingsMigrate,
            Params = new SettingsMigrateParams
            {
                LegacySettingsPath = @"C:\old\settings.json",
                TargetSettingsPath = @"C:\new\settings.json",
            },
        };

        var requestJson = JsonLineSerializer.Serialize(request);
        requestJson.Should().Contain("\"method\":\"settings_migrate\"");
        requestJson.Should().Contain("\"legacySettingsPath\"");

        var result = new SettingsMigrateResult
        {
            Migrated = true,
            Warnings = ["missing optional provider"],
        };

        var resultJson = JsonLineSerializer.Serialize(result);
        var back = JsonLineSerializer.Deserialize<SettingsMigrateResult>(resultJson);

        back.Should().NotBeNull();
        back!.Migrated.Should().BeTrue();
        back.Warnings.Should().Equal("missing optional provider");
    }
}
