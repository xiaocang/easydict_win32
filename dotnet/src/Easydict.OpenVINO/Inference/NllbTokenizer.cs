using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace Easydict.OpenVINO.Inference;

/// <summary>
/// NLLB-200 tokenizer that combines:
///  1. A SentencePiece tokenizer (loaded via <see cref="LlamaTokenizer.Create(Stream, bool, bool, IReadOnlyDictionary{string, int}?)"/>,
///     which transparently handles both BPE and Unigram SentencePiece models)
///     for the base 256 000-token vocabulary stored in <c>sentencepiece.bpe.model</c>;
///  2. an "added tokens" table parsed from <c>tokenizer.json</c> — these are the
///     202 language-code tokens (e.g. <c>eng_Latn</c>, <c>zho_Hans</c>) and a
///     handful of special tokens (<c>&lt;s&gt;</c>, <c>&lt;pad&gt;</c>, <c>&lt;/s&gt;</c>,
///     <c>&lt;unk&gt;</c>) that live above index 256 000.
/// </summary>
public sealed class NllbTokenizer : INllbTokenizer
{
    private const string NllbSentencePieceNormalizerName = "nmt_nfkc";
    private const string MicrosoftTokenizerCompatibleNormalizerName = "identity";
    // NLLB's HuggingFace tokenizer reserves model id 3 for <unk>, shifting
    // SentencePiece content pieces by +1 compared with Microsoft.ML.Tokenizers.
    private const int FirstSentencePieceContentId = 3;
    private const int SentencePieceModelIdOffset = 1;

    private readonly Tokenizer _spm;
    private readonly IReadOnlyDictionary<string, int> _addedTokens;
    private readonly IReadOnlyDictionary<int, string> _addedTokensReverse;
    private readonly HashSet<int> _specialTokenIds;

    public int BosTokenId { get; }
    public int PadTokenId { get; }
    public int EosTokenId { get; }
    public int UnkTokenId { get; }

    /// <summary>
    /// Loads a tokenizer from the model directory. Expects
    /// <c>sentencepiece.bpe.model</c> and <c>tokenizer.json</c> alongside each
    /// other (the standard HuggingFace NLLB layout).
    /// </summary>
    public static NllbTokenizer LoadFromDirectory(string modelDir)
    {
        var spmPath = Path.Combine(modelDir, "sentencepiece.bpe.model");
        var tokenizerJsonPath = Path.Combine(modelDir, "tokenizer.json");

        if (!File.Exists(spmPath))
        {
            throw new FileNotFoundException(
                $"NLLB SentencePiece model not found at '{spmPath}'", spmPath);
        }
        if (!File.Exists(tokenizerJsonPath))
        {
            throw new FileNotFoundException(
                $"NLLB tokenizer.json not found at '{tokenizerJsonPath}'", tokenizerJsonPath);
        }

        using var spmStream = OpenCompatibleSentencePieceModelStream(spmPath);
        // LlamaTokenizer.Create reads a SentencePiece .model file regardless of
        // BPE vs Unigram model type — the name is historical (Llama uses Unigram,
        // NLLB uses BPE; the same factory handles both).
        Tokenizer spm = LlamaTokenizer.Create(
            spmStream,
            addBeginOfSentence: false,
            addEndOfSentence: false,
            specialTokens: new Dictionary<string, int>());

        var addedTokens = ParseAddedTokens(tokenizerJsonPath);
        return new NllbTokenizer(spm, addedTokens);
    }

    public NllbTokenizer(Tokenizer spm, IReadOnlyDictionary<string, int> addedTokens)
    {
        _spm = spm ?? throw new ArgumentNullException(nameof(spm));
        _addedTokens = addedTokens ?? throw new ArgumentNullException(nameof(addedTokens));
        _addedTokensReverse = addedTokens.ToDictionary(kv => kv.Value, kv => kv.Key);
        _specialTokenIds = new HashSet<int>(addedTokens.Values);

        BosTokenId = LookupRequired("<s>");
        PadTokenId = LookupRequired("<pad>");
        EosTokenId = LookupRequired("</s>");
        UnkTokenId = LookupRequired("<unk>");
    }

    public IReadOnlyList<int> EncodeSource(string text, string srcFloresCode)
    {
        var langId = GetLanguageTokenId(srcFloresCode);
        var spIds = _spm.EncodeToIds(NormalizeInputForNllbTokenizer(text));
        var result = new List<int>(spIds.Count + 2)
        {
            langId,
        };
        result.AddRange(spIds.Select(ToModelSentencePieceId));
        result.Add(EosTokenId);
        return result;
    }

    public string Decode(IReadOnlyList<int> tokenIds)
    {
        // Strip language-code / control tokens and pass the rest to SPM.
        var content = tokenIds
            .Where(id => !_specialTokenIds.Contains(id))
            .Select(ToTokenizerSentencePieceId)
            .ToArray();
        return _spm.Decode(content) ?? string.Empty;
    }

    public string? DecodeSingle(int tokenId)
    {
        if (_specialTokenIds.Contains(tokenId))
        {
            return null;
        }

        return _spm.Decode(new[] { ToTokenizerSentencePieceId(tokenId) });
    }

    internal static int ToModelSentencePieceId(int tokenizerId)
    {
        return tokenizerId >= FirstSentencePieceContentId
            ? tokenizerId + SentencePieceModelIdOffset
            : tokenizerId;
    }

    internal static int ToTokenizerSentencePieceId(int modelId)
    {
        return modelId >= FirstSentencePieceContentId + SentencePieceModelIdOffset
            ? modelId - SentencePieceModelIdOffset
            : modelId;
    }

    internal static Stream OpenCompatibleSentencePieceModelStream(string spmPath)
    {
        var modelBytes = File.ReadAllBytes(spmPath);
        // Microsoft.ML.Tokenizers 1.0.x rejects SentencePiece's "nmt_nfkc"
        // normalizer name even though NLLB uses it. Keep the model file
        // untouched and rewrite only the in-memory protobuf to a supported
        // same-length boundary; EncodeSource applies .NET NFKC before tokenizing.
        TryRewriteUnsupportedNormalizerName(modelBytes);
        return new MemoryStream(modelBytes, writable: false);
    }

    internal static bool TryRewriteUnsupportedNormalizerName(byte[] modelBytes)
    {
        var source = Encoding.UTF8.GetBytes(NllbSentencePieceNormalizerName);
        var replacement = Encoding.UTF8.GetBytes(MicrosoftTokenizerCompatibleNormalizerName);

        if (source.Length != replacement.Length)
        {
            throw new InvalidOperationException(
                "SentencePiece normalizer compatibility rewrite requires equal-length tokens.");
        }

        for (var i = 0; i <= modelBytes.Length - source.Length; i++)
        {
            if (!modelBytes.AsSpan(i, source.Length).SequenceEqual(source))
            {
                continue;
            }

            replacement.CopyTo(modelBytes.AsSpan(i, replacement.Length));
            return true;
        }

        return false;
    }

    internal static string NormalizeInputForNllbTokenizer(string text)
    {
        return text.Normalize(NormalizationForm.FormKC);
    }

    public int GetLanguageTokenId(string floresCode)
    {
        if (_addedTokens.TryGetValue(floresCode, out var id))
        {
            return id;
        }

        throw new ArgumentException(
            $"FLORES-200 language code '{floresCode}' is not in the loaded tokenizer's added-tokens table.",
            nameof(floresCode));
    }

    private int LookupRequired(string token)
    {
        if (_addedTokens.TryGetValue(token, out var id))
        {
            return id;
        }

        throw new InvalidOperationException(
            $"NLLB tokenizer is missing required special token '{token}'. The tokenizer.json file may be corrupt or from a different model.");
    }

    private static IReadOnlyDictionary<string, int> ParseAddedTokens(string tokenizerJsonPath)
    {
        // tokenizer.json schema (HuggingFace fast tokenizers):
        //   {
        //     "added_tokens": [
        //       { "id": 0, "content": "<s>", "special": true, ... },
        //       { "id": 256047, "content": "eng_Latn", "special": true, ... },
        //       ...
        //     ],
        //     ...
        //   }
        // We only need the (content, id) pairs.

        using var fs = File.OpenRead(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(fs);

        if (!doc.RootElement.TryGetProperty("added_tokens", out var addedTokensElement)
            || addedTokensElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"tokenizer.json at '{tokenizerJsonPath}' has no 'added_tokens' array.");
        }

        var dict = new Dictionary<string, int>(addedTokensElement.GetArrayLength());
        foreach (var token in addedTokensElement.EnumerateArray())
        {
            if (token.TryGetProperty("content", out var contentEl)
                && token.TryGetProperty("id", out var idEl)
                && contentEl.ValueKind == JsonValueKind.String
                && idEl.TryGetInt32(out var id))
            {
                var content = contentEl.GetString();
                if (!string.IsNullOrEmpty(content))
                {
                    dict[content] = id;
                }
            }
        }

        if (dict.Count == 0)
        {
            throw new InvalidDataException(
                $"tokenizer.json at '{tokenizerJsonPath}' has no usable entries in 'added_tokens'.");
        }

        return dict;
    }
}
