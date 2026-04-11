using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Easydict.TranslationService;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services.DocumentExport;

namespace Easydict.WinUI.Services;

internal static class LongDocumentCliCommand
{
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly AliasBinding[] EnvAliasBindings =
    [
        new("OPENAI_API_KEY", nameof(SettingsService.OpenAIApiKey)),
        new("OPENAI_MODEL", nameof(SettingsService.OpenAIModel)),
        new("OPENAI_ENDPOINT", nameof(SettingsService.OpenAIEndpoint)),
        new("OPENAI_BASE_URL", nameof(SettingsService.OpenAIEndpoint), NormalizeChatCompletionsEndpoint),
        new("OPENAI_API_BASE", nameof(SettingsService.OpenAIEndpoint), NormalizeChatCompletionsEndpoint),
        new("OPENAI_TEMPERATURE", nameof(SettingsService.OpenAITemperature)),
        new("CUSTOM_OPENAI_API_KEY", nameof(SettingsService.CustomOpenAIApiKey)),
        new("CUSTOM_OPENAI_MODEL", nameof(SettingsService.CustomOpenAIModel)),
        new("CUSTOM_OPENAI_ENDPOINT", nameof(SettingsService.CustomOpenAIEndpoint)),
        new("CUSTOM_OPENAI_BASE_URL", nameof(SettingsService.CustomOpenAIEndpoint), NormalizeChatCompletionsEndpoint),
        new("CUSTOM_OPENAI_API_BASE", nameof(SettingsService.CustomOpenAIEndpoint), NormalizeChatCompletionsEndpoint),
        new("OLLAMA_ENDPOINT", nameof(SettingsService.OllamaEndpoint), NormalizeChatCompletionsEndpoint),
        new("OLLAMA_BASE_URL", nameof(SettingsService.OllamaEndpoint), NormalizeChatCompletionsEndpoint),
        new("OLLAMA_HOST", nameof(SettingsService.OllamaEndpoint), NormalizeChatCompletionsEndpoint),
        new("OLLAMA_MODEL", nameof(SettingsService.OllamaModel)),
        new("GEMINI_API_KEY", nameof(SettingsService.GeminiApiKey)),
        new("GEMINI_MODEL", nameof(SettingsService.GeminiModel)),
        new("DEEPSEEK_API_KEY", nameof(SettingsService.DeepSeekApiKey)),
        new("DEEPSEEK_MODEL", nameof(SettingsService.DeepSeekModel)),
        new("GROQ_API_KEY", nameof(SettingsService.GroqApiKey)),
        new("GROQ_MODEL", nameof(SettingsService.GroqModel)),
        new("ZHIPU_API_KEY", nameof(SettingsService.ZhipuApiKey)),
        new("ZHIPU_MODEL", nameof(SettingsService.ZhipuModel)),
        new("DOUBAO_API_KEY", nameof(SettingsService.DoubaoApiKey)),
        new("ARK_API_KEY", nameof(SettingsService.DoubaoApiKey)),
        new("DOUBAO_ENDPOINT", nameof(SettingsService.DoubaoEndpoint)),
        new("ARK_ENDPOINT", nameof(SettingsService.DoubaoEndpoint)),
        new("DOUBAO_MODEL", nameof(SettingsService.DoubaoModel)),
        new("GITHUB_MODELS_TOKEN", nameof(SettingsService.GitHubModelsToken)),
        new("GITHUB_MODELS_MODEL", nameof(SettingsService.GitHubModelsModel)),
        new("DEEPL_API_KEY", nameof(SettingsService.DeepLApiKey)),
        new("DEEPL_USE_FREE_API", nameof(SettingsService.DeepLUseFreeApi)),
        new("EASYDICT_PROXY_ENABLED", nameof(SettingsService.ProxyEnabled)),
        new("EASYDICT_PROXY_URI", nameof(SettingsService.ProxyUri)),
        new("EASYDICT_PROXY_BYPASS_LOCAL", nameof(SettingsService.ProxyBypassLocal)),
        new("EASYDICT_DOCUMENT_OUTPUT_MODE", nameof(SettingsService.DocumentOutputMode)),
        new("EASYDICT_LAYOUT_DETECTION_MODE", nameof(SettingsService.LayoutDetectionMode)),
        new("EASYDICT_LONGDOC_MAX_CONCURRENCY", nameof(SettingsService.LongDocMaxConcurrency)),
        new("EASYDICT_LONGDOC_PAGE_RANGE", nameof(SettingsService.LongDocPageRange)),
        new("EASYDICT_LONGDOC_CUSTOM_PROMPT", nameof(SettingsService.LongDocCustomPrompt)),
        new("EASYDICT_LONGDOC_DOCUMENT_CONTEXT_PASS", nameof(SettingsService.LongDocEnableDocumentContextPass)),
        new("EASYDICT_ENABLE_TRANSLATION_CACHE", nameof(SettingsService.EnableTranslationCache)),
    ];

    private static readonly Dictionary<string, Language> LanguageAliases = BuildLanguageAliases();

    internal static bool IsCommand(string[] args) => args.Any(IsCommandToken);

    private static bool IsCommandToken(string arg) =>
        string.Equals(arg, "--translate-long-doc", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "translate-long-doc", StringComparison.OrdinalIgnoreCase);

    internal static async Task<int> RunAsync(string[] args)
    {
        var filteredArgs = args.Where(arg => !IsCommandToken(arg)).ToArray();

        if (filteredArgs.Length == 0 || filteredArgs.Any(arg => arg is "-h" or "--help"))
        {
            PrintUsage();
            return 0;
        }

        Options options;
        try
        {
            options = ParseArgs(filteredArgs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 1;
        }

        try
        {
            var settings = SettingsService.Instance;
            var envOverrides = string.IsNullOrWhiteSpace(options.EnvFile)
                ? new Dictionary<string, string>(KeyComparer)
                : LoadEnvFile(options.EnvFile);

            ApplyEnvironmentVariables(envOverrides);
            ApplyEnvironmentOverrides(settings);
            ApplyOptionOverrides(settings, options);

            var manager = TranslationManagerService.Instance.Manager;
            if (options.ListServices)
            {
                PrintSupportedServices(manager);
                return 0;
            }

            var inputPath = Path.GetFullPath(options.InputPath);
            if (!File.Exists(inputPath))
                throw new FileNotFoundException("Input file not found.", inputPath);

            var inputMode = DetermineInputMode(inputPath);
            var outputPath = Path.GetFullPath(options.OutputPath ?? BuildDefaultOutputPath(inputPath, inputMode));
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException($"Cannot determine output directory for '{outputPath}'.");

            Directory.CreateDirectory(outputDirectory);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += OnCancelKeyPress;

            try
            {
                var serviceId = ResolveServiceId(manager, options.ServiceId ?? GetEnvironmentValue("EASYDICT_SERVICE_ID", "LONGDOC_SERVICE_ID", "SERVICE_ID"));
                var outputMode = ParseDocumentOutputMode(options.OutputMode ?? settings.DocumentOutputMode);
                var pdfExportMode = ParsePdfExportMode(options.PdfExportMode);

                using var translator = new LongDocumentTranslationService();

                // Layout model and CJK font downloads are independent network IO — overlap them.
                var layoutTask = ResolveLayoutModeAsync(translator, inputMode, options, settings, cts.Token);
                var fontTask = EnsureFontReadyAsync(inputMode, options.TargetLanguage, cts.Token);
                await Task.WhenAll(layoutTask, fontTask).ConfigureAwait(false);
                var layoutMode = await layoutTask.ConfigureAwait(false);

                Console.WriteLine($"Input: {inputPath}");
                Console.WriteLine($"Output: {outputPath}");
                Console.WriteLine($"Service: {serviceId}");
                Console.WriteLine($"Source language: {options.SourceLanguage.ToCode()}");
                Console.WriteLine($"Target language: {options.TargetLanguage.ToCode()}");
                Console.WriteLine($"Layout detection: {layoutMode}");
                Console.WriteLine($"Output mode: {outputMode}");
                Console.WriteLine();

                var reporter = new ConsoleProgressReporter();

                var result = await translator.TranslateToPdfAsync(
                    inputMode,
                    inputPath,
                    options.SourceLanguage,
                    options.TargetLanguage,
                    outputPath,
                    serviceId,
                    reporter.ReportStatus,
                    cts.Token,
                    layoutDetection: layoutMode,
                    outputMode: outputMode,
                    pdfExportMode: pdfExportMode,
                    visionEndpoint: options.VisionEndpoint,
                    visionApiKey: options.VisionApiKey,
                    visionModel: options.VisionModel,
                    progress: reporter.Progress).ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine($"State: {result.State}");
                Console.WriteLine($"Output: {result.OutputPath}");
                if (!string.IsNullOrWhiteSpace(result.BilingualOutputPath) &&
                    !string.Equals(result.BilingualOutputPath, result.OutputPath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Bilingual output: {result.BilingualOutputPath}");
                }

                if (result.State == LongDocumentJobState.PartialSuccess)
                {
                    Console.WriteLine($"Failed chunk indexes: {string.Join(", ", result.FailedChunkIndexes.Select(i => i + 1))}");

                    if (result.QualityReport?.FailedBlocks is { Count: > 0 } failures)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Failed block details:");

                        // Build lookup from SourceBlockId → source text using the checkpoint
                        // to show a short preview of each failing chunk.
                        var sourceTextByBlockId = new Dictionary<string, string>(StringComparer.Ordinal);
                        for (var i = 0; i < result.Checkpoint.ChunkMetadata.Count; i++)
                        {
                            var meta = result.Checkpoint.ChunkMetadata[i];
                            if (i < result.Checkpoint.SourceChunks.Count)
                                sourceTextByBlockId[meta.SourceBlockId] = result.Checkpoint.SourceChunks[i];
                        }

                        foreach (var failure in failures
                            .OrderBy(f => f.PageNumber)
                            .ThenBy(f => f.SourceBlockId, StringComparer.Ordinal))
                        {
                            Console.WriteLine($"  p{failure.PageNumber} {failure.SourceBlockId} retries={failure.RetryCount}: {failure.Error}");
                            if (sourceTextByBlockId.TryGetValue(failure.SourceBlockId, out var sourceText) &&
                                !string.IsNullOrEmpty(sourceText))
                            {
                                var preview = sourceText.Replace('\n', ' ').Replace('\r', ' ');
                                if (preview.Length > 200)
                                    preview = preview[..200] + "…";
                                Console.WriteLine($"    src({sourceText.Length}): {preview}");
                            }
                        }
                    }

                    return 2;
                }

                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
            }

            void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Console.Error.WriteLine("Cancellation requested.");
                cts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Translation canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Options ParseArgs(IReadOnlyList<string> args)
    {
        string? inputPath = null;
        string? outputPath = null;
        string? envFile = null;
        string? serviceId = null;
        string? outputMode = null;
        string? layoutMode = null;
        string? pdfExportMode = null;
        string? pageRange = null;
        string? visionEndpoint = null;
        string? visionApiKey = null;
        string? visionModel = null;
        int? maxConcurrency = null;
        var sourceLanguage = Language.Auto;
        Language? targetLanguage = null;
        var listServices = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--input":
                case "-i":
                    inputPath = ReadValue(args, ref i, arg);
                    break;
                case "--output":
                case "-o":
                    outputPath = ReadValue(args, ref i, arg);
                    break;
                case "--env-file":
                case "-e":
                    envFile = ReadValue(args, ref i, arg);
                    break;
                case "--service":
                case "--service-id":
                case "-s":
                    serviceId = ReadValue(args, ref i, arg);
                    break;
                case "--source-language":
                case "--from":
                    sourceLanguage = ParseLanguage(ReadValue(args, ref i, arg), allowAuto: true, optionName: arg);
                    break;
                case "--target-language":
                case "--to":
                case "-t":
                    targetLanguage = ParseLanguage(ReadValue(args, ref i, arg), allowAuto: false, optionName: arg);
                    break;
                case "--output-mode":
                    outputMode = ReadValue(args, ref i, arg);
                    break;
                case "--layout":
                    layoutMode = ReadValue(args, ref i, arg);
                    break;
                case "--pdf-export-mode":
                    pdfExportMode = ReadValue(args, ref i, arg);
                    break;
                case "--page-range":
                case "--pages":
                case "--page":
                    pageRange = ReadValue(args, ref i, arg);
                    break;
                case "--max-concurrency":
                    maxConcurrency = ParsePositiveInt(ReadValue(args, ref i, arg), arg, minValue: 1, maxValue: 16);
                    break;
                case "--vision-endpoint":
                    visionEndpoint = ReadValue(args, ref i, arg);
                    break;
                case "--vision-api-key":
                    visionApiKey = ReadValue(args, ref i, arg);
                    break;
                case "--vision-model":
                    visionModel = ReadValue(args, ref i, arg);
                    break;
                case "--list-services":
                    listServices = true;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown argument: {arg}");

                    inputPath ??= arg;
                    break;
            }
        }

        if (!listServices && string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Input file is required.");

        if (!listServices && targetLanguage is null)
            throw new ArgumentException("Target language is required.");

        return new Options(
            InputPath: Path.GetFullPath(inputPath ?? "."),
            OutputPath: string.IsNullOrWhiteSpace(outputPath) ? null : Path.GetFullPath(outputPath),
            EnvFile: string.IsNullOrWhiteSpace(envFile) ? null : Path.GetFullPath(envFile),
            ServiceId: string.IsNullOrWhiteSpace(serviceId) ? null : serviceId,
            SourceLanguage: sourceLanguage,
            TargetLanguage: targetLanguage ?? Language.English,
            OutputMode: outputMode,
            LayoutMode: layoutMode,
            PdfExportMode: pdfExportMode,
            PageRange: pageRange,
            MaxConcurrency: maxConcurrency,
            VisionEndpoint: visionEndpoint,
            VisionApiKey: visionApiKey,
            VisionModel: visionModel,
            ListServices: listServices);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
            throw new ArgumentException($"Missing value for {option}.");

        index++;
        return args[index];
    }

    private static int ParsePositiveInt(string value, string option, int minValue, int maxValue)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minValue ||
            parsed > maxValue)
        {
            throw new ArgumentException($"{option} must be an integer between {minValue} and {maxValue}.");
        }

        return parsed;
    }

    private static Language ParseLanguage(string rawValue, bool allowAuto, string optionName)
    {
        if (!TryParseLanguage(rawValue, allowAuto, out var language))
        {
            throw new ArgumentException($"{optionName} has unsupported language value '{rawValue}'.");
        }

        return language;
    }

    private static bool TryParseLanguage(string rawValue, bool allowAuto, out Language language)
    {
        var trimmed = (rawValue ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            language = default;
            return false;
        }

        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            language = Language.Auto;
            return allowAuto;
        }

        // Standard short codes (zh, zh-cn, en, ja, ...)
        var fromCode = LanguageExtensions.FromCode(trimmed);
        if (fromCode != Language.Auto)
        {
            language = fromCode;
            return true;
        }

        // Friendly aliases and enum names
        if (LanguageAliases.TryGetValue(NormalizeToken(trimmed), out language))
            return allowAuto || language != Language.Auto;

        language = default;
        return false;
    }

    private static Dictionary<string, Language> BuildLanguageAliases()
    {
        var result = new Dictionary<string, Language>(KeyComparer)
        {
            ["chinese"] = Language.SimplifiedChinese,
            ["simplifiedchinese"] = Language.SimplifiedChinese,
            ["traditionalchinese"] = Language.TraditionalChinese,
            ["classicalchinese"] = Language.ClassicalChinese,
            ["tagalog"] = Language.Filipino,
        };

        // Enum names: english, japanese, simplifiedchinese, ...
        foreach (Language language in Enum.GetValues(typeof(Language)))
            result[NormalizeToken(language.ToString())] = language;

        return result;
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }

    private static LongDocumentInputMode DetermineInputMode(string inputPath)
    {
        return Path.GetExtension(inputPath).ToLowerInvariant() switch
        {
            ".pdf" => LongDocumentInputMode.Pdf,
            ".txt" => LongDocumentInputMode.PlainText,
            ".md" or ".markdown" => LongDocumentInputMode.Markdown,
            var ext => throw new NotSupportedException($"Unsupported input extension '{ext}'. Supported: .pdf, .txt, .md")
        };
    }

    private static string BuildDefaultOutputPath(string inputPath, LongDocumentInputMode inputMode)
    {
        var folder = Path.GetDirectoryName(inputPath)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        var extension = inputMode switch
        {
            LongDocumentInputMode.PlainText => ".txt",
            LongDocumentInputMode.Markdown => ".md",
            _ => ".pdf"
        };

        return Path.Combine(folder, $"{fileName}_translated{extension}");
    }

    private static Dictionary<string, string> LoadEnvFile(string envFilePath)
    {
        if (!File.Exists(envFilePath))
            throw new FileNotFoundException("Environment file not found.", envFilePath);

        var result = new Dictionary<string, string>(KeyComparer);
        foreach (var rawLine in File.ReadAllLines(envFilePath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                line = line[7..].TrimStart();

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var value = line[(separatorIndex + 1)..].Trim();
            result[key] = ParseEnvValue(value);
        }

        return result;
    }

    private static string ParseEnvValue(string rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
            return string.Empty;

        if ((rawValue.StartsWith('"') && rawValue.EndsWith('"')) ||
            (rawValue.StartsWith('\'') && rawValue.EndsWith('\'')))
        {
            var inner = rawValue[1..^1];
            return rawValue[0] == '"'
                ? inner
                    .Replace("\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\r", "\r", StringComparison.Ordinal)
                    .Replace("\\t", "\t", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                : inner;
        }

        var hashIndex = rawValue.IndexOf(" #", StringComparison.Ordinal);
        return hashIndex >= 0 ? rawValue[..hashIndex].TrimEnd() : rawValue;
    }

    private static void ApplyEnvironmentVariables(IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static readonly Dictionary<string, PropertyInfo> SettingsPropertyCache = BuildSettingsPropertyCache();

    private static Dictionary<string, PropertyInfo> BuildSettingsPropertyCache()
    {
        var cache = new Dictionary<string, PropertyInfo>(KeyComparer);
        foreach (var binding in EnvAliasBindings)
        {
            if (cache.ContainsKey(binding.PropertyName))
                continue;

            var prop = typeof(SettingsService).GetProperty(binding.PropertyName, BindingFlags.Instance | BindingFlags.Public);
            if (prop is not null && prop.CanWrite && prop.GetIndexParameters().Length == 0)
                cache[binding.PropertyName] = prop;
        }
        return cache;
    }

    private static void ApplyEnvironmentOverrides(SettingsService settings)
    {
        foreach (var binding in EnvAliasBindings)
        {
            var rawValue = Environment.GetEnvironmentVariable(binding.Alias);
            if (string.IsNullOrEmpty(rawValue))
                continue;

            if (!SettingsPropertyCache.TryGetValue(binding.PropertyName, out var property))
                continue;

            var transformed = binding.Transform?.Invoke(rawValue) ?? rawValue;
            if (TryConvertFromString(transformed, property.PropertyType, out var value))
                property.SetValue(settings, value);
        }
    }

    private static bool TryConvertFromString(string rawValue, Type propertyType, out object? value)
    {
        value = null;

        if (propertyType == typeof(string))
        {
            value = rawValue;
            return true;
        }

        if (propertyType == typeof(bool) || propertyType == typeof(bool?))
        {
            if (bool.TryParse(rawValue, out var boolValue))
            {
                value = boolValue;
                return true;
            }

            if (string.Equals(rawValue, "1", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (string.Equals(rawValue, "0", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }

            return false;
        }

        if (propertyType == typeof(int) || propertyType == typeof(int?))
        {
            if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                value = intValue;
                return true;
            }

            return false;
        }

        if (propertyType == typeof(double) || propertyType == typeof(double?))
        {
            if (double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
            {
                value = doubleValue;
                return true;
            }

            return false;
        }

        if (propertyType == typeof(List<string>))
        {
            value = rawValue
                .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            return true;
        }

        return false;
    }

    private static void ApplyOptionOverrides(SettingsService settings, Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.PageRange))
            settings.LongDocPageRange = options.PageRange.Trim();

        if (options.MaxConcurrency.HasValue)
            settings.LongDocMaxConcurrency = options.MaxConcurrency.Value;

        if (!string.IsNullOrWhiteSpace(options.OutputMode))
            settings.DocumentOutputMode = ParseDocumentOutputMode(options.OutputMode).ToString();

        if (!string.IsNullOrWhiteSpace(options.LayoutMode))
            settings.LayoutDetectionMode = ParseLayoutDetectionMode(options.LayoutMode).ToString();
    }

    private static string ResolveServiceId(TranslationManager manager, string? requestedServiceId)
    {
        var supportedServices = manager.Services.Values
            .Where(IsLongDocSupportedService)
            .OrderBy(service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (supportedServices.Count == 0)
        {
            throw new InvalidOperationException("No long-document-capable translation services are available.");
        }

        if (!string.IsNullOrWhiteSpace(requestedServiceId))
        {
            var requested = supportedServices.FirstOrDefault(service =>
                string.Equals(service.ServiceId, requestedServiceId, StringComparison.OrdinalIgnoreCase));

            if (requested is null)
                throw new InvalidOperationException($"Translation service '{requestedServiceId}' was not found.");

            if (!requested.IsConfigured)
                throw new InvalidOperationException($"Translation service '{requested.ServiceId}' is not configured.");

            return requested.ServiceId;
        }

        var firstConfigured = supportedServices.FirstOrDefault(service => service.IsConfigured);
        if (firstConfigured is not null)
            return firstConfigured.ServiceId;

        throw new InvalidOperationException(
            "No configured long-document translation service found. Pass --service or provide credentials in the env file.");
    }

    private static bool IsLongDocSupportedService(ITranslationService service)
    {
        if (string.Equals(service.ServiceId, "builtin", StringComparison.OrdinalIgnoreCase))
            return false;

        return service is IStreamTranslationService;
    }

    private static void PrintSupportedServices(TranslationManager manager)
    {
        var services = manager.Services.Values
            .Where(IsLongDocSupportedService)
            .OrderBy(service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (services.Count == 0)
        {
            Console.WriteLine("No long-document-capable services available.");
            return;
        }

        foreach (var service in services)
        {
            Console.WriteLine($"{service.ServiceId} | {service.DisplayName} | Configured={service.IsConfigured}");
        }
    }

    private static async Task<LayoutDetectionMode> ResolveLayoutModeAsync(
        LongDocumentTranslationService translator,
        LongDocumentInputMode inputMode,
        Options options,
        SettingsService settings,
        CancellationToken ct)
    {
        if (inputMode is not LongDocumentInputMode.Pdf)
            return LayoutDetectionMode.Heuristic;

        var requestedMode = ParseLayoutDetectionMode(options.LayoutMode ?? settings.LayoutDetectionMode);
        if (requestedMode == LayoutDetectionMode.Heuristic)
            return requestedMode;

        var downloadService = translator.GetLayoutModelDownloadService();

        if (requestedMode == LayoutDetectionMode.Auto)
            return downloadService.IsReady ? LayoutDetectionMode.OnnxLocal : LayoutDetectionMode.Heuristic;

        if (requestedMode == LayoutDetectionMode.OnnxLocal && !downloadService.IsReady)
        {
            Console.WriteLine("Downloading ONNX layout model...");
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (p.TotalBytes > 0)
                    Console.WriteLine($"[download:{p.Stage}] {p.Percentage:F0}%");
            });
            await downloadService.EnsureAvailableAsync(progress, ct).ConfigureAwait(false);
        }

        return requestedMode;
    }

    private static async Task EnsureFontReadyAsync(
        LongDocumentInputMode inputMode,
        Language targetLanguage,
        CancellationToken ct)
    {
        if (inputMode is not LongDocumentInputMode.Pdf || !FontDownloadService.RequiresCjkFont(targetLanguage))
            return;

        using var fontService = new FontDownloadService();
        if (fontService.IsFontDownloaded(targetLanguage))
            return;

        Console.WriteLine($"Downloading font for {targetLanguage.GetDisplayName()}...");
        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            if (p.TotalBytes > 0)
                Console.WriteLine($"[font:{p.Stage}] {p.Percentage:F0}%");
        });
        await fontService.EnsureFontAsync(targetLanguage, progress, ct).ConfigureAwait(false);
    }

    private static DocumentOutputMode ParseDocumentOutputMode(string? rawValue)
    {
        return NormalizeToken(rawValue) switch
        {
            "" or "monolingual" or "translated" or "mono" => DocumentOutputMode.Monolingual,
            "bilingual" or "dual" => DocumentOutputMode.Bilingual,
            "both" => DocumentOutputMode.Both,
            _ => throw new ArgumentException($"Unsupported output mode '{rawValue}'. Use Monolingual, Bilingual, or Both.")
        };
    }

    private static LayoutDetectionMode ParseLayoutDetectionMode(string? rawValue)
    {
        return NormalizeToken(rawValue) switch
        {
            "" or "auto" => LayoutDetectionMode.Auto,
            "heuristic" => LayoutDetectionMode.Heuristic,
            "onnx" or "onnxlocal" => LayoutDetectionMode.OnnxLocal,
            "vision" or "visionllm" => LayoutDetectionMode.VisionLLM,
            _ => throw new ArgumentException($"Unsupported layout mode '{rawValue}'. Use Auto, Heuristic, OnnxLocal, or VisionLLM.")
        };
    }

    private static PdfExportMode ParsePdfExportMode(string? rawValue)
    {
        return NormalizeToken(rawValue) switch
        {
            "" or "mupdf" or "contentstreamreplacement" or "contentstream" => PdfExportMode.ContentStreamReplacement,
            "overlay" => PdfExportMode.Overlay,
            _ => throw new ArgumentException($"Unsupported PDF export mode '{rawValue}'. Use ContentStreamReplacement or Overlay.")
        };
    }

    private static string? GetEnvironmentValue(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string NormalizeChatCompletionsEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return endpoint;

        var trimmed = endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return $"{trimmed}/chat/completions";
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project dotnet/src/Easydict.WinUI -p:WindowsPackageType=None -p:EnableLocalDebugLongDocCli=true -- --translate-long-doc --input <file> --target-language <lang> [options]");
        Console.WriteLine();
        Console.WriteLine("Required:");
        Console.WriteLine("  -i, --input              Input file (.pdf, .txt, .md).");
        Console.WriteLine("  -t, --target-language    Target language code or name (e.g. zh, en, ja).");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("      --from               Source language. Default: auto");
        Console.WriteLine("  -o, --output             Output file path. Default: <input>_translated.<ext>");
        Console.WriteLine("  -e, --env-file           .env file to load before service initialization.");
        Console.WriteLine("  -s, --service            Translation service id. Default: first configured streaming service.");
        Console.WriteLine("      --output-mode        Monolingual, Bilingual, or Both.");
        Console.WriteLine("      --layout             Auto, Heuristic, OnnxLocal, or VisionLLM.");
        Console.WriteLine("      --pdf-export-mode    ContentStreamReplacement or Overlay.");
        Console.WriteLine("      --page              Single PDF page, e.g. 2.");
        Console.WriteLine("      --page-range        Page range for PDF input, e.g. 1-3,5.");
        Console.WriteLine("      --max-concurrency    1-16.");
        Console.WriteLine("      --vision-endpoint    Vision layout API endpoint.");
        Console.WriteLine("      --vision-api-key     Vision layout API key.");
        Console.WriteLine("      --vision-model       Vision layout model.");
        Console.WriteLine("      --list-services      Print available long-document services and exit.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  powershell -File scripts/translate-long-doc.ps1 -InputFile C:\\docs\\paper.pdf -TargetLanguage zh -EnvFile .env");
        Console.WriteLine("  dotnet run --project dotnet/src/Easydict.WinUI -p:WindowsPackageType=None -p:EnableLocalDebugLongDocCli=true -- --translate-long-doc -i C:\\docs\\paper.pdf -t zh -e .env -s openai");
    }

    private sealed record AliasBinding(string Alias, string PropertyName, Func<string, string>? Transform = null);

    private sealed record Options(
        string InputPath,
        string? OutputPath,
        string? EnvFile,
        string? ServiceId,
        Language SourceLanguage,
        Language TargetLanguage,
        string? OutputMode,
        string? LayoutMode,
        string? PdfExportMode,
        string? PageRange,
        int? MaxConcurrency,
        string? VisionEndpoint,
        string? VisionApiKey,
        string? VisionModel,
        bool ListServices);

    private sealed class ConsoleProgressReporter
    {
        private readonly object _gate = new();
        private DateTime _lastProgressAt = DateTime.MinValue;
        private double _lastPercentage = -1;
        private string? _lastStatus;

        public IProgress<LongDocumentTranslationProgress> Progress => new Progress<LongDocumentTranslationProgress>(ReportProgress);

        public void ReportStatus(string message)
        {
            lock (_gate)
            {
                if (string.Equals(_lastStatus, message, StringComparison.Ordinal))
                    return;

                _lastStatus = message;
                Console.WriteLine($"[status] {message}");
            }
        }

        private void ReportProgress(LongDocumentTranslationProgress progress)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                var percentageDelta = Math.Abs(progress.Percentage - _lastPercentage);
                if ((now - _lastProgressAt).TotalMilliseconds < 500 && percentageDelta < 1)
                    return;

                _lastProgressAt = now;
                _lastPercentage = progress.Percentage;

                var detail = progress.TotalBlocks > 0
                    ? $"{progress.GetStageDisplayName()} {progress.CurrentBlock}/{progress.TotalBlocks} blocks page {progress.CurrentPage}/{progress.TotalPages}"
                    : progress.GetStageDisplayName();
                Console.WriteLine($"[progress] {progress.Percentage:F0}% {detail}");
            }
        }
    }
}
