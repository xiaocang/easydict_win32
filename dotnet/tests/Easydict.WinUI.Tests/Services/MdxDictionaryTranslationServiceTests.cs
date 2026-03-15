using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class MdxDictionaryTranslationServiceTests
{
    [Fact]
    public void ToReadableText_ShouldStripTagsAndDecodeEntities()
    {
        var html = "<div>Hello&nbsp;<b>World</b><br/>Line 2 &amp; more<script>alert(1)</script></div>";

        var text = MdxDictionaryTranslationService.ToReadableText(html);

        text.Should().Be("Hello World\nLine 2 & more");
    }

    [Fact]
    public void ToReadableText_ShouldDropEmptyLinesAfterCleanup()
    {
        var html = "<p>  A  </p><p> </p><div> B </div>";

        var text = MdxDictionaryTranslationService.ToReadableText(html);

        text.Should().Be("A\nB");
    }
}
