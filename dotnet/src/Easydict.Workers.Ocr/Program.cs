using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
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

        using var reader = new StreamReader(Console.OpenStandardInput());
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var shouldExit = await DispatchAsync(line);
            if (shouldExit)
            {
                break;
            }
        }

        return 0;
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
                    return true;

                case OcrMethods.Recognize:
                    if (!_configured)
                    {
                        await WriteErrorAsync(request.Id, WorkerErrorCodes.InvalidParams,
                            "Worker has not received a configure request yet");
                        return false;
                    }

                    var result = await RecognizeAsync(ParseParams<OcrRecognizeParams>(request.Params));
                    await WriteResponseAsync(request.Id, result);
                    return true;

                default:
                    await WriteErrorAsync(request.Id, IpcErrorCodes.MethodNotFound,
                        $"Unknown method: {request.Method}");
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(request.Id, WorkerErrorCodes.Cancelled, $"Request {request.Id} cancelled");
            return true;
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
        if (parameters.PixelWidth <= 0 || parameters.PixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "OCR image dimensions must be positive.");
        }

        var expectedLength = checked(parameters.PixelWidth * parameters.PixelHeight * 4);
        var pixelData = await File.ReadAllBytesAsync(parameters.PixelDataPath);
        if (pixelData.Length < expectedLength)
        {
            throw new ArgumentException(
                $"pixel data length ({pixelData.Length}) is less than expected ({expectedLength})");
        }

        using var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            parameters.PixelWidth,
            parameters.PixelHeight,
            BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixelData.AsBuffer());
        Array.Clear(pixelData);

        var engine = CreateEngine(parameters.PreferredLanguageTag);
        if (engine is null)
        {
            return new OcrResultDto();
        }

        var winResult = await engine.RecognizeAsync(bitmap).AsTask();
        var lines = winResult.Lines.Select(ConvertLine).ToList();

        return new OcrResultDto
        {
            Text = string.Join(Environment.NewLine, lines.Select(line => line.Text)),
            Lines = lines,
            TextAngle = winResult.TextAngle,
            DetectedLanguage = ConvertLanguage(engine),
        };
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

    private static async Task WriteEventAsync(string eventName, object data)
    {
        await Console.Out.WriteLineAsync(JsonLineSerializer.Serialize(new
        {
            @event = eventName,
            data,
        }));
        await Console.Out.FlushAsync();
    }

    private static async Task WriteResponseAsync(string id, object result)
    {
        await Console.Out.WriteLineAsync(JsonLineSerializer.Serialize(new
        {
            id,
            result,
        }));
        await Console.Out.FlushAsync();
    }

    private static async Task WriteErrorAsync(string id, string code, string message)
    {
        await Console.Out.WriteLineAsync(JsonLineSerializer.Serialize(new
        {
            id,
            error = new { code, message },
        }));
        await Console.Out.FlushAsync();
    }
}
