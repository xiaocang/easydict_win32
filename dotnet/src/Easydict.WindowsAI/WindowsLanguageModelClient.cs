using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Collections;
using System.Globalization;
using Easydict.WindowsAI.Services;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;
using Microsoft.Win32;

namespace Easydict.WindowsAI;

/// <summary>
/// Real <see cref="IWindowsLanguageModelClient"/> backed by the in-box
/// <c>Microsoft.Windows.AI.Text.LanguageModel</c> (Phi Silica) WinRT API.
/// Only callable on a Copilot+ PC; on other hardware, <see cref="GetReadyState"/>
/// returns a non-Ready state and the consumer surfaces a friendly error.
/// </summary>
public sealed class WindowsLanguageModelClient : IWindowsLanguageModelClient
{
    private const int ServiceDisabledHResult = unchecked((int)0x80070422);
    private const int PackageResourceInUseHResult = unchecked((int)0x80073D02);
    private const int UnspecifiedFailureHResult = unchecked((int)0x80004005);

    private readonly record struct WindowsBuildInfo(string? CurrentBuild, int? Ubr);

    // Signatures of the broker-side init failure that surface as the generic
    // 0x80004005 from the WinRT facade. Matched against exception.ToString()
    // so the runtime hint can be more specific than the bare HRESULT entry.
    private static readonly string[] NpuInitFailureMarkers =
    {
        "Unknown PsResult: -967",
        "LoadModelAndInitializeSession",
    };

    /// <summary>
    /// Optional hook that resolves a hint resource key into localized text.
    /// Set this once at app startup (e.g. to <c>LocalizationService.GetString</c>);
    /// when unset, the English default text is returned so logs, tests, and
    /// non-UI consumers remain readable.
    /// </summary>
    public static Func<string, string?>? HintLocalizer { get; set; }

    private static string Localize(string resourceKey, string defaultText)
    {
        var localized = HintLocalizer?.Invoke(resourceKey);
        return string.IsNullOrWhiteSpace(localized) || localized == resourceKey
            ? defaultText
            : localized!;
    }

    public WindowsAIReadyState GetReadyState()
    {
        try
        {
            var fingerprint = GetHealthFingerprint();
            if (WindowsAIBaselineDiagnostics.IsBelowMinimumOsBaseline(fingerprint)
                || IsWindowsAIBaselineMissing(fingerprint))
            {
                return WindowsAIReadyState.UnsupportedWindowsAIBaseline;
            }
        }
        catch
        {
            // Baseline diagnostics are best-effort. Fall through to the WinRT
            // readiness check if we cannot read the OS build/UBR fingerprint.
        }

        try
        {
            return MapReadyState(LanguageModel.GetReadyState());
        }
        catch
        {
            // GetReadyState should never throw, but if the WinRT activation itself
            // fails (missing runtime, unsupported OS), treat it as not-supported.
            return WindowsAIReadyState.NotSupportedOnCurrentSystem;
        }
    }

    public WindowsAIHealthFingerprint GetHealthFingerprint()
    {
        var languageModelAssembly = typeof(LanguageModel).Assembly.GetName();
        var rawReadyState = TryGetRawReadyState();
        var buildInfo = TryGetWindowsBuildInfo();
        return new WindowsAIHealthFingerprint(
            OsBuild: FormatFullWindowsBuild(buildInfo.CurrentBuild, buildInfo.Ubr, Environment.OSVersion.Version),
            Ubr: buildInfo.Ubr,
            WindowsAppSdkVersion: languageModelAssembly.Version?.ToString() ?? "unknown",
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            BackendName: "PhiSilica",
            ComponentMarker: FormatComponentMarker(languageModelAssembly.Name, rawReadyState),
            WindowsActivated: TryGetWindowsActivationStatus(),
            PhiSilicaAiComponentsPresent: TryGetPhiSilicaAiComponentsPresence(rawReadyState));
    }

    public async Task<WindowsAIReadyState> EnsureReadyAsync(
        CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        var initialState = GetReadyState();
        if (initialState != WindowsAIReadyState.NotReady)
        {
            return initialState;
        }

        var operation = LanguageModel.EnsureReadyAsync();
        if (progress is not null)
        {
            operation.Progress = (_, value) => progress.Report(value);
        }

        var result = await operation.AsTask(cancellationToken);
        if (result.Status == AIFeatureReadyResultState.Success)
        {
            progress?.Report(100);
            return WindowsAIReadyState.Ready;
        }

        var refreshedState = GetReadyState();
        if (refreshedState == WindowsAIReadyState.Ready)
        {
            return WindowsAIReadyState.Ready;
        }

        if (refreshedState != WindowsAIReadyState.NotReady)
        {
            return refreshedState;
        }

        throw CreatePreparationException(result, initialState, refreshedState);
    }

    public async Task<WindowsAIResponse> GenerateAsync(
        string prompt,
        WindowsAIGenerationOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var model = await LanguageModel.CreateAsync().AsTask(cancellationToken);
            var sdkOptions = ToSdkOptions(options);

            var result = await model.GenerateResponseAsync(prompt, sdkOptions).AsTask(cancellationToken);
            return MapResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WindowsLanguageModelException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateRuntimeException("generate", ex);
        }
    }

    public async Task WarmUpAsync(
        string prompt,
        WindowsAIGenerationOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var model = await LanguageModel.CreateAsync().AsTask(cancellationToken);
            var sdkOptions = ToSdkOptions(options);

            var result = await model.GenerateResponseAsync(prompt, sdkOptions).AsTask(cancellationToken);
            if (result.Status != LanguageModelResponseStatus.Complete)
            {
                var mapped = MapResult(result);
                throw new WindowsLanguageModelException(mapped.Status, mapped.ErrorMessage);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WindowsLanguageModelException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateRuntimeException("warmup", ex);
        }
    }

    public IAsyncEnumerable<string> GenerateStreamAsync(
        string prompt,
        WindowsAIGenerationOptions options,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _ = StreamToChannelAsync(prompt, options, channel.Writer, cancellationToken);
        return ReadChannelAsync(channel.Reader, cancellationToken);
    }

    private static async Task StreamToChannelAsync(
        string prompt,
        WindowsAIGenerationOptions options,
        ChannelWriter<string> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var model = await LanguageModel.CreateAsync().AsTask(cancellationToken);
            var sdkOptions = ToSdkOptions(options);

            var operation = model.GenerateResponseAsync(prompt, sdkOptions);
            operation.Progress = (_, token) =>
            {
                if (!string.IsNullOrEmpty(token))
                {
                    writer.TryWrite(token);
                }
            };

            var result = await operation.AsTask(cancellationToken);

            if (result.Status != LanguageModelResponseStatus.Complete)
            {
                var mapped = MapResult(result);
                throw new WindowsLanguageModelException(mapped.Status, mapped.ErrorMessage);
            }

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex is OperationCanceledException or WindowsLanguageModelException
                ? ex
                : CreateRuntimeException("stream", ex));
        }
    }

    private static async IAsyncEnumerable<string> ReadChannelAsync(
        ChannelReader<string> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in reader.ReadAllAsync(cancellationToken))
        {
            yield return chunk;
        }
    }

    private static LanguageModelOptions ToSdkOptions(WindowsAIGenerationOptions options)
    {
        return new LanguageModelOptions
        {
            Temperature = options.Temperature,
            TopK = options.TopK,
            TopP = options.TopP,
        };
    }

    private static int? TryGetUbr()
    {
        return TryGetWindowsBuildInfo().Ubr;
    }

    private static WindowsBuildInfo TryGetWindowsBuildInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return new WindowsBuildInfo(
                key?.GetValue("CurrentBuild")?.ToString(),
                TryParseRegistryInt(key?.GetValue("UBR")));
        }
        catch
        {
            return new WindowsBuildInfo(null, null);
        }
    }

    private static int? TryParseRegistryInt(object? value)
    {
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            string stringValue when int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    internal static string FormatFullWindowsBuild(
        string? currentBuild,
        int? ubr,
        Version fallbackVersion)
    {
        var build = string.IsNullOrWhiteSpace(currentBuild)
            ? null
            : currentBuild.Trim();

        if (build is not null && ubr is { } updateBuildRevision)
        {
            return $"10.0.{build}.{updateBuildRevision}";
        }

        if (build is not null)
        {
            var fallbackRevision = fallbackVersion.Revision >= 0
                ? fallbackVersion.Revision
                : 0;
            return $"10.0.{build}.{fallbackRevision}";
        }

        return fallbackVersion.ToString();
    }

    private static AIFeatureReadyState? TryGetRawReadyState()
    {
        try
        {
            return LanguageModel.GetReadyState();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWindowsAIBaselineMissing(WindowsAIHealthFingerprint fingerprint)
    {
        return fingerprint.WindowsActivated == false
            && fingerprint.PhiSilicaAiComponentsPresent == false;
    }

    private static string FormatComponentMarker(
        string? languageModelAssemblyName,
        AIFeatureReadyState? rawReadyState)
    {
        var assemblyName = languageModelAssemblyName ?? "Microsoft.Windows.AI.Text";
        var readyState = rawReadyState?.ToString() ?? "unknown";
        return $"{assemblyName}; readyState={readyState}";
    }

    private static bool? TryGetPhiSilicaAiComponentsPresence(AIFeatureReadyState? rawReadyState)
    {
        return rawReadyState switch
        {
            AIFeatureReadyState.Ready => true,
            AIFeatureReadyState.OSUpdateNeeded => false,
            _ => null,
        };
    }

    private static bool? TryGetWindowsActivationStatus()
    {
        // Best-effort diagnostic only. Avoid a compile-time System.Management
        // dependency so Windows AI remains usable when WMI is unavailable.
        try
        {
            var searcherType = Type.GetType(
                "System.Management.ManagementObjectSearcher, System.Management",
                throwOnError: false);
            if (searcherType is null)
            {
                return null;
            }

            const string query =
                "SELECT LicenseStatus FROM SoftwareLicensingProduct " +
                "WHERE PartialProductKey IS NOT NULL " +
                "AND ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'";

            using var searcher = Activator.CreateInstance(searcherType, query) as IDisposable;
            if (searcher is null)
            {
                return null;
            }

            var collection = searcherType.GetMethod("Get", Type.EmptyTypes)?.Invoke(searcher, null);
            using var disposableCollection = collection as IDisposable;
            if (collection is not IEnumerable items)
            {
                return null;
            }

            foreach (var item in items)
            {
                var value = item.GetType()
                    .GetProperty("Item")?
                    .GetValue(item, new object[] { "LicenseStatus" });
                if (value is null)
                {
                    continue;
                }

                if (Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return null;
        }
    }

    private static WindowsAIReadyState MapReadyState(AIFeatureReadyState state) => state switch
    {
        AIFeatureReadyState.Ready => WindowsAIReadyState.Ready,
        AIFeatureReadyState.NotReady => WindowsAIReadyState.NotReady,
        AIFeatureReadyState.CapabilityMissing => WindowsAIReadyState.CapabilityMissing,
        AIFeatureReadyState.NotCompatibleWithSystemHardware => WindowsAIReadyState.NotCompatibleWithSystemHardware,
        AIFeatureReadyState.OSUpdateNeeded => WindowsAIReadyState.OSUpdateNeeded,
        AIFeatureReadyState.DisabledByUser => WindowsAIReadyState.DisabledByUser,
        _ => WindowsAIReadyState.NotSupportedOnCurrentSystem,
    };

    private static WindowsAIResponse MapResult(LanguageModelResponseResult result) => result.Status switch
    {
        LanguageModelResponseStatus.Complete =>
            new WindowsAIResponse(WindowsAIResponseStatus.Complete, result.Text ?? string.Empty),

        LanguageModelResponseStatus.PromptLargerThanContext =>
            new WindowsAIResponse(
                WindowsAIResponseStatus.PromptLargerThanContext,
                string.Empty,
                result.ExtendedError?.Message),

        LanguageModelResponseStatus.BlockedByPolicy =>
            new WindowsAIResponse(
                WindowsAIResponseStatus.BlockedByPolicy,
                string.Empty,
                result.ExtendedError?.Message),

        _ =>
            new WindowsAIResponse(
                WindowsAIResponseStatus.Error,
                string.Empty,
                result.ExtendedError?.Message ?? result.Status.ToString()),
    };

    private static Exception CreatePreparationException(
        AIFeatureReadyResult result,
        WindowsAIReadyState initialState,
        WindowsAIReadyState refreshedState)
    {
        var diagnostics = new List<string>
        {
            $"result={result.Status}",
            $"readyBefore={initialState}",
            $"readyAfter={refreshedState}",
        };
        AddEnvironmentFingerprint(diagnostics);
        AddIfPresent(diagnostics, "display", result.ErrorDisplayText);
        AddExceptionDetail(diagnostics, "extended", result.ExtendedError);
        AddExceptionDetail(diagnostics, "error", result.Error);
        AddIfPresent(diagnostics, "hint", GetPreparationHint(result));

        var detail = string.Join("; ", diagnostics);
        var message = $"Windows could not prepare the Phi Silica language model: {detail}";

        return new InvalidOperationException(message, result.ExtendedError ?? result.Error);
    }

    private static void AddEnvironmentFingerprint(List<string> diagnostics)
    {
        var buildInfo = TryGetWindowsBuildInfo();
        diagnostics.Add($"osBuild={FormatFullWindowsBuild(buildInfo.CurrentBuild, buildInfo.Ubr, Environment.OSVersion.Version)}");
        diagnostics.Add($"processArch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    }

    private static void AddIfPresent(List<string> diagnostics, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add($"{name}={value.Trim()}");
        }
    }

    private static void AddExceptionDetail(List<string> diagnostics, string name, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        diagnostics.Add($"{name}HResult=0x{exception.HResult:X8}");
        AddIfPresent(diagnostics, $"{name}Message", exception.Message);
    }

    private static string? GetPreparationHint(AIFeatureReadyResult result)
    {
        var extendedHint = result.ExtendedError is { } extended
            ? GetPreparationHintForHResult(extended.HResult)
            : null;
        if (extendedHint is not null)
        {
            return extendedHint;
        }

        return result.Error is { } error
            ? GetPreparationHintForHResult(error.HResult)
            : null;
    }

    internal static string? GetPreparationHintForHResult(int hresult) => hresult switch
    {
        ServiceDisabledHResult => Localize(
            PhiSilicaResources.HintKeys.ServiceDisabled,
            "A required Windows update/download service is disabled or cannot be started. " +
            "Enable Windows Update and Delivery Optimization, then try again."),
        PackageResourceInUseHResult => Localize(
            PhiSilicaResources.HintKeys.PackageResourceInUse,
            "Windows downloaded the model package but could not finish installing it because a related package or resource is in use. " +
            "Close Easydict and other Windows AI apps, or restart Windows, then try again."),
        UnspecifiedFailureHResult => Localize(
            PhiSilicaResources.HintKeys.NpuRuntimeReset,
            "Windows returned a generic failure after the model reported ready. " +
            "This usually means the Windows AI model session or first inference failed after the model package was installed. " +
            "Close other Windows AI apps and heavy processes, restart Windows to reset the NPU/model runtime, then retry. " +
            "If this persists, use Foundry Local or OpenVINO as the fallback."),
        _ => null,
    };

    private static WindowsLanguageModelException CreateRuntimeException(string operation, Exception exception)
    {
        return new WindowsLanguageModelException(
            WindowsAIResponseStatus.Error,
            CreateRuntimeExceptionMessage(operation, exception),
            exception);
    }

    internal static string CreateRuntimeExceptionMessage(string operation, Exception exception)
    {
        var diagnostics = new List<string>
        {
            $"operation={operation}",
            $"exception={exception.GetType().Name}",
            $"hResult=0x{exception.HResult:X8}",
            $"message={NormalizeDiagnosticMessage(exception.Message)}",
        };
        AddEnvironmentFingerprint(diagnostics);
        AddIfPresent(diagnostics, "hint", GetRuntimeHint(exception));

        return $"Windows AI runtime failed while running Phi Silica: {string.Join("; ", diagnostics)}";
    }

    internal static string NormalizeDiagnosticMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "<empty>";
        }

        var collapsed = string.Join(
            " ",
            message.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Equals("Unspecified error Unspecified error", StringComparison.OrdinalIgnoreCase)
            ? "Unspecified error"
            : collapsed;
    }

    internal static string? GetRuntimeHint(Exception exception)
    {
        if (exception.HResult == UnspecifiedFailureHResult && HasNpuInitFailureMarker(exception))
        {
            return Localize(
                PhiSilicaResources.HintKeys.NpuModelSessionInit,
                "Windows AI downloaded the model but failed while initializing the Phi Silica model session or first inference. " +
                "Close other Windows AI apps and heavy processes, restart Windows to reset the NPU/model runtime, then retry. " +
                "If this persists, use Foundry Local or OpenVINO as the fallback.");
        }

        return GetRuntimeHintForHResult(exception.HResult);
    }

    internal static string? GetRuntimeHintForHResult(int hresult) =>
        GetPreparationHintForHResult(hresult);

    private static bool HasNpuInitFailureMarker(Exception exception)
    {
        var details = exception.ToString();
        foreach (var marker in NpuInitFailureMarkers)
        {
            if (details.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

internal sealed class WindowsLanguageModelException : Exception
{
    public WindowsAIResponseStatus Status { get; }

    public WindowsLanguageModelException(WindowsAIResponseStatus status, string? message, Exception? innerException = null)
        : base(message ?? status.ToString(), innerException)
    {
        Status = status;
    }
}
