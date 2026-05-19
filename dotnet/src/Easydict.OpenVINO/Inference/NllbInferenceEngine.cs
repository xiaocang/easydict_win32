using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Easydict.OpenVINO.Inference;

/// <summary>
/// Greedy-decode inference engine for NLLB-200 backed by ONNX Runtime. The
/// encoder is routed through the OpenVINO Execution Provider when available;
/// the decoder currently uses the default CPU EP because the dynamic
/// autoregressive decoder subgraphs can trigger OpenVINO EP output-name
/// mismatches on ORT/OpenVINO 1.21.
///
/// First-pass design notes:
///  - Batch size 1 only.
///  - Non-merged decoder (no KV cache) for now — every step re-runs the decoder
///    on the full prefix. O(N²) tokens-out but minimal plumbing. Switching to
///    <c>decoder_model_merged_quantized.onnx</c> is a future optimization.
///  - Greedy argmax sampling. No beam search.
/// </summary>
public sealed class NllbInferenceEngine : INllbInferenceEngine, IDisposable
{
    private const int EncoderHiddenDim = 1024; // NLLB-200-distilled-600M
    private const int PadTokenId = 1;
    private const int EosTokenId = 2;
    private static readonly object OpenVinoRuntimeFallbackLock = new();
    private static readonly HashSet<OpenVINODevice> OpenVinoRuntimeDisabledDevices = [];

    private readonly InferenceSession _encoderSession;
    private readonly InferenceSession _decoderSession;
    private readonly Func<InferenceSession>? _encoderCpuFallbackFactory;
    private readonly OpenVINODevice _encoderDevice;
    private readonly string _encoderInputIdsName;
    private readonly string _encoderAttentionMaskName;
    private readonly string _encoderHiddenStateOutputName;
    private readonly string _decoderInputIdsName;
    private readonly string _decoderEncoderAttentionMaskName;
    private readonly string _decoderEncoderHiddenStatesName;
    private readonly string _decoderLogitsOutputName;
    private readonly object _encoderFallbackLock = new();
    private InferenceSession? _encoderCpuFallbackSession;
    private bool _useEncoderCpuFallback;
    private bool _primaryEncoderSessionDisposed;
    private bool _disposed;

    public static NllbInferenceEngine Load(
        string modelDirectory,
        OpenVINODevice device,
        string? precisionHint = null)
    {
        var encoderPath = Path.Combine(modelDirectory, "encoder_model_quantized.onnx");
        var decoderPath = Path.Combine(modelDirectory, "decoder_model_quantized.onnx");

        if (!File.Exists(encoderPath))
        {
            throw new FileNotFoundException($"NLLB encoder not found at '{encoderPath}'", encoderPath);
        }
        if (!File.Exists(decoderPath))
        {
            throw new FileNotFoundException($"NLLB decoder not found at '{decoderPath}'", decoderPath);
        }

        var encoderSession = CreateEncoderSession(encoderPath, device, precisionHint);
        var decoderSession = new InferenceSession(decoderPath, BuildCpuSessionOptions());

        return new NllbInferenceEngine(
            encoderSession,
            decoderSession,
            () => new InferenceSession(encoderPath, BuildCpuSessionOptions()),
            device);
    }

    internal NllbInferenceEngine(
        InferenceSession encoderSession,
        InferenceSession decoderSession,
        Func<InferenceSession>? encoderCpuFallbackFactory = null,
        OpenVINODevice encoderDevice = OpenVINODevice.CPU)
    {
        _encoderSession = encoderSession;
        _decoderSession = decoderSession;
        _encoderCpuFallbackFactory = encoderCpuFallbackFactory;
        _encoderDevice = encoderDevice;

        // Resolve ONNX I/O names defensively — different Optimum versions have
        // slightly different naming for NLLB exports.
        _encoderInputIdsName            = ResolveInputName(_encoderSession, "input_ids");
        _encoderAttentionMaskName       = ResolveInputName(_encoderSession, "attention_mask");
        _encoderHiddenStateOutputName   = ResolveOutputName(_encoderSession, "last_hidden_state");
        _decoderInputIdsName            = ResolveInputName(_decoderSession, "input_ids");
        _decoderEncoderAttentionMaskName = ResolveInputName(_decoderSession, "encoder_attention_mask");
        _decoderEncoderHiddenStatesName  = ResolveInputName(_decoderSession, "encoder_hidden_states");
        _decoderLogitsOutputName        = ResolveOutputName(_decoderSession, "logits");
    }

    public IAsyncEnumerable<int> GenerateAsync(
        IReadOnlyList<int> encoderInputIds,
        int forcedBosTokenId,
        int maxNewTokens,
        CancellationToken cancellationToken)
    {
        if (encoderInputIds.Count == 0)
        {
            return EmptyTokenStream();
        }

        // Single background worker that runs the encoder once and the decoder
        // loop to completion, writing tokens into a channel. The previous
        // implementation queued a fresh Task.Run for every decoder step (one
        // dispatch per token, ~256/translation), which spent measurable time
        // in thread-pool scheduling. With the channel pattern the producer is
        // one task and the consumer's `await` only resumes when a token is
        // actually available — no per-step round trips through the scheduler.
        var channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _ = Task.Run(
            () => RunDecodeAsync(
                encoderInputIds,
                forcedBosTokenId,
                maxNewTokens,
                channel.Writer,
                cancellationToken),
            cancellationToken);

        return ReadChannelAsync(channel.Reader, cancellationToken);
    }

    private Task RunDecodeAsync(
        IReadOnlyList<int> encoderInputIds,
        int forcedBosTokenId,
        int maxNewTokens,
        ChannelWriter<int> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Encoder forward (once per translation).
            using var encoder = RunEncoder(encoderInputIds);

            // Decoder greedy loop. NLLB's decoding convention: prime with
            // [</s>, <tgt_lang>]; the model generates the actual translation
            // after this two-token seed (we don't yield the seed to the caller).
            var decoderInput = new long[maxNewTokens + 2];
            decoderInput[0] = EosTokenId;
            decoderInput[1] = forcedBosTokenId;
            var decoderLength = 2;

            for (var step = 0; step < maxNewTokens; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var nextToken = RunDecoderStep(
                    encoder.Hidden,
                    encoder.AttentionMask,
                    encoder.SrcLen,
                    decoderInput.AsMemory(0, decoderLength));

                if (nextToken == EosTokenId || nextToken == PadTokenId)
                {
                    break;
                }

                writer.TryWrite(nextToken);
                decoderInput[decoderLength++] = nextToken;
            }

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }

        return Task.CompletedTask;
    }

    private static async IAsyncEnumerable<int> ReadChannelAsync(
        ChannelReader<int> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var token in reader.ReadAllAsync(cancellationToken))
        {
            yield return token;
        }
    }

    private static async IAsyncEnumerable<int> EmptyTokenStream()
    {
        // Used when encoderInputIds is empty — no encoder/decoder pass at all.
        // The `await` is a no-op required to keep this a valid async iterator.
        await Task.CompletedTask;
        yield break;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_encoderFallbackLock)
        {
            DisposePrimaryEncoderSession();
            _encoderCpuFallbackSession?.Dispose();
            _encoderCpuFallbackSession = null;
        }
        _decoderSession.Dispose();
    }

    // ── Encoder ─────────────────────────────────────────────────────────

    private EncoderRunResult RunEncoder(
        IReadOnlyList<int> encoderInputIds)
    {
        var srcLen = encoderInputIds.Count;
        var inputIdsData = new long[srcLen];
        var attentionData = new long[srcLen];
        for (var i = 0; i < srcLen; i++)
        {
            inputIdsData[i] = encoderInputIds[i];
            attentionData[i] = 1L;
        }

        var inputIdsShape = new[] { 1, srcLen };
        var inputIdsTensor = new DenseTensor<long>(inputIdsData, inputIdsShape);
        var attentionTensor = new DenseTensor<long>(attentionData, inputIdsShape);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_encoderInputIdsName, inputIdsTensor),
            NamedOnnxValue.CreateFromTensor(_encoderAttentionMaskName, attentionTensor),
        };

        try
        {
            lock (_encoderFallbackLock)
            {
                if (_useEncoderCpuFallback)
                {
                    return RunEncoderWithSession(GetEncoderCpuFallbackSession(), inputs, attentionTensor, srcLen);
                }

                return RunEncoderWithSession(_encoderSession, inputs, attentionTensor, srcLen);
            }
        }
        catch (OnnxRuntimeException ex) when (!_useEncoderCpuFallback && IsOpenVinoRuntimeFailure(ex) && _encoderCpuFallbackFactory is not null)
        {
            lock (_encoderFallbackLock)
            {
                Debug.WriteLine($"[NllbInferenceEngine] OpenVINO encoder run failed ({ex.Message}); switching encoder to CPU EP.");
                RecordOpenVinoEncoderRuntimeFailure(_encoderDevice, ex);
                _useEncoderCpuFallback = true;
                DisposePrimaryEncoderSession();
                return RunEncoderWithSession(GetEncoderCpuFallbackSession(), inputs, attentionTensor, srcLen);
            }
        }
    }

    private InferenceSession GetEncoderCpuFallbackSession()
    {
        if (_encoderCpuFallbackSession is not null)
        {
            return _encoderCpuFallbackSession;
        }

        if (_encoderCpuFallbackFactory is null)
        {
            return _encoderSession;
        }

        lock (_encoderFallbackLock)
        {
            _encoderCpuFallbackSession ??= _encoderCpuFallbackFactory();
            return _encoderCpuFallbackSession;
        }
    }

    private void DisposePrimaryEncoderSession()
    {
        if (_primaryEncoderSessionDisposed)
        {
            return;
        }

        _primaryEncoderSessionDisposed = true;
        try
        {
            _encoderSession.Dispose();
            Debug.WriteLine("[NllbInferenceEngine] Primary encoder session disposed.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NllbInferenceEngine] Failed to dispose primary encoder session: {ex.Message}");
        }
    }

    private EncoderRunResult RunEncoderWithSession(
        InferenceSession session,
        IReadOnlyCollection<NamedOnnxValue> inputs,
        DenseTensor<long> attentionTensor,
        int srcLen)
    {
        using var outputs = session.Run(inputs);
        var hidden = outputs
            .First(o => o.Name == _encoderHiddenStateOutputName)
            .AsTensor<float>();

        // Copy out so we can reuse across decoder steps without re-running encoder.
        var hiddenOwner = PooledDenseTensor<float>.CopyFrom(hidden);
        return new EncoderRunResult(hiddenOwner, attentionTensor, srcLen);
    }

    // ── Decoder (single greedy step) ────────────────────────────────────

    private int RunDecoderStep(
        DenseTensor<float> encoderHidden,
        DenseTensor<long> encoderAttentionMask,
        int srcLen,
        Memory<long> decoderInputSoFar)
    {
        var tgtLen = decoderInputSoFar.Length;
        var decoderInputTensor = new DenseTensor<long>(decoderInputSoFar, new[] { 1, tgtLen });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_decoderEncoderAttentionMaskName, encoderAttentionMask),
            NamedOnnxValue.CreateFromTensor(_decoderInputIdsName, decoderInputTensor),
            NamedOnnxValue.CreateFromTensor(_decoderEncoderHiddenStatesName, encoderHidden),
        };

        using var outputs = _decoderSession.Run(inputs);
        var logits = outputs
            .First(o => o.Name == _decoderLogitsOutputName)
            .AsTensor<float>();

        // logits shape: [1, tgtLen, vocab_size]. Prefer direct dense-buffer access;
        // fall back to materialization only if ORT returns a non-dense tensor.
        var dense = logits as DenseTensor<float> ?? logits.ToDenseTensor();
        var buffer = dense.Buffer.Span;
        var vocabSize = (int)dense.Dimensions[2];
        var lastPosBase = (tgtLen - 1) * vocabSize;

        int bestId = 0;
        float bestLogit = float.NegativeInfinity;
        for (var v = 0; v < vocabSize; v++)
        {
            var logit = buffer[lastPosBase + v];
            if (logit > bestLogit)
            {
                bestLogit = logit;
                bestId = v;
            }
        }

        return bestId;
    }

    private sealed class EncoderRunResult : IDisposable
    {
        private readonly PooledDenseTensor<float> _hiddenOwner;

        public EncoderRunResult(
            PooledDenseTensor<float> hiddenOwner,
            DenseTensor<long> attentionMask,
            int srcLen)
        {
            _hiddenOwner = hiddenOwner;
            AttentionMask = attentionMask;
            SrcLen = srcLen;
        }

        public DenseTensor<float> Hidden => _hiddenOwner.Tensor;
        public DenseTensor<long> AttentionMask { get; }
        public int SrcLen { get; }

        public void Dispose() => _hiddenOwner.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static InferenceSession CreateEncoderSession(
        string encoderPath,
        OpenVINODevice device,
        string? precisionHint)
    {
        if (device == OpenVINODevice.CPU || IsOpenVinoEncoderRuntimeDisabled(device))
        {
            if (device != OpenVINODevice.CPU)
            {
                Debug.WriteLine($"[NllbInferenceEngine] OpenVINO encoder runtime disabled for {device}; using CPU EP.");
            }

            return new InferenceSession(encoderPath, BuildCpuSessionOptions());
        }

        try
        {
            return new InferenceSession(encoderPath, BuildOpenVinoSessionOptions(device, precisionHint));
        }
        catch (Exception ex)
        {
            // Device not present, EP unavailable, or model unsupported by the
            // requested OpenVINO target. Fall back to CPU so the provider still
            // produces a translation instead of failing at load time.
            Debug.WriteLine($"[NllbInferenceEngine] OpenVINO encoder session unavailable ({ex.Message}); falling back to CPU EP.");
            return new InferenceSession(encoderPath, BuildCpuSessionOptions());
        }
    }

    private static SessionOptions BuildOpenVinoSessionOptions(OpenVINODevice device, string? precisionHint)
    {
        var options = new SessionOptions
        {
            // Known OpenVINO EP runtime incompatibilities are caught and handled
            // below. Keep ORT from printing scary error logs for the expected
            // first-chance failure before we switch to CPU.
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL,
        };

        // OpenVINO EP options: device_type controls hardware target.
        // precision hint: FP16 is the sweet spot for NPU; INT8 is set by the model itself.
        var ovOptions = new Dictionary<string, string>
        {
            ["device_type"] = device.ToOpenVINOString(),
        };
        if (!string.IsNullOrWhiteSpace(precisionHint))
        {
            ovOptions["precision"] = precisionHint;
        }

        try
        {
            options.AppendExecutionProvider("OpenVINO", ovOptions);
            Debug.WriteLine($"[NllbInferenceEngine] OpenVINO EP appended for encoder (device={device}).");
        }
        catch (Exception ex)
        {
            // OpenVINO EP unavailable on this build/host — fall back to CPU EP so we
            // still produce a translation, just slower. The settings page will reflect
            // the device drop via OpenVINOTranslationService telemetry.
            Debug.WriteLine($"[NllbInferenceEngine] OpenVINO EP unavailable ({ex.Message}); falling back to default EPs.");
        }

        return options;
    }

    private static SessionOptions BuildCpuSessionOptions()
    {
        return new SessionOptions();
    }

    internal static bool IsOpenVinoRuntimeFailure(Exception ex)
    {
        return ex.Message.Contains("OpenVINO-EP", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("OpenVINOExecutionProvider", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("openvino_ep", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsOpenVinoEncoderRuntimeDisabled(OpenVINODevice device)
    {
        lock (OpenVinoRuntimeFallbackLock)
        {
            return OpenVinoRuntimeDisabledDevices.Contains(device);
        }
    }

    internal static void RecordOpenVinoEncoderRuntimeFailure(OpenVINODevice device, Exception ex)
    {
        if (!IsOpenVinoRuntimeFailure(ex) || device == OpenVINODevice.CPU)
        {
            return;
        }

        lock (OpenVinoRuntimeFallbackLock)
        {
            OpenVinoRuntimeDisabledDevices.Add(device);
        }
    }

    internal static void ResetOpenVinoEncoderRuntimeFailuresForTests()
    {
        lock (OpenVinoRuntimeFallbackLock)
        {
            OpenVinoRuntimeDisabledDevices.Clear();
        }
    }

    private static string ResolveInputName(InferenceSession session, string expected)
    {
        if (session.InputMetadata.ContainsKey(expected))
        {
            return expected;
        }

        // Some Optimum exports prefix names. Fall back to the first input that
        // matches by suffix.
        var match = session.InputMetadata.Keys.FirstOrDefault(
            k => k.EndsWith(expected, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        throw new InvalidOperationException(
            $"Expected ONNX input '{expected}' not found. Session inputs: {string.Join(", ", session.InputMetadata.Keys)}.");
    }

    private static string ResolveOutputName(InferenceSession session, string expected)
    {
        if (session.OutputMetadata.ContainsKey(expected))
        {
            return expected;
        }

        var match = session.OutputMetadata.Keys.FirstOrDefault(
            k => k.EndsWith(expected, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        throw new InvalidOperationException(
            $"Expected ONNX output '{expected}' not found. Session outputs: {string.Join(", ", session.OutputMetadata.Keys)}.");
    }
}
