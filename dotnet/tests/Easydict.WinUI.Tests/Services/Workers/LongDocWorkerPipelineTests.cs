extern alias LongDocWorker;

using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using WorkerHandlerException = LongDocWorker::Easydict.Workers.LongDoc.Infrastructure.WorkerHandlerException;
using WorkerLongDocumentPipeline = LongDocWorker::Easydict.Workers.LongDoc.Infrastructure.WorkerLongDocumentPipeline;
using WorkerLongDocumentSourceDocumentBuilder = LongDocWorker::Easydict.Workers.LongDoc.Infrastructure.WorkerLongDocumentSourceDocumentBuilder;
using WinUiLongDocumentTranslationService = Easydict.WinUI.Services.LongDocumentTranslationService;
using Xunit;

namespace Easydict.WinUI.Tests.Services.Workers;

[Trait("Category", "WinUI")]
public sealed class LongDocWorkerPipelineTests
{
    [Theory]
    [InlineData("windows-local-ai")]
    [InlineData("foundry-local")]
    public async Task TranslateAsync_NativeLocalAiService_FailsBeforeWorkerNestedLocalAiRoute(
        string serviceId)
    {
        var pipeline = new WorkerLongDocumentPipeline(FakeTranslateAsync);

        var act = async () => await pipeline.TranslateAsync(
            new TranslateDocumentParams
            {
                InputPath = "missing-input.txt",
                OutputPath = "unused.txt",
                InputMode = "PlainText",
                From = "English",
                To = "SimplifiedChinese",
                ServiceId = serviceId,
                OutputMode = "Monolingual",
            },
            new SettingsSnapshot(),
            progress: null,
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<WorkerHandlerException>();
        error.Which.Code.Should().Be(WorkerErrorCodes.InvalidParams);
        error.Which.Message.Should().Contain("requires a Rust-native route");
        error.Which.Message.Should().Contain("LocalAI worker");
    }

    [Fact]
    public async Task TranslateAsync_PlainText_WritesFlatResultFile()
    {
        var tempDir = Directory.CreateTempSubdirectory("easydict-longdoc-worker-test-");
        var inputPath = Path.Combine(tempDir.FullName, "source.txt");
        var outputPath = Path.Combine(tempDir.FullName, "translated.txt");
        var resultPath = Path.Combine(tempDir.FullName, "result.json");

        try
        {
            await File.WriteAllTextAsync(inputPath, "Hello world.\n\nSecond block.");

            var pipeline = new WorkerLongDocumentPipeline(FakeTranslateAsync);
            var result = await pipeline.TranslateAsync(
                new TranslateDocumentParams
                {
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    InputMode = "PlainText",
                    From = "English",
                    To = "SimplifiedChinese",
                    ServiceId = "fake",
                    OutputMode = "Monolingual",
                    ResultJsonPath = resultPath,
                },
                new SettingsSnapshot
                {
                    LongDocMaxConcurrency = 1,
                    LongDocEnableDocumentContextPass = false,
                },
                progress: null,
                CancellationToken.None);

            result.ResultJsonPath.Should().Be(resultPath);

            var hydrated = await LongDocResultFileStore.ReadAsync(resultPath);
            hydrated.State.Should().Be("Completed");
            hydrated.OutputPath.Should().Be(outputPath);
            hydrated.TotalChunks.Should().Be(2);
            hydrated.SucceededChunks.Should().Be(2);
            hydrated.FailedChunkIndexes.Should().BeEmpty();

            var output = await File.ReadAllTextAsync(outputPath);
            output.Should().Contain("TR:Hello world.");
            output.Should().Contain("TR:Second block.");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TranslateAsync_Markdown_Bilingual_WritesBilingualOutput()
    {
        var tempDir = Directory.CreateTempSubdirectory("easydict-longdoc-worker-test-");
        var inputPath = Path.Combine(tempDir.FullName, "source.md");
        var outputPath = Path.Combine(tempDir.FullName, "translated.md");

        try
        {
            await File.WriteAllTextAsync(inputPath, "# Title\n\nBody text.");

            var pipeline = new WorkerLongDocumentPipeline(FakeTranslateAsync);
            var result = await pipeline.TranslateAsync(
                new TranslateDocumentParams
                {
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    InputMode = "Markdown",
                    From = "English",
                    To = "SimplifiedChinese",
                    ServiceId = "fake",
                    OutputMode = "Bilingual",
                },
                new SettingsSnapshot
                {
                    LongDocMaxConcurrency = 1,
                    LongDocEnableDocumentContextPass = false,
                },
                progress: null,
                CancellationToken.None);

            result.OutputPath.Should().EndWith("-bilingual.md");
            File.Exists(outputPath).Should().BeFalse();
            File.Exists(result.OutputPath).Should().BeTrue();

            var output = await File.ReadAllTextAsync(result.OutputPath!);
            output.Should().Contain("> # Title");
            output.Should().Contain("TR:# Title");
            output.Should().Contain("> Body text.");
            output.Should().Contain("TR:Body text.");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TranslateAsync_Pdf_WritesValidPdfOutput()
    {
        var tempDir = Directory.CreateTempSubdirectory("easydict-longdoc-worker-test-");
        var inputPath = Path.Combine(tempDir.FullName, "source.pdf");
        var outputPath = Path.Combine(tempDir.FullName, "translated.pdf");

        try
        {
            CreatePdf(inputPath, ["Hello PDF world."]);

            var events = new List<BlockTranslatedEventData>();
            var pipeline = new WorkerLongDocumentPipeline(FakeTranslateAsync);
            var result = await pipeline.TranslateAsync(
                new TranslateDocumentParams
                {
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    InputMode = "Pdf",
                    From = "English",
                    To = "SimplifiedChinese",
                    ServiceId = "fake",
                    OutputMode = "Monolingual",
                },
                new SettingsSnapshot
                {
                    LongDocMaxConcurrency = 1,
                    LongDocEnableDocumentContextPass = false,
                },
                progress: null,
                CancellationToken.None,
                (block, _) =>
                {
                    events.Add(block);
                    return Task.CompletedTask;
                });

            result.State.Should().Be("Completed");
            result.OutputPath.Should().Be(outputPath);
            File.Exists(outputPath).Should().BeTrue();
            events.Should().ContainSingle();
            events[0].TranslatedText.Should().Contain("TR:");

            using var outputDoc = PdfPigDocument.Open(outputPath);
            outputDoc.NumberOfPages.Should().BeGreaterThan(0);
            outputDoc.GetPages()
                .Select(page => page.Text)
                .Should()
                .Contain(text => text.Contains("TR:", StringComparison.Ordinal));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TranslateAsync_Pdf_RespectsPageRange()
    {
        var tempDir = Directory.CreateTempSubdirectory("easydict-longdoc-worker-test-");
        var inputPath = Path.Combine(tempDir.FullName, "source.pdf");
        var outputPath = Path.Combine(tempDir.FullName, "translated.pdf");

        try
        {
            CreatePdf(inputPath, ["First page only.", "Second page only."]);

            var pipeline = new WorkerLongDocumentPipeline(FakeTranslateAsync);
            var result = await pipeline.TranslateAsync(
                new TranslateDocumentParams
                {
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    InputMode = "Pdf",
                    From = "English",
                    To = "SimplifiedChinese",
                    ServiceId = "fake",
                    OutputMode = "Monolingual",
                    PageRange = "2",
                },
                new SettingsSnapshot
                {
                    LongDocMaxConcurrency = 1,
                    LongDocEnableDocumentContextPass = false,
                },
                progress: null,
                CancellationToken.None);

            result.State.Should().Be("Completed");
            result.TotalChunks.Should().Be(1);

            using var outputDoc = PdfPigDocument.Open(outputPath);
            var text = string.Join("\n", outputDoc.GetPages().Select(page => page.Text));
            var compactText = text.Replace(" ", string.Empty, StringComparison.Ordinal);
            compactText.Should().Contain("Secondpageonly");
            compactText.Should().NotContain("Firstpageonly");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WorkerSourceBuilder_PdfHeuristic_MatchesInProcBuilder()
    {
        var tempDir = Directory.CreateTempSubdirectory("easydict-longdoc-worker-test-");
        var inputPath = Path.Combine(tempDir.FullName, "source.pdf");

        try
        {
            CreatePdf(inputPath, ["First page only.", "Second page only."]);

            var expected = await BuildInProcBasicSourceDocumentAsync(
                LongDocumentInputMode.Pdf,
                inputPath,
                pageRange: "2");

            using var builder = new WorkerLongDocumentSourceDocumentBuilder();
            var actual = await builder.BuildAsync(
                LongDocumentInputMode.Pdf,
                inputPath,
                LayoutDetectionMode.Heuristic,
                visionEndpoint: null,
                visionApiKey: null,
                visionModel: null,
                pageRange: "2",
                enableTatrTableStructure: true,
                proxyEnabled: false,
                proxyUri: null,
                proxyBypassLocal: false,
                onProgress: null,
                CancellationToken.None);

            Snapshot(actual).Should().BeEquivalentTo(Snapshot(expected), options => options.WithStrictOrdering());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static Task<TranslationResult> FakeTranslateAsync(
        TranslationRequest request,
        string serviceId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new TranslationResult
        {
            OriginalText = request.Text,
            TranslatedText = $"TR:{request.Text}",
            ServiceName = serviceId,
            DetectedLanguage = request.FromLanguage,
            TargetLanguage = request.ToLanguage,
        });
    }

    private static void CreatePdf(string path, IReadOnlyList<string> pages)
    {
        CjkFontResolver.EnsureInitialized();
        using var doc = new PdfDocument();
        foreach (var text in pages)
        {
            var page = doc.AddPage();
            page.Width = 595;
            page.Height = 842;
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString(text, new XFont("Arial", 12), XBrushes.Black, new XRect(72, 72, 450, 24), XStringFormats.TopLeft);
        }

        doc.Save(path);
    }

    private static async Task<SourceDocument> BuildInProcBasicSourceDocumentAsync(
        LongDocumentInputMode mode,
        string input,
        string? pageRange)
    {
        var method = typeof(WinUiLongDocumentTranslationService).GetMethod(
            "BuildSourceDocumentBasicAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        method.Should().NotBeNull();
        var task = (Task<SourceDocument>)method!.Invoke(null, [mode, input, pageRange])!;
        return await task.ConfigureAwait(false);
    }

    private static object Snapshot(SourceDocument document)
    {
        return new
        {
            document.DocumentId,
            Pages = document.Pages.Select(page => new
            {
                page.PageNumber,
                page.IsScanned,
                Blocks = page.Blocks.Select(block => new
                {
                    block.BlockId,
                    block.BlockType,
                    block.Text,
                    block.ParentBlockId,
                    block.IsFormulaLike,
                    block.BoundingBox,
                    block.FallbackText,
                    block.DetectedFontNames,
                    block.TextStyle,
                    block.FormulaCharacters,
                    block.CharacterLevelProtectedText,
                }).ToList(),
            }).ToList(),
        };
    }
}
