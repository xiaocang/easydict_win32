namespace Easydict.TranslationService.Models;

/// <summary>
/// Defines the operation mode for a query.
/// </summary>
public enum QueryMode
{
    /// <summary>Standard cross-language translation.</summary>
    Translation,

    /// <summary>Same-language grammar correction with diff and explanations.</summary>
    GrammarCorrection,
}
