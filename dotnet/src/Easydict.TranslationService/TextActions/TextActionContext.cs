using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.TextActions;

/// <summary>
/// The text an action operates on plus the languages in effect when it was triggered.
/// </summary>
/// <param name="Text">The source text (selected or entered).</param>
/// <param name="Translation">The best available translation of <paramref name="Text"/>, if any.</param>
/// <param name="From">Source language (may be <see cref="Language.Auto"/>).</param>
/// <param name="To">Target language.</param>
public sealed record TextActionContext(string Text, string? Translation, Language From, Language To);
