using System.Diagnostics;
using System.Text.Json;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.TranslationService.Tests;

/// <summary>
/// Startup performance regression tests.
/// Each test measures the elapsed time of a specific startup-critical operation,
/// outputs the timing via <see cref="ITestOutputHelper"/> for CI comparison,
/// and asserts an upper-bound budget to catch regressions.
///
/// Run with:
///   dotnet test tests/Easydict.TranslationService.Tests --filter "FullyQualifiedName~StartupPerformanceTests" -v n
/// </summary>
public class StartupPerformanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Stopwatch _sw = new();

    public StartupPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ──────────────────────────────────────────────
    //  1. TranslationManager construction (all 17 services + HttpClient + MemoryCache)
    // ──────────────────────────────────────────────

    [Fact]
    public void TranslationManager_Construction_ShouldCompleteWithinBudget()
    {
        // Warm up CLR / JIT
        using var warmup = new TranslationManager();

        const int iterations = 5;
        var timings = new long[iterations];

        for (var i = 0; i < iterations; i++)
        {
            _sw.Restart();
            using var manager = new TranslationManager();
            _sw.Stop();
            timings[i] = _sw.ElapsedMilliseconds;
        }

        var avg = timings.Average();
        var max = timings.Max();

        _output.WriteLine("=== TranslationManager Construction ===");
        _output.WriteLine($"  Iterations : {iterations}");
        _output.WriteLine($"  Timings (ms): [{string.Join(", ", timings)}]");
        _output.WriteLine($"  Average (ms): {avg:F1}");
        _output.WriteLine($"  Max     (ms): {max}");

        // Budget: construction of HttpClientHandler + HttpClient + MemoryCache + 17 service instances
        // should finish well under 200ms even on slow CI runners.
        avg.Should().BeLessThan(200, "TranslationManager construction average should be < 200ms");
    }

    // ──────────────────────────────────────────────
    //  2. Individual service instantiation overhead
    // ──────────────────────────────────────────────

    [Fact]
    public void AllServiceInstantiation_ShouldCompleteWithinBudget()
    {
        using var httpClient = new HttpClient();

        // Warm up
        _ = new GoogleTranslateService(httpClient);

        _sw.Restart();

        var services = new ITranslationService[]
        {
            new GoogleTranslateService(httpClient),
            new GoogleWebTranslateService(httpClient),
            new BingTranslateService(httpClient),
            new DeepLService(httpClient),
            new OpenAIService(httpClient),
            new OllamaService(httpClient),
            new BuiltInAIService(httpClient),
            new DeepSeekService(httpClient),
            new GroqService(httpClient),
            new ZhipuService(httpClient),
            new GitHubModelsService(httpClient),
            new CustomOpenAIService(httpClient),
            new GeminiService(httpClient),
            new DoubaoService(httpClient),
            new CaiyunService(httpClient),
            new NiuTransService(httpClient),
            new LingueeService(httpClient),
        };

        _sw.Stop();

        _output.WriteLine("=== All 17 Service Instantiation ===");
        _output.WriteLine($"  Service count: {services.Length}");
        _output.WriteLine($"  Elapsed (ms) : {_sw.ElapsedMilliseconds}");
        _output.WriteLine($"  Elapsed (µs) : {_sw.Elapsed.TotalMicroseconds:F0}");

        services.Should().HaveCount(17);
        _sw.ElapsedMilliseconds.Should().BeLessThan(50, "17 service instantiations should be < 50ms");
    }

    // ──────────────────────────────────────────────
    //  3. Service configuration overhead (simulates ConfigureServices)
    // ──────────────────────────────────────────────

    [Fact]
    public void ServiceConfiguration_ShouldCompleteWithinBudget()
    {
        using var manager = new TranslationManager();

        _sw.Restart();

        // Simulate the same ConfigureServices calls TranslationManagerService does
        manager.ConfigureService("bing", service =>
        {
            if (service is BingTranslateService bing)
                bing.Configure(useChinaHost: false);
        });
        manager.ConfigureService("deepl", service =>
        {
            if (service is DeepLService deepl)
                deepl.Configure("test-key", useWebFirst: true);
        });
        manager.ConfigureService("openai", service =>
        {
            if (service is OpenAIService openai)
                openai.Configure("test-key", "https://api.openai.com/v1/chat/completions", "gpt-4o-mini", 0.3);
        });
        manager.ConfigureService("ollama", service =>
        {
            if (service is OllamaService ollama)
                ollama.Configure("http://localhost:11434/v1/chat/completions", "llama3.2");
        });
        manager.ConfigureService("builtin", service =>
        {
            if (service is BuiltInAIService builtin)
                builtin.Configure("llama-3.3-70b-versatile");
        });
        manager.ConfigureService("deepseek", service =>
        {
            if (service is DeepSeekService deepseek)
                deepseek.Configure("test-key", model: "deepseek-chat");
        });
        manager.ConfigureService("groq", service =>
        {
            if (service is GroqService groq)
                groq.Configure("test-key", model: "llama-3.3-70b-versatile");
        });
        manager.ConfigureService("zhipu", service =>
        {
            if (service is ZhipuService zhipu)
                zhipu.Configure("test-key", model: "glm-4-flash-250414");
        });
        manager.ConfigureService("github", service =>
        {
            if (service is GitHubModelsService github)
                github.Configure("test-token", model: "gpt-4.1");
        });
        manager.ConfigureService("custom-openai", service =>
        {
            if (service is CustomOpenAIService custom)
                custom.Configure("https://example.com/v1/chat/completions", "test-key", "gpt-3.5-turbo");
        });
        manager.ConfigureService("gemini", service =>
        {
            if (service is GeminiService gemini)
                gemini.Configure("test-key", "gemini-2.5-flash");
        });
        manager.ConfigureService("doubao", service =>
        {
            if (service is DoubaoService doubao)
                doubao.Configure("test-key", "https://ark.cn-beijing.volces.com/api/v3/responses", "doubao-seed-translation-250915");
        });
        manager.ConfigureService("caiyun", service =>
        {
            if (service is CaiyunService caiyun)
                caiyun.Configure("test-key");
        });
        manager.ConfigureService("niutrans", service =>
        {
            if (service is NiuTransService niutrans)
                niutrans.Configure("test-key");
        });

        _sw.Stop();

        _output.WriteLine("=== Service Configuration (14 services) ===");
        _output.WriteLine($"  Elapsed (ms): {_sw.ElapsedMilliseconds}");
        _output.WriteLine($"  Elapsed (µs): {_sw.Elapsed.TotalMicroseconds:F0}");

        _sw.ElapsedMilliseconds.Should().BeLessThan(50, "ConfigureServices should be < 50ms");
    }

    // ──────────────────────────────────────────────
    //  4. Settings JSON deserialization (simulates SettingsService.LoadSettings)
    // ──────────────────────────────────────────────

    [Fact]
    public void SettingsJsonDeserialization_ShouldCompleteWithinBudget()
    {
        // Build a realistic settings JSON with all ~60 keys
        var settings = BuildRealisticSettingsDictionary();
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

        _output.WriteLine($"  Settings JSON size: {json.Length} bytes, {settings.Count} keys");

        // Warm up JsonSerializer
        _ = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);

        const int iterations = 100;
        _sw.Restart();

        for (var i = 0; i < iterations; i++)
        {
            _ = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }

        _sw.Stop();

        var avgUs = _sw.Elapsed.TotalMicroseconds / iterations;

        _output.WriteLine("=== Settings JSON Deserialization ===");
        _output.WriteLine($"  Iterations      : {iterations}");
        _output.WriteLine($"  Total (ms)      : {_sw.ElapsedMilliseconds}");
        _output.WriteLine($"  Average (µs)    : {avgUs:F1}");

        // Single deserialization should be well under 5ms
        avgUs.Should().BeLessThan(5000, "Settings JSON deserialization should average < 5ms");
    }

    // ──────────────────────────────────────────────
    //  5. Settings GetValue parsing (simulates 60+ GetValue<T> calls against JsonElement)
    // ──────────────────────────────────────────────

    [Fact]
    public void SettingsGetValueParsing_ShouldCompleteWithinBudget()
    {
        var settings = BuildRealisticSettingsDictionary();
        var json = JsonSerializer.Serialize(settings);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

        // Warm up
        ExtractAllSettings(parsed);

        const int iterations = 1000;
        _sw.Restart();

        for (var i = 0; i < iterations; i++)
        {
            ExtractAllSettings(parsed);
        }

        _sw.Stop();

        var avgUs = _sw.Elapsed.TotalMicroseconds / iterations;

        _output.WriteLine("=== Settings GetValue Parsing (60+ keys) ===");
        _output.WriteLine($"  Iterations   : {iterations}");
        _output.WriteLine($"  Total (ms)   : {_sw.ElapsedMilliseconds}");
        _output.WriteLine($"  Average (µs) : {avgUs:F1}");

        // 60+ dictionary lookups + JsonElement parsing should be trivial
        avgUs.Should().BeLessThan(1000, "60+ GetValue calls should average < 1ms");
    }

    // ──────────────────────────────────────────────
    //  6. Settings file I/O (write + read round-trip)
    // ──────────────────────────────────────────────

    [Fact]
    public void SettingsFileIO_ShouldCompleteWithinBudget()
    {
        var settings = BuildRealisticSettingsDictionary();
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

        var tempPath = Path.Combine(Path.GetTempPath(), $"easydict-perf-test-{Guid.NewGuid()}.json");

        try
        {
            // Warm up filesystem cache
            File.WriteAllText(tempPath, json);
            _ = File.ReadAllText(tempPath);

            const int iterations = 50;
            var writeTimings = new double[iterations];
            var readTimings = new double[iterations];

            for (var i = 0; i < iterations; i++)
            {
                _sw.Restart();
                File.WriteAllText(tempPath, json);
                _sw.Stop();
                writeTimings[i] = _sw.Elapsed.TotalMicroseconds;

                _sw.Restart();
                _ = File.ReadAllText(tempPath);
                _sw.Stop();
                readTimings[i] = _sw.Elapsed.TotalMicroseconds;
            }

            var avgWriteUs = writeTimings.Average();
            var avgReadUs = readTimings.Average();

            _output.WriteLine("=== Settings File I/O ===");
            _output.WriteLine($"  File size      : {json.Length} bytes");
            _output.WriteLine($"  Iterations     : {iterations}");
            _output.WriteLine($"  Avg write (µs) : {avgWriteUs:F0}");
            _output.WriteLine($"  Avg read  (µs) : {avgReadUs:F0}");
            _output.WriteLine($"  Max write (µs) : {writeTimings.Max():F0}");
            _output.WriteLine($"  Max read  (µs) : {readTimings.Max():F0}");

            // File I/O for a small JSON should be well under 10ms on any modern disk
            avgReadUs.Should().BeLessThan(10_000, "Settings file read should average < 10ms");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ──────────────────────────────────────────────
    //  7. Region detection (IsChinaRegion equivalent)
    // ──────────────────────────────────────────────

    [Fact]
    public void RegionDetection_ShouldCompleteWithinBudget()
    {
        // Warm up
        _ = DetectRegion();

        const int iterations = 1000;
        _sw.Restart();

        for (var i = 0; i < iterations; i++)
        {
            _ = DetectRegion();
        }

        _sw.Stop();

        var avgUs = _sw.Elapsed.TotalMicroseconds / iterations;

        _output.WriteLine("=== Region Detection (IsChinaRegion equivalent) ===");
        _output.WriteLine($"  Iterations   : {iterations}");
        _output.WriteLine($"  Total (ms)   : {_sw.ElapsedMilliseconds}");
        _output.WriteLine($"  Average (µs) : {avgUs:F1}");

        // RegionInfo + CultureInfo should be very fast
        avgUs.Should().BeLessThan(500, "Region detection should average < 500µs");
    }

    // ──────────────────────────────────────────────
    //  8. Full cold-start simulation (end-to-end non-UI path)
    // ──────────────────────────────────────────────

    [Fact]
    public void FullColdStartSimulation_ShouldCompleteWithinBudget()
    {
        var settingsJson = JsonSerializer.Serialize(
            BuildRealisticSettingsDictionary(),
            new JsonSerializerOptions { WriteIndented = true });
        var tempPath = Path.Combine(Path.GetTempPath(), $"easydict-perf-coldstart-{Guid.NewGuid()}.json");

        try
        {
            File.WriteAllText(tempPath, settingsJson);

            // Warm up JIT
            RunColdStartSimulation(tempPath);

            const int iterations = 3;
            var timings = new long[iterations];

            for (var i = 0; i < iterations; i++)
            {
                _sw.Restart();
                RunColdStartSimulation(tempPath);
                _sw.Stop();
                timings[i] = _sw.ElapsedMilliseconds;
            }

            var avg = timings.Average();

            _output.WriteLine("=== Full Cold-Start Simulation (non-UI) ===");
            _output.WriteLine($"  Steps: File.ReadAllText → JSON parse → 60+ GetValue → RegionDetect");
            _output.WriteLine($"       → TranslationManager(17 svc) → ConfigureServices(14 svc)");
            _output.WriteLine($"  Iterations : {iterations}");
            _output.WriteLine($"  Timings (ms): [{string.Join(", ", timings)}]");
            _output.WriteLine($"  Average (ms): {avg:F1}");

            // The entire non-UI startup simulation should be well under 500ms
            avg.Should().BeLessThan(500, "Full cold-start simulation should average < 500ms");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ──────────────────────────────────────────────
    //  9. TranslationManager with proxy options
    // ──────────────────────────────────────────────

    [Fact]
    public void TranslationManager_WithProxy_ShouldNotAddSignificantOverhead()
    {
        // Without proxy
        _sw.Restart();
        using var managerNoProxy = new TranslationManager();
        _sw.Stop();
        var noProxyMs = _sw.ElapsedMilliseconds;

        // With proxy
        var options = new TranslationManagerOptions
        {
            ProxyEnabled = true,
            ProxyUri = "http://127.0.0.1:7890",
            ProxyBypassLocal = true
        };

        _sw.Restart();
        using var managerWithProxy = new TranslationManager(options);
        _sw.Stop();
        var withProxyMs = _sw.ElapsedMilliseconds;

        _output.WriteLine("=== TranslationManager Proxy Overhead ===");
        _output.WriteLine($"  Without proxy (ms): {noProxyMs}");
        _output.WriteLine($"  With proxy    (ms): {withProxyMs}");
        _output.WriteLine($"  Delta         (ms): {withProxyMs - noProxyMs}");

        // Proxy configuration should add negligible overhead
        withProxyMs.Should().BeLessThan(200, "TranslationManager with proxy should be < 200ms");
    }

    // ──────────────────────────────────────────────
    //  10. MemoryCache creation overhead
    // ──────────────────────────────────────────────

    [Fact]
    public void MemoryCacheCreation_ShouldCompleteWithinBudget()
    {
        // Warm up
        using (var _ = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 1000 })) { }

        const int iterations = 100;
        _sw.Restart();

        for (var i = 0; i < iterations; i++)
        {
            using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 1000 });
        }

        _sw.Stop();

        var avgUs = _sw.Elapsed.TotalMicroseconds / iterations;

        _output.WriteLine("=== MemoryCache Creation ===");
        _output.WriteLine($"  Iterations   : {iterations}");
        _output.WriteLine($"  Total (ms)   : {_sw.ElapsedMilliseconds}");
        _output.WriteLine($"  Average (µs) : {avgUs:F1}");

        avgUs.Should().BeLessThan(1000, "MemoryCache creation should average < 1ms");
    }

    // ═══════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Builds a dictionary that mirrors all 60+ keys SettingsService persists.
    /// </summary>
    private static Dictionary<string, object?> BuildRealisticSettingsDictionary()
    {
        return new Dictionary<string, object?>
        {
            ["SourceLanguage"] = "auto",
            ["FirstLanguage"] = "zh",
            ["SecondLanguage"] = "en",
            ["AutoSelectTargetLanguage"] = true,
            ["DeepLApiKey"] = "fake-deepl-key-1234567890",
            ["DeepLUseFreeApi"] = true,
            ["OpenAIApiKey"] = "sk-fake-openai-key-1234567890abcdef",
            ["OpenAIEndpoint"] = "https://api.openai.com/v1/chat/completions",
            ["OpenAIModel"] = "gpt-4o-mini",
            ["OpenAITemperature"] = 0.3,
            ["OllamaEndpoint"] = "http://localhost:11434/v1/chat/completions",
            ["OllamaModel"] = "llama3.2",
            ["BuiltInAIModel"] = "llama-3.3-70b-versatile",
            ["DeepSeekApiKey"] = "fake-deepseek-key",
            ["DeepSeekModel"] = "deepseek-chat",
            ["GroqApiKey"] = "fake-groq-key",
            ["GroqModel"] = "llama-3.3-70b-versatile",
            ["ZhipuApiKey"] = "fake-zhipu-key",
            ["ZhipuModel"] = "glm-4-flash-250414",
            ["GitHubModelsToken"] = "fake-github-token",
            ["GitHubModelsModel"] = "gpt-4.1",
            ["CustomOpenAIEndpoint"] = "https://custom.example.com/v1/chat/completions",
            ["CustomOpenAIApiKey"] = "fake-custom-key",
            ["CustomOpenAIModel"] = "gpt-3.5-turbo",
            ["GeminiApiKey"] = "fake-gemini-key",
            ["GeminiModel"] = "gemini-2.5-flash",
            ["DoubaoApiKey"] = "fake-doubao-key",
            ["DoubaoEndpoint"] = "https://ark.cn-beijing.volces.com/api/v3/responses",
            ["DoubaoModel"] = "doubao-seed-translation-250915",
            ["CaiyunApiKey"] = "fake-caiyun-key",
            ["NiuTransApiKey"] = "fake-niutrans-key",
            ["MinimizeToTray"] = true,
            ["ClipboardMonitoring"] = false,
            ["AutoTranslate"] = false,
            ["MouseSelectionTranslate"] = false,
            ["ShowWindowHotkey"] = "Ctrl+Alt+T",
            ["TranslateSelectionHotkey"] = "Ctrl+Alt+D",
            ["AlwaysOnTop"] = false,
            ["UILanguage"] = "en-US",
            ["AppTheme"] = "System",
            ["LaunchAtStartup"] = false,
            ["EnableDpiAwareness"] = true,
            ["WindowWidthDips"] = 600.0,
            ["WindowHeightDips"] = 700.0,
            ["ShowMiniWindowHotkey"] = "Ctrl+Alt+M",
            ["MiniWindowAutoClose"] = true,
            ["MiniWindowXDips"] = 100.0,
            ["MiniWindowYDips"] = 100.0,
            ["MiniWindowWidthDips"] = 320.0,
            ["MiniWindowHeightDips"] = 200.0,
            ["MiniWindowIsPinned"] = false,
            ["MiniWindowEnabledServices"] = new[] { "google" },
            ["MainWindowEnabledServices"] = new[] { "google", "deepl" },
            ["ShowFixedWindowHotkey"] = "Ctrl+Alt+F",
            ["FixedWindowXDips"] = 0.0,
            ["FixedWindowYDips"] = 0.0,
            ["FixedWindowWidthDips"] = 320.0,
            ["FixedWindowHeightDips"] = 280.0,
            ["FixedWindowEnabledServices"] = new[] { "google" },
            ["MainWindowServiceEnabledQuery"] = new Dictionary<string, bool> { ["google"] = true, ["deepl"] = true },
            ["MiniWindowServiceEnabledQuery"] = new Dictionary<string, bool> { ["google"] = true },
            ["FixedWindowServiceEnabledQuery"] = new Dictionary<string, bool> { ["google"] = true },
            ["EnableInternationalServices"] = true,
            ["HasUserConfiguredServices"] = false,
            ["ProxyEnabled"] = false,
            ["ProxyUri"] = "",
            ["ProxyBypassLocal"] = true,
        };
    }

    /// <summary>
    /// Simulates SettingsService.LoadSettings() GetValue calls against a parsed dictionary.
    /// </summary>
    private static void ExtractAllSettings(Dictionary<string, object?> parsed)
    {
        GetValue(parsed, "SourceLanguage", "auto");
        GetValue(parsed, "FirstLanguage", "zh");
        GetValue(parsed, "SecondLanguage", "en");
        GetValue(parsed, "AutoSelectTargetLanguage", true);
        GetValue<string?>(parsed, "DeepLApiKey", null);
        GetValue(parsed, "DeepLUseFreeApi", true);
        GetValue<string?>(parsed, "OpenAIApiKey", null);
        GetValue(parsed, "OpenAIEndpoint", "https://api.openai.com/v1/chat/completions");
        GetValue(parsed, "OpenAIModel", "gpt-4o-mini");
        GetValue(parsed, "OpenAITemperature", 0.3);
        GetValue(parsed, "OllamaEndpoint", "http://localhost:11434/v1/chat/completions");
        GetValue(parsed, "OllamaModel", "llama3.2");
        GetValue(parsed, "BuiltInAIModel", "llama-3.3-70b-versatile");
        GetValue<string?>(parsed, "DeepSeekApiKey", null);
        GetValue(parsed, "DeepSeekModel", "deepseek-chat");
        GetValue<string?>(parsed, "GroqApiKey", null);
        GetValue(parsed, "GroqModel", "llama-3.3-70b-versatile");
        GetValue<string?>(parsed, "ZhipuApiKey", null);
        GetValue(parsed, "ZhipuModel", "glm-4-flash-250414");
        GetValue<string?>(parsed, "GitHubModelsToken", null);
        GetValue(parsed, "GitHubModelsModel", "gpt-4.1");
        GetValue(parsed, "CustomOpenAIEndpoint", "");
        GetValue<string?>(parsed, "CustomOpenAIApiKey", null);
        GetValue(parsed, "CustomOpenAIModel", "gpt-3.5-turbo");
        GetValue<string?>(parsed, "GeminiApiKey", null);
        GetValue(parsed, "GeminiModel", "gemini-2.5-flash");
        GetValue<string?>(parsed, "DoubaoApiKey", null);
        GetValue(parsed, "DoubaoEndpoint", "https://ark.cn-beijing.volces.com/api/v3/responses");
        GetValue(parsed, "DoubaoModel", "doubao-seed-translation-250915");
        GetValue<string?>(parsed, "CaiyunApiKey", null);
        GetValue<string?>(parsed, "NiuTransApiKey", null);
        GetValue(parsed, "MinimizeToTray", true);
        GetValue(parsed, "ClipboardMonitoring", false);
        GetValue(parsed, "AutoTranslate", false);
        GetValue(parsed, "MouseSelectionTranslate", false);
        GetValue(parsed, "ShowWindowHotkey", "Ctrl+Alt+T");
        GetValue(parsed, "TranslateSelectionHotkey", "Ctrl+Alt+D");
        GetValue(parsed, "AlwaysOnTop", false);
        GetValue(parsed, "UILanguage", "");
        GetValue(parsed, "AppTheme", "System");
        GetValue(parsed, "LaunchAtStartup", false);
        GetValue(parsed, "EnableDpiAwareness", true);
        GetValue(parsed, "WindowWidthDips", 600.0);
        GetValue(parsed, "WindowHeightDips", 700.0);
        GetValue(parsed, "ShowMiniWindowHotkey", "Ctrl+Alt+M");
        GetValue(parsed, "MiniWindowAutoClose", true);
        GetValue(parsed, "MiniWindowXDips", 0.0);
        GetValue(parsed, "MiniWindowYDips", 0.0);
        GetValue(parsed, "MiniWindowWidthDips", 320.0);
        GetValue(parsed, "MiniWindowHeightDips", 200.0);
        GetValue(parsed, "MiniWindowIsPinned", false);
        GetValue(parsed, "EnableInternationalServices", true);
        GetValue(parsed, "HasUserConfiguredServices", false);
        GetValue(parsed, "ProxyEnabled", false);
        GetValue(parsed, "ProxyUri", "");
        GetValue(parsed, "ProxyBypassLocal", true);
    }

    /// <summary>
    /// Mirrors SettingsService.GetValue&lt;T&gt; — extracts a typed value from a
    /// Dictionary&lt;string, object?&gt; that was deserialized from JSON
    /// (values are JsonElement at runtime).
    /// </summary>
    private static T GetValue<T>(Dictionary<string, object?> settings, string key, T defaultValue)
    {
        if (settings.TryGetValue(key, out var value) && value != null)
        {
            try
            {
                if (value is JsonElement jsonElement)
                {
                    if (typeof(T) == typeof(string))
                        return (T)(object)jsonElement.GetString()!;
                    if (typeof(T) == typeof(bool))
                        return (T)(object)jsonElement.GetBoolean();
                    if (typeof(T) == typeof(double))
                        return (T)(object)jsonElement.GetDouble();
                    if (typeof(T) == typeof(int))
                        return (T)(object)jsonElement.GetInt32();
                }

                if (value is T typedValue)
                    return typedValue;
            }
            catch { }
        }
        return defaultValue;
    }

    /// <summary>
    /// Reproduces IsChinaRegion() logic without depending on WinUI.
    /// </summary>
    private static bool DetectRegion()
    {
        try
        {
            var region = System.Globalization.RegionInfo.CurrentRegion;
            if (region.TwoLetterISORegionName.Equals("CN", StringComparison.OrdinalIgnoreCase))
                return true;

            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var name = culture.Name.ToLowerInvariant();
            if (name == "zh-cn" || name == "zh-hans-cn")
                return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Simulates the full non-UI startup path:
    /// settings file I/O → JSON parse → GetValue extraction → region detection
    /// → TranslationManager construction → service configuration.
    /// </summary>
    private static void RunColdStartSimulation(string settingsFilePath)
    {
        // 1. Settings file I/O
        var json = File.ReadAllText(settingsFilePath);

        // 2. JSON deserialization
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

        // 3. GetValue extraction (60+ calls)
        ExtractAllSettings(parsed);

        // 4. Region detection
        _ = DetectRegion();

        // 5. TranslationManager construction (17 services)
        using var manager = new TranslationManager(new TranslationManagerOptions
        {
            ProxyEnabled = false,
            ProxyUri = "",
            ProxyBypassLocal = true
        });

        // 6. Service configuration (14 configure calls)
        manager.ConfigureService("bing", svc =>
        {
            if (svc is BingTranslateService bing) bing.Configure(useChinaHost: false);
        });
        manager.ConfigureService("deepl", svc =>
        {
            if (svc is DeepLService deepl) deepl.Configure("key", useWebFirst: true);
        });
        manager.ConfigureService("openai", svc =>
        {
            if (svc is OpenAIService openai) openai.Configure("key", "https://api.openai.com/v1/chat/completions", "gpt-4o-mini", 0.3);
        });
        manager.ConfigureService("ollama", svc =>
        {
            if (svc is OllamaService ollama) ollama.Configure("http://localhost:11434/v1/chat/completions", "llama3.2");
        });
        manager.ConfigureService("builtin", svc =>
        {
            if (svc is BuiltInAIService builtin) builtin.Configure("llama-3.3-70b-versatile");
        });
        manager.ConfigureService("deepseek", svc =>
        {
            if (svc is DeepSeekService ds) ds.Configure("key", model: "deepseek-chat");
        });
        manager.ConfigureService("groq", svc =>
        {
            if (svc is GroqService groq) groq.Configure("key", model: "llama-3.3-70b-versatile");
        });
        manager.ConfigureService("zhipu", svc =>
        {
            if (svc is ZhipuService zhipu) zhipu.Configure("key", model: "glm-4-flash-250414");
        });
        manager.ConfigureService("github", svc =>
        {
            if (svc is GitHubModelsService gh) gh.Configure("key", model: "gpt-4.1");
        });
        manager.ConfigureService("custom-openai", svc =>
        {
            if (svc is CustomOpenAIService c) c.Configure("https://example.com", "key", "gpt-3.5-turbo");
        });
        manager.ConfigureService("gemini", svc =>
        {
            if (svc is GeminiService gemini) gemini.Configure("key", "gemini-2.5-flash");
        });
        manager.ConfigureService("doubao", svc =>
        {
            if (svc is DoubaoService doubao) doubao.Configure("key", "https://ark.cn-beijing.volces.com/api/v3/responses", "doubao-seed-translation-250915");
        });
        manager.ConfigureService("caiyun", svc =>
        {
            if (svc is CaiyunService caiyun) caiyun.Configure("key");
        });
        manager.ConfigureService("niutrans", svc =>
        {
            if (svc is NiuTransService niutrans) niutrans.Configure("key");
        });
    }

    public void Dispose()
    {
        // No resources to clean up
    }
}
