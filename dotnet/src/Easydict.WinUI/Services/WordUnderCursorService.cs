using System.Diagnostics;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services.ScreenCapture;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;

namespace Easydict.WinUI.Services;

/// <summary>
/// How the word under the pointer was obtained.
/// </summary>
public enum WordSource
{
    /// <summary>UI Automation TextPattern (accessible text).</summary>
    Uia,

    /// <summary>OCR of the pixels around the pointer.</summary>
    Ocr,
}

/// <summary>
/// A word found under the pointer with its on-screen rectangle (physical pixels, virtual-desktop coordinates).
/// </summary>
public sealed record WordUnderCursor(string Text, OcrRect ScreenRect, WordSource Source);

/// <summary>
/// Finds the word under a screen point for hover word lookup.
/// Strategy: UI Automation first (element from point → nearest ancestor with TextPattern →
/// RangeFromPoint → expand to word), then an OCR fallback over a small screen capture around
/// the pointer for applications that expose no accessible text (Chromium without UIA, images…).
/// All methods are safe to call from a thread-pool thread.
/// </summary>
public sealed class WordUnderCursorService
{
    /// <summary>How many ancestors of the hit element are inspected for a TextPattern.</summary>
    internal const int MaxUiaAncestorDepth = 6;

    /// <summary>Longest text accepted as a single word.</summary>
    internal const int MaxWordLength = 48;

    /// <summary>Longest CJK run accepted as a single lookup unit.</summary>
    internal const int MaxCjkWordLength = 8;

    /// <summary>Characters requested from the expanded UIA range (anything longer is not a word).</summary>
    internal const int MaxUiaRangeTextLength = 64;

    /// <summary>Tolerance (px) when checking that the pointer is inside the UIA word rectangle.</summary>
    internal const int UiaHitTolerancePx = 4;

    /// <summary>Rectangles taller than this are documents/paragraphs, not words.</summary>
    internal const int MaxUiaRangeHeightPx = 120;

    /// <summary>Tolerance (source px) when picking the OCR word nearest to the pointer.</summary>
    internal const int OcrHitTolerancePx = 6;

    /// <summary>Capture boxes smaller than this are not worth OCR-ing.</summary>
    internal const int MinOcrCaptureWidth = 16;
    internal const int MinOcrCaptureHeight = 12;

    private readonly IOcrService _ocrService;
    private readonly Func<string?> _preferredOcrLanguage;

    public WordUnderCursorService(IOcrService ocrService, Func<string?> preferredOcrLanguage)
    {
        _ocrService = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
        _preferredOcrLanguage = preferredOcrLanguage ?? throw new ArgumentNullException(nameof(preferredOcrLanguage));
    }

    /// <summary>
    /// Find the word under (<paramref name="screenX"/>, <paramref name="screenY"/>).
    /// </summary>
    /// <param name="screenX">Pointer X in physical pixels.</param>
    /// <param name="screenY">Pointer Y in physical pixels.</param>
    /// <param name="useOcrFallback">Whether to OCR the pixels around the pointer when UIA yields nothing.</param>
    /// <param name="excludeFromCapture">Screen rectangle (e.g. our own visible popup) to keep out of the OCR capture.</param>
    /// <param name="dpiScale">DPI scale of the monitor under the pointer (1.0 = 96 DPI).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onOcrRetry">Called on the recognition thread when OCR starts an additional pass.</param>
    public async Task<WordUnderCursor?> GetWordAtAsync(
        int screenX,
        int screenY,
        bool useOcrFallback,
        OcrRect? excludeFromCapture,
        double dpiScale,
        CancellationToken cancellationToken,
        Action? onOcrRetry = null)
    {
        var uiaWord = await TryGetWordViaUiaGuardedAsync(screenX, screenY, cancellationToken);
        if (uiaWord is not null)
        {
            return uiaWord;
        }

        if (!useOcrFallback)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await TryGetWordViaOcrAsync(screenX, screenY, excludeFromCapture, dpiScale, cancellationToken, onOcrRetry);
    }

    // ---------------------------------------------------------------------
    // UI Automation path
    // ---------------------------------------------------------------------

    /// <summary>
    /// Run the UIA lookup under the shared single-flight gate and execution timeout used by
    /// <see cref="TextSelectionService"/>, so a hung Chromium provider costs at most
    /// <see cref="TextSelectionService.UiaExecutionTimeoutMs"/> and never overlaps another UIA call.
    /// </summary>
    private static async Task<WordUnderCursor?> TryGetWordViaUiaGuardedAsync(int x, int y, CancellationToken cancellationToken)
    {
        var semaphore = TextSelectionService.AutomationSemaphore;
        var semaphoreAcquired = false;
        try
        {
            semaphoreAcquired = await semaphore.WaitAsync(TextSelectionService.UiaSemaphoreTimeoutMs, cancellationToken);
            if (!semaphoreAcquired)
            {
                Debug.WriteLine("[WordUnderCursor] UIA busy, skipping UIA path");
                return null;
            }

            var uiaTask = Task.Run(() => TryGetWordViaUia(TextSelectionService.Automation, x, y), cancellationToken);
            _ = uiaTask.ContinueWith(task =>
            {
                _ = task.Exception; // Observe failures after the caller times out.
                semaphore.Release();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            semaphoreAcquired = false; // Released by the continuation above.

            var completed = await Task.WhenAny(uiaTask, Task.Delay(TextSelectionService.UiaExecutionTimeoutMs, cancellationToken));
            if (completed == uiaTask)
            {
                return await uiaTask;
            }

            Debug.WriteLine("[WordUnderCursor] UIA timed out, skipping UIA path");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WordUnderCursor] UIA failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (semaphoreAcquired)
            {
                semaphore.Release();
            }
        }
    }

    /// <summary>
    /// UIA lookup: element from point → walk up to the first ancestor with a TextPattern →
    /// RangeFromPoint → ExpandToEnclosingUnit(Word). Runs synchronously; call from a worker thread.
    /// </summary>
    internal static WordUnderCursor? TryGetWordViaUia(UIA3Automation automation, int x, int y)
    {
        var point = new System.Drawing.Point(x, y);

        AutomationElement? element;
        try
        {
            element = automation.FromPoint(point);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WordUnderCursor] FromPoint failed: {ex.Message}");
            return null;
        }

        if (element is null)
        {
            return null;
        }

        if (BelongsToOwnProcess(element))
        {
            return null;
        }

        var current = element;
        for (var depth = 0; depth <= MaxUiaAncestorDepth && current is not null; depth++)
        {
            if (IsPasswordElement(current))
            {
                return null;
            }

            ITextPattern? textPattern = null;
            try
            {
                if (!current.Patterns.Text.TryGetPattern(out textPattern))
                {
                    textPattern = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WordUnderCursor] TextPattern query failed: {ex.Message}");
            }

            if (textPattern is not null)
            {
                var word = TryGetWordFromPattern(textPattern, point);
                if (word is not null)
                {
                    return word;
                }
            }

            current = GetParentSafely(current);
        }

        return null;
    }

    private static WordUnderCursor? TryGetWordFromPattern(ITextPattern textPattern, System.Drawing.Point point)
    {
        try
        {
            var range = textPattern.RangeFromPoint(point);
            if (range is null)
            {
                return null;
            }

            range.ExpandToEnclosingUnit(TextUnit.Word);
            var normalized = NormalizeWord(range.GetText(MaxUiaRangeTextLength));
            if (normalized is null || !LooksLikeWord(normalized))
            {
                return null;
            }

            // RangeFromPoint returns the nearest range even when the pointer is not over text,
            // so require that one of the word's rectangles actually contains the pointer.
            foreach (var rectangle in range.GetBoundingRectangles())
            {
                if (rectangle.Width <= 0 || rectangle.Height <= 0 || rectangle.Height > MaxUiaRangeHeightPx)
                {
                    continue;
                }

                var rect = new OcrRect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
                if (rect.Inflate(UiaHitTolerancePx, UiaHitTolerancePx).Contains(point.X, point.Y))
                {
                    return new WordUnderCursor(normalized, rect, WordSource.Uia);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WordUnderCursor] Text range lookup failed: {ex.Message}");
            return null;
        }
    }

    private static bool BelongsToOwnProcess(AutomationElement element)
    {
        try
        {
            return element.Properties.ProcessId.ValueOrDefault == Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPasswordElement(AutomationElement element)
    {
        try
        {
            return element.Properties.IsPassword.ValueOrDefault;
        }
        catch
        {
            return false;
        }
    }

    private static AutomationElement? GetParentSafely(AutomationElement element)
    {
        try
        {
            return element.Parent;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // OCR fallback path
    // ---------------------------------------------------------------------

    private async Task<WordUnderCursor?> TryGetWordViaOcrAsync(
        int x,
        int y,
        OcrRect? excludeFromCapture,
        double dpiScale,
        CancellationToken cancellationToken,
        Action? onOcrRetry)
    {
        try
        {
            if (!_ocrService.IsAvailable)
            {
                return null;
            }

            var virtualScreen = ScreenRegionCapture.GetVirtualScreenBounds();
            var captureRect = ScreenRegionCapture.ComputeCaptureRect(x, y, dpiScale, virtualScreen, excludeFromCapture);
            if (captureRect.Width < MinOcrCaptureWidth || captureRect.Height < MinOcrCaptureHeight)
            {
                return null;
            }

            // Small UI text OCRs noticeably better when upscaled at low scale factors.
            var upscale = dpiScale < 1.5 ? 2 : 1;

            using var capture = ScreenRegionCapture.Capture(captureRect, upscale);
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _ocrService.RecognizeAsync(capture, _preferredOcrLanguage(), cancellationToken, onOcrRetry);
            if (result.Lines.Count == 0)
            {
                return null;
            }

            var localX = (x - capture.ScreenRect.X) * upscale;
            var localY = (y - capture.ScreenRect.Y) * upscale;
            var word = OcrWordHitTester.FindWordAt(result.Lines, localX, localY, OcrHitTolerancePx * upscale);
            if (word is null)
            {
                return null;
            }

            var normalized = NormalizeWord(word.Text);
            if (normalized is null || !LooksLikeWord(normalized))
            {
                return null;
            }

            var r = word.BoundingRect;
            var screenRect = new OcrRect(
                capture.ScreenRect.X + r.X / upscale,
                capture.ScreenRect.Y + r.Y / upscale,
                r.Width / upscale,
                r.Height / upscale);
            return new WordUnderCursor(normalized, screenRect, WordSource.Ocr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WordUnderCursor] OCR fallback failed: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Pure text helpers (unit-tested)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Trim whitespace, quotes and punctuation from both ends of a raw word.
    /// Returns null when nothing is left. Internal apostrophes/hyphens are preserved ("don't", "well-known").
    /// </summary>
    internal static string? NormalizeWord(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = 0;
        var end = raw.Length - 1;
        while (start <= end && IsTrimmable(raw[start])) start++;
        while (end >= start && IsTrimmable(raw[end])) end--;

        if (end < start)
        {
            return null;
        }

        return raw.Substring(start, end - start + 1);
    }

    /// <summary>
    /// Whether a normalized token is worth looking up: a single Latin-script word
    /// (letters with optional digits, apostrophes or hyphens) or a short CJK run.
    /// URLs, e-mail addresses, pure numbers and anything with whitespace are rejected.
    /// </summary>
    internal static bool LooksLikeWord(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length > MaxWordLength)
        {
            return false;
        }

        var hasLetter = false;
        var hasCjk = false;
        foreach (var c in word)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }

            if (c is '/' or '\\' or '@' or ':' or '.' or ',' or ';' or '=' or '?' or '&' or '#' or '<' or '>' or '|' or '"' or '(' or ')' or '[' or ']' or '{' or '}')
            {
                return false;
            }

            if (IsCjk(c))
            {
                hasCjk = true;
                hasLetter = true;
                continue;
            }

            if (char.IsLetter(c))
            {
                hasLetter = true;
                continue;
            }

            if (char.IsDigit(c) || c is '\'' or '\u2019' or '-' or '\u00AD')
            {
                continue;
            }

            return false;
        }

        if (!hasLetter)
        {
            return false;
        }

        if (hasCjk)
        {
            // A CJK lookup unit must be short and purely CJK (mixed scripts are not one word).
            if (word.Length > MaxCjkWordLength)
            {
                return false;
            }

            foreach (var c in word)
            {
                if (!IsCjk(c))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsTrimmable(char c)
    {
        if (char.IsWhiteSpace(c) || char.IsControl(c))
        {
            return true;
        }

        if (c is '\u200B' or '\u200C' or '\u200D' or '\uFEFF')
        {
            return true; // zero-width characters
        }

        // Hyphens/apostrophes inside a word are fine, but never as a leading/trailing character.
        return char.IsPunctuation(c) || char.IsSymbol(c);
    }

    private static bool IsCjk(char c)
    {
        // CJK Unified Ideographs (+ Extension A, Compatibility), Hiragana, Katakana, Hangul.
        return (c >= '\u4E00' && c <= '\u9FFF')
            || (c >= '\u3400' && c <= '\u4DBF')
            || (c >= '\uF900' && c <= '\uFAFF')
            || (c >= '\u3040' && c <= '\u309F')
            || (c >= '\u30A0' && c <= '\u30FF')
            || (c >= '\uAC00' && c <= '\uD7AF');
    }
}
