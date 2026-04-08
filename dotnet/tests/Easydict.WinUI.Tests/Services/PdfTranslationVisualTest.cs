// Integration test: translates a PDF with DeepSeek and renders the output to PNG
// for visual verification of coordinate placement and text replacement quality.
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
using MuPDF.NET;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Integration")]
public class PdfTranslationVisualTest
{
    [SkippableFact]
    public async Task ExportTranslatedPdf_RendersTextAtCorrectPositions()
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
        using var service = new LongDocumentTranslationService();

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

        // ── Render pages 1-2 to PNG for visual inspection ─────────────────────
        var muDoc = new Document(outputPath);
        try
        {
            foreach (var pageNumber in new[] { 1, 2, 4 }.Where(pageNumber => pageNumber <= muDoc.PageCount))
            {
                var muPage = muDoc[pageNumber - 1];
                var mat = new Matrix(1.5f, 1.5f); // 108 DPI
                var pix = muPage.GetPixmap(mat);
                var pngPath = Path.Combine(outputDir, $"translated_p{pageNumber}.png");
                pix.Save(pngPath, "png");
                System.Diagnostics.Debug.WriteLine($"[VisualTest] Saved PNG: {pngPath}");
            }
        }
        finally
        {
            muDoc.Close();
        }
    }
}
