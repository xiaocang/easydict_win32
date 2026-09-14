using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Easydict.BobPlugin.Package;
using Easydict.BobPlugin.Runtime.Bridges;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace Easydict.BobPlugin.Runtime;

/// <summary>
/// Runs one plugin instance: owns its JavaScript engine and the thread that engine lives on,
/// installs the host bridges, evaluates the plugin, and turns its callbacks into an async stream.
///
/// Threading contract: the engine and every JsValue are touched only on <see cref="JsEventLoop"/>'s
/// thread. Host bridges are registered as delegates taking primitives and must never block; they
/// queue work elsewhere and post results back onto the loop.
/// </summary>
internal sealed class BobScriptHost : IDisposable
{
    private const string PreludeResourceSuffix = "bob-prelude.js";
    private const string CryptoShimResourceSuffix = "crypto-js-shim.js";

    private readonly BobPluginPackage _package;
    private readonly string _sandboxDirectory;
    private readonly IReadOnlyDictionary<string, string> _optionValues;
    private readonly int _sliceTimeoutSeconds;
    private readonly HttpClient _httpClient;
    private readonly IBobHostLogger _logger;

    private readonly JsEventLoop _loop;
    private readonly ConcurrentDictionary<int, BobCallContext> _calls = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);

    private Engine? _engine;
    private TimerBridge? _timers;
    private HttpBridge? _http;
    private FileBridge? _files;
    private bool _poisoned;
    private int _nextCallId;
    private bool _disposed;

    public BobScriptHost(
        BobPluginPackage package,
        string sandboxDirectory,
        IReadOnlyDictionary<string, string> optionValues,
        int sliceTimeoutSeconds,
        HttpClient httpClient,
        IBobHostLogger logger)
    {
        _package = package;
        _sandboxDirectory = sandboxDirectory;
        _optionValues = optionValues;
        _sliceTimeoutSeconds = sliceTimeoutSeconds > 0 ? sliceTimeoutSeconds : 15;
        _httpClient = httpClient;
        _logger = logger;
        _loop = new JsEventLoop($"BobPlugin:{package.Manifest.Identifier}");
    }

    /// <summary>Build the engine if it is not up, or rebuild it after a runaway script was cut off.</summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine is not null && !_poisoned)
            {
                return;
            }

            await _loop.InvokeAsync(InitializeOnLoopThread).ConfigureAwait(false);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    /// <summary>Call the plugin's <c>translate</c> and stream what it reports back.</summary>
    public IAsyncEnumerable<BobCallEvent> CallTranslateAsync(string queryJson, TimeSpan timeout, CancellationToken cancellationToken)
        => CallAsync("__ed_callTranslate", queryJson, timeout, cancellationToken);

    /// <summary>Call the plugin's optional <c>pluginValidate</c>.</summary>
    public IAsyncEnumerable<BobCallEvent> CallValidateAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => CallAsync("__ed_callValidate", payloadJson: null, timeout, cancellationToken);

    /// <summary>The language codes the plugin declares, or an empty list.</summary>
    public async Task<IReadOnlyList<string>> GetSupportedLanguageCodesAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var json = await _loop.InvokeAsync(() => InvokeToString("__ed_supportLanguages")).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The plugin's declared timeout, or <c>null</c> when it does not declare one.</summary>
    public async Task<TimeSpan?> GetDeclaredTimeoutAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var seconds = await _loop.InvokeAsync(() =>
        {
            var value = Invoke("__ed_pluginTimeout");
            return value is not null && value.IsNumber() ? value.AsNumber() : -1d;
        }).ConfigureAwait(false);

        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>True when the plugin exposes the named entry point.</summary>
    public async Task<bool> HasEntryPointAsync(string name, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _loop.InvokeAsync(() =>
        {
            var value = Invoke("__ed_hasEntryPoint", name);
            return value is not null && value.IsBoolean() && value.AsBoolean();
        }).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<BobCallEvent> CallAsync(
        string entryPoint,
        string? payloadJson,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var callId = Interlocked.Increment(ref _nextCallId);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var context = new BobCallContext(callId, linked);
        _calls[callId] = context;

        // Fires for both user cancellation and the per-call timeout; the plugin is told through
        // its cancelSignal, and its in-flight HTTP requests are aborted.
        using var registration = linked.Token.Register(() =>
        {
            if (!context.TryBeginCompletion())
            {
                return;
            }

            context.CancelHttp();
            _loop.Post(() => TryInvoke("__ed_cancel", callId));
            context.CompleteWithError(cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : new TranslationException($"The plugin did not finish within {timeout.TotalSeconds:0.#}s.")
                {
                    ErrorCode = TranslationErrorCode.Timeout
                });
        });

        if (timeout > TimeSpan.Zero)
        {
            linked.CancelAfter(timeout);
        }

        _loop.Post(() =>
        {
            try
            {
                if (payloadJson is null)
                {
                    Invoke(entryPoint, callId);
                }
                else
                {
                    Invoke(entryPoint, callId, payloadJson);
                }
            }
            catch (Exception ex)
            {
                if (context.TryBeginCompletion())
                {
                    context.CompleteWithError(TranslateEngineException(ex));
                }
            }
        });

        try
        {
            await foreach (var callEvent in context.Channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return callEvent;
            }
        }
        finally
        {
            _calls.TryRemove(callId, out _);
            context.Dispose();
        }
    }

    /// <summary>Build the engine, install the bridges and evaluate the plugin. Runs on the loop thread.</summary>
    private void InitializeOnLoopThread()
    {
        _timers?.Dispose();

        var engine = new Engine(options =>
        {
            // Cuts off a plugin that never yields; the call is failed and the engine rebuilt.
            options.TimeoutInterval(TimeSpan.FromSeconds(_sliceTimeoutSeconds));
            options.LimitRecursion(256);
            options.Culture(CultureInfo.InvariantCulture);
        });

        _engine = engine;
        _timers = new TimerBridge(PostCallback);
        _files = new FileBridge(_sandboxDirectory, _package.Directory, _logger);
        _http = new HttpBridge(_httpClient, id => _calls.TryGetValue(id, out var call) ? call : null, PostCallback, _logger);

        RegisterBridges(engine);

        engine.Execute(LoadEmbeddedScript(CryptoShimResourceSuffix));
        engine.Execute(LoadEmbeddedScript(PreludeResourceSuffix));
        InstallPluginGlobals(engine);

        try
        {
            // Evaluated at global scope: plugins define either top-level functions or exports.x.
            engine.Execute(File.ReadAllText(_package.MainScriptPath));
        }
        catch (JavaScriptException ex)
        {
            throw new BobPluginException(BobPluginError.ScriptLoadFailed, $"main.js failed to load: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not BobPluginException)
        {
            throw new BobPluginException(BobPluginError.ScriptLoadFailed, $"main.js could not be evaluated: {ex.Message}", ex);
        }

        JintCompat.ProcessPendingJobs(engine);
        _poisoned = false;
    }

    private void RegisterBridges(Engine engine)
    {
        engine.SetValue("__ed_log", new Action<string, string>((level, message) => _logger.Log(level, message)));

        engine.SetValue("__ed_utf8ToB64", new Func<string, string>(DataCodec.Utf8ToBase64));
        engine.SetValue("__ed_b64ToUtf8", new Func<string, string>(DataCodec.Base64ToUtf8));
        engine.SetValue("__ed_b64ToHex", new Func<string, string>(DataCodec.Base64ToHex));
        engine.SetValue("__ed_hexToB64", new Func<string, string>(DataCodec.HexToBase64));
        engine.SetValue("__ed_latin1ToB64", new Func<string, string>(DataCodec.Latin1ToBase64));
        engine.SetValue("__ed_b64ToLatin1", new Func<string, string>(DataCodec.Base64ToLatin1));
        engine.SetValue("__ed_b64Length", new Func<string, double>(b64 => DataCodec.FromBase64(b64).Length));

        engine.SetValue("__ed_hash", new Func<string, string, string>(CryptoBridge.Hash));
        engine.SetValue("__ed_hmac", new Func<string, string, string, string>(CryptoBridge.Hmac));
        engine.SetValue("__ed_aes", new Func<bool, string, string, string, string>(CryptoBridge.Aes));

        engine.SetValue("__ed_setTimeout", new Func<JsValue?, double, double>((fn, ms) => _timers!.SetTimeout(fn, ms)));
        engine.SetValue("__ed_clearTimeout", new Action<double>(id => _timers!.ClearTimeout(id)));

        engine.SetValue("__ed_http", new Action<double, string, JsValue?, JsValue?>(
            (callId, optionsJson, streamCallback, doneCallback) => _http!.Send(callId, optionsJson, streamCallback, doneCallback)));

        engine.SetValue("__ed_readModule", new Func<string, string?>(ReadModuleSource));

        engine.SetValue("__ed_fileRead", new Func<string, string?>(path => _files!.ReadBase64(path)));
        engine.SetValue("__ed_fileWrite", new Func<string, string, bool>((path, base64) => _files!.WriteBase64(path, base64)));
        engine.SetValue("__ed_fileExists", new Func<string, bool>(path => _files!.Exists(path)));
        engine.SetValue("__ed_fileDelete", new Func<string, bool>(path => _files!.Delete(path)));
        engine.SetValue("__ed_fileList", new Func<string, string>(path => _files!.List(path)));
        engine.SetValue("__ed_fileMkdir", new Func<string, bool>(path => _files!.CreateDirectory(path)));

        engine.SetValue("__ed_onStream", new Action<double, string>(OnStream));
        engine.SetValue("__ed_onCompletion", new Action<double, string>(OnCompletion));
    }

    /// <summary>Define $option, $info and $env for the plugin.</summary>
    private void InstallPluginGlobals(Engine engine)
    {
        var manifest = _package.Manifest;
        var info = new Dictionary<string, string?>
        {
            ["identifier"] = manifest.Identifier,
            ["version"] = manifest.Version,
            ["name"] = manifest.Name,
            ["summary"] = manifest.Summary,
            ["author"] = manifest.Author,
            ["homepage"] = manifest.Homepage,
            ["category"] = manifest.Category
        };

        // Bob's own $env carries host details; verify the exact shape against
        // https://bobtranslate.com/plugin if a plugin turns out to depend on more than this.
        var env = new Dictionary<string, string>
        {
            ["platform"] = "windows",
            ["host"] = "easydict",
            ["locale"] = CultureInfo.CurrentUICulture.Name
        };

        engine.Execute($"var $option = {JsonSerializer.Serialize(_optionValues)};");
        engine.Execute($"var $info = {JsonSerializer.Serialize(info)};");
        engine.Execute($"var $env = {JsonSerializer.Serialize(env)};");
    }

    private void OnStream(double callId, string payloadJson)
    {
        if (_calls.TryGetValue((int)callId, out var context))
        {
            context.PublishStream(payloadJson);
        }
    }

    private void OnCompletion(double callId, string payloadJson)
    {
        if (_calls.TryGetValue((int)callId, out var context) && context.TryBeginCompletion())
        {
            context.PublishCompletion(payloadJson);
        }
    }

    /// <summary>Invoke a JS callback with one JSON string argument, on the loop thread.</summary>
    private void PostCallback(JsValue? callback, string json)
    {
        if (callback is null || callback.IsNull() || callback.IsUndefined())
        {
            return;
        }

        _loop.Post(() =>
        {
            var engine = _engine;
            if (engine is null)
            {
                return;
            }

            try
            {
                engine.Invoke(callback, json);
                JintCompat.ProcessPendingJobs(engine);
            }
            catch (Exception ex)
            {
                _logger.Log("error", $"A plugin callback threw: {DescribeEngineException(ex)}");
                MarkPoisonedIfFatal(ex);
            }
        });
    }

    /// <summary>Source of a module the plugin required, or <c>null</c> when it is not in the package.</summary>
    private string? ReadModuleSource(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var relative = name.Replace('\\', '/').TrimStart('.', '/');
        if (!Path.HasExtension(relative))
        {
            relative += ".js";
        }

        var candidate = Path.GetFullPath(Path.Combine(_package.Directory, relative));
        var root = _package.Directory.EndsWith(Path.DirectorySeparatorChar)
            ? _package.Directory
            : _package.Directory + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(candidate);
        }
        catch (IOException ex)
        {
            _logger.Log("warn", $"require('{name}') failed: {ex.Message}");
            return null;
        }
    }

    private JsValue? Invoke(string function, params object?[] arguments)
    {
        var engine = _engine;
        if (engine is null)
        {
            return null;
        }

        var result = engine.Invoke(function, arguments);
        JintCompat.ProcessPendingJobs(engine);
        return result;
    }

    private void TryInvoke(string function, params object?[] arguments)
    {
        try
        {
            Invoke(function, arguments);
        }
        catch (Exception ex)
        {
            _logger.Log("warn", $"{function} failed: {DescribeEngineException(ex)}");
            MarkPoisonedIfFatal(ex);
        }
    }

    private string? InvokeToString(string function)
    {
        var value = Invoke(function);
        return value is null || value.IsNull() || value.IsUndefined() ? null : value.AsString();
    }

    /// <summary>
    /// Turn an engine failure into the host's exception type. A script cut off by the slice timeout
    /// leaves the engine in an unknown state, so it is also marked for rebuild.
    /// </summary>
    private TranslationException TranslateEngineException(Exception ex)
    {
        MarkPoisonedIfFatal(ex);

        if (IsTimeout(ex))
        {
            return new TranslationException("The plugin script ran too long without yielding.", ex)
            {
                ErrorCode = TranslationErrorCode.Timeout
            };
        }

        return new TranslationException($"The plugin failed: {DescribeEngineException(ex)}", ex)
        {
            ErrorCode = TranslationErrorCode.Unknown
        };
    }

    private void MarkPoisonedIfFatal(Exception ex)
    {
        if (IsTimeout(ex))
        {
            _poisoned = true;
        }
    }

    /// <summary>
    /// Recognise the engine's execution-timeout signal without depending on its exact type name,
    /// which differs between engine versions.
    /// </summary>
    private static bool IsTimeout(Exception ex)
        => ex is TimeoutException
            || ex.GetType().Name.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || (ex.InnerException is not null && IsTimeout(ex.InnerException));

    private static string DescribeEngineException(Exception ex)
        => ex is JavaScriptException jsException ? jsException.Message : ex.Message;

    private static string LoadEmbeddedScript(string resourceSuffix)
    {
        var assembly = typeof(BobScriptHost).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith(resourceSuffix, StringComparison.Ordinal))
            ?? throw new BobPluginException(BobPluginError.ScriptLoadFailed, $"Embedded script '{resourceSuffix}' is missing from the build.");

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new BobPluginException(BobPluginError.ScriptLoadFailed, $"Embedded script '{name}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var context in _calls.Values)
        {
            if (context.TryBeginCompletion())
            {
                context.CompleteWithError(new ObjectDisposedException(nameof(BobScriptHost)));
            }

            context.Dispose();
        }

        _calls.Clear();
        _timers?.Dispose();
        _loop.Post(() => _engine = null);
        _loop.Dispose();
        _initializationGate.Dispose();
    }
}
