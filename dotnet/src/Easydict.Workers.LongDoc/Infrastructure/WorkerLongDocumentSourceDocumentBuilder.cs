using System.Diagnostics;
using System.Net;
using Easydict.TranslationService.LongDocument;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.DocumentExport;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace Easydict.Workers.LongDoc.Infrastructure;

internal sealed class WorkerLongDocumentSourceDocumentBuilder : IDisposable
{
    private LayoutModelDownloadService? _layoutModelDownloadService;
    private DocLayoutYoloService? _docLayoutYoloService;
    private TableStructureRecognitionService? _tatrService;
    private HttpClient? _modelDownloadHttpClient;
    private HttpClient? _visionHttpClient;
    private VisionLayoutDetectionService? _visionLayoutDetectionService;
    private LayoutDetectionStrategy? _layoutDetectionStrategy;
    private bool _disposed;

    public async Task<SourceDocument> BuildAsync(
        LongDocumentInputMode mode,
        string input,
        LayoutDetectionMode layoutDetection,
        string? visionEndpoint,
        string? visionApiKey,
        string? visionModel,
        string? pageRange,
        bool enableTatrTableStructure,
        bool proxyEnabled,
        string? proxyUri,
        bool proxyBypassLocal,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (mode is LongDocumentInputMode.PlainText or LongDocumentInputMode.Markdown ||
            layoutDetection == LayoutDetectionMode.Heuristic)
        {
            return await LongDocumentSourceExtraction.BuildSourceDocumentBasicAsync(mode, input, pageRange)
                .ConfigureAwait(false);
        }

        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Source file not found.", input);
        }

        var strategy = GetLayoutDetectionStrategy(
            enableTatrTableStructure,
            proxyEnabled,
            proxyUri,
            proxyBypassLocal);
        if (layoutDetection == LayoutDetectionMode.Auto && !strategy.IsOnnxDownloaded)
        {
            return await LongDocumentSourceExtraction.BuildSourceDocumentBasicAsync(mode, input, pageRange)
                .ConfigureAwait(false);
        }

        onProgress?.Invoke("Analyzing page layouts with ML model...");

        return await Task.Run(async () =>
        {
            using var document = PdfPigDocument.Open(input);
            var totalPages = document.NumberOfPages;
            var selectedPages = PageRangeParser.Parse(pageRange, totalPages);
            var pages = new List<SourceDocumentPage>();

            for (var pageNumber = 1; pageNumber <= totalPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (selectedPages is not null && !selectedPages.Contains(pageNumber))
                {
                    continue;
                }

                var page = document.GetPage(pageNumber);
                var pageText = page.Text;
                var scanned = string.IsNullOrWhiteSpace(pageText);
                if (scanned)
                {
                    pages.Add(new SourceDocumentPage
                    {
                        PageNumber = pageNumber,
                        IsScanned = true,
                        Blocks = [],
                    });
                    continue;
                }

                var heuristicBlocks = LongDocumentSourceExtraction.ExtractLayoutBlocksFromPage(page).ToList();
                if (heuristicBlocks.Count == 0)
                {
                    pages.Add(new SourceDocumentPage
                    {
                        PageNumber = pageNumber,
                        IsScanned = true,
                        Blocks = [],
                    });
                    continue;
                }

                try
                {
                    var enhancedBlocks = await strategy.DetectAndExtractAsync(
                        page,
                        input,
                        pageNumber - 1,
                        layoutDetection,
                        visionEndpoint,
                        visionApiKey,
                        visionModel,
                        cancellationToken).ConfigureAwait(false);

                    if (enhancedBlocks.Count > 0)
                    {
                        pages.Add(new SourceDocumentPage
                        {
                            PageNumber = pageNumber,
                            IsScanned = false,
                            Blocks = enhancedBlocks.Select(block => block.Block).ToList(),
                        });
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LongDocWorker] ML detection failed for page {pageNumber}: {ex.Message}");
                }

                pages.Add(new SourceDocumentPage
                {
                    PageNumber = pageNumber,
                    IsScanned = false,
                    Blocks = heuristicBlocks,
                });
            }

            if (pages.Count == 0)
            {
                pages.Add(new SourceDocumentPage
                {
                    PageNumber = 1,
                    IsScanned = true,
                    Blocks = [],
                });
            }

            return new SourceDocument
            {
                DocumentId = Path.GetFileNameWithoutExtension(input),
                Pages = pages,
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    private LayoutDetectionStrategy GetLayoutDetectionStrategy(
        bool enableTatrTableStructure,
        bool proxyEnabled,
        string? proxyUri,
        bool proxyBypassLocal)
    {
        if (_layoutDetectionStrategy is not null)
        {
            return _layoutDetectionStrategy;
        }

        _modelDownloadHttpClient ??= CreateHttpClient(proxyEnabled, proxyUri, proxyBypassLocal);
        _layoutModelDownloadService ??= new LayoutModelDownloadService(_modelDownloadHttpClient);
        _docLayoutYoloService ??= new DocLayoutYoloService(_layoutModelDownloadService);
        _tatrService ??= new TableStructureRecognitionService(_layoutModelDownloadService);
        _visionHttpClient ??= CreateHttpClient(proxyEnabled, proxyUri, proxyBypassLocal);
        _visionLayoutDetectionService ??= new VisionLayoutDetectionService(_visionHttpClient);
        _layoutDetectionStrategy = new LayoutDetectionStrategy(
            _docLayoutYoloService,
            _visionLayoutDetectionService,
            _layoutModelDownloadService,
            _tatrService,
            () => enableTatrTableStructure);

        return _layoutDetectionStrategy;
    }

    private static HttpClient CreateHttpClient(bool proxyEnabled, string? proxyUri, bool proxyBypassLocal)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        if (proxyEnabled &&
            !string.IsNullOrWhiteSpace(proxyUri) &&
            Uri.TryCreate(proxyUri, UriKind.Absolute, out var parsedProxyUri))
        {
            handler.Proxy = new WebProxy(parsedProxyUri)
            {
                BypassProxyOnLocal = proxyBypassLocal,
            };
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Easydict-Win32/1.0");
        return client;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _docLayoutYoloService?.Dispose();
        _tatrService?.Dispose();
        _layoutModelDownloadService?.Dispose();
        _modelDownloadHttpClient?.Dispose();
        _visionHttpClient?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WorkerLongDocumentSourceDocumentBuilder));
        }
    }
}
