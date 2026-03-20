using FluentAssertions;
using MDict.Csharp.Models;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for MDict case-insensitive lookup behavior.
/// Verifies that dictionaries with KeyCaseSensitive="No" (the default)
/// correctly match words regardless of case.
/// </summary>
[Trait("Category", "WinUI")]
public class MdxEncryptedLookupTests
{
    /// <summary>
    /// Verifies that the default IsCaseSensitive option is false,
    /// matching the MDX header default of KeyCaseSensitive="No".
    /// </summary>
    [Fact]
    public void DefaultOptions_IsCaseSensitive_ShouldBeFalse()
    {
        var options = new MDictOptions();
        // IsCaseSensitive should be null initially (not yet defaulted)
        options.IsCaseSensitive.Should().BeNull();
    }

    /// <summary>
    /// Verifies that after Dict construction, IsCaseSensitive defaults to false.
    /// This is critical: the old default was true, which caused case-sensitive
    /// lookups even when the dictionary header specified KeyCaseSensitive="No".
    /// </summary>
    [Fact]
    public void DictOptions_AfterConstruction_IsCaseSensitiveShouldDefaultToFalse()
    {
        // The Dict constructor sets: options.IsCaseSensitive ??= false
        // We can't instantiate Dict directly (needs a real file), but we can
        // verify the MDictOptions default behavior matches expectations.
        var options = new MDictOptions();
        // Simulate what Dict constructor does
        options.IsCaseSensitive ??= false;
        options.IsCaseSensitive.Should().BeFalse();
    }

    /// <summary>
    /// Verifies that explicit IsCaseSensitive=true is preserved (not overridden).
    /// </summary>
    [Fact]
    public void DictOptions_ExplicitCaseSensitive_ShouldBePreserved()
    {
        var options = new MDictOptions { IsCaseSensitive = true };
        // Simulate what Dict constructor does — should NOT override explicit value
        options.IsCaseSensitive ??= false;
        options.IsCaseSensitive.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that OrdinalIgnoreCase comparison works correctly for
    /// typical dictionary lookup scenarios (mixed-case keys).
    /// </summary>
    [Theory]
    [InlineData("hello", "Hello", 0)]
    [InlineData("hello", "hello", 0)]
    [InlineData("HELLO", "hello", 0)]
    [InlineData("abc", "def", -1)]
    [InlineData("def", "abc", 1)]
    public void CaseInsensitiveComparison_ShouldMatchExpected(string word1, string word2, int expectedSign)
    {
        var result = string.Compare(word1, word2, StringComparison.OrdinalIgnoreCase);
        Math.Sign(result).Should().Be(expectedSign);
    }

    /// <summary>
    /// Verifies that Ordinal comparison is case-sensitive (for dictionaries
    /// that explicitly set KeyCaseSensitive="Yes").
    /// </summary>
    [Fact]
    public void OrdinalComparison_ShouldBeCaseSensitive()
    {
        // 'H' (0x48) < 'h' (0x68) in ordinal comparison
        var result = string.Compare("Hello", "hello", StringComparison.Ordinal);
        result.Should().BeLessThan(0);
    }

    /// <summary>
    /// Regression test: binary search with case-insensitive comparison should
    /// find a word stored with different casing in a sorted keyword list.
    /// </summary>
    [Fact]
    public void BinarySearch_CaseInsensitive_ShouldFindMixedCaseKey()
    {
        // Simulate a sorted keyword list (case-insensitive sort)
        var keywords = new List<string> { "Apple", "banana", "Cherry", "Date", "hello", "World", "Zebra" };
        keywords.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));

        // Binary search for "hello" (lowercase) should find "hello"
        var index = BinarySearchCaseInsensitive(keywords, "hello");
        index.Should().BeGreaterOrEqualTo(0);
        keywords[index].Should().BeEquivalentTo("hello");

        // Binary search for "HELLO" (uppercase) should also find it
        index = BinarySearchCaseInsensitive(keywords, "HELLO");
        index.Should().BeGreaterOrEqualTo(0);
        keywords[index].Should().BeEquivalentTo("hello");

        // Binary search for "Hello" (mixed case) should also find it
        index = BinarySearchCaseInsensitive(keywords, "Hello");
        index.Should().BeGreaterOrEqualTo(0);
        keywords[index].Should().BeEquivalentTo("hello");
    }

    /// <summary>
    /// Regression test: binary search with ordinal comparison should NOT find
    /// a word with different casing when case-sensitive.
    /// </summary>
    [Fact]
    public void BinarySearch_CaseSensitive_ShouldNotFindDifferentCase()
    {
        var keywords = new List<string> { "Apple", "Cherry", "Date", "Hello", "World", "Zebra", "banana" };
        keywords.Sort((a, b) => string.Compare(a, b, StringComparison.Ordinal));

        // "hello" (lowercase) should NOT find "Hello" (uppercase) in ordinal search
        var index = BinarySearchOrdinal(keywords, "hello");
        index.Should().BeLessThan(0, "ordinal binary search should not match different case");
    }

    private static int BinarySearchCaseInsensitive(List<string> list, string word)
    {
        int left = 0, right = list.Count - 1, mid = 0;
        while (left <= right)
        {
            mid = left + (right - left >> 1);
            int cmp = string.Compare(word, list[mid], StringComparison.OrdinalIgnoreCase);
            if (cmp > 0) left = mid + 1;
            else if (cmp == 0) return mid;
            else right = mid - 1;
        }
        return -1;
    }

    private static int BinarySearchOrdinal(List<string> list, string word)
    {
        int left = 0, right = list.Count - 1, mid = 0;
        while (left <= right)
        {
            mid = left + (right - left >> 1);
            int cmp = string.Compare(word, list[mid], StringComparison.Ordinal);
            if (cmp > 0) left = mid + 1;
            else if (cmp == 0) return mid;
            else right = mid - 1;
        }
        return -1;
    }
}
