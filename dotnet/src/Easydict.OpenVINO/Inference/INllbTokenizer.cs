namespace Easydict.OpenVINO.Inference;

/// <summary>
/// Tokenizer abstraction for NLLB-200. Hides SentencePiece BPE plus the
/// HuggingFace "added tokens" layer (FLORES-200 language codes and special
/// tokens like <c>&lt;s&gt;</c>, <c>&lt;pad&gt;</c>) so tests can stub it.
/// </summary>
public interface INllbTokenizer
{
    /// <summary>Token id for <c>&lt;s&gt;</c>.</summary>
    int BosTokenId { get; }

    /// <summary>Token id for <c>&lt;pad&gt;</c>.</summary>
    int PadTokenId { get; }

    /// <summary>Token id for <c>&lt;/s&gt;</c> (also used as decoder seed).</summary>
    int EosTokenId { get; }

    /// <summary>Token id for <c>&lt;unk&gt;</c>.</summary>
    int UnkTokenId { get; }

    /// <summary>
    /// Encodes a source string for NLLB-200's encoder. Returns
    /// <c>[src_lang_id] + spm_tokens + [eos_id]</c>.
    /// </summary>
    IReadOnlyList<int> EncodeSource(string text, string srcFloresCode);

    /// <summary>
    /// Decodes a sequence of token ids back to text, stripping special tokens
    /// (language codes, <c>&lt;s&gt;</c>, <c>&lt;/s&gt;</c>, <c>&lt;pad&gt;</c>).
    /// </summary>
    string Decode(IReadOnlyList<int> tokenIds);

    /// <summary>
    /// Decodes a single token id. Returns null when the token is a special token
    /// that should not appear in user-facing output. Used during streaming so
    /// each generated id can be surfaced incrementally.
    /// </summary>
    string? DecodeSingle(int tokenId);

    /// <summary>
    /// Returns the token id for a FLORES-200 language code (e.g. <c>eng_Latn</c>).
    /// Throws if the model wasn't loaded with that language as an added token.
    /// </summary>
    int GetLanguageTokenId(string floresCode);
}
