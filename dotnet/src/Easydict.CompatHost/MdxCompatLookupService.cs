using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Easydict.SidecarClient.Protocol;
using MDict.Csharp.Models;

namespace Easydict.CompatHost;

public sealed class MdxCompatLookupService : ICompatHostMdxLookupService
{
    private const int MaxRedirectHops = 5;
    private const int MaxFuzzyEntries = 20;

    private static readonly Regex ScriptStyleRegex = new(
        "<(script|style)[^>]*>[\\s\\S]*?</\\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex LinkRedirectRegex = new(@"^@@@LINK=(.+)", RegexOptions.Compiled);

    private readonly Func<ImportedMdxDictionarySnapshot, IMdxDictionaryReader> _readerFactory;

    public MdxCompatLookupService()
        : this(dictionary => new FileMdxDictionaryReader(dictionary))
    {
    }

    internal MdxCompatLookupService(Func<ImportedMdxDictionarySnapshot, IMdxDictionaryReader> readerFactory)
    {
        _readerFactory = readerFactory;
    }

    public Task<MdxLookupResult> LookupAsync(
        MdxLookupParams parameters,
        SettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dictionary = FindDictionary(settings, parameters.DictionaryId);
        if (dictionary is null)
        {
            return Task.FromResult(new MdxLookupResult { Entries = [] });
        }

        if (string.IsNullOrWhiteSpace(parameters.Query))
        {
            throw new CompatHostException(IpcErrorCodes.InvalidParams, "MDX lookup query cannot be empty");
        }

        try
        {
            using var reader = _readerFactory(dictionary);
            var entries = parameters.Fuzzy
                ? LookupFuzzy(reader, parameters.Query.Trim(), dictionary.DisplayName)
                : LookupExact(reader, parameters.Query.Trim(), dictionary.DisplayName);

            return Task.FromResult(new MdxLookupResult { Entries = entries });
        }
        catch (FileNotFoundException ex)
        {
            throw new CompatHostException(IpcErrorCodes.ServiceError, ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase))
        {
            throw new CompatHostException(
                IpcErrorCodes.ServiceError,
                "MDX dictionary credentials are required or invalid");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CompatHost:MDX] Lookup failed for {parameters.DictionaryId}: {ex}");
            throw new CompatHostException(IpcErrorCodes.ServiceError, ex.Message);
        }
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

    private static ImportedMdxDictionarySnapshot? FindDictionary(
        SettingsSnapshot settings,
        string dictionaryId)
    {
        return settings.ImportedMdxDictionaries?
            .FirstOrDefault(dictionary =>
                string.Equals(dictionary.ServiceId, dictionaryId, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<MdxLookupEntry> LookupExact(
        IMdxDictionaryReader reader,
        string query,
        string dictionaryName)
    {
        var definition = ResolveDefinition(reader, query, out var resolvedKey);
        if (string.IsNullOrWhiteSpace(definition))
        {
            return [];
        }

        return
        [
            new MdxLookupEntry
            {
                Key = resolvedKey,
                Html = definition,
                DictionaryName = dictionaryName,
            },
        ];
    }

    private static IReadOnlyList<MdxLookupEntry> LookupFuzzy(
        IMdxDictionaryReader reader,
        string query,
        string dictionaryName)
    {
        var entries = new List<MdxLookupEntry>();
        foreach (var candidate in reader.FuzzyKeys(query).Take(MaxFuzzyEntries))
        {
            var definition = ResolveDefinition(reader, candidate, out var resolvedKey);
            if (string.IsNullOrWhiteSpace(definition))
            {
                continue;
            }

            entries.Add(new MdxLookupEntry
            {
                Key = resolvedKey,
                Html = definition,
                DictionaryName = dictionaryName,
            });
        }

        return entries;
    }

    private static string? ResolveDefinition(
        IMdxDictionaryReader reader,
        string query,
        out string resolvedKey)
    {
        var current = query;
        resolvedKey = query;

        for (var i = 0; i < MaxRedirectHops; i++)
        {
            var (key, definition) = reader.Lookup(current);
            if (string.IsNullOrWhiteSpace(definition))
            {
                return null;
            }

            var match = LinkRedirectRegex.Match(definition.Trim());
            if (match.Success)
            {
                current = match.Groups[1].Value.Trim();
                resolvedKey = current;
                continue;
            }

            resolvedKey = string.IsNullOrWhiteSpace(key) ? current : key;
            return definition;
        }

        Trace.WriteLine($"[CompatHost:MDX] Too many @@@LINK redirections for '{query}'");
        return null;
    }

    internal interface IMdxDictionaryReader : IDisposable
    {
        (string Key, string? Html) Lookup(string query);
        IEnumerable<string> FuzzyKeys(string query);
    }

    private sealed class FileMdxDictionaryReader : IMdxDictionaryReader
    {
        private readonly MdxDict _dict;

        public FileMdxDictionaryReader(ImportedMdxDictionarySnapshot dictionary)
        {
            if (!File.Exists(dictionary.FilePath))
            {
                throw new FileNotFoundException("MDX dictionary file not found.", dictionary.FilePath);
            }

            var options = new MDictOptions();
            if (!string.IsNullOrEmpty(dictionary.Regcode) && !string.IsNullOrEmpty(dictionary.Email))
            {
                options.Passcode = $"{dictionary.Regcode}\t{dictionary.Email}";
            }

            _dict = new MdxDict(dictionary.FilePath, options);
        }

        public (string Key, string? Html) Lookup(string query)
        {
            var (key, definition) = _dict.Lookup(query);
            return (key, definition);
        }

        public IEnumerable<string> FuzzyKeys(string query)
        {
            return _dict.FuzzySearch(query, MaxFuzzyEntries, distance: 3)
                .Select(item => item.KeyText);
        }

        public void Dispose()
        {
        }
    }
}
