namespace Easydict.TranslationService.ContentPreservation;

/// <summary>
/// Unified interface for content preservation (detect → protect → restore → fallback).
/// Separates evidence-based detection from policy decisions, and keeps the token map
/// as a single source of truth across the entire pipeline.
/// </summary>
public interface IContentPreservationService
{
    /// <summary>
    /// Analyzes a block to determine how its content should be preserved.
    /// </summary>
    ProtectionPlan Analyze(BlockContext context);

    /// <summary>
    /// Applies content protection (placeholder substitution) according to the plan.
    /// </summary>
    ProtectedBlock Protect(BlockContext context, ProtectionPlan plan);

    /// <summary>
    /// Restores protected content in translated text using the stored token map.
    /// </summary>
    RestoreOutcome Restore(string translatedText, ProtectedBlock protectedBlock);

    /// <summary>
    /// Resolves a restore outcome into final text, applying fallback policy as needed.
    /// </summary>
    string ResolveFallback(RestoreOutcome outcome, ProtectedBlock protectedBlock);
}
