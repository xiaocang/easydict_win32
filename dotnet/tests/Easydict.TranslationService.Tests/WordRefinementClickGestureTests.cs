using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests;

public sealed class WordRefinementClickGestureTests
{
    [Fact]
    public void Release_ActivatesPressedWordOnce()
    {
        var gesture = new WordRefinementClickGesture();
        gesture.Press(1, "word", 10, 10);
        gesture.Release(1, "word", 11, 10).Should().Be("word");
        gesture.Release(1, "word", 11, 10).Should().BeNull();
    }

    [Fact]
    public void DraggingBackToStart_DoesNotActivateAWord()
    {
        var gesture = new WordRefinementClickGesture();
        gesture.Press(1, "word", 10, 10);
        gesture.Move(1, 20, 10);
        gesture.Release(1, "word", 10, 10).Should().BeNull();
    }

    [Fact]
    public void CancelledPointer_DoesNotActivateAWord()
    {
        var gesture = new WordRefinementClickGesture();
        gesture.Press(1, "word", 10, 10);
        gesture.Cancel();
        gesture.Release(1, "word", 10, 10).Should().BeNull();
    }

    [Fact]
    public void ReleasingOverAnotherWord_DoesNotActivateAWord()
    {
        var gesture = new WordRefinementClickGesture();
        gesture.Press(1, "first", 10, 10);
        gesture.Release(1, "second", 10, 10).Should().BeNull();
    }

    [Fact]
    public void AnotherPointer_CannotConsumeThePendingClick()
    {
        var gesture = new WordRefinementClickGesture();
        gesture.Press(1, "word", 10, 10);
        gesture.Release(2, "word", 10, 10).Should().BeNull();
        gesture.Release(1, "word", 10, 10).Should().Be("word");
    }
}
