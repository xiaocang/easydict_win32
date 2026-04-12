using System.Collections.Generic;
using System.Linq;
using Easydict.TranslationService.LongDocument;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace Easydict.WinUI.Services.DocumentExport;

internal static class PdfPageSelectionHelper
{
    internal static IReadOnlyList<int>? ResolveSelectedPages(string? pageRange, int totalPages)
    {
        var selectedPages = PageRangeParser.Parse(pageRange, totalPages);
        return selectedPages is null
            ? null
            : selectedPages.OrderBy(pageNumber => pageNumber).ToArray();
    }

    internal static void FilterPdfInPlace(string pdfPath, string? pageRange)
    {
        using var sourceDoc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        var selectedPages = ResolveSelectedPages(pageRange, sourceDoc.PageCount);
        if (selectedPages is null || selectedPages.Count == sourceDoc.PageCount)
        {
            return;
        }

        var tempPath = BuildTempPath(pdfPath);
        try
        {
            WriteSelectedPages(sourceDoc, tempPath, selectedPages);
            File.Copy(tempPath, pdfPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void WriteSelectedPages(PdfDocument sourceDoc, string outputPath, IReadOnlyList<int> selectedPages)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var targetDoc = new PdfDocument();
        foreach (var pageNumber in selectedPages)
        {
            var pageIndex = pageNumber - 1;
            if (pageIndex < 0 || pageIndex >= sourceDoc.PageCount)
            {
                continue;
            }

            targetDoc.AddPage(sourceDoc.Pages[pageIndex]);
        }

        targetDoc.Save(outputPath);
    }

    private static string BuildTempPath(string pdfPath)
    {
        var directory = Path.GetDirectoryName(pdfPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileNameWithoutExtension(pdfPath);
        var extension = Path.GetExtension(pdfPath);
        return Path.Combine(directory, $"{fileName}.pagesel.{Guid.NewGuid():N}{extension}");
    }
}
