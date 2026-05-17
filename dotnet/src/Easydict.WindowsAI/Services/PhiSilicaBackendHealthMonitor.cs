using Easydict.TranslationService.LocalModels;

namespace Easydict.WindowsAI.Services;

public enum PhiSilicaBackendHealthState
{
    NotChecked,
    EnsuringPackage,
    CreatingSession,
    WarmingUp,
    Healthy,
    Unhealthy,
}

public sealed record PhiSilicaBackendHealthSnapshot(
    PhiSilicaBackendHealthState State,
    WindowsAIHealthFingerprint? Fingerprint = null,
    string? DetailMessage = null);

public sealed class PhiSilicaBackendHealthMonitor
{
    private const string WarmUpPrompt = "Reply with OK.";

    private static readonly WindowsAIGenerationOptions WarmUpOptions =
        new(Temperature: 0.1f, TopK: 1, TopP: 0.9f);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _warmUpSemaphore = new(1, 1);
    private PhiSilicaBackendHealthSnapshot _snapshot =
        new(PhiSilicaBackendHealthState.NotChecked);

    public static PhiSilicaBackendHealthMonitor Shared { get; } = new();

    public PhiSilicaBackendHealthSnapshot GetSnapshot(IWindowsLanguageModelClient client)
    {
        var fingerprint = client.GetHealthFingerprint();
        lock (_sync)
        {
            return IsSameFingerprint(_snapshot.Fingerprint, fingerprint)
                ? _snapshot
                : new PhiSilicaBackendHealthSnapshot(
                    PhiSilicaBackendHealthState.NotChecked,
                    fingerprint);
        }
    }

    public LocalModelStatus GetStatus(IWindowsLanguageModelClient client)
    {
        var readyState = client.GetReadyState();
        if (readyState != WindowsAIReadyState.Ready)
        {
            ResetIfFingerprintChanged(client.GetHealthFingerprint());
            return PhiSilicaTranslationService.MapReadyStateToStatus(readyState);
        }

        var snapshot = GetSnapshot(client);
        return snapshot.State switch
        {
            PhiSilicaBackendHealthState.Healthy =>
                new LocalModelStatus(LocalModelState.Ready, PhiSilicaResources.StatusKeys.Ready),

            PhiSilicaBackendHealthState.Unhealthy =>
                CreateUnhealthyStatus(snapshot),

            PhiSilicaBackendHealthState.EnsuringPackage
                or PhiSilicaBackendHealthState.CreatingSession
                or PhiSilicaBackendHealthState.WarmingUp =>
                    new LocalModelStatus(LocalModelState.Preparing, PhiSilicaResources.StatusKeys.WarmingUp),

            _ =>
                new LocalModelStatus(
                    LocalModelState.NeedsPreparation,
                    PhiSilicaResources.StatusKeys.WarmupRequired,
                    DetailMessage: snapshot.Fingerprint?.ToString()),
        };
    }

    public async Task EnsureHealthyAsync(
        IWindowsLanguageModelClient client,
        Action<PhiSilicaBackendHealthSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var readyState = client.GetReadyState();
        if (readyState != WindowsAIReadyState.Ready)
        {
            ResetIfFingerprintChanged(client.GetHealthFingerprint());
            throw new WindowsLanguageModelException(
                WindowsAIResponseStatus.Error,
                PhiSilicaTranslationService.GetReadyStateMessage(readyState));
        }

        var fingerprint = client.GetHealthFingerprint();
        var snapshot = GetSnapshot(client);
        if (snapshot.State == PhiSilicaBackendHealthState.Healthy)
        {
            return;
        }

        if (snapshot.State == PhiSilicaBackendHealthState.Unhealthy)
        {
            throw CreateUnhealthyException(snapshot);
        }

        await _warmUpSemaphore.WaitAsync(cancellationToken);
        try
        {
            readyState = client.GetReadyState();
            if (readyState != WindowsAIReadyState.Ready)
            {
                ResetIfFingerprintChanged(fingerprint);
                throw new WindowsLanguageModelException(
                    WindowsAIResponseStatus.Error,
                    PhiSilicaTranslationService.GetReadyStateMessage(readyState));
            }

            fingerprint = client.GetHealthFingerprint();
            snapshot = GetSnapshot(client);
            if (snapshot.State == PhiSilicaBackendHealthState.Healthy)
            {
                return;
            }

            if (snapshot.State == PhiSilicaBackendHealthState.Unhealthy)
            {
                throw CreateUnhealthyException(snapshot);
            }

            Report(PhiSilicaBackendHealthState.CreatingSession, fingerprint, null, progress);
            Report(PhiSilicaBackendHealthState.WarmingUp, fingerprint, null, progress);
            await client.WarmUpAsync(WarmUpPrompt, WarmUpOptions, cancellationToken);
            Report(PhiSilicaBackendHealthState.Healthy, fingerprint, null, progress);
        }
        catch (OperationCanceledException)
        {
            ResetIfFingerprintChanged(fingerprint);
            throw;
        }
        catch (Exception ex)
        {
            MarkUnhealthy(fingerprint, ex.Message, progress);
            throw;
        }
        finally
        {
            _warmUpSemaphore.Release();
        }
    }

    public void MarkUnhealthy(
        IWindowsLanguageModelClient client,
        string detailMessage)
    {
        MarkUnhealthy(client.GetHealthFingerprint(), detailMessage, progress: null);
    }

    public void Reset()
    {
        lock (_sync)
        {
            _snapshot = new PhiSilicaBackendHealthSnapshot(PhiSilicaBackendHealthState.NotChecked);
        }
    }

    private void MarkUnhealthy(
        WindowsAIHealthFingerprint fingerprint,
        string detailMessage,
        Action<PhiSilicaBackendHealthSnapshot>? progress)
    {
        Report(PhiSilicaBackendHealthState.Unhealthy, fingerprint, detailMessage, progress);
    }

    private void Report(
        PhiSilicaBackendHealthState state,
        WindowsAIHealthFingerprint fingerprint,
        string? detailMessage,
        Action<PhiSilicaBackendHealthSnapshot>? progress)
    {
        var snapshot = new PhiSilicaBackendHealthSnapshot(state, fingerprint, detailMessage);
        lock (_sync)
        {
            _snapshot = snapshot;
        }

        progress?.Invoke(snapshot);
    }

    private void ResetIfFingerprintChanged(WindowsAIHealthFingerprint fingerprint)
    {
        lock (_sync)
        {
            if (!IsSameFingerprint(_snapshot.Fingerprint, fingerprint))
            {
                _snapshot = new PhiSilicaBackendHealthSnapshot(
                    PhiSilicaBackendHealthState.NotChecked,
                    fingerprint);
            }
        }
    }

    private static WindowsLanguageModelException CreateUnhealthyException(
        PhiSilicaBackendHealthSnapshot snapshot)
    {
        return new WindowsLanguageModelException(
            WindowsAIResponseStatus.Error,
            $"Phi Silica backend is unhealthy for the current Windows AI fingerprint. {FormatDetail(snapshot)}");
    }

    private static LocalModelStatus CreateUnhealthyStatus(PhiSilicaBackendHealthSnapshot snapshot)
        => PhiSilicaTranslationService.CreateRuntimeFailureStatus(
            snapshot.DetailMessage,
            snapshot.Fingerprint);

    private static string FormatDetail(PhiSilicaBackendHealthSnapshot snapshot)
        => PhiSilicaTranslationService.FormatRuntimeFailureDetail(
            snapshot.DetailMessage,
            snapshot.Fingerprint);

    private static bool IsSameFingerprint(
        WindowsAIHealthFingerprint? left,
        WindowsAIHealthFingerprint? right)
    {
        return left is not null && left.Equals(right);
    }
}
