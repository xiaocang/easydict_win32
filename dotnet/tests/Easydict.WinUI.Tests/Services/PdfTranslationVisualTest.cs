// Integration test: translates a PDF with DeepSeek and verifies the exported PDF
// contains readable page text. Visual rendering is handled by PdfToImages.
//
// Prerequisites:
//   EASYDICT_TEST_PDF  — absolute path to the input PDF
//   DEEPSEEK_API_KEY   — DeepSeek API key
//
// Run explicitly via the helper script:
//   pwsh -File dotnet\scripts\run-visual-test.ps1
//
// Or manually:
//   $env:EASYDICT_TEST_PDF = "C:\path\to\paper.pdf"
//   $env:DEEPSEEK_API_KEY  = "sk-..."
//   dotnet test tests/Easydict.WinUI.Tests --filter "PdfTranslationVisualTest"

using Easydict.TranslationService.Models;
using LayoutDetectionMode = Easydict.TranslationService.LongDocument.LayoutDetectionMode;
using Easydict.TranslationService.Services;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using Xunit;
using WinUiLongDocumentTranslationService = Easydict.WinUI.Services.LongDocumentTranslationService;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Integration")]
public class PdfTranslationVisualTest
{
    [SkippableFact]
    public async Task ExportTranslatedPdf_ContainsReadableText()
    {
        // ── Read configuration from environment variables ──────────────────────
        var pdfPath = Environment.GetEnvironmentVariable("EASYDICT_TEST_PDF") ?? "";
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";

        Skip.If(string.IsNullOrEmpty(pdfPath), "Set EASYDICT_TEST_PDF env var to run this test");
        Skip.If(string.IsNullOrEmpty(apiKey), "Set DEEPSEEK_API_KEY env var to run this test");

        File.Exists(pdfPath).Should().BeTrue($"Test PDF not found: {pdfPath}");

        var outputDir = Path.GetDirectoryName(pdfPath)!;
        var outputPath = Path.Combine(outputDir, "output_translated.pdf");

        // ── Configure DeepSeek on TranslationManagerService ───────────────────
        using var handle = TranslationManagerService.Instance.AcquireHandle();
        handle.Manager.ConfigureService("deepseek", s =>
        {
            if (s is DeepSeekService d)
                d.Configure(apiKey);
        });

        // ── Invoke production pipeline (same as the actual app) ───────────────
        using var service = new WinUiLongDocumentTranslationService();

        // Mirror what EnsureOnnxReadyAsync() does in the UI:
        // use OnnxLocal if the model is already downloaded, else fall back to heuristic
        var downloadSvc = service.GetLayoutModelDownloadService();
        var layoutMode = downloadSvc.IsReady
            ? LayoutDetectionMode.OnnxLocal
            : LayoutDetectionMode.Heuristic;
        System.Diagnostics.Debug.WriteLine($"[VisualTest] Layout detection: {layoutMode}");

        var result = await service.TranslateToPdfAsync(
            mode: LongDocumentInputMode.Pdf,
            input: pdfPath,
            from: Language.English,
            to: Language.SimplifiedChinese,
            outputPath: outputPath,
            serviceId: "deepseek",
            onProgress: msg => System.Diagnostics.Debug.WriteLine($"[VisualTest] {msg}"),
            pdfExportMode: PdfExportMode.ContentStreamReplacement,
            layoutDetection: layoutMode);

        result.SucceededChunks.Should().BeGreaterThan(0, "at least some chunks should translate successfully");
        File.Exists(outputPath).Should().BeTrue();

        using var outputDoc = PdfPigDocument.Open(outputPath);
        outputDoc.NumberOfPages.Should().BeGreaterThan(0);
        outputDoc.GetPages().Select(page => page.Text)
            .Should().Contain(text => !string.IsNullOrWhiteSpace(text), "the exported document should retain searchable page text");
    }
}
