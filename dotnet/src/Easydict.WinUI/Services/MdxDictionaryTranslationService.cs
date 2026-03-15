using System.Net;
using System.Text.RegularExpressions;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using MDict.Csharp.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Local MDict (*.mdx) backed dictionary service.
/// </summary>
public sealed class MdxDictionaryTranslationService : ITranslationService
{
    private static readonly Regex ScriptStyleRegex = new("<(script|style)[^>]*>[\\s\\S]*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private readonly MdxDict _dict;

    public MdxDictionaryTranslationService(string serviceId, string displayName, string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("MDX dictionary file not found.", filePath);
        }

        ServiceId = serviceId;
        DisplayName = displayName;
        FilePath = filePath;
        _dict = new MdxDict(filePath);
    }

    public string FilePath { get; }

    public string ServiceId { get; }

    public string DisplayName { get; }

    public bool RequiresApiKey => false;

    public bool IsConfigured => true;

    public IReadOnlyList<Language> SupportedLanguages { get; } = Enum.GetValues<Language>();

    public bool SupportsLanguagePair(Language from, Language to) => true;

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = request.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new TranslationException("Text cannot be empty")
            {
                ErrorCode = TranslationErrorCode.Unknown,
                ServiceId = ServiceId
            };
        }

        var entry = _dict.Lookup(query);
        var definition = entry.Definition;
        if (string.IsNullOrWhiteSpace(definition))
        {
            throw new TranslationException($"No result found in dictionary: {query}")
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
                ServiceId = ServiceId
            };
        }

        var plainText = ToReadableText(definition);
        var wordResult = new WordResult
        {
            Definitions =
            [
                new Definition
                {
                    PartOfSpeech = "dictionary",
                    Meanings = [plainText]
                }
            ]
        };

        return Task.FromResult(new TranslationResult
        {
            OriginalText = request.Text,
            TranslatedText = plainText,
            TargetLanguage = request.ToLanguage,
            DetectedLanguage = request.FromLanguage,
            ServiceName = DisplayName,
            WordResult = wordResult
        });
    }

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Language.Auto);

    internal static string ToReadableText(string html)
    {
        var normalized = html.Replace("\r\n", "\n");
        normalized = ScriptStyleRegex.Replace(normalized, string.Empty);
        normalized = normalized.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</p>", "\n\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</div>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</li>", "\n", StringComparison.OrdinalIgnoreCase);

        normalized = TagRegex.Replace(normalized, string.Empty);
        normalized = WebUtility.HtmlDecode(normalized);

        var lines = normalized
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join("\n", lines);
    }
}
