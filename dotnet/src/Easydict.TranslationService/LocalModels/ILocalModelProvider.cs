namespace Easydict.TranslationService.LocalModels;

/// <summary>
/// Shared abstraction for translation services that run a model on-device
/// (Windows AI / Phi Silica, OpenVINO + NLLB, Ollama-style stacks, etc.).
/// Lets the Settings page render a uniform "Local Models" section: status badge,
/// "Prepare" button, optional progress bar — without each provider needing
/// hand-written UI.
/// </summary>
public interface ILocalModelProvider
{
    /// <summary>
    /// Matches the implementing <see cref="ITranslationService.ServiceId"/>.
    /// Used to correlate the local-model UI back to a translation service.
    /// </summary>
    string ServiceId { get; }

    /// <summary>
    /// Cheap, synchronous snapshot of the current state. No I/O.
    /// </summary>
    LocalModelStatus GetStatus();

    /// <summary>
    /// Fires when state transitions (download progress, ready, failed, etc.).
    /// Subscribers run on whichever thread the provider raises the event from;
    /// UI consumers should marshal to the dispatcher.
    /// </summary>
    event EventHandler<LocalModelStatus>? StatusChanged;

    /// <summary>
    /// Idempotently prepare the model. For Phi Silica this triggers
    /// <c>LanguageModel.EnsureReadyAsync</c>; for OpenVINO this downloads model
    /// files into the per-user cache. Returns the final post-attempt status.
    /// Throws <see cref="OperationCanceledException"/> on cancel; other failures
    /// produce a <see cref="LocalModelStatus"/> with <see cref="LocalModelState.Failed"/>
    /// rather than throwing.
    /// </summary>
    Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken);
}

public enum LocalModelState
{
    /// <summary>Model is loaded / installed and ready to translate.</summary>
    Ready,

    /// <summary>System supports this provider but the model is not yet prepared.</summary>
    NeedsPreparation,

    /// <summary>Preparation in progress (download, install, EnsureReady…).</summary>
    Preparing,

    /// <summary>This device cannot run the provider (missing NPU, unsupported OS, missing capability, region-blocked…).</summary>
    NotCompatible,

    /// <summary>The last preparation attempt failed and won't be retried automatically.</summary>
    Failed,
}

/// <summary>
/// Snapshot of a local-model provider's state. <see cref="ProgressPercent"/> is
/// only meaningful when <see cref="State"/> is <see cref="LocalModelState.Preparing"/>.
/// <see cref="ResourceKey"/> points at a string in <c>Resources.resw</c>; the
/// settings page resolves it to the active UI language.
/// </summary>
public sealed record LocalModelStatus(
    LocalModelState State,
    string ResourceKey,
    double? ProgressPercent = null,
    string? DetailMessage = null);
