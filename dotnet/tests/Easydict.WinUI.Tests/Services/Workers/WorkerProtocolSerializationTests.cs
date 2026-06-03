using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services.Workers;

/// <summary>
/// Round-trips every protocol POCO through JsonLineSerializer to catch wire-format
/// regressions (renamed properties, missing [JsonPropertyName], etc.). The worker
/// processes deserialize via the same options so this test exercises both ends.
/// </summary>
[Trait("Category", "WinUI")]
public sealed class WorkerProtocolSerializationTests
{
    [Fact]
    public void ReadyEventData_RoundTrips()
    {
        var ready = new ReadyEventData
        {
            WorkerKind = WorkerKinds.LongDoc,
            WorkerVersion = "1.0.0",
            ProtocolVersion = WorkerProtocolVersion.Current,
            Capabilities = new[] { "configure", "translate_document" },
        };
        var json = JsonLineSerializer.Serialize(ready);
        var back = JsonLineSerializer.Deserialize<ReadyEventData>(json);

        back.Should().NotBeNull();
        back!.WorkerKind.Should().Be(WorkerKinds.LongDoc);
        back.WorkerVersion.Should().Be("1.0.0");
        back.ProtocolVersion.Should().Be(WorkerProtocolVersion.Current);
        back.Capabilities.Should().BeEquivalentTo("configure", "translate_document");

        // Wire format must be camelCase keys.
        json.Should().Contain("\"workerKind\"");
        json.Should().Contain("\"protocolVersion\"");
    }

    [Fact]
    public void ConfigureParams_RoundTrips_WithSettingsSnapshot()
    {
        var snapshot = new SettingsSnapshot
        {
            OpenAIApiKey = "sk-test",
            OpenAIModel = "gpt-4o-mini",
            OpenAITemperature = 0.3f,
            CaiyunToken = "caiyun-token",
            NiuTransApiKey = "niu-key",
            YoudaoAppKey = "youdao-key",
            YoudaoAppSecret = "youdao-secret",
            YoudaoUseOfficialApi = true,
            ProxyEnabled = true,
            ProxyUri = "http://localhost:7890",
            FoundryLocalEndpoint = "http://localhost:5000",
            LocalAIProvider = LocalAiProviderModes.Auto,
            OcrEngine = "CustomApi",
            OcrApiKey = "ocr-key",
            OcrEndpoint = "https://ocr.example.test/v1/responses",
            OcrModel = "gpt-vision",
            OcrSystemPrompt = "Extract text.",
            OcrLanguage = "ja-JP",
            ImportedMdxDictionaries =
            [
                new ImportedMdxDictionarySnapshot
                {
                    ServiceId = "mdx::demo",
                    DisplayName = "Demo Dictionary",
                    FilePath = @"C:\Dicts\demo.mdx",
                    MddFilePaths = [@"C:\Dicts\demo.mdd"],
                },
            ],
        };
        var configure = new ConfigureParams { Settings = snapshot };

        var json = JsonLineSerializer.Serialize(configure);
        var back = JsonLineSerializer.Deserialize<ConfigureParams>(json);

        back.Should().NotBeNull();
        back!.Settings.Should().NotBeNull();
        back.Settings.OpenAIApiKey.Should().Be("sk-test");
        back.Settings.OpenAITemperature.Should().Be(0.3f);
        back.Settings.CaiyunToken.Should().Be("caiyun-token");
        back.Settings.NiuTransApiKey.Should().Be("niu-key");
        back.Settings.YoudaoAppKey.Should().Be("youdao-key");
        back.Settings.YoudaoAppSecret.Should().Be("youdao-secret");
        back.Settings.YoudaoUseOfficialApi.Should().BeTrue();
        back.Settings.ProxyEnabled.Should().BeTrue();
        back.Settings.LocalAIProvider.Should().Be(LocalAiProviderModes.Auto);
        back.Settings.OcrEngine.Should().Be("CustomApi");
        back.Settings.OcrApiKey.Should().Be("ocr-key");
        back.Settings.OcrEndpoint.Should().Be("https://ocr.example.test/v1/responses");
        back.Settings.OcrModel.Should().Be("gpt-vision");
        back.Settings.OcrSystemPrompt.Should().Be("Extract text.");
        back.Settings.OcrLanguage.Should().Be("ja-JP");
        back.Settings.ImportedMdxDictionaries.Should().ContainSingle();
        back.Settings.ImportedMdxDictionaries![0].MddFilePaths.Should().Equal(@"C:\Dicts\demo.mdd");

        // Sensitive values still appear as plaintext in the wire format — by design,
        // since this only crosses the anonymous stdin pipe between host and worker.
        json.Should().Contain("\"openAIApiKey\":\"sk-test\"");
        json.Should().Contain("\"caiyunToken\":\"caiyun-token\"");
        json.Should().Contain("\"niuTransApiKey\":\"niu-key\"");
        json.Should().Contain("\"youdaoAppKey\":\"youdao-key\"");
        json.Should().Contain("\"youdaoAppSecret\":\"youdao-secret\"");
        json.Should().Contain("\"youdaoUseOfficialApi\":true");
        json.Should().Contain("\"ocrEngine\":\"CustomApi\"");
        json.Should().Contain("\"ocrApiKey\":\"ocr-key\"");
        json.Should().Contain("\"ocrEndpoint\":\"https://ocr.example.test/v1/responses\"");
        json.Should().Contain("\"ocrModel\":\"gpt-vision\"");
        json.Should().Contain("\"ocrSystemPrompt\":\"Extract text.\"");
        json.Should().Contain("\"ocrLanguage\":\"ja-JP\"");
        json.Should().Contain("\"importedMdxDictionaries\"");
    }

    [Fact]
    public void TranslateDocumentParams_RoundTrips()
    {
        var p = new TranslateDocumentParams
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
        };
        var json = JsonLineSerializer.Serialize(p);
        var back = JsonLineSerializer.Deserialize<TranslateDocumentParams>(json);

        back.Should().NotBeNull();
        back!.InputPath.Should().Be(@"C:\docs\paper.pdf");
        back.PageRange.Should().Be("1-10");
        back.ResultJsonPath.Should().Be(@"C:\Temp\easydict-result.json");
        json.Should().Contain("\"resultJsonPath\"");
    }

    [Fact]
    public async Task TranslateDocumentResult_WritesAndReadsThroughResultJsonPath()
    {
        var path = LongDocResultFileStore.CreateTempPath();
        try
        {
            var result = new TranslateDocumentResult
            {
                State = "Completed",
                OutputPath = @"C:\docs\paper_zh.pdf",
                BilingualOutputPath = @"C:\docs\paper_bilingual.pdf",
                TotalChunks = 3,
                SucceededChunks = 2,
                FailedChunkIndexes = [1],
                QualityReport = "{\"totalBlocks\":3}",
            };

            await LongDocResultFileStore.WriteAsync(path, result);
            var back = await LongDocResultFileStore.ReadAsync(path);

            back.State.Should().Be("Completed");
            back.OutputPath.Should().Be(@"C:\docs\paper_zh.pdf");
            back.BilingualOutputPath.Should().Be(@"C:\docs\paper_bilingual.pdf");
            back.TotalChunks.Should().Be(3);
            back.SucceededChunks.Should().Be(2);
            back.FailedChunkIndexes.Should().Equal(1);
            back.QualityReport.Should().Be("{\"totalBlocks\":3}");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ChunkEventData_RoundTrips()
    {
        var chunk = new ChunkEventData { Text = "Hello, " };
        var json = JsonLineSerializer.Serialize(chunk);
        var back = JsonLineSerializer.Deserialize<ChunkEventData>(json);

        back.Should().NotBeNull();
        back!.Text.Should().Be("Hello, ");
    }

    [Fact]
    public void CancelRequestParams_RoundTrips()
    {
        var p = new CancelRequestParams { TargetRequestId = "req-42" };
        var json = JsonLineSerializer.Serialize(p);
        var back = JsonLineSerializer.Deserialize<CancelRequestParams>(json);
        back.Should().NotBeNull();
        back!.TargetRequestId.Should().Be("req-42");
    }
}
