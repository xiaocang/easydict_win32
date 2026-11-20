using System.Runtime.InteropServices.WindowsRuntime;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfPageRenderOptions = Windows.Data.Pdf.PdfPageRenderOptions;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Regressions for the two review findings on the Overlay export path:
/// protected inline script lines must survive source-text removal, and the drawing clip must reach
/// the extra lines the CJK readability floor produces.
/// </summary>
[Trait("Category", "WinUI")]
public class PdfExportServiceReviewFindingTests
{
    private const double PageWidth = 612;
    private const double PageHeight = 792;

    [Fact]
    public void TryRemoveSourceLinesInStream_KeepsProtectedCitationLineAndRemovesTheBodyLine()
    {
        var sourcePath = NewTempPath("protected-source");
        var outputPath = NewTempPath("protected-output");
        try
        {
            // Two separate text-showing operators: one body line, one citation line.
            WriteTwoLineSourcePdf(sourcePath);

            var groups = PdfExportService.ReadSourceLetterGroups(sourcePath, 1);
            groups.Should().NotBeEmpty();

            var (bodyRect, citationRect) = BuildLineRectsFromGroups(groups);
            bodyRect.Width.Should().BeGreaterThan(citationRect.Width);

            using (var doc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify))
            {
                var page = doc.Pages[0];
                var result = PdfExportService.TryRemoveSourceLinesInStream(
                    page,
                    [bodyRect, citationRect],
                    groups,
                    [citationRect]);

                // The body line goes; the protected citation line stays, so the citation ink the
                // overlay relies on cannot be deleted from the content stream.
                result.RemovedLines.Should().Be(1);
                result.RetainedLineIndexes.Should().Equal(1);
                doc.Save(outputPath);
            }

            var text = ReadPageText(outputPath);
            text.Should().NotContain("Retrieval augmented");
            text.Should().Contain("13");
        }
        finally
        {
            Delete(sourcePath, outputPath);
        }
    }

    [Fact]
    public void TryRemoveSourceLinesInStream_WithoutProtection_RemovesBothLines()
    {
        // Counterpart of the regression above: the same document is fully removable when nothing is
        // protected, which is what the old call site did for every block.
        var sourcePath = NewTempPath("unprotected-source");
        var outputPath = NewTempPath("unprotected-output");
        try
        {
            WriteTwoLineSourcePdf(sourcePath);
            var groups = PdfExportService.ReadSourceLetterGroups(sourcePath, 1);
            var (bodyRect, citationRect) = BuildLineRectsFromGroups(groups);

            using (var doc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify))
            {
                var result = PdfExportService.TryRemoveSourceLinesInStream(
                    doc.Pages[0],
                    [bodyRect, citationRect],
                    groups);
                result.RemovedLines.Should().Be(2);
                result.RetainedLineIndexes.Should().BeEmpty();
                doc.Save(outputPath);
            }

            ReadPageText(outputPath).Should().NotContain("Retrieval augmented");
            ReadPageText(outputPath).Should().NotContain("13");
        }
        finally
        {
            Delete(sourcePath, outputPath);
        }
    }

    [Fact]
    public void TryHideSourceTextWithProtectedLines_RefusesToHideAProtectedBlock()
    {
        // "3 Tr" hides every glyph run of the text object, so the fallback used when no line could be
        // removed must stay away from blocks that keep a citation or subscript verbatim.
        var sourcePath = NewTempPath("hide-source");
        var outputPath = NewTempPath("hide-output");
        try
        {
            WriteTwoLineSourcePdf(sourcePath);
            var groups = PdfExportService.ReadSourceLetterGroups(sourcePath, 1);
            var (_, citationRect) = BuildLineRectsFromGroups(groups);

            PdfExportService.ShouldHideSourceText([citationRect]).Should().BeFalse();
            PdfExportService.ShouldHideSourceText([]).Should().BeTrue();
            PdfExportService.ShouldHideSourceText(null).Should().BeTrue();

            using (var doc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify))
            {
                var hidden = PdfExportService.TryHideSourceTextWithProtectedLines(
                    doc.Pages[0],
                    "Retrieval augmented models are strong baselines",
                    [citationRect]);
                hidden.Should().BeFalse();
                doc.Save(outputPath);
            }

            // Nothing was hidden, so the citation the overlay keeps is still there to read.
            ReadPageText(outputPath).Should().Contain("13");
        }
        finally
        {
            Delete(sourcePath, outputPath);
        }
    }

    [SkippableFact]
    public async Task Export_ReadabilityFloorOverflow_IsVisibleBelowTheOriginalBlock()
    {
        // The readability floor only applies to CJK targets, and the Chinese text must be extractable
        // afterwards, so this case needs a real CJK font. CI installs the pinned Noto Sans SC.
        Skip.IfNot(HasCjkFont(), "No CJK font installed; the pinned Noto Sans SC is required");

        var sourcePath = NewTempPath("floor-source");
        var outputPath = NewTempPath("floor-output");
        try
        {
            // A short block: at the 7.5 pt readability floor the translation needs more lines than
            // the box holds, which is exactly the case the clip rect used to cut off.
            WriteShortBlockSourcePdf(sourcePath);
            var groups = PdfExportService.ReadSourceLetterGroups(sourcePath, 1);
            groups.Should().NotBeEmpty();

            var checkpoint = CreateShortBlockCheckpoint(sourcePath);
            // Go through the public entry point: it registers the CJK font, so this also covers the
            // production export path rather than only the backfill helper.
            var issues = ExportAndReadIssues(checkpoint, sourcePath, outputPath);
            issues.Select(issue => issue.Kind).Should().Contain("readability-floor");
            issues.Select(issue => issue.Kind).Should().Contain("clip-extended");
            issues.Select(issue => issue.Kind).Should().NotContain("truncated");

            var (width, height, bgra) = await RenderPageAsync(outputPath, 0);
            width.Should().BeGreaterThan(0);
            height.Should().BeGreaterThan(0);

            // Reproduce the clip the exporter used before this fix: the block box plus the default
            // expansion of ExpandOverlayClipRect for this padding. Anything drawn past it was cut
            // away, so the last line has to be visible below that line.
            var blockBottomPoints = PageHeight - checkpoint.ChunkMetadata[0].BoundingBox!.Value.Y;
            var padding = Math.Clamp(12 * 0.30, 3, 12);
            var expandY = Math.Min(8.0, Math.Max(2.0, padding * 0.9));
            var oldClipBottom = blockBottomPoints + expandY;

            var lowestInkPoints = FindLowestInkPointsY(bgra, width, height, 55, 250);
            lowestInkPoints.Should().BeGreaterThan(
                oldClipBottom,
                "the readability floor produced lines below the old clip and they must be drawn, not cut away");

            // The whole translation made it onto the page, not just the lines above the old clip.
            ReadPageText(outputPath).Should().Contain("推理开销");
        }
        finally
        {
            Delete(sourcePath, outputPath);
        }
    }

    [SkippableFact]
    public async Task Export_ReadabilityFloorOverflow_StopsAbovePreservedSourceInk()
    {
        Skip.IfNot(HasCjkFont(), "No CJK font installed; the pinned Noto Sans SC is required");

        var sourcePath = NewTempPath("floor-preserved-source");
        var outputPath = NewTempPath("floor-preserved-output");
        try
        {
            // The block's own text plus a source line underneath that no block translates (a caption
            // or a footer). The overflow has to fit inside the default 8 pt clip expansion, which is
            // exactly the case where the boundary used to go unchecked.
            WriteShortBlockWithPreservedLineSourcePdf(sourcePath);
            var groups = PdfExportService.ReadSourceLetterGroups(sourcePath, 1);
            groups.Should().NotBeEmpty();

            var preservedTop = PreservedLineTop(groups);
            var checkpoint = CreateShortBlockCheckpoint(sourcePath, fontSize: 9);
            var issues = ExportAndReadIssues(checkpoint, sourcePath, outputPath);

            var kinds = issues.Select(issue => issue.Kind).ToList();
            kinds.Should().Contain("readability-floor");
            kinds.Should().Contain("clip-limited");

            var (width, height, bgra) = await RenderPageAsync(outputPath, 0);

            // The caption keeps its ink: the block above it neither draws over it nor erases it.
            HasInkInBand(bgra, width, height, 55, 250, preservedTop, preservedTop + 5)
                .Should().BeTrue("the preserved source line must still be drawn, not erased or covered");
        }
        finally
        {
            Delete(sourcePath, outputPath);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string NewTempPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.pdf");

    /// <summary>
    /// Runs the export through <see cref="PdfExportService.Export"/>, which is the entry point that
    /// sets up the CJK font resolver, and returns the per-block issues of the monolingual PDF.
    /// </summary>
    private static IReadOnlyList<BackfillBlockIssue> ExportAndReadIssues(
        LongDocumentTranslationCheckpoint checkpoint,
        string sourcePath,
        string outputPath)
    {
        var result = new PdfExportService().Export(checkpoint, sourcePath, outputPath);
        result.OutputPath.Should().Be(outputPath);
        var metrics = result.BackfillMetrics;
        metrics.Should().NotBeNull();
        return metrics!.BlockIssues ?? [];
    }

    private static bool HasCjkFont()
    {
        var localFonts = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict",
            "Fonts");
        if (File.Exists(Path.Combine(localFonts, "NotoSansSC-Regular.ttf")))
            return true;

        var systemFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return File.Exists(Path.Combine(systemFonts, "Deng.ttf")) ||
               File.Exists(Path.Combine(systemFonts, "simhei.ttf"));
    }

    private static void Delete(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // A locked temp file is harmless for a test run.
            }
        }
    }

    private static void WriteTwoLineSourcePdf(string path)
    {
        using var doc = new PdfSharpCore.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = PageWidth;
        page.Height = PageHeight;
        using var gfx = XGraphics.FromPdfPage(page);
        var bodyFont = new XFont("Arial", 9);
        var scriptFont = new XFont("Arial", 5);
        gfx.DrawString("Retrieval augmented models are strong baselines", bodyFont, XBrushes.Black, 60, 120);
        gfx.DrawString("[13]", scriptFont, XBrushes.Black, 300, 134);
        doc.Save(path);
    }

    private static void WriteShortBlockWithPreservedLineSourcePdf(string path)
    {
        using var doc = new PdfSharpCore.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = PageWidth;
        page.Height = PageHeight;
        using var gfx = XGraphics.FromPdfPage(page);
        var font = new XFont("Arial", 9);
        gfx.DrawString("Dense retrieval is strong", font, XBrushes.Black, 60, 100);
        // Placed just below the block bottom (its ink starts around 101 pt from the page top). The
        // default 8 pt clip expansion would reach 108 pt and cover it, but the boundary stops short.
        gfx.DrawString("Figure 1: untranslated caption", font, XBrushes.Black, 60, 110);
        doc.Save(path);
    }

    /// <summary>
    /// Top edge (measured from the page top) of the lower of the two source lines: the one no block
    /// claims, so the overlay has to leave it alone.
    /// </summary>
    private static double PreservedLineTop(IReadOnlyList<PdfExportService.SourceTextLetterGroup> groups)
    {
        var lowest = groups.OrderBy(group => group.Bottom).First();
        return PageHeight - lowest.Top;
    }

    private static void WriteShortBlockSourcePdf(string path)
    {
        using var doc = new PdfSharpCore.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = PageWidth;
        page.Height = PageHeight;
        using var gfx = XGraphics.FromPdfPage(page);
        var font = new XFont("Arial", 9);
        // BoundingBox for the block is (60, 692, 180, 10): 10 pt tall, so a 7.5 pt CJK translation
        // needs more lines than it holds.
        gfx.DrawString("Dense retrieval is strong", font, XBrushes.Black, 60, 100);
        doc.Save(path);
    }

    private static LongDocumentTranslationCheckpoint CreateShortBlockCheckpoint(string sourcePath, double fontSize = 12)
    {
        const double left = 60;
        const double bottom = 692;
        const double width = 180;
        const double height = 10;

        return new LongDocumentTranslationCheckpoint
        {
            InputMode = LongDocumentInputMode.Pdf,
            SourceFilePath = sourcePath,
            TargetLanguage = Language.SimplifiedChinese,
            SourceChunks = ["Dense retrieval is strong"],
            ChunkMetadata =
            [
                new LongDocumentChunkMetadata
                {
                    ChunkIndex = 0,
                    PageNumber = 1,
                    SourceBlockId = "p1-body-b1",
                    SourceBlockType = SourceBlockType.Paragraph,
                    OrderInPage = 0,
                    RegionType = LayoutRegionType.Body,
                    RegionConfidence = 0.9,
                    RegionSource = LayoutRegionSource.BlockIdFallback,
                    ReadingOrderScore = 1,
                    BoundingBox = new BlockRect(left, bottom, width, height),
                    TextStyle = new BlockTextStyle
                    {
                        FontSize = fontSize,
                        RotationAngle = 0,
                        Alignment = TextAlignment.Left,
                        LineSpacing = 14
                    },
                    DetectedFontNames = ["Arial"]
                }
            ],
            TranslatedChunks = new Dictionary<int, string>
            {
                [0] = "稠密检索在多个公开基准上表现很强，并且在开放域问答任务里同样稳定，而且几乎不需要额外的训练数据和推理开销。"
            },
            FailedChunkIndexes = []
        };
    }

    private static (XRect Body, XRect Citation) BuildLineRectsFromGroups(
        IReadOnlyList<PdfExportService.SourceTextLetterGroup> groups)
    {
        var bands = new List<List<PdfExportService.SourceTextLetterGroup>>();
        foreach (var group in groups.OrderByDescending(group => group.Bottom))
        {
            var band = bands.LastOrDefault();
            if (band is not null && Math.Abs(band.Average(g => g.Bottom) - group.Bottom) <= 3)
            {
                band.Add(group);
                continue;
            }

            bands.Add([group]);
        }

        bands.Should().HaveCountGreaterThanOrEqualTo(2);
        var rects = bands.Select(ToPageRect).OrderByDescending(rect => rect.Width).ToList();
        // The body line spans the block; the citation is the short run kept next to it.
        return (rects[0], rects[^1]);
    }

    private static XRect ToPageRect(List<PdfExportService.SourceTextLetterGroup> band)
    {
        var left = band.Min(group => group.Left);
        var right = band.Max(group => group.Right);
        var bottom = band.Min(group => group.Bottom);
        var top = band.Max(group => group.Top);
        return new XRect(left, PageHeight - top, Math.Max(1, right - left), Math.Max(1, top - bottom));
    }

    private static string ReadPageText(string path)
    {
        using var document = PdfPigDocument.Open(path);
        return document.GetPage(1).Text;
    }

    private static async Task<(int Width, int Height, byte[] Bgra)> RenderPageAsync(string path, int pageIndex)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        using var page = document.GetPage((uint)pageIndex);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Round(PageWidth * 200 / 72),
            DestinationHeight = (uint)Math.Round(PageHeight * 200 / 72),
            BitmapEncoderId = BitmapEncoder.PngEncoderId
        });

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return ((int)decoder.PixelWidth, (int)decoder.PixelHeight, pixels.DetachPixelData());
    }

    private static int FindLowestInkRow(byte[] bgra, int width, int height, double xFromPoints, double xToPoints)
    {
        var xFrom = Math.Max(0, (int)Math.Round(xFromPoints * width / PageWidth));
        var xTo = Math.Min(width - 1, (int)Math.Round(xToPoints * width / PageWidth));
        var lowest = 0;
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width * 4;
            for (var x = xFrom; x <= xTo; x++)
            {
                var offset = rowOffset + x * 4;
                // Any channel well below white counts as ink.
                if (bgra[offset] < 160 || bgra[offset + 1] < 160 || bgra[offset + 2] < 160)
                {
                    lowest = y;
                    break;
                }
            }
        }

        return lowest;
    }

    /// <summary>
    /// Lowest row carrying ink, expressed in PDF points. The scale comes from the bitmap the renderer
    /// actually returned, so it stays correct when the destination size is rounded.
    /// </summary>
    private static double FindLowestInkPointsY(byte[] bgra, int width, int height, double xFromPoints, double xToPoints) =>
        FindLowestInkRow(bgra, width, height, xFromPoints, xToPoints) * PageHeight / height;

    private static bool HasInkInBand(
        byte[] bgra,
        int width,
        int height,
        double xFromPoints,
        double xToPoints,
        double yFromPoints,
        double yToPoints)
    {
        var xFrom = Math.Max(0, (int)Math.Round(xFromPoints * width / PageWidth));
        var xTo = Math.Min(width - 1, (int)Math.Round(xToPoints * width / PageWidth));
        var yFrom = Math.Max(0, (int)Math.Floor(yFromPoints * height / PageHeight));
        var yTo = Math.Min(height - 1, (int)Math.Ceiling(yToPoints * height / PageHeight));
        for (var y = yFrom; y <= yTo; y++)
        {
            var rowOffset = y * width * 4;
            for (var x = xFrom; x <= xTo; x++)
            {
                var offset = rowOffset + x * 4;
                if (bgra[offset] < 160 || bgra[offset + 1] < 160 || bgra[offset + 2] < 160)
                    return true;
            }
        }

        return false;
    }
}
