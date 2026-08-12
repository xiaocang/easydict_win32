using Easydict.SidecarClient.Protocol;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Easydict.Workers.Ocr;

internal sealed class PpOcrV6ModelException : Exception
{
    public PpOcrV6ModelException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed class PpOcrV6Pipeline : IDisposable
{
    private const float DetectionThreshold = 0.2f;
    private const float BoxThreshold = 0.45f;
    private const float UnclipRatio = 1.4f;
    private const int DetectionLimitSide = 64;
    private const int DetectionMaxSide = 4000;
    private const int RecognitionHeight = 48;
    private const int RecognitionMaxWidth = 3200;

    private readonly string _modelId;
    private readonly int _threadCount;
    private readonly bool _useGpu;
    private readonly PpOcrV6ModelStore _store;
    private InferenceSession? _detector;
    private InferenceSession? _recognizer;
    private string _detectorInputName = string.Empty;
    private string _recognizerInputName = string.Empty;
    private IReadOnlyList<string> _characters = [];
    private bool _disposed;

    public PpOcrV6Pipeline(
        string modelId,
        int threadCount,
        bool useGpu = false,
        PpOcrV6ModelStore? store = null)
    {
        _modelId = modelId;
        _threadCount = Math.Clamp(threadCount, PpOcrV6ModelCatalog.MinThreadCount, PpOcrV6ModelCatalog.MaxThreadCount);
        _useGpu = useGpu;
        _store = store ?? new PpOcrV6ModelStore();
    }

    public async Task<OcrResultDto> RecognizeAsync(
        byte[] pixelData,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateImage(pixelData, width, height);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var detectionInput = PrepareDetectionInput(pixelData, width, height);
        var detectionOutput = RunSession(
            _detector!,
            _detectorInputName,
            detectionInput.Data,
            detectionInput.Shape);
        var boxes = DecodeDetection(
            detectionOutput.Data,
            detectionOutput.Shape,
            width,
            height,
            detectionInput.InputWidth,
            detectionInput.InputHeight);

        var lines = new List<OcrLineDto>(boxes.Count);
        foreach (var box in boxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var crop = ExtractCrop(pixelData, width, height, box);
            var recognitionInput = PrepareRecognitionInput(crop);
            var recognitionOutput = RunSession(
                _recognizer!,
                _recognizerInputName,
                recognitionInput.Data,
                recognitionInput.Shape);
            var (text, confidence) = DecodeRecognition(
                recognitionOutput.Data,
                recognitionOutput.Shape);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            lines.Add(new OcrLineDto
            {
                Text = text,
                Confidence = confidence,
                BoundingRect = new OcrRectDto(box.X, box.Y, box.Width, box.Height),
            });
        }

        var ordered = lines
            .OrderBy(line => line.BoundingRect.Y)
            .ThenBy(line => line.BoundingRect.X)
            .ToList();
        return new OcrResultDto
        {
            Text = string.Join(Environment.NewLine, ordered.Select(line => line.Text)),
            Lines = ordered,
            Engine = OcrEngines.PpOcrV6,
            ModelId = _modelId,
        };
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_detector is not null && _recognizer is not null)
        {
            return;
        }

        var state = _store.GetStateBySize(_modelId);
        if (state == PpOcrV6ModelState.Missing)
        {
            throw new PpOcrV6ModelException(WorkerErrorCodes.ModelMissing, $"PP-OCRv6 model '{_modelId}' is not installed.");
        }
        if (state != PpOcrV6ModelState.Installed)
        {
            throw new PpOcrV6ModelException(WorkerErrorCodes.ModelInvalid, $"PP-OCRv6 model '{_modelId}' failed integrity validation.");
        }

        var paths = _store.GetPaths(_modelId);
        _characters = await LoadCharacterDictionaryAsync(paths.RecognizerConfig, cancellationToken)
            .ConfigureAwait(false);
        if (_characters.Count == 0)
        {
            throw new PpOcrV6ModelException(WorkerErrorCodes.ModelInvalid, "PP-OCRv6 recognition dictionary is empty.");
        }

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = _threadCount,
            InterOpNumThreads = 1,
        };
        if (_useGpu)
        {
            try
            {
                options.AppendExecutionProvider_DML(0);
            }
            catch (Exception ex)
            {
                throw new PpOcrV6ModelException(
                    WorkerErrorCodes.GpuUnavailable,
                    $"DirectML GPU provider is unavailable: {ex.Message}");
            }
        }
        try
        {
            _detector = new InferenceSession(paths.DetectorModel, options);
            _recognizer = new InferenceSession(paths.RecognizerModel, options);
            _detectorInputName = ValidateSession(_detector, "detector");
            _recognizerInputName = ValidateSession(_recognizer, "recognizer");
            ValidateRecognizerClasses(_recognizer);
        }
        catch (PpOcrV6ModelException)
        {
            ReleaseSessions();
            throw;
        }
        catch (Exception ex)
        {
            ReleaseSessions();
            throw new PpOcrV6ModelException(WorkerErrorCodes.RuntimeMissing, $"Unable to load PP-OCRv6 ONNX sessions: {ex.Message}");
        }
    }

    private void ValidateRecognizerClasses(InferenceSession session)
    {
        var output = session.OutputMetadata.Values.FirstOrDefault(metadata =>
            metadata.Dimensions.Length >= 3 && metadata.Dimensions[^1] > 0);
        if (output is not null && output.Dimensions[^1] != _characters.Count + 1)
        {
            throw new PpOcrV6ModelException(
                WorkerErrorCodes.ModelInvalid,
                $"PP-OCRv6 recognizer dictionary has {_characters.Count} characters but the model exposes {output.Dimensions[^1] - 1} classes.");
        }
    }

    private void ReleaseSessions()
    {
        _detector?.Dispose();
        _recognizer?.Dispose();
        _detector = null;
        _recognizer = null;
    }

    private static string ValidateSession(InferenceSession session, string name)
    {
        if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count == 0)
        {
            throw new PpOcrV6ModelException(WorkerErrorCodes.ModelInvalid, $"PP-OCRv6 {name} session has an unsupported tensor contract.");
        }

        var input = session.InputMetadata.Single();
        if (input.Value.ElementType != typeof(float))
        {
            throw new PpOcrV6ModelException(WorkerErrorCodes.ModelInvalid, $"PP-OCRv6 {name} input must be float32.");
        }

        return input.Key;
    }

    private static async Task<IReadOnlyList<string>> LoadCharacterDictionaryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var result = new List<string>();
        var inDictionary = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("character_dict:", StringComparison.Ordinal))
            {
                inDictionary = true;
                continue;
            }
            if (!inDictionary)
            {
                continue;
            }
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (line.Length - trimmed.Length <= 2)
                {
                    break;
                }
                continue;
            }

            var value = trimmed[2..].TrimStart();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }
            else
            {
                value = value.Trim();
            }
            result.Add(value);
        }

        if (result.Count == 0 || result[^1] != " ")
        {
            result.Add(" ");
        }

        return result;
    }

    private static void ValidateImage(byte[] pixelData, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "OCR image dimensions must be positive.");
        }
        var expected = checked(width * height * 4);
        if (pixelData.Length < expected)
        {
            throw new ArgumentException("OCR pixel buffer is shorter than the requested image dimensions.", nameof(pixelData));
        }
    }

    private static DetectionInput PrepareDetectionInput(byte[] pixels, int width, int height)
    {
        var ratio = Math.Min(width, height) < DetectionLimitSide
            ? DetectionLimitSide / (double)Math.Min(width, height)
            : 1.0;
        var scaledWidth = Math.Max(1, (int)Math.Round(width * ratio, MidpointRounding.ToEven));
        var scaledHeight = Math.Max(1, (int)Math.Round(height * ratio, MidpointRounding.ToEven));
        if (Math.Max(scaledWidth, scaledHeight) > DetectionMaxSide)
        {
            ratio = DetectionMaxSide / (double)Math.Max(scaledWidth, scaledHeight);
            scaledWidth = Math.Max(1, (int)Math.Round(scaledWidth * ratio, MidpointRounding.ToEven));
            scaledHeight = Math.Max(1, (int)Math.Round(scaledHeight * ratio, MidpointRounding.ToEven));
        }

        var inputWidth = RoundToMultipleOf32(scaledWidth);
        var inputHeight = RoundToMultipleOf32(scaledHeight);
        var channelSize = inputWidth * inputHeight;
        var data = new float[3 * channelSize];
        for (var y = 0; y < inputHeight; y++)
        {
            var sourceY = Math.Min(height - 1, (int)((y + 0.5) * height / inputHeight));
            for (var x = 0; x < inputWidth; x++)
            {
                var sourceX = Math.Min(width - 1, (int)((x + 0.5) * width / inputWidth));
                var source = (sourceY * width + sourceX) * 4;
                var destination = y * inputWidth + x;
                data[destination] = Normalize(pixels[source], 0);
                data[channelSize + destination] = Normalize(pixels[source + 1], 1);
                data[2 * channelSize + destination] = Normalize(pixels[source + 2], 2);
            }
        }

        return new DetectionInput(data, [1, 3, inputHeight, inputWidth], inputWidth, inputHeight);
    }

    private static readonly float[] DetectionMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] DetectionStd = [0.229f, 0.224f, 0.225f];

    private static float Normalize(byte value, int channel)
    {
        return (value / 255f - DetectionMean[channel]) / DetectionStd[channel];
    }

    private static int RoundToMultipleOf32(int value)
    {
        return Math.Max(32, (int)Math.Round(value / 32.0, MidpointRounding.ToEven) * 32);
    }

    private static List<PpBox> DecodeDetection(
        float[] output,
        int[] shape,
        int originalWidth,
        int originalHeight,
        int inputWidth,
        int inputHeight)
    {
        var mapHeight = shape.Length >= 4 ? shape[^2] : shape.Length == 3 ? shape[^2] : 0;
        var mapWidth = shape.Length >= 3 ? shape[^1] : 0;
        if (mapHeight <= 0 || mapWidth <= 0 || output.Length < mapHeight * mapWidth)
        {
            throw new PpOcrV6ModelException(WorkerErrorCodes.ModelInvalid, "PP-OCRv6 detector output shape is unsupported.");
        }

        var probabilities = new float[mapHeight * mapWidth];
        for (var i = 0; i < probabilities.Length; i++)
        {
            var value = output[i];
            probabilities[i] = value is >= 0 and <= 1 ? value : 1f / (1f + MathF.Exp(-value));
        }

        var visited = new bool[probabilities.Length];
        var queue = new int[probabilities.Length];
        var boxes = new List<PpBox>();
        for (var y = 0; y < mapHeight; y++)
        {
            for (var x = 0; x < mapWidth; x++)
            {
                var start = y * mapWidth + x;
                if (visited[start] || probabilities[start] < DetectionThreshold)
                {
                    continue;
                }

                var head = 0;
                var tail = 0;
                queue[tail++] = start;
                visited[start] = true;
                var minX = x;
                var maxX = x;
                var minY = y;
                var maxY = y;
                var area = 0;
                var score = 0f;
                while (head < tail)
                {
                    var current = queue[head++];
                    var currentX = current % mapWidth;
                    var currentY = current / mapWidth;
                    minX = Math.Min(minX, currentX);
                    maxX = Math.Max(maxX, currentX);
                    minY = Math.Min(minY, currentY);
                    maxY = Math.Max(maxY, currentY);
                    area++;
                    score += probabilities[current];
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var nx = currentX + dx;
                            var ny = currentY + dy;
                            if (nx < 0 || nx >= mapWidth || ny < 0 || ny >= mapHeight) continue;
                            var next = ny * mapWidth + nx;
                            if (!visited[next] && probabilities[next] >= DetectionThreshold)
                            {
                                visited[next] = true;
                                queue[tail++] = next;
                            }
                        }
                    }
                }

                if (maxX - minX < 3 || maxY - minY < 3 || score / area < BoxThreshold)
                {
                    continue;
                }

                var distance = (maxX - minX + 1) * (maxY - minY + 1) * UnclipRatio /
                               (2f * ((maxX - minX + 1) + (maxY - minY + 1)));
                var scaleX = originalWidth / (double)inputWidth;
                var scaleY = originalHeight / (double)inputHeight;
                var x1 = Math.Clamp((minX - distance) * inputWidth / mapWidth * scaleX, 0, originalWidth);
                var y1 = Math.Clamp((minY - distance) * inputHeight / mapHeight * scaleY, 0, originalHeight);
                var x2 = Math.Clamp((maxX + 1 + distance) * inputWidth / mapWidth * scaleX, 0, originalWidth);
                var y2 = Math.Clamp((maxY + 1 + distance) * inputHeight / mapHeight * scaleY, 0, originalHeight);
                boxes.Add(new PpBox(x1, y1, x2 - x1, y2 - y1));
            }
        }

        return boxes;
    }

    private static ImageCrop ExtractCrop(byte[] pixels, int imageWidth, int imageHeight, PpBox box)
    {
        var left = Math.Clamp((int)Math.Floor(box.X), 0, imageWidth - 1);
        var top = Math.Clamp((int)Math.Floor(box.Y), 0, imageHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(box.X + box.Width), left + 1, imageWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(box.Y + box.Height), top + 1, imageHeight);
        var width = right - left;
        var height = bottom - top;
        var source = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var src = ((top + y) * imageWidth + left + x) * 4;
                var dst = (y * width + x) * 3;
                source[dst] = pixels[src];
                source[dst + 1] = pixels[src + 1];
                source[dst + 2] = pixels[src + 2];
            }
        }

        if (height <= width * 1.5)
        {
            return new ImageCrop(source, width, height);
        }

        var rotated = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var outputX = y;
                var outputY = width - 1 - x;
                var src = (y * width + x) * 3;
                var dst = (outputY * height + outputX) * 3;
                rotated[dst] = source[src];
                rotated[dst + 1] = source[src + 1];
                rotated[dst + 2] = source[src + 2];
            }
        }
        return new ImageCrop(rotated, height, width);
    }

    private static RecognitionInput PrepareRecognitionInput(ImageCrop crop)
    {
        var targetWidth = Math.Clamp(
            (int)Math.Ceiling(RecognitionHeight * (double)crop.Width / Math.Max(1, crop.Height)),
            1,
            RecognitionMaxWidth);
        var channelSize = RecognitionHeight * targetWidth;
        var data = new float[3 * channelSize];
        for (var y = 0; y < RecognitionHeight; y++)
        {
            var sourceY = Math.Min(crop.Height - 1, (int)((y + 0.5) * crop.Height / RecognitionHeight));
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Min(crop.Width - 1, (int)((x + 0.5) * crop.Width / targetWidth));
                var source = (sourceY * crop.Width + sourceX) * 3;
                var destination = y * targetWidth + x;
                data[destination] = crop.Pixels[source + 2] / 127.5f - 1f;
                data[channelSize + destination] = crop.Pixels[source + 1] / 127.5f - 1f;
                data[2 * channelSize + destination] = crop.Pixels[source] / 127.5f - 1f;
            }
        }
        return new RecognitionInput(data, [1, 3, RecognitionHeight, targetWidth]);
    }

    private (float[] Data, int[] Shape) RunSession(
        InferenceSession session,
        string inputName,
        float[] data,
        int[] shape)
    {
        var tensor = new DenseTensor<float>(data, shape);
        var input = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        using var results = session.Run([input]);
        var output = results.First().AsTensor<float>();
        return (output.ToArray(), output.Dimensions.ToArray());
    }

    private (string Text, float Confidence) DecodeRecognition(float[] output, int[] shape)
    {
        if (shape.Length != 3 || shape[0] != 1 || shape[2] <= 1)
        {
            throw new PpOcrV6ModelException(WorkerErrorCodes.ModelInvalid, "PP-OCRv6 recognizer output shape is unsupported.");
        }

        var timeSteps = shape[1];
        var classes = shape[2];
        var previous = -1;
        var confidenceSum = 0f;
        var confidenceCount = 0;
        var text = new System.Text.StringBuilder();
        for (var time = 0; time < timeSteps; time++)
        {
            var offset = time * classes;
            var bestClass = 0;
            var bestValue = output[offset];
            var maxLogit = bestValue;
            for (var cls = 1; cls < classes; cls++)
            {
                var logit = output[offset + cls];
                if (logit > bestValue)
                {
                    bestClass = cls;
                    bestValue = logit;
                }
                maxLogit = MathF.Max(maxLogit, logit);
            }

            var expSum = 0f;
            for (var cls = 0; cls < classes; cls++)
            {
                expSum += MathF.Exp(output[offset + cls] - maxLogit);
            }
            var bestProbability = MathF.Exp(bestValue - maxLogit) / expSum;

            if (bestClass != 0 && bestClass != previous)
            {
                var characterIndex = bestClass - 1;
                if (characterIndex >= 0 && characterIndex < _characters.Count)
                {
                    text.Append(_characters[characterIndex]);
                    confidenceSum += bestProbability;
                    confidenceCount++;
                }
            }
            previous = bestClass;
        }

        return (text.ToString(), confidenceCount == 0 ? 0f : confidenceSum / confidenceCount);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseSessions();
    }

    private sealed record DetectionInput(float[] Data, int[] Shape, int InputWidth, int InputHeight);
    private sealed record RecognitionInput(float[] Data, int[] Shape);
    private sealed record ImageCrop(byte[] Pixels, int Width, int Height);
    private readonly record struct PpBox(double X, double Y, double Width, double Height);
}
