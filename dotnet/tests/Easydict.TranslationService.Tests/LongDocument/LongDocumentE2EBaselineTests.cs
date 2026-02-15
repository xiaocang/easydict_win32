using System.Text;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.LongDocument;

public class LongDocumentE2EBaselineTests
{
    [Fact]
    public async Task TranslateAsync_ShouldMatchLongDocumentBaselineSnapshot()
    {
        var sut = new LongDocumentTranslationService(translateWithService: (request, _, _) =>
            Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"ZH:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = Language.SimplifiedChinese
            }));

        var source = new SourceDocument
        {
            DocumentId = "baseline-doc-1",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "p1-left-b1",
                            BlockType = SourceBlockType.Heading,
                            Text = "ABSTRACT"
                        },
                        new SourceDocumentBlock
                        {
                            BlockId = "p1-left-b2",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "Energy is represented by $E = mc^2$ in relativity.",
                            BoundingBox = new BlockRect(12, 120, 220, 40)
                        },
                        new SourceDocumentBlock
                        {
                            BlockId = "p1-right-b1",
                            BlockType = SourceBlockType.Paragraph,
                            Text = "The right column continues the discussion.",
                            BoundingBox = new BlockRect(260, 100, 220, 30)
                        }
                    ]
                },
                new SourceDocumentPage
                {
                    PageNumber = 2,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            BlockId = "p2-table-b1",
                            BlockType = SourceBlockType.TableCell,
                            Text = "Result A"
                        },
                        new SourceDocumentBlock
                        {
                            BlockId = "p2-table-b2",
                            BlockType = SourceBlockType.TableCell,
                            Text = "Result B"
                        }
                    ]
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.SimplifiedChinese,
            ServiceId = "google",
            EnableFormulaProtection = true,
            EnableOcrFallback = true,
            MaxRetriesPerBlock = 1
        });

        var snapshot = BuildSnapshot(result);
        var expected = string.Join('\n',
            "P1|p1-left-b1|ZH:ABSTRACT",
            "P1|p1-left-b2|ZH:Energy is represented by $E = mc^2$ in relativity.",
            "P1|p1-right-b1|ZH:The right column continues the discussion.",
            "P2|p2-table-b1|ZH:Result A",
            "P2|p2-table-b2|ZH:Result B");

        snapshot.Should().Be(expected);
        result.QualityReport.StageTimingsMs.Keys.Should().Contain(new[]
        {
            "ingest", "build-ir", "formula-protection", "translate", "structured-layout-output"
        });
    }

    [Fact]
    public async Task TranslateAsync_ShouldMatchScannedPageOcrBaseline()
    {
        var sut = new LongDocumentTranslationService(
            translateWithService: (request, _, _) => Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = $"EN:{request.Text}",
                ServiceName = "fake",
                TargetLanguage = Language.English
            }),
            ocrExtractor: (_, _) => Task.FromResult<string?>("Recovered from OCR"));

        var source = new SourceDocument
        {
            DocumentId = "baseline-ocr",
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    IsScanned = true,
                    Blocks = []
                }
            ]
        };

        var result = await sut.TranslateAsync(source, new LongDocumentTranslationOptions
        {
            ToLanguage = Language.English,
            ServiceId = "google",
            EnableOcrFallback = true
        });

        result.Pages.Should().ContainSingle();
        result.Pages[0].Blocks.Should().ContainSingle();
        result.Pages[0].Blocks[0].OriginalText.Should().Be("Recovered from OCR");
        result.Pages[0].Blocks[0].TranslatedText.Should().Be("EN:Recovered from OCR");
    }

    private static string BuildSnapshot(LongDocumentTranslationResult result)
    {
        var sb = new StringBuilder();
        foreach (var page in result.Pages.OrderBy(p => p.PageNumber))
        {
            foreach (var block in page.Blocks)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append($"P{page.PageNumber}|{block.SourceBlockId}|{block.TranslatedText}");
            }
        }

        return sb.ToString();
    }
}
