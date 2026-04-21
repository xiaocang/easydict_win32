using System.Diagnostics;
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
    private static readonly Regex ScriptStyleRegex = new("<(script|style)[^>]*>[\\s\\S]*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex LinkRedirectRegex = new(@"^@@@LINK=(.+)", RegexOptions.Compiled);
    private Func<string, (string KeyText, string? Definition)>? _lookup;
    private Func<IEnumerable<string>>? _enumerateKeys;
    private readonly List<MddDict> _mddDicts = [];

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
            _enumerateKeys = dict.EnumerateKeys;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("credentials required", StringComparison.OrdinalIgnoreCase))
        {
            // Encrypted dictionary without valid credentials — register in unconfigured state
            IsEncrypted = true;
            _lookup = null;
            _enumerateKeys = null;
        }
        catch (Exception ex) when (
            !string.IsNullOrEmpty(regcode) &&
            (ex is InvalidOperationException || ex is FormatException))
        {
            // Encrypted dictionary with invalid credentials
            IsEncrypted = true;
            _lookup = null;
            _enumerateKeys = null;
        }
    }

    internal MdxDictionaryTranslationService(
        string serviceId,
        string displayName,
        string filePath,
        Func<string, (string KeyText, string? Definition)> lookup,
        Func<IEnumerable<string>>? enumerateKeys = null)
    {
        ServiceId = serviceId;
        DisplayName = displayName;
        FilePath = filePath;
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _enumerateKeys = enumerateKeys;
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
        _enumerateKeys = null;
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

    public bool CanEnumerateKeys => _enumerateKeys != null;

    public IReadOnlyList<Language> SupportedLanguages { get; } = Enum.GetValues<Language>();

    public bool SupportsLanguagePair(Language from, Language to) => true;

    /// <summary>
    /// Whether any MDD resource files are loaded.
    /// </summary>
    public bool HasMddResources => _mddDicts.Count > 0;

    /// <summary>
    /// Directory containing the MDX file (used for virtual host mapping of loose resources).
    /// </summary>
    public string DictionaryDirectory => Path.GetDirectoryName(FilePath) ?? string.Empty;

    /// <summary>
    /// Discovers companion MDD files for an MDX dictionary.
    /// Scans same directory for {baseName}.mdd, {baseName}.1.mdd, {baseName}.2.mdd, etc.
    /// </summary>
    internal static List<string> DiscoverMddFiles(string mdxFilePath)
    {
        var result = new List<string>();
        var dir = Path.GetDirectoryName(mdxFilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return result;

        var baseName = Path.GetFileNameWithoutExtension(mdxFilePath);

        // Check unnumbered: {baseName}.mdd
        var unnumbered = Path.Combine(dir, $"{baseName}.mdd");
        if (File.Exists(unnumbered))
            result.Add(unnumbered);

        // Check numbered: {baseName}.1.mdd, {baseName}.2.mdd, ...
        for (int i = 1; i <= 99; i++)
        {
            var numbered = Path.Combine(dir, $"{baseName}.{i}.mdd");
            if (File.Exists(numbered))
                result.Add(numbered);
            else
                break;
        }

        return result;
    }

    /// <summary>
    /// Loads MDD resource files. Tolerates individual load failures.
    /// </summary>
    public void LoadMddFiles(IReadOnlyList<string> mddPaths)
    {
        foreach (var path in mddPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    _mddDicts.Add(new MddDict(path));
                    Debug.WriteLine($"[MdxDictionary] Loaded MDD: {path}");
                }
                else
                {
                    Debug.WriteLine($"[MdxDictionary] MDD file not found: {path}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MdxDictionary] Failed to load MDD '{path}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Looks up a resource (CSS, image, audio, etc.) across all loaded MDD files.
    /// Returns raw bytes or null if not found.
    /// </summary>
    public byte[]? LookupResource(string resourceKey)
    {
        if (_mddDicts.Count == 0 || string.IsNullOrEmpty(resourceKey))
            return null;

        // Normalize: MDD keys typically use backslash prefix
        var normalizedKey = resourceKey.Replace('/', '\\');
        if (!normalizedKey.StartsWith('\\'))
            normalizedKey = '\\' + normalizedKey;

        foreach (var mdd in _mddDicts)
        {
            try
            {
                var result = mdd.Locate(normalizedKey);
                if (result.Definition != null)
                {
                    return Convert.FromBase64String(result.Definition);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MdxDictionary] MDD lookup error for '{normalizedKey}': {ex.Message}");
            }
        }

        return null;
    }

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
        _enumerateKeys = dict.EnumerateKeys;
    }

    public IEnumerable<string> EnumerateKeys()
    {
        if (_enumerateKeys is null)
        {
            throw new InvalidOperationException("Dictionary keys are not available until the dictionary is configured.");
        }

        return _enumerateKeys();
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsConfigured)
        {
            return new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = string.Empty,
                TargetLanguage = request.ToLanguage,
                DetectedLanguage = request.FromLanguage,
                ServiceName = DisplayName,
                ResultKind = TranslationResultKind.NoResult,
                InfoMessage = "🔒 This dictionary is encrypted. Please configure credentials in Settings → Service Configuration."
            };
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

        return await Task.Run(() =>
        {
            var definition = ResolveDefinition(query);
            if (string.IsNullOrWhiteSpace(definition))
            {
                return new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = string.Empty,
                    TargetLanguage = request.ToLanguage,
                    DetectedLanguage = request.FromLanguage,
                    ServiceName = DisplayName,
                    ResultKind = TranslationResultKind.NoResult,
                    InfoMessage = $"No result found in dictionary: {query}"
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

            return new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = plainText,
                TargetLanguage = request.ToLanguage,
                DetectedLanguage = request.FromLanguage,
                ServiceName = DisplayName,
                WordResult = wordResult,
                RawHtml = HasMddResources ? definition : null
            };
        }, cancellationToken);
    }

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Language.Auto);

    /// <summary>
    /// Resolves the definition for a query, following @@@LINK= redirections (up to 5 hops).
    /// </summary>
    private string? ResolveDefinition(string query, int maxHops = 5)
    {
        var current = query;
        for (int i = 0; i < maxHops; i++)
        {
            var entry = _lookup!(current);
            var definition = entry.Definition;
            if (string.IsNullOrWhiteSpace(definition))
                return null;

            var match = LinkRedirectRegex.Match(definition.Trim());
            if (match.Success)
            {
                current = match.Groups[1].Value.Trim();
                continue;
            }

            return definition;
        }

        Debug.WriteLine($"[MdxDictionary] Too many @@@LINK redirections for '{query}'");
        return null;
    }

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
