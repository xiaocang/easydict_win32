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
            catch (OnnxRuntimeException)
            {
                if (ReferenceEquals(_ppOcrV6Pipeline, pipeline))
                {
                    DisposePpOcrV6Pipeline();
                }
                throw;
            }
        }

        var nativePixelData = await ReadPixelDataAsync(parameters).ConfigureAwait(false);
        using var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            parameters.PixelWidth,
            parameters.PixelHeight,
            BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(nativePixelData.AsBuffer());
        Array.Clear(nativePixelData);

        var engine = CreateEngine(parameters.PreferredLanguageTag);
        if (engine is null)
        {
            return new OcrResultDto { Engine = OcrEngines.WindowsNative };
        }

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
