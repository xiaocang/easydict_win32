using System.Reflection;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class PdfExportModeCompatibilityTests
{
    [Theory]
    [InlineData("", PdfExportMode.Overlay)]
    [InlineData("Overlay", PdfExportMode.Overlay)]
    [InlineData("mupdf", PdfExportMode.Overlay)]
    [InlineData("ContentStreamReplacement", PdfExportMode.Overlay)]
    [InlineData("contentstream", PdfExportMode.Overlay)]
    [InlineData("Reconstruct", PdfExportMode.Overlay)]
    public void CliLegacyAndCurrentValuesResolveToOverlay(string value, PdfExportMode expected)
    {
        var parser = typeof(LongDocumentCliCommand).GetMethod(
            "ParsePdfExportMode",
            BindingFlags.Static | BindingFlags.NonPublic);

        parser.Should().NotBeNull();
        parser!.Invoke(null, [value]).Should().Be(expected);
    }
}
