using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using Windows.Graphics.Imaging;
using SidecarClientType = Easydict.SidecarClient.SidecarClient;
using WinOcr = Windows.Media.Ocr;

namespace Easydict.CompatHost;

public sealed class OcrWorkerCompatRecognizer : ICompatHostOcrRecognizer
{
    private const string WorkerSubdir = "ocr";
    private const string WorkerExeName = "Easydict.Workers.Ocr.exe";
    private const int HandshakeTimeoutMs = 10_000;
    private const int ConfigureTimeoutMs = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Func<SidecarClientOptions> _optionsFactory;
    private readonly Func<OcrRecognizeParams, SettingsSnapshot, CancellationToken, Task<OcrResultDto>>? _fallbackRecognizer;

    public OcrWorkerCompatRecognizer()
        : this(CreateDefaultOptions, WindowsOcrCompatFallback.RecognizeAsync)
    {
    }

    internal OcrWorkerCompatRecognizer(Func<SidecarClientOptions> optionsFactory)
        : this(optionsFactory, null)
    {
    }

    internal OcrWorkerCompatRecognizer(
        Func<SidecarClientOptions> optionsFactory,
        Func<OcrRecognizeParams, SettingsSnapshot, CancellationToken, Task<OcrResultDto>>? fallbackRecognizer)
    {
        _optionsFactory = optionsFactory;
        _fallbackRecognizer = fallbackRecognizer;
    }

    public async Task<OcrResultDto> RecognizeAsync(
        OcrRecognizeParams parameters,
        SettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        await using var client = new SidecarClientType(_optionsFactory());

        try
        {
            await StartAndConfigureAsync(client, settings, cancellationToken).ConfigureAwait(false);

            return await client.SendRequestAsync<OcrResultDto>(
                    OcrMethods.Recognize,
                    parameters,
                    timeoutMs: 0,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? new OcrResultDto();
        }
        catch (SidecarErrorException ex)
        {
            throw new CompatHostException(ex.Error.Code, ex.Error.Message, ex.Error.Details);
        }
        catch (SidecarProcessExitedException ex) when (_fallbackRecognizer is not null)
        {
            Trace.WriteLine($"[CompatHost:OcrWorker] Falling back to in-proc Windows OCR after worker exit: {ex.Message}");
            return await _fallbackRecognizer(parameters, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OcrWorkerUnavailableException ex) when (_fallbackRecognizer is not null)
        {
            Trace.WriteLine($"[CompatHost:OcrWorker] Falling back to in-proc Windows OCR: {ex.Message}");
            return await _fallbackRecognizer(parameters, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OcrWorkerUnavailableException ex)
        {
            throw new CompatHostException(ex.Code, ex.Message);
        }
        catch (SidecarException ex)
        {
            throw new CompatHostException(IpcErrorCodes.ServiceError, ex.Message);
        }
    }

    internal static string ResolveOcrWorkerPath(string baseDirectory)
    {
        return Path.Combine(baseDirectory, "workers", WorkerSubdir, WorkerExeName);
    }

    internal static Dictionary<string, string> BuildWorkerEnvironment(string baseDirectory)
    {
        var dotnetRoot = Path.Combine(baseDirectory, "dotnet");
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EASYDICT_WORKER_SHARED_DIR"] = Path.Combine(baseDirectory, "workers", "shared"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };

        if (HasBundledDotnetRuntime(dotnetRoot))
        {
            variables["DOTNET_ROOT"] = dotnetRoot;
            variables["DOTNET_ROOT_X64"] = dotnetRoot;
            variables["DOTNET_ROOT_ARM64"] = dotnetRoot;
        }

        return variables;
    }

    private static SidecarClientOptions CreateDefaultOptions()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return new SidecarClientOptions
        {
            ExecutablePath = ResolveOcrWorkerPath(baseDirectory),
            DefaultTimeoutMs = 0,
            EnvironmentVariables = BuildWorkerEnvironment(baseDirectory),
        };
    }

    private static async Task StartAndConfigureAsync(
        SidecarClientType client,
        SettingsSnapshot settings,
        CancellationToken cancellationToken)
    {
        var readyTcs = new TaskCompletionSource<ReadyEventData>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnEvent(IpcEvent evt)
        {
            if (evt.Event != WorkerEvents.Ready || evt.Data is null) return;

            try
            {
                var ready = evt.Data.Value.Deserialize<ReadyEventData>(JsonOptions);
                if (ready is not null)
                {
                    readyTcs.TrySetResult(ready);
                }
            }
            catch (JsonException ex)
            {
                readyTcs.TrySetException(new CompatHostException(
                    IpcErrorCodes.ServiceError,
                    $"OCR worker emitted malformed ready event: {ex.Message}"));
            }
        }

        void OnProcessExited(int? exitCode)
        {
            readyTcs.TrySetException(new OcrWorkerUnavailableException(
                IpcErrorCodes.ServiceError,
                $"OCR worker exited before ready/configure completed: code={exitCode}"));
        }

        void OnStderrLog(string line)
        {
            Trace.WriteLine($"[CompatHost:OcrWorker] {line}");
        }

        client.OnEvent += OnEvent;
        client.OnProcessExited += OnProcessExited;
        client.OnStderrLog += OnStderrLog;

        try
        {
            try
            {
                client.Start();
            }
            catch (Win32Exception ex)
            {
                throw new OcrWorkerUnavailableException(
                    IpcErrorCodes.ServiceError,
                    $"Failed to start OCR worker: {ex.Message}");
            }

            ReadyEventData ready;
            try
            {
                ready = await readyTcs.Task
                    .WaitAsync(TimeSpan.FromMilliseconds(HandshakeTimeoutMs), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new OcrWorkerUnavailableException(
                    IpcErrorCodes.ServiceError,
                    $"OCR worker did not emit ready within {HandshakeTimeoutMs}ms");
            }

            if (ready.WorkerKind != WorkerKinds.Ocr)
            {
                throw new OcrWorkerUnavailableException(
                    IpcErrorCodes.ServiceError,
                    $"Expected OCR worker kind '{WorkerKinds.Ocr}' but worker reported '{ready.WorkerKind}'");
            }

            if (ready.ProtocolVersion != WorkerProtocolVersion.Current)
            {
                throw new OcrWorkerUnavailableException(
                    WorkerErrorCodes.VersionMismatch,
                    $"OCR worker reports protocol version {ready.ProtocolVersion}; host expects {WorkerProtocolVersion.Current}");
            }

            var configureResult = await client.SendRequestAsync<ConfigureResult>(
                    WorkerMethods.Configure,
                    new ConfigureParams { Settings = settings },
                    timeoutMs: ConfigureTimeoutMs,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (configureResult?.Ok != true)
            {
                throw new OcrWorkerUnavailableException(
                    IpcErrorCodes.ServiceError,
                    "OCR worker configure request did not return ok=true");
            }
        }
        finally
        {
            client.OnEvent -= OnEvent;
            client.OnProcessExited -= OnProcessExited;
            client.OnStderrLog -= OnStderrLog;
        }
    }

    private static bool HasBundledDotnetRuntime(string dotnetRoot)
    {
        return Directory.Exists(Path.Combine(dotnetRoot, "host", "fxr"))
            && Directory.Exists(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App"));
    }

    private sealed class OcrWorkerUnavailableException : Exception
    {
        public OcrWorkerUnavailableException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}

internal static class WindowsOcrCompatFallback
{
    public static async Task<OcrResultDto> RecognizeAsync(
        OcrRecognizeParams parameters,
        SettingsSnapshot settings,
        CancellationToken cancellationToken)
    {
        if (parameters.PixelWidth <= 0 || parameters.PixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "OCR image dimensions must be positive.");
        }

        var expectedLength = checked(parameters.PixelWidth * parameters.PixelHeight * 4);
        var pixelData = await File.ReadAllBytesAsync(parameters.PixelDataPath, cancellationToken)
            .ConfigureAwait(false);
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

        var preferredLanguageTag = parameters.PreferredLanguageTag;
        if (string.IsNullOrWhiteSpace(preferredLanguageTag))
        {
            preferredLanguageTag = settings.OcrLanguage;
        }

        var engine = CreateEngine(preferredLanguageTag);
        if (engine is null)
        {
            return new OcrResultDto();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var winResult = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken)
            .ConfigureAwait(false);
        var lines = GroupAndSortLines(winResult.Lines.Select(ConvertLine).ToList());

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
        if (!string.IsNullOrWhiteSpace(preferredLanguageTag) &&
            !preferredLanguageTag.Equals("auto", StringComparison.OrdinalIgnoreCase))
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
                Trace.WriteLine($"[CompatHost:WindowsOcrFallback] Failed to create engine for {preferredLanguageTag}: {ex.Message}");
            }
        }

        return WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static OcrLineDto ConvertLine(WinOcr.OcrLine line)
    {
        var words = line.Words.Select(word => word.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        var text = MergeWords(words);

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        foreach (var word in line.Words)
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
            BoundingRect = boundingRect,
        };
    }

    private static IReadOnlyList<OcrLineDto> GroupAndSortLines(IReadOnlyList<OcrLineDto> lines)
    {
        if (lines.Count <= 1)
        {
            return lines;
        }

        var heights = lines
            .Select(line => line.BoundingRect.Height)
            .Where(height => height > 0)
            .ToList();
        var averageHeight = heights.Count == 0 ? 20.0 : heights.Average();
        var yTolerance = averageHeight * 0.5;

        var sorted = lines
            .OrderBy(line => line.BoundingRect.Y)
            .ToList();
        var rows = new List<List<OcrLineDto>>();
        var currentRow = new List<OcrLineDto> { sorted[0] };
        var currentRowYSum = sorted[0].BoundingRect.Y;

        foreach (var line in sorted.Skip(1))
        {
            var currentRowYAverage = currentRowYSum / currentRow.Count;
            if (Math.Abs(line.BoundingRect.Y - currentRowYAverage) <= yTolerance)
            {
                currentRowYSum += line.BoundingRect.Y;
                currentRow.Add(line);
            }
            else
            {
                rows.Add(currentRow);
                currentRow = [line];
                currentRowYSum = line.BoundingRect.Y;
            }
        }
        rows.Add(currentRow);

        return rows
            .SelectMany(row => row.OrderBy(line => line.BoundingRect.X))
            .ToList();
    }

    private static string MergeWords(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return string.Empty;
        }

        var result = new System.Text.StringBuilder(words[0]);
        for (var index = 1; index < words.Count; index++)
        {
            var previous = words[index - 1];
            var current = words[index];
            if (previous.Length == 0 || current.Length == 0)
            {
                result.Append(current);
                continue;
            }

            if (!IsCjkChar(previous[^1]) || !IsCjkChar(current[0]))
            {
                result.Append(' ');
            }

            result.Append(current);
        }

        return result.ToString();
    }

    private static bool IsCjkChar(char character)
    {
        return character is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\u3040' and <= '\u309F'
            or >= '\u30A0' and <= '\u30FF'
            or >= '\uAC00' and <= '\uD7AF'
            or >= '\u3000' and <= '\u303F'
            or >= '\uFF00' and <= '\uFFEF';
    }

    private static OcrLanguageDto? ConvertLanguage(WinOcr.OcrEngine engine)
    {
        var language = engine.RecognizerLanguage;
        return language is null
            ? null
            : new OcrLanguageDto { Tag = language.LanguageTag, DisplayName = language.DisplayName };
    }
}
