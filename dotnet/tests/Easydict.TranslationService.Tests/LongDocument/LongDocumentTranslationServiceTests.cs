using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using FluentAssertions;

namespace Easydict.TranslationService.Tests.LongDocument;

public class LongDocumentTranslationServiceTests
{
    [Fact]
    public async Task TranslateAsync_BuildsStructuredIr_PreservesMetadataAndCaptionOwnership()
    {
        var fakeService = new FakeTranslationService(text => $"ZH:{text}");
        var sut = new LongDocumentTranslationService(fakeService);

        var request = new LongDocumentTranslationRequest
        {
            FromLanguage = Language.English,
            ToLanguage = Language.ChineseSimplified,
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            Id = "p1-b1",
                            PageNumber = 1,
                            ReadingOrder = 1,
                            BlockType = DocumentBlockType.Paragraph,
                            Text = "This is a paragraph.",
                            Coordinates = new DocumentCoordinates(1, 1, 100, 24),
                        },
                        new SourceDocumentBlock
                        {
                            Id = "p1-b2",
                            PageNumber = 1,
                            ReadingOrder = 2,
                            BlockType = DocumentBlockType.Table,
                            Text = "A | B",
                        },
                        new SourceDocumentBlock
                        {
                            Id = "p1-b3",
                            PageNumber = 1,
                            ReadingOrder = 3,
                            BlockType = DocumentBlockType.Caption,
                            ParentBlockId = "p1-b2",
                            Text = "Table 1: Summary",
                        },
                    ],
                },
            ],
        };

        var result = await sut.TranslateAsync(request);

        result.IntermediateRepresentation.Pages.Should().HaveCount(1);
        result.IntermediateRepresentation.Pages[0].Blocks.Should().HaveCount(3);
        result.Pages[0].Blocks[0].SourceHash.Should().NotBeNullOrWhiteSpace();
        result.Pages[0].Blocks[0].Coordinates.Should().BeEquivalentTo(new DocumentCoordinates(1, 1, 100, 24));

        result.StructuredOutputText.Should().Contain("Paragraph: ZH:This is a paragraph.");
        result.StructuredOutputText.Should().Contain("Caption (for Table:p1-b2): ZH:Table 1: Summary");
    }

    [Fact]
    public async Task TranslateAsync_ProtectsFormulaBlocks_AndSkipsTranslation()
    {
        var fakeService = new FakeTranslationService(text => $"TR:{text}");
        var sut = new LongDocumentTranslationService(fakeService);

        var request = new LongDocumentTranslationRequest
        {
            FromLanguage = Language.English,
            ToLanguage = Language.ChineseSimplified,
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            Id = "f1",
                            PageNumber = 1,
                            ReadingOrder = 1,
                            BlockType = DocumentBlockType.Formula,
                            Text = "$$E=mc^2$$",
                        },
                        new SourceDocumentBlock
                        {
                            Id = "p1",
                            PageNumber = 1,
                            ReadingOrder = 2,
                            BlockType = DocumentBlockType.Paragraph,
                            Text = "Equation $$E=mc^2$$ should remain.",
                        },
                    ],
                },
            ],
        };

        var result = await sut.TranslateAsync(request);

        result.Pages[0].Blocks[0].TranslatedText.Should().Be("[FORMULA_BLOCK]");
        result.Pages[0].Blocks[1].SourceText.Should().Contain("[FORMULA_BLOCK]");
        fakeService.TranslateCalls.Should().Be(1);
    }

    [Fact]
    public async Task TranslateAsync_UsesOcrFallbackGlossaryAndQualityReport()
    {
        var fakeService = new FakeTranslationService(text =>
        {
            if (text.Contains("fail", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("synthetic failure");
            }

            return $"AI output: {text}";
        });

        var sut = new LongDocumentTranslationService(fakeService);

        var request = new LongDocumentTranslationRequest
        {
            FromLanguage = Language.English,
            ToLanguage = Language.ChineseSimplified,
            IsScannedPdf = true,
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            Id = "orig",
                            PageNumber = 1,
                            ReadingOrder = 1,
                            BlockType = DocumentBlockType.Paragraph,
                            Text = "original ignored",
                        },
                    ],
                },
            ],
            OcrFallbackPages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 5,
                    Blocks =
                    [
                        new SourceDocumentBlock
                        {
                            Id = "ocr1",
                            PageNumber = 5,
                            ReadingOrder = 1,
                            BlockType = DocumentBlockType.Paragraph,
                            Text = "AI term",
                        },
                        new SourceDocumentBlock
                        {
                            Id = "ocr2",
                            PageNumber = 5,
                            ReadingOrder = 2,
                            BlockType = DocumentBlockType.Paragraph,
                            Text = "please fail",
                        },
                    ],
                },
            ],
            Options = new LongDocumentTranslationOptions
            {
                EnableOcrFallback = true,
                EnableGlossaryConsistency = true,
                Glossary = new Dictionary<string, string>
                {
                    ["AI"] = "人工智能",
                },
                MaxRetriesPerBlock = 2,
            },
        };

        var result = await sut.TranslateAsync(request);

        result.IntermediateRepresentation.UsedOcrFallback.Should().BeTrue();
        result.Pages[0].PageNumber.Should().Be(5);
        result.Pages[0].Blocks[0].TranslatedText.Should().Contain("人工智能 output");
        result.QualityReport.FailedPages.Should().ContainSingle().Which.Should().Be(5);
        result.QualityReport.FailedBlocks.Should().ContainSingle(x => x.BlockId == "ocr2");
        result.QualityReport.RetryCount.Should().Be(2);
        result.QualityReport.StageTimings.Should().Contain(x => x.Stage == "translate");
    }

    private sealed class FakeTranslationService : ITranslationService
    {
        private readonly Func<string, string> _translator;

        public FakeTranslationService(Func<string, string> translator)
        {
            _translator = translator;
        }

        public int TranslateCalls { get; private set; }

        public string ServiceId => "fake";
        public string DisplayName => "Fake";
        public bool RequiresApiKey => false;
        public bool IsConfigured => true;
        public IReadOnlyList<Language> SupportedLanguages => [Language.Auto, Language.English, Language.ChineseSimplified];

        public bool SupportsLanguagePair(Language from, Language to) => true;

        public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Language.English);
        }

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
        {
            TranslateCalls++;
            var translated = _translator(request.Text);
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = translated,
                TargetLanguage = request.ToLanguage,
                ServiceName = "fake",
            });
        }
    }
}
