namespace Easydict.TranslationService.FormulaProtection;

/// <summary>
/// Represents a protected formula token extracted from document text.
/// </summary>
/// <param name="Type">Classification of the formula token.</param>
/// <param name="Raw">Original text: e.g. "\frac{\alpha}{2}"</param>
/// <param name="Placeholder">Replacement placeholder: e.g. "{v3}"</param>
/// <param name="Simplified">PDF-render form: e.g. "α/2"</param>
public record FormulaToken(
    FormulaTokenType Type,
    string Raw,
    string Placeholder,
    string Simplified
);
