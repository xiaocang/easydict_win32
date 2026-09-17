using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using Microsoft.ML.OnnxRuntime;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace Easydict.Workers.Ocr;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static bool _configured;
    private static PpOcrV6Pipeline? _ppOcrV6Pipeline;
    private static PpOcrV6PipelineKey? _ppOcrV6PipelineKey;

    public static async Task<int> Main(string[] args)
    {
        WorkerSharedAssemblyResolver.Install();

        Trace.Listeners.Clear();
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
        Trace.AutoFlush = true;

        await WriteEventAsync(WorkerEvents.Ready, new ReadyEventData
        {
            WorkerKind = WorkerKinds.Ocr,
            WorkerVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ProtocolVersion = WorkerProtocolVersion.Current,
            Capabilities =
            [
                WorkerMethods.Configure,
                OcrMethods.Recognize,
                WorkerMethods.Shutdown,
            ],
        });

        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput());
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (await DispatchAsync(line))
                {
                    break;
                }
            }

            return 0;
        }
        finally
        {
            DisposePpOcrV6Pipeline();
        }
    }

    private static async Task<bool> DispatchAsync(string jsonLine)
    {
        IpcRequest? request;
        try
        {
            request = JsonLineSerializer.Deserialize<IpcRequest>(jsonLine);
        }
        catch (JsonException ex)
        {
            Trace.WriteLine($"[OcrWorker] Malformed JSON on stdin: {ex.Message}");
            return false;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Method))
        {
            Trace.WriteLine("[OcrWorker] Missing id/method on inbound request");
            return false;
        }

        try
        {
            switch (request.Method)
            {
                case WorkerMethods.Configure:
                    _configured = true;
                    await WriteResponseAsync(request.Id, new ConfigureResult { Ok = true });
                    return false;

                case WorkerMethods.Shutdown:
                    await WriteResponseAsync(request.Id, new { ok = true });
                    DisposePpOcrV6Pipeline();
                    return true;

                case OcrMethods.Recognize:
                    if (!_configured)
                    {
                        await WriteErrorAsync(request.Id, WorkerErrorCodes.InvalidParams,
                            "Worker has not received a configure request yet");
                        return false;
                    }

                    var parameters = ParseParams<OcrRecognizeParams>(request.Params);
                    var result = await RecognizeAsync(parameters);
                    await WriteResponseAsync(request.Id, result);
                    return !string.Equals(parameters.Engine, OcrEngines.PpOcrV6, StringComparison.OrdinalIgnoreCase);

                default:
                    await WriteErrorAsync(request.Id, IpcErrorCodes.MethodNotFound,
                        $"Unknown method: {request.Method}");
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(request.Id, WorkerErrorCodes.Cancelled, $"Request {request.Id} cancelled");
            return false;
        }
        catch (PpOcrV6ModelException ex)
        {
            Trace.WriteLine($"[OcrWorker] PP-OCRv6 error ({ex.Code}): {ex.Message}");
            await WriteErrorAsync(request.Id, ex.Code, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[OcrWorker] Unhandled exception in {request.Method}: {ex}");
            await WriteErrorAsync(request.Id, WorkerErrorCodes.Internal, ex.Message);
            return true;
        }
    }

    private static T ParseParams<T>(object? parameters)
    {
        if (parameters is JsonElement element)
        {
            return element.Deserialize<T>(JsonOptions)
                ?? throw new InvalidOperationException($"{typeof(T).Name} was null");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(parameters, JsonOptions);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
            ?? throw new InvalidOperationException($"{typeof(T).Name} was null");
    }

    private static async Task<OcrResultDto> RecognizeAsync(OcrRecognizeParams parameters)
    {
        if (string.Equals(parameters.Engine, OcrEngines.PpOcrV6, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(parameters.ModelId))
            {
                throw new PpOcrV6ModelException(WorkerErrorCodes.InvalidParams, "PP-OCRv6 modelId is required.");
            }

            if (!PpOcrV6ModelCatalog.TryGet(parameters.ModelId, out _))
            {
                throw new PpOcrV6ModelException(
                    WorkerErrorCodes.ModelInvalid,
                    $"Unknown PP-OCRv6 model '{parameters.ModelId}'.");
            }

            if (!PpOcrV6ModelCatalog.SupportsLanguage(parameters.ModelId, parameters.PreferredLanguageTag))
            {
                throw new PpOcrV6ModelException(
                    WorkerErrorCodes.UnsupportedLanguage,
                    $"PP-OCRv6 model '{parameters.ModelId}' does not support '{parameters.PreferredLanguageTag}'.");
            }

            var pixelData = await ReadPixelDataAsync(parameters).ConfigureAwait(false);
            var pipeline = GetPpOcrV6Pipeline(parameters);
            try
            {
                return await pipeline.RecognizeAsync(
                    pixelData,
                    parameters.PixelWidth,
                    parameters.PixelHeight).ConfigureAwait(false);
            }
            catch (PpOcrV6ModelException)
            {
                throw;
            }
            catch (OnnxRuntimeException ex)
            {
                if (ReferenceEquals(_ppOcrV6Pipeline, pipeline))
                {
                    DisposePpOcrV6Pipeline();
                }
                throw new PpOcrV6ModelException(
                    WorkerErrorCodes.InferenceError,
                    $"PP-OCRv6 inference failed: {ex.Message}");
            }
        }

        var nativePixelData = await ReadPixelDataAsync(parameters).ConfigureAwait(false);
        var engine = CreateEngine(parameters.PreferredLanguageTag);
        if (engine is null)
        {
            Array.Clear(nativePixelData);
            return new OcrResultDto { Engine = OcrEngines.WindowsNative };
        }

        try
        {
            var firstPass = await RunNativeOcrPassAsync(
                engine, nativePixelData, parameters.PixelWidth, parameters.PixelHeight).ConfigureAwait(false);

            return await RefineWithUpscaledPassAsync(
                engine, firstPass, nativePixelData, parameters.PixelWidth, parameters.PixelHeight)
                .ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(nativePixelData);
        }
    }

    /// <summary>
    /// Screenshots taken on a laptop panel often carry text too small for the engine, which
    /// then returns partial text or nothing at all. When the first pass came back with
    /// small (or no) lines, recognize the capture again enlarged and keep the better reading.
    /// Mirrors <c>WindowsOcrService.RefineWithUpscaledPassAsync</c> (Easydict.WinUI) so both the
    /// out-of-process worker — the path every shipped build uses by default — and the in-process
    /// fallback recover the same small text (see issue #217).
    /// </summary>
    private static async Task<OcrResultDto> RefineWithUpscaledPassAsync(
        WinOcr.OcrEngine engine,
        OcrResultDto firstPass,
        byte[] pixelData,
        int pixelWidth,
        int pixelHeight)
    {
        var lineHeights = firstPass.Lines.Select(line => line.BoundingRect.Height).ToList();
        var scale = OcrImageScaling.ComputeRetryScale(
            lineHeights, pixelWidth, pixelHeight, (int)WinOcr.OcrEngine.MaxImageDimension);
        if (scale <= 1.0) return firstPass;

        var (scaledWidth, scaledHeight) = OcrImageScaling.ScaledSize(pixelWidth, pixelHeight, scale);
        Trace.WriteLine(
            $"[OcrWorker] Retrying at {scaledWidth}x{scaledHeight} (x{scale:F2}) — " +
            $"first pass median line height {OcrImageScaling.MedianLineHeight(lineHeights):F1}px");

        byte[] scaledPixels;
        try
        {
            scaledPixels = OcrImageScaling.ScaleBgra(pixelData, pixelWidth, pixelHeight, scaledWidth, scaledHeight);
        }
        catch (OutOfMemoryException ex)
        {
            Trace.WriteLine($"[OcrWorker] Upscale skipped: {ex.Message}");
            return firstPass;
        }

        try
        {
            var secondPass = await RunNativeOcrPassAsync(
                engine, scaledPixels, scaledWidth, scaledHeight).ConfigureAwait(false);

            if (!OcrImageScaling.ShouldPreferRetry(firstPass.Text, secondPass.Text))
            {
                return firstPass;
            }

            return MapToSourceCoordinates(
                secondPass,
                (double)scaledWidth / pixelWidth,
                (double)scaledHeight / pixelHeight);
        }
        finally
        {
            Array.Clear(scaledPixels);
        }
    }

    private static async Task<OcrResultDto> RunNativeOcrPassAsync(
        WinOcr.OcrEngine engine, byte[] pixelData, int width, int height)
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(pixelData.AsBuffer());

        var winResult = await engine.RecognizeAsync(bitmap).AsTask();
        var lines = winResult.Lines.Select(ConvertLine).ToList();

        return new OcrResultDto
        {
            Text = string.Join(Environment.NewLine, lines.Select(line => line.Text)),
            Lines = lines,
            TextAngle = winResult.TextAngle,
            DetectedLanguage = ConvertLanguage(engine),
            Engine = OcrEngines.WindowsNative,
        };
    }

    /// <summary>
    /// Maps a result recognized on an enlarged image back onto source-image coordinates,
    /// so callers keep working in the coordinate space of the original capture.
    /// </summary>
    private static OcrResultDto MapToSourceCoordinates(OcrResultDto result, double scaleX, double scaleY)
    {
        if (result.Lines.Count == 0) return result;

        var lines = result.Lines
            .Select(line =>
            {
                var (x, y, w, h) = OcrImageScaling.MapRect(
                    line.BoundingRect.X, line.BoundingRect.Y,
                    line.BoundingRect.Width, line.BoundingRect.Height,
                    scaleX, scaleY);
                return new OcrLineDto
                {
                    Text = line.Text,
                    Confidence = line.Confidence,
                    Words = line.Words,
                    BoundingRect = new OcrRectDto(x, y, w, h),
                };
            })
            .ToList();

        return new OcrResultDto
        {
            Text = result.Text,
            Lines = lines,
            DetectedLanguage = result.DetectedLanguage,
            TextAngle = result.TextAngle,
            Engine = result.Engine,
            ModelId = result.ModelId,
        };
    }

    private static PpOcrV6Pipeline GetPpOcrV6Pipeline(OcrRecognizeParams parameters)
    {
        var modelId = parameters.ModelId!;
        var threadCount = Math.Clamp(
            parameters.ThreadCount ?? Environment.ProcessorCount,
            PpOcrV6ModelCatalog.MinThreadCount,
            PpOcrV6ModelCatalog.MaxThreadCount);
        var key = new PpOcrV6PipelineKey(modelId, threadCount, parameters.UseGpu);
        if (_ppOcrV6Pipeline is not null && _ppOcrV6PipelineKey == key)
        {
            return _ppOcrV6Pipeline;
        }

        _ppOcrV6Pipeline?.Dispose();
        _ppOcrV6Pipeline = new PpOcrV6Pipeline(modelId, threadCount, parameters.UseGpu);
        _ppOcrV6PipelineKey = key;
        return _ppOcrV6Pipeline;
    }

    private static void DisposePpOcrV6Pipeline()
    {
        _ppOcrV6Pipeline?.Dispose();
        _ppOcrV6Pipeline = null;
        _ppOcrV6PipelineKey = null;
    }

    private static async Task<byte[]> ReadPixelDataAsync(OcrRecognizeParams parameters)
    {
        if (parameters.PixelWidth <= 0 || parameters.PixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "OCR image dimensions must be positive.");
        }

        var expectedLength = checked(parameters.PixelWidth * parameters.PixelHeight * 4);
        var pixelData = await File.ReadAllBytesAsync(parameters.PixelDataPath).ConfigureAwait(false);
        if (pixelData.Length < expectedLength)
        {
            throw new ArgumentException(
                $"pixel data length ({pixelData.Length}) is less than expected ({expectedLength})");
        }

        return pixelData;
    }

    private static WinOcr.OcrEngine? CreateEngine(string? preferredLanguageTag)
    {
        if (!string.IsNullOrWhiteSpace(preferredLanguageTag))
        {
            try
            {
                var language = new Windows.Globalization.Language(preferredLanguageTag);
                var engine = WinOcr.OcrEngine.TryCreateFromLanguage(language);
                if (engine is not null)
                {
                    return engine;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[OcrWorker] Failed to create engine for {preferredLanguageTag}: {ex.Message}");
            }
        }

        return WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static OcrLineDto ConvertLine(WinOcr.OcrLine line)
    {
        var recognizedWords = line.Words
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .ToList();
        var words = recognizedWords.Select(word => word.Text).ToList();
        // Legacy fallback text (naive space join). The host prefers the raw Words below and
        // re-merges them with the CJK-aware merger so this space join is not used when Words flow through.
        var text = string.Join(" ", words);

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        foreach (var word in recognizedWords)
        {
            var rect = word.BoundingRect;
            minX = Math.Min(minX, rect.X);
            minY = Math.Min(minY, rect.Y);
            maxX = Math.Max(maxX, rect.X + rect.Width);
            maxY = Math.Max(maxY, rect.Y + rect.Height);
        }

        var boundingRect = minX == double.MaxValue
            ? new OcrRectDto()
            : new OcrRectDto(minX, minY, maxX - minX, maxY - minY);

        return new OcrLineDto
        {
            Text = text,
            Words = words,
            BoundingRect = boundingRect,
        };
    }

    private static OcrLanguageDto? ConvertLanguage(WinOcr.OcrEngine engine)
    {
        var language = engine.RecognizerLanguage;
        return language is null
            ? null
            : new OcrLanguageDto { Tag = language.LanguageTag, DisplayName = language.DisplayName };
    }

    private static Task WriteEventAsync(string eventName, object data) =>
        WriteLineAsync(new { @event = eventName, data });

    private static Task WriteResponseAsync(string id, object result) =>
        WriteLineAsync(new { id, result });

    private static Task WriteErrorAsync(string id, string code, string message) =>
        WriteLineAsync(new { id, error = new { code, message } });

    private readonly record struct PpOcrV6PipelineKey(string ModelId, int ThreadCount, bool UseGpu);

    private static async Task WriteLineAsync(object value)
    {
        await Console.Out.WriteLineAsync(JsonLineSerializer.Serialize(value));
        await Console.Out.FlushAsync();
    }
}
