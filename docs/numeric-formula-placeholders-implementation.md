# Numeric Formula Placeholders Implementation

**Date:** 2025-02-24
**Component:** Easydict Win32 - Long Document Translation Service
**Status:** ✅ Completed

---

## Summary

Successfully implemented numeric formula placeholders (`{vN}`) to replace the previous hash-based placeholder system (`[[FORMULA_{counter}_{hash}]]`) in the long document translation service. This change aligns with PDFMathTranslate's formula placeholder format, providing better interoperability and simpler debugging.

---

## Changes Made

### 1. Core Implementation (`LongDocumentTranslationService.cs`)

**File:** `dotnet/src/Easydict.TranslationService/LongDocument/LongDocumentTranslationService.cs`

#### Changed Data Structure

**Before:**
```csharp
private sealed record FormulaProtectionResult(
    string ProtectedText,
    IReadOnlyDictionary<string, FormulaToken> TokenMap)
```

**After:**
```csharp
private sealed record FormulaProtectionResult(
    string ProtectedText,
    IReadOnlyList<FormulaToken> TokenMap)
```

**Rationale:** Numeric placeholders use index-based lookup, so a `List<T>` is more appropriate than a `Dictionary<K,V>`.

#### Added Regex for Placeholder Detection

```csharp
private static readonly Regex NumericPlaceholderRegex = new(@"\{v(\d+)\}", RegexOptions.Compiled);
```

#### Updated Protection Logic

**Before:**
```csharp
var protectedText = FormulaRegex.Replace(text, match =>
{
    var token = $"[[FORMULA_{counter}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(match.Value)))[..8]}]]";
    map[token] = new FormulaToken(match.Value, ClassifyFormulaToken(match.Value));
    counter++;
    return token;
});
```

**After:**
```csharp
var protectedText = FormulaRegex.Replace(text, match =>
{
    var token = $"{{v{counter}}}";
    tokens.Add(new FormulaToken(match.Value, ClassifyFormulaToken(match.Value)));
    counter++;
    return token;
});
```

#### Updated Restoration Logic

**Before:**
```csharp
var restored = text;
foreach (var pair in protection.TokenMap)
{
    restored = restored.Replace(pair.Key, pair.Value.RawText, StringComparison.Ordinal);
}

if (Regex.IsMatch(restored, @"\[\[FORMULA_[^\]]+\]\]"))
{
    return originalText;
}
```

**After:**
```csharp
var restored = NumericPlaceholderRegex.Replace(text, match =>
{
    var indexStr = match.Groups[1].Value;
    if (int.TryParse(indexStr, out var index) && index >= 0 && index < protection.TokenMap.Count)
    {
        return protection.TokenMap[index].RawText;
    }
    return match.Value;
});

if (NumericPlaceholderRegex.IsMatch(restored))
{
    return originalText;
}
```

#### Updated IsFormulaOnlyText Check

**Before:**
```csharp
var cleaned = Regex.Replace(protectedText, @"\[\[FORMULA_[^\]]+\]\]", string.Empty).Trim();
```

**After:**
```csharp
var cleaned = NumericPlaceholderRegex.Replace(protectedText, string.Empty).Trim();
```

---

### 2. Test Updates (`LongDocumentTranslationServiceTests.cs`)

**File:** `dotnet/tests/Easydict.TranslationService.Tests/LongDocument/LongDocumentTranslationServiceTests.cs`

#### Updated Existing Tests

- **Line 81:** Changed assertion from `"[[FORMULA_"` to `"{v"`
- **Line 128:** Changed assertion from `"[[FORMULA_"` to `"{v"`
- **Line 188:** Changed fake translation result from `"ZH:[[FORMULA_0_ABCDEF12]]("` to `"ZH:{v0}("`

#### Added New Tests

1. **`TranslateAsync_ShouldUseNumericPlaceholdersForMultipleFormulas`**
   - Tests that multiple formulas in a single block get `{v0}`, `{v1}`, etc.
   - Verifies placeholders replace the original formula text

2. **`TranslateAsync_ShouldRestoreNumericPlaceholdersInCorrectOrder`**
   - Tests that placeholders are restored in the correct order
   - Ensures `{v0}` → first formula, `{v1}` → second formula, etc.

3. **`TranslateAsync_ShouldHandleMixedFormulaAndText`**
   - Tests that placeholders are correctly restored when mixed with translated text
   - Verifies the regex replacement works correctly

---

### 3. Bug Fix (`FormulaDetectionTests.cs`)

**File:** `dotnet/tests/Easydict.TranslationService.Tests/LongDocument/FormulaDetectionTests.cs`

#### Fixed Issues

1. Added missing `using Easydict.TranslationService.Models;` directive
2. Fixed `Models.Language` → `Language`
3. Fixed `Models.TranslationResult` → `TranslationResult`
4. Fixed `TranslationResult.ServiceId` → `TranslationResult.ServiceName`
5. Added missing `OriginalText` property to `TranslationResult` initialization
6. Changed test method from synchronous to asynchronous (`public async Task`)

---

## Benefits

### 1. **Simpler Format**
   - `{v0}` is more readable than `[[FORMULA_0_ABCDEF12345678]]`
   - Easier to debug during development
   - Smaller string size (less memory overhead)

### 2. **Better Interoperability**
   - Compatible with PDFMathTranslate's formula placeholder format
   - Easier to share translations between tools
   - Consistent format across projects

### 3. **Performance Improvements**
   - No SHA256 hash computation
   - Index-based lookup is O(1) instead of O(n) string replacement
   - Regex-based restoration is more efficient

### 4. **Maintained Safety**
   - All validation still in place:
     - Formula delimiter balance checking
     - Unreplaced placeholder detection
     - Fallback to original text on error

---

## Example

### Input Text
```
The equations $a^2+b^2=c^2$ and $E=mc^2$ are fundamental to physics.
```

### Protected Text (sent to translator)
```
The equations {v0} and {v1} are fundamental to physics.
```

### After Translation (假设翻译为中文)
```
公式 {v0} 和 {v1} 是物理学的基础。
```

### Restored Text (formulas reinserted)
```
公式 $a^2+b^2=c^2$ 和 $E=mc^2$ 是物理学的基础。
```

---

## Backward Compatibility

### Cache Considerations

**Note:** This change affects the format of text sent to translation services. Any cached translations using the old `[[FORMULA_{counter}_{hash}]]` format will not be compatible with the new `{vN}` format.

**Migration Strategy:** (Optional)
- If backward compatibility with cached translations is required, add a format detection flag:
  ```csharp
  public enum FormulaPlaceholderFormat { LegacyHash, Numeric }
  public LongDocumentTranslationOptions { FormulaPlaceholderFormat PlaceholderFormat { get; init; } = FormulaPlaceholderFormat.Numeric; }
  ```

**Recommendation:** For this implementation, we accept the cache break as a one-time cost. The benefits of the new format outweigh the inconvenience of re-translating cached documents.

---

## Testing Results

All 63 tests in `Easydict.TranslationService.Tests` pass successfully:
```
已通过! - 失败: 0，通过: 63，已跳过: 0
```

Specific tests for numeric formula placeholders:
- ✅ Multiple formulas in single block
- ✅ Correct order restoration
- ✅ Mixed formula and text handling
- ✅ Formula-only text detection
- ✅ Unbalanced delimiter fallback
- ✅ All existing regression tests

---

## Files Modified

1. `dotnet/src/Easydict.TranslationService/LongDocument/LongDocumentTranslationService.cs`
   - Lines 415-477: Formula protection and restoration logic

2. `dotnet/tests/Easydict.TranslationService.Tests/LongDocument/LongDocumentTranslationServiceTests.cs`
   - Lines 81-128: Updated existing test assertions
   - Lines 188: Updated fake translation data
   - Lines 370-470: Added 3 new test methods

3. `dotnet/tests/Easydict.TranslationService.Tests/LongDocument/FormulaDetectionTests.cs`
   - Lines 1-6: Added missing using directive
   - Lines 142-165: Fixed test method signature and data structures

---

## Next Steps

1. ✅ **COMPLETED:** Implement numeric formula placeholders
2. **TODO:** (Optional) Add backward compatibility layer for legacy cache format
3. **TODO:** Update documentation to reflect new placeholder format
4. **TODO:** Consider exposing placeholder format as a user-facing option for advanced debugging

---

## References

- Cross-pollination analysis: `docs/cross-pollination-analysis.md`
- PDFMathTranslate formula placeholder implementation: `pdf2zh/converter.py` (lines 256-280)
- Original issue: Implementation of cross-pollination opportunities between Easydict Win32 and PDFMathTranslate
