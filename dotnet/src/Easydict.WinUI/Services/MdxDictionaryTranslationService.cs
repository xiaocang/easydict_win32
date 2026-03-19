using System.Net;
using System.Text.RegularExpressions;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using MDict.Csharp.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Local MDict (*.mdx) backed dictionary service.
/// Supports both plain and type-2 encrypted (Salsa20/8) dictionaries.
/// </summary>
public sealed class MdxDictionaryTranslationService : ITranslationService
{
    private static readonly Regex ScriptStyleRegex = new("<(script|style)[^>]*>[\\s\\S]*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private Func<string, (string KeyText, string? Definition)>? _lookup;

    /// <summary>
    /// Creates a service for an MDX dictionary file.
    /// If the dictionary is encrypted and no credentials are provided, the service is created
    /// in an unconfigured state — queries will return a "needs credentials" message.
    /// </summary>
    public MdxDictionaryTranslationService(string serviceId, string displayName, string filePath,
        string? regcode = null, string? email = null)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("MDX dictionary file not found.", filePath);
        }

        ServiceId = serviceId;
        DisplayName = displayName;
        FilePath = filePath;

        try
        {
            var options = new MDictOptions();
            if (!string.IsNullOrEmpty(regcode) && !string.IsNullOrEmpty(email))
            {
                options.Passcode = $"{regcode}\t{email}";
            }

            var dict = new MdxDict(filePath, options);
            _lookup = dict.Lookup;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("credentials required", StringComparison.OrdinalIgnoreCase))
        {
            // Encrypted dictionary without valid credentials — register in unconfigured state
            IsEncrypted = true;
            _lookup = null;
        }
        catch (Exception ex) when (
            !string.IsNullOrEmpty(regcode) &&
            (ex is InvalidOperationException || ex is FormatException))
        {
            // Encrypted dictionary with invalid credentials
            IsEncrypted = true;
            _lookup = null;
        }
    }

    internal MdxDictionaryTranslationService(
        string serviceId,
        string displayName,
        string filePath,
        Func<string, (string KeyText, string? Definition)> lookup)
    {
        ServiceId = serviceId;
        DisplayName = displayName;
        FilePath = filePath;
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
    }

    /// <summary>
    /// For testing: create an encrypted-but-unconfigured service.
    /// </summary>
    internal MdxDictionaryTranslationService(
        string serviceId,
        string displayName,
        string filePath,
        bool isEncrypted)
    {
        ServiceId = serviceId;
        DisplayName = displayName;
        FilePath = filePath;
        IsEncrypted = isEncrypted;
        _lookup = isEncrypted ? null : throw new ArgumentException("Use other constructor for non-encrypted dicts");
    }

    public string FilePath { get; }

    public string ServiceId { get; }

    public string DisplayName { get; }

    /// <summary>
    /// True if the underlying MDX file uses type-2 encryption (Salsa20/8).
    /// </summary>
    public bool IsEncrypted { get; }

    public bool RequiresApiKey => IsEncrypted;

    public bool IsConfigured => _lookup != null;

    public IReadOnlyList<Language> SupportedLanguages { get; } = Enum.GetValues<Language>();

    public bool SupportsLanguagePair(Language from, Language to) => true;

    /// <summary>
    /// Re-initialize the dictionary with new credentials.
    /// Called after user enters email + regcode in settings.
    /// </summary>
    public void Configure(string regcode, string email)
    {
        var options = new MDictOptions
        {
            Passcode = $"{regcode}\t{email}"
        };
        var dict = new MdxDict(FilePath, options);
        _lookup = dict.Lookup;
    }

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConfigured)
        {
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = string.Empty,
                TargetLanguage = request.ToLanguage,
                DetectedLanguage = request.FromLanguage,
                ServiceName = DisplayName,
                ResultKind = TranslationResultKind.NoResult,
                InfoMessage = "🔒 This dictionary is encrypted. Please configure credentials in Settings → Service Configuration."
            });
        }

        var query = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new TranslationException("Text cannot be empty")
            {
                ErrorCode = TranslationErrorCode.Unknown,
                ServiceId = ServiceId
            };
        }

        var entry = _lookup!(query);
        var definition = entry.Definition;
        if (string.IsNullOrWhiteSpace(definition))
        {
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = string.Empty,
                TargetLanguage = request.ToLanguage,
                DetectedLanguage = request.FromLanguage,
                ServiceName = DisplayName,
                ResultKind = TranslationResultKind.NoResult,
                InfoMessage = $"No result found in dictionary: {query}"
            });
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
