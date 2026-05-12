using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Easydict.OpenVINO.Inference;

/// <summary>
/// Greedy-decode inference engine for NLLB-200 backed by ONNX Runtime with the
/// OpenVINO Execution Provider. Loads encoder/decoder sessions once and reuses
/// them across translations.
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

    private readonly InferenceSession _encoderSession;
    private readonly InferenceSession _decoderSession;
    private readonly string _encoderInputIdsName;
    private readonly string _encoderAttentionMaskName;
    private readonly string _encoderHiddenStateOutputName;
    private readonly string _decoderInputIdsName;
    private readonly string _decoderEncoderAttentionMaskName;
    private readonly string _decoderEncoderHiddenStatesName;
    private readonly string _decoderLogitsOutputName;
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

        var sessionOptions = BuildSessionOptions(device, precisionHint);

        var encoderSession = new InferenceSession(encoderPath, sessionOptions);
        var decoderSession = new InferenceSession(decoderPath, sessionOptions);

        return new NllbInferenceEngine(encoderSession, decoderSession);
    }

    internal NllbInferenceEngine(InferenceSession encoderSession, InferenceSession decoderSession)
    {
        _encoderSession = encoderSession;
        _decoderSession = decoderSession;

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
            var (encoderHidden, encoderAttentionMask, srcLen) = RunEncoder(encoderInputIds);

            // Decoder greedy loop. NLLB's decoding convention: prime with
            // [</s>, <tgt_lang>]; the model generates the actual translation
            // after this two-token seed (we don't yield the seed to the caller).
            var decoderInput = new List<long>(maxNewTokens + 2)
            {
                EosTokenId,
                forcedBosTokenId,
            };

            for (var step = 0; step < maxNewTokens; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var nextToken = RunDecoderStep(encoderHidden, encoderAttentionMask, srcLen, decoderInput);

                if (nextToken == EosTokenId || nextToken == PadTokenId)
                {
                    break;
                }

                writer.TryWrite(nextToken);
                decoderInput.Add(nextToken);
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
        _encoderSession.Dispose();
        _decoderSession.Dispose();
    }

    // ── Encoder ─────────────────────────────────────────────────────────

    private (DenseTensor<float> hidden, DenseTensor<long> attentionMask, int srcLen) RunEncoder(
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

        using var outputs = _encoderSession.Run(inputs);
        var hidden = outputs
            .First(o => o.Name == _encoderHiddenStateOutputName)
            .AsTensor<float>();

        // Copy out so we can reuse across decoder steps without re-running encoder.
        var hiddenDense = hidden.ToDenseTensor();
        return (hiddenDense, attentionTensor, srcLen);
    }

    // ── Decoder (single greedy step) ────────────────────────────────────

    private int RunDecoderStep(
        DenseTensor<float> encoderHidden,
        DenseTensor<long> encoderAttentionMask,
        int srcLen,
        IReadOnlyList<long> decoderInputSoFar)
    {
        var tgtLen = decoderInputSoFar.Count;
        var decoderInputData = decoderInputSoFar.ToArray();
        var decoderInputTensor = new DenseTensor<long>(decoderInputData, new[] { 1, tgtLen });

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

        // logits shape: [1, tgtLen, vocab_size]. Argmax over the LAST position's
        // vocab vector. We materialize a DenseTensor and walk its row-major
        // buffer directly — using Tensor.GetValue(int) only worked accidentally
        // (it relies on a row-major linear layout that Tensor<T> doesn't actually
        // promise) and isn't future-proof. A span walk is also faster than
        // calling the 3D indexer in a 256k-iteration loop.
        var dense = logits.ToDenseTensor();
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

    // ── Helpers ─────────────────────────────────────────────────────────

    private static SessionOptions BuildSessionOptions(OpenVINODevice device, string? precisionHint)
    {
        var options = new SessionOptions();

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
            Debug.WriteLine($"[NllbInferenceEngine] OpenVINO EP appended (device={device}).");
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
