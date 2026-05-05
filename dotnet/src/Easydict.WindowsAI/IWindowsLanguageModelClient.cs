namespace Easydict.WindowsAI;

/// <summary>
/// Abstraction over <c>Microsoft.Windows.AI.Text.LanguageModel</c> so the translation
/// service can be unit-tested without a Copilot+ PC and without taking a hard
/// runtime dependency on the WinRT activation surface in tests.
/// </summary>
public interface IWindowsLanguageModelClient
{
    WindowsAIReadyState GetReadyState();

    /// <summary>
    /// Triggers <c>LanguageModel.EnsureReadyAsync()</c> when state is <see cref="WindowsAIReadyState.NotReady"/>.
    /// Returns the post-attempt ready state (so callers can decide whether to proceed or fail).
    /// </summary>
    Task<WindowsAIReadyState> EnsureReadyAsync(CancellationToken cancellationToken);

    Task<WindowsAIResponse> GenerateAsync(
        string prompt,
        WindowsAIGenerationOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams response tokens as they're generated. Each yielded string is an
    /// incremental chunk (not accumulated), matching <see cref="Easydict.TranslationService.IStreamTranslationService"/>'s contract.
    /// </summary>
    IAsyncEnumerable<string> GenerateStreamAsync(
        string prompt,
        WindowsAIGenerationOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Mirrors the subset of <c>Microsoft.Windows.AI.AIFeatureReadyState</c> we surface to users.
/// Defined locally so consumers (tests, settings page) can reference it without WinRT.
/// </summary>
public enum WindowsAIReadyState
{
    Ready,
    NotReady,
    CapabilityMissing,
    NotCompatibleWithSystemHardware,
    OSUpdateNeeded,
    DisabledByUser,
    NotSupportedOnCurrentSystem,
}

public enum WindowsAIResponseStatus
{
    Complete,
    PromptLargerThanContext,
    BlockedByPolicy,
    Error,
}

public sealed record WindowsAIResponse(
    WindowsAIResponseStatus Status,
    string Text,
    string? ErrorMessage = null);

/// <summary>
/// Generation options for Phi Silica. Defaults are tuned for translation
/// (Microsoft's defaults of 0.9/40/0.9 are too creative; their best-practices doc
/// recommends lowering Temperature/TopK for deterministic output).
/// TopK is uint to match <c>Microsoft.Windows.AI.Text.LanguageModelOptions.TopK</c>.
/// </summary>
public sealed record WindowsAIGenerationOptions(
    float Temperature = 0.1f,
    uint TopK = 1,
    float TopP = 0.9f);
