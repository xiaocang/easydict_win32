using Easydict.TextLayout.Layout;
using Easydict.TextLayout.Preparation;
using Easydict.TextLayout.Tests.Helpers;
using FluentAssertions;

namespace Easydict.TextLayout.Tests.Layout;

/// <summary>
/// Tests that kinsoku (line-start prohibition) and left-sticky punctuation rules
/// are enforced during line breaking.
/// </summary>
public class KinsokuLayoutTests
{
    private readonly TextLayoutEngine _engine = TextLayoutEngine.Instance;
    private readonly FixedWidthMeasurer _measurer = new();

    private LayoutLinesResult LayoutLines(string text, double maxWidth)
    {
        var prepared = _engine.Prepare(new TextPrepareRequest { Text = text }, _measurer);
        return _engine.LayoutWithLines(prepared, maxWidth);
    }

    // --- Kinsoku line-start prohibition ---

    [Fact]
    public void Kinsoku_IdeographicComma_NeverStartsLine()
    {
        // "你好世界、" = 你(10) 好(10) 世(10) 界(10) 、(10) = 50
        // Width = 25: without kinsoku → "你好" / "世界" / "、"
        // With kinsoku → 、 carried to line 2: "你好" / "世界、"
        var result = LayoutLines("你好世界、", 25);
        result.Lines.Should().HaveCount(2);
        result.Lines[0].Text.Should().Be("你好");
        // 、 must NOT start line 2 — it should be carried to line with 世界
        result.Lines[1].Text.Should().StartWith("世界");
        result.Lines[1].Text.Should().Contain("、");
    }

    [Fact]
    public void Kinsoku_IdeographicPeriod_NeverStartsLine()
    {
        // "你好。世界" = 你(10) 好(10) 。(close-punct, 10) 世(10) 界(10)
        // 。 is ClosePunctuation → grouped with 好 via close-punct grouping
        // Width = 25: "你好。" (30 > 25, but close-punct grouped) / "世界"
        var result = LayoutLines("你好。世界", 25);
        // 。 should be on the same line as 好, not starting a new line
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith("。");
        }
    }

    [Fact]
    public void Kinsoku_SmallKana_NeverStartsLine()
    {
        // "あいっう" = あ(10) い(10) っ(10) う(10) = 40
        // っ (small tsu) is prohibited line start
        // Width = 15: without kinsoku → "あ" / "い" / "っ" / "う"
        // With kinsoku → っ carried: "あ" / "いっ" / "う"
        var result = LayoutLines("あいっう", 15);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith("っ",
                "small kana っ must not start a line (kinsoku rule)");
        }
    }

    [Fact]
    public void Kinsoku_ProlongedSoundMark_NeverStartsLine()
    {
        // "カラー" = カ(10) ラ(10) ー(10) = 30
        // ー (prolonged sound mark) is prohibited line start
        // Width = 15: without kinsoku → "カ" / "ラ" / "ー"
        // With kinsoku → ー carried: "カ" / "ラー"
        var result = LayoutLines("カラー", 15);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith("ー",
                "prolonged sound mark ー must not start a line (kinsoku rule)");
        }
    }

    [Fact]
    public void Kinsoku_IterationMark_NeverStartsLine()
    {
        // "日々の" = 日(10) 々(10) の(10) = 30
        // 々 (ideographic iteration mark) is prohibited line start
        // Width = 12: without kinsoku → "日" / "々" / "の"
        // With kinsoku → 々 carried: "日々" / "の"
        var result = LayoutLines("日々の", 12);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith("々",
                "iteration mark 々 must not start a line (kinsoku rule)");
        }
    }

    [Fact]
    public void Kinsoku_MiddleDot_NeverStartsLine()
    {
        // "カタカナ・ひらがな" — ・ is katakana middle dot, prohibited line start
        // Width = 35: layout should ensure ・ doesn't start a line
        var result = LayoutLines("カタカナ・ひらがな", 35);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith("・",
                "katakana middle dot ・ must not start a line (kinsoku rule)");
        }
    }

    [Fact]
    public void Kinsoku_FullwidthExclamation_NeverStartsLine()
    {
        // "すごい！ですね" — ！ is fullwidth exclamation
        // Already handled as ClosePunctuation, but verify it doesn't start a line
        var result = LayoutLines("すごい！ですね", 25);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith("！",
                "fullwidth exclamation must not start a line");
        }
    }

    // --- Left-sticky punctuation ---

    [Fact]
    public void LeftSticky_AsciiPeriodAfterCjk_StaysAttached()
    {
        // "你好世界." = 你(10) 好(10) 世(10) 界(10) .(6) = 46
        // . (ASCII period) should stick to CJK 界, not start a new line
        // Width = 25: without left-sticky → "你好" / "世界" / "."
        // With left-sticky → "你好" / "世界."
        var result = LayoutLines("你好世界.", 25);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith(".",
                "ASCII period after CJK must not start a line (left-sticky rule)");
        }
    }

    [Fact]
    public void LeftSticky_AsciiCommaAfterCjk_StaysAttached()
    {
        // "你好世界," = 你(10) 好(10) 世(10) 界(10) ,(6) = 46
        var result = LayoutLines("你好世界,", 25);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith(",",
                "ASCII comma after CJK must not start a line (left-sticky rule)");
        }
    }

    [Fact]
    public void LeftSticky_AsciiExclamationAfterCjk_StaysAttached()
    {
        // "你好世界!" = 你(10) 好(10) 世(10) 界(10) !(6) = 46
        var result = LayoutLines("你好世界!", 25);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith("!",
                "ASCII exclamation after CJK must not start a line (left-sticky rule)");
        }
    }

    [Fact]
    public void LeftSticky_AfterLatinWord_DoesNotApply()
    {
        // Left-sticky should only apply after CJK, not after Latin words
        // "Hello. World" → normal word-break behavior, period is ClosePunctuation anyway
        var result = LayoutLines("Hello. World", 40);
        // This should behave normally — "Hello." fits, no special behavior
        result.Lines[0].Text.Should().Contain("Hello.");
    }

    // --- Edge cases ---

    [Fact]
    public void Kinsoku_WhenLineIsEmpty_EmitsAnywayToAvoidInfiniteLoop()
    {
        // If the very first segment on an empty line is kinsoku-prohibited,
        // we must still emit it to avoid an infinite loop.
        // "っあ" with width = 8 (< single CJK width of 10)
        // Both chars must still be emitted, one per line.
        var result = LayoutLines("っあ", 8);
        result.Lines.Should().HaveCount(2);
    }

    [Fact]
    public void Kinsoku_RightAngleBracket_ClassifiedAsClosePunctuation()
    {
        // 〉 (U+3009) should be ClosePunctuation and grouped with preceding content
        var result = LayoutLines("〈テスト〉です", 35);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith("〉",
                "right angle bracket must not start a line");
        }
    }

    [Fact]
    public void Kinsoku_ConsecutiveProhibited_AllCarried()
    {
        // "あいっっう" — two consecutive っ
        // Width = 15: should carry both っっ to the line with い
        var result = LayoutLines("あいっっう", 15);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith("っ",
                "consecutive small kana must not start a line");
        }
    }

    [Fact]
    public void LeftSticky_Ellipsis_AfterCjk_StaysAttached()
    {
        // "你好世界…" = 你(10) 好(10) 世(10) 界(10) …(6) = 46
        // … (horizontal ellipsis U+2026) is left-sticky
        var result = LayoutLines("你好世界\u2026", 25);
        foreach (var line in result.Lines)
        {
            line.Text.Should().NotStartWith("\u2026",
                "ellipsis after CJK must not start a line (left-sticky rule)");
        }
    }
}
