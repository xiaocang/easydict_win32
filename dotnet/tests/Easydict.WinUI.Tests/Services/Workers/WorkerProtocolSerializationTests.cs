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
            ProxyEnabled = true,
            ProxyUri = "http://localhost:7890",
            FoundryLocalEndpoint = "http://localhost:5000",
            LocalAIProvider = LocalAiProviderModes.Auto,
        };
        var configure = new ConfigureParams { Settings = snapshot };

        var json = JsonLineSerializer.Serialize(configure);
        var back = JsonLineSerializer.Deserialize<ConfigureParams>(json);

        back.Should().NotBeNull();
        back!.Settings.Should().NotBeNull();
        back.Settings.OpenAIApiKey.Should().Be("sk-test");
        back.Settings.OpenAITemperature.Should().Be(0.3f);
        back.Settings.ProxyEnabled.Should().BeTrue();
        back.Settings.LocalAIProvider.Should().Be(LocalAiProviderModes.Auto);

        // Sensitive values still appear as plaintext in the wire format — by design,
        // since this only crosses the anonymous stdin pipe between host and worker.
        json.Should().Contain("\"openAIApiKey\":\"sk-test\"");
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
    public void LocalModelStatusDto_RoundTrips()
    {
        var dto = new LocalModelStatusDto
        {
            State = "Ready",
            StatusKey = "Ready",
            Detail = null,
        };
        var json = JsonLineSerializer.Serialize(dto);
        var back = JsonLineSerializer.Deserialize<LocalModelStatusDto>(json);
        back.Should().NotBeNull();
        back!.State.Should().Be("Ready");
    }

    [Fact]
    public void OcrRecognizeParams_RoundTrips()
    {
        var p = new OcrRecognizeParams
        {
            PixelDataPath = @"C:\Temp\capture.bgra",
            PixelWidth = 320,
            PixelHeight = 200,
            PreferredLanguageTag = "en-US",
        };

        var json = JsonLineSerializer.Serialize(p);
        var back = JsonLineSerializer.Deserialize<OcrRecognizeParams>(json);

        back.Should().NotBeNull();
        back!.PixelDataPath.Should().Be(@"C:\Temp\capture.bgra");
        back.PixelWidth.Should().Be(320);
        back.PixelHeight.Should().Be(200);
        back.PreferredLanguageTag.Should().Be("en-US");
        json.Should().Contain("\"pixelDataPath\"");
    }

    [Fact]
    public void OcrResultDto_RoundTrips()
    {
        var result = new OcrResultDto
        {
            Text = "hello",
            TextAngle = 1.5,
            DetectedLanguage = new OcrLanguageDto { Tag = "en-US", DisplayName = "English" },
            Lines =
            [
                new OcrLineDto
                {
                    Text = "hello",
                    Words = ["hel", "lo"],
                    BoundingRect = new OcrRectDto(1, 2, 3, 4),
                },
            ],
        };

        var json = JsonLineSerializer.Serialize(result);
        var back = JsonLineSerializer.Deserialize<OcrResultDto>(json);

        back.Should().NotBeNull();
        back!.Text.Should().Be("hello");
        back.TextAngle.Should().Be(1.5);
        back.DetectedLanguage!.Tag.Should().Be("en-US");
        back.Lines.Should().ContainSingle();
        back.Lines[0].BoundingRect.Width.Should().Be(3);
        back.Lines[0].Words.Should().Equal("hel", "lo");
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
