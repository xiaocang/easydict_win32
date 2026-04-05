using Easydict.TranslationService.FormulaProtection;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.FormulaProtection;

public class FormulaRestorerTests
{
    private readonly FormulaRestorer _restorer = new();

    private static FormulaToken MakeToken(string raw, string simplified) =>
        new(FormulaTokenType.InlineMath, raw, "{v0}", simplified);

    [Fact]
    public void Restore_EmptyTokens_ReturnsOriginalText()
    {
        var result = _restorer.Restore("{v0}", Array.Empty<FormulaToken>(), "original", useSimplified: false);
        result.Should().Be("{v0}");
    }

    [Fact]
    public void Restore_UnresolvablePlaceholder_ReturnsFallback()
    {
        // {v5} doesn't exist in 2-token list → fallback
        var tokens = new[]
        {
            MakeToken("\\alpha", "α"),
            MakeToken("\\beta", "β"),
        };
        var result = _restorer.Restore("text {v5} more", tokens, "FALLBACK", useSimplified: false);
        result.Should().Be("FALLBACK");
    }

    [Fact]
    public void Restore_UseSimplifiedFalse_ReturnsRaw()
    {
        var tokens = new[] { MakeToken("\\alpha", "α") };
        var result = _restorer.Restore("The {v0} letter.", tokens, "original", useSimplified: false);
        result.Should().Be("The \\alpha letter.");
    }

    [Fact]
    public void Restore_UseSimplifiedTrue_ReturnsSimplified()
    {
        var tokens = new[] { MakeToken("\\alpha", "α") };
        var result = _restorer.Restore("The {v0} letter.", tokens, "original", useSimplified: true);
        result.Should().Be("The α letter.");
    }

    [Fact]
    public void Restore_MultipleTokens_AllRestored()
    {
        var tokens = new[]
        {
            MakeToken("\\alpha", "α"),
            MakeToken("\\beta", "β"),
        };
        var result = _restorer.Restore("{v0} and {v1}", tokens, "original", useSimplified: false);
        result.Should().Be("\\alpha and \\beta");
    }

    [Fact]
    public void Restore_ImbalancedDelimiters_ReturnsFallback()
    {
        // Restoring a raw token that creates unbalanced braces → fallback
        var tokens = new[] { MakeToken("{unbalanced", "{unbalanced") };
        var result = _restorer.Restore("{v0}", tokens, "FALLBACK", useSimplified: false);
        result.Should().Be("FALLBACK");
    }

    [Fact]
    public void Restore_BalancedDelimiters_ReturnsRestored()
    {
        var tokens = new[] { MakeToken("$x + y$", "x + y") };
        var result = _restorer.Restore("We have {v0} here.", tokens, "FALLBACK", useSimplified: false);
        result.Should().Be("We have $x + y$ here.");
    }

    [Fact]
    public void Restore_EmptyText_ReturnsEmptyText()
    {
        var tokens = new[] { MakeToken("\\alpha", "α") };
        var result = _restorer.Restore("", tokens, "FALLBACK", useSimplified: false);
        result.Should().Be("");
    }

    [Fact]
    public void Restore_DefaultUsesRaw()
    {
        // Default: useSimplified = false
        var tokens = new[] { MakeToken("\\alpha", "α") };
        var result = _restorer.Restore("The {v0} letter.", tokens, "original");
        result.Should().Be("The \\alpha letter.");
    }

    [Fact]
    public void Restore_LlmDroppedOnePlaceholder_PartialRestore()
    {
        // 2 tokens, LLM output only contains {v0}, drops {v1}.
        // 1 of 2 present (50%) → partial restore: replace {v0}, {v1} simply absent.
        var tokens = new[]
        {
            MakeToken("x_1", "x-1"),
            MakeToken("x_n", "x-n"),
        };
        var result = _restorer.Restore("符号表示 {v0} 的序列", tokens, "ORIGINAL", useSimplified: false);
        // Partial restore: {v0} replaced, missing {v1} is just absent from output
        result.Should().Be("符号表示 x_1 的序列");
    }

    [Fact]
    public void Restore_MostPlaceholdersMissing_FallsBackToOriginal()
    {
        // 4 tokens, LLM only kept 1 → 25% < 50% → full fallback
        var tokens = new[]
        {
            MakeToken("\\alpha", "α"),
            MakeToken("\\beta", "β"),
            MakeToken("\\gamma", "γ"),
            MakeToken("\\delta", "δ"),
        };
        var result = _restorer.Restore("只有 {v0} 剩下", tokens, "ORIGINAL", useSimplified: false);
        result.Should().Be("ORIGINAL");
    }

    [Fact]
    public void Restore_AllPlaceholdersMissing_FallsBackToOriginal()
    {
        // No placeholders at all → full fallback
        var tokens = new[]
        {
            MakeToken("x_1", "x-1"),
            MakeToken("x_n", "x-n"),
        };
        var result = _restorer.Restore("完全没有占位符", tokens, "ORIGINAL", useSimplified: false);
        result.Should().Be("ORIGINAL");
    }

    [Fact]
    public void Restore_AllPlaceholdersPresent_ReturnsRestored()
    {
        // Confirm normal path still works when LLM preserves all placeholders
        var tokens = new[]
        {
            MakeToken("x_1", "x-1"),
            MakeToken("x_n", "x-n"),
        };
        var result = _restorer.Restore("符号表示 {v0} 和 {v1}", tokens, "ORIGINAL", useSimplified: false);
        result.Should().Be("符号表示 x_1 和 x_n");
    }

    [Fact]
    public void Restore_PartialWithUnresolvableIndex_FallsBack()
    {
        // Partial path: {v0} present (1 of 2 = 50%), but also has {v99} (unresolvable).
        // After replacement, {v99} remains → fallback.
        var tokens = new[]
        {
            MakeToken("\\alpha", "α"),
            MakeToken("\\beta", "β"),
        };
        var result = _restorer.Restore("{v0} and {v99}", tokens, "FALLBACK", useSimplified: false);
        result.Should().Be("FALLBACK");
    }

    [Fact]
    public void Restore_PartialMajority_RestoresPresent()
    {
        // 4 tokens, 3 present (75%) → partial restore
        var tokens = new[]
        {
            MakeToken("\\alpha", "α"),
            MakeToken("\\beta", "β"),
            MakeToken("\\gamma", "γ"),
            MakeToken("\\delta", "δ"),
        };
        var result = _restorer.Restore("{v0}, {v1}, {v2} 在此", tokens, "ORIGINAL", useSimplified: false);
        result.Should().Be("\\alpha, \\beta, \\gamma 在此");
    }

    [Fact]
    public void RestoreWithDiagnostics_AllPresent_ReportsFullRestore()
    {
        var tokens = new[] { MakeToken("\\alpha", "α"), MakeToken("\\beta", "β") };
        var result = _restorer.RestoreWithDiagnostics("{v0} and {v1}", tokens, "ORIGINAL");
        result.Status.Should().Be(FormulaRestoreStatus.FullRestore);
        result.DroppedCount.Should().Be(0);
        result.MissingIndices.Should().BeEmpty();
        result.Text.Should().Be("\\alpha and \\beta");
    }

    [Fact]
    public void RestoreWithDiagnostics_PartialRestore_ReportsMissingIndices()
    {
        // 4 tokens, 3 present (75%) → partial restore, missing index 3
        var tokens = new[]
        {
            MakeToken("\\alpha", "α"),
            MakeToken("\\beta", "β"),
            MakeToken("\\gamma", "γ"),
            MakeToken("\\delta", "δ"),
        };
        var result = _restorer.RestoreWithDiagnostics("{v0} {v1} {v2} here", tokens, "ORIGINAL");
        result.Status.Should().Be(FormulaRestoreStatus.PartialRestore);
        result.DroppedCount.Should().Be(1);
        result.MissingIndices.Should().Equal(3);
    }

    [Fact]
    public void RestoreWithDiagnostics_BelowHalf_ReportsFallback()
    {
        // 4 tokens, only 1 present (25%) → fallback, missing 1/2/3
        var tokens = new[]
        {
            MakeToken("\\alpha", "α"),
            MakeToken("\\beta", "β"),
            MakeToken("\\gamma", "γ"),
            MakeToken("\\delta", "δ"),
        };
        var result = _restorer.RestoreWithDiagnostics("only {v0} remaining", tokens, "ORIGINAL");
        result.Status.Should().Be(FormulaRestoreStatus.FallbackToOriginal);
        result.DroppedCount.Should().Be(3);
        result.MissingIndices.Should().Equal(1, 2, 3);
        result.Text.Should().Be("ORIGINAL");
    }

    [Fact]
    public void RestoreWithDiagnostics_NoPlaceholders_ReportsFallback()
    {
        var tokens = new[] { MakeToken("\\alpha", "α") };
        var result = _restorer.RestoreWithDiagnostics("no placeholders here", tokens, "ORIGINAL");
        result.Status.Should().Be(FormulaRestoreStatus.FallbackToOriginal);
        result.DroppedCount.Should().Be(1);
        result.MissingIndices.Should().Equal(0);
    }
}
