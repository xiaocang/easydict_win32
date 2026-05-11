namespace Easydict.OpenVINO.Inference;

/// <summary>
/// Tokens-in, tokens-out abstraction over an NLLB-200 ONNX session. The
/// translation service handles prompt construction and detokenization; this
/// interface only knows about token ids so it can be backed by a fake
/// in tests.
/// </summary>
public interface INllbInferenceEngine
{
    /// <summary>
    /// Runs encoder once + decoder greedy loop, yielding each generated token id
    /// as it's produced (excluding the seed and the final <c>&lt;/s&gt;</c>).
    /// </summary>
    /// <param name="encoderInputIds">
    /// Output of <see cref="INllbTokenizer.EncodeSource"/> — already prefixed
    /// with the source language token and suffixed with EOS.
    /// </param>
    /// <param name="forcedBosTokenId">
    /// Target-language token id that NLLB-200 expects as the second decoder
    /// token (after EOS). Forces translation to that language.
    /// </param>
    /// <param name="maxNewTokens">Hard cap on generated tokens. Typically 256 for sentences, 512 for paragraphs.</param>
    IAsyncEnumerable<int> GenerateAsync(
        IReadOnlyList<int> encoderInputIds,
        int forcedBosTokenId,
        int maxNewTokens,
        CancellationToken cancellationToken);
}
