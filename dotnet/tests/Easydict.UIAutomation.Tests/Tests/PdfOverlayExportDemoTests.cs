using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.UIAutomation.Tests.Infrastructure;
using Easydict.Workers.LongDoc.Infrastructure;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class PdfOverlayExportDemoTests
{
    /// <summary>The translation the offline demo writes into the exported PDF.</summary>
    private const string TranslatedNeedle = "这是固定的中文导出演示译文。";

    private readonly ITestOutputHelper _output;

    public PdfOverlayExportDemoTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PdfOverlayExportDemo_ExportsAndDisplaysOfflineFixedInput()
    {
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var appWindow = launcher.GetMainWindow();
        PrepareLongDocumentScreen(appWindow);

        var handshakeDirectory = Environment.GetEnvironmentVariable("EASYDICT_DEMO_HANDSHAKE_DIR");
        SignalRecordingReadyAndWait(launcher, appWindow, handshakeDirectory);

        var demoOutputDirectory = Environment.GetEnvironmentVariable("DEMO_OUTPUT_DIR");
        var keepArtifacts = !string.IsNullOrWhiteSpace(demoOutputDirectory);
        var runDirectory = keepArtifacts
            ? Path.Combine(demoOutputDirectory!, "pdf-export-demo")
            : Path.Combine(Path.GetTempPath(), "easydict-pdf-export-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);

        var repositoryRoot = FindRepositoryRoot();
        var inputPath = Path.Combine(repositoryRoot, "lib", "PdfPig", "src", "UglyToad.PdfPig.Tests", "Integration", "Documents", "2108.11480.pdf");
        File.Exists(inputPath).Should().BeTrue($"the bundled fixed-input paper must exist at {inputPath}");
        var translatedPath = Path.Combine(runDirectory, "overlay-real-paper-translated.pdf");
        var resultPath = Path.Combine(runDirectory, "overlay-real-paper-result.json");
        var profileDirectory = Path.Combine(Path.GetTempPath(), "easydict-edge-pdf-demo-" + Guid.NewGuid().ToString("N"));
        Process? edgeLauncher = null;

        try
        {
            var allowFallbackOriginal = Environment.GetEnvironmentVariable("EASYDICT_TEST_ALLOW_CJK_SYSTEM_FONT_FALLBACK");
            Environment.SetEnvironmentVariable("EASYDICT_TEST_ALLOW_CJK_SYSTEM_FONT_FALLBACK", "1");
            TranslateDocumentResult result;
            try
            {
                var pipeline = new WorkerLongDocumentPipeline(TranslateOfflinePaperAsync);
                result = await pipeline.TranslateAsync(
                    new TranslateDocumentParams
                    {
                        InputPath = inputPath,
                        OutputPath = translatedPath,
                        ResultJsonPath = resultPath,
                        InputMode = "Pdf",
                        From = "English",
                        To = "SimplifiedChinese",
                        ServiceId = "offline-demo",
                        OutputMode = "Both",
                        PageRange = "1",
                        PdfExportMode = "ContentStreamReplacement",
                    },
                    new SettingsSnapshot
                    {
                        LongDocMaxConcurrency = 1,
                        LongDocEnableDocumentContextPass = false,
                    },
                    progress: null,
                    CancellationToken.None);
            }
            finally
            {
                Environment.SetEnvironmentVariable("EASYDICT_TEST_ALLOW_CJK_SYSTEM_FONT_FALLBACK", allowFallbackOriginal);
            }

            // The Worker writes the full result to ResultJsonPath and returns a pointer envelope,
            // so chunk counters and the quality report only exist in that file.
            result.ResultJsonPath.Should().NotBeNullOrWhiteSpace("the Worker must hand back its result file path");
            File.Exists(result.ResultJsonPath!).Should().BeTrue($"the Worker must write its result file to {result.ResultJsonPath}");
            result = await LongDocResultFileStore.ReadAsync(result.ResultJsonPath!);

            (result.State is "Completed" or "PartiallyCompleted")
                .Should().BeTrue($"the offline Worker export must finish; quality report: {result.QualityReport}");
            result.SucceededChunks.Should().BeGreaterThan(0);
            File.Exists(translatedPath).Should().BeTrue("the Worker must produce the translated PDF before it is displayed");
            new FileInfo(translatedPath).Length.Should().BeGreaterThan(10_000);
            result.BilingualOutputPath.Should().NotBeNullOrWhiteSpace();
            File.Exists(result.BilingualOutputPath!).Should().BeTrue();
            result.QualityReport.Should().NotBeNullOrWhiteSpace();

            using (var quality = JsonDocument.Parse(result.QualityReport!))
            {
                quality.RootElement.GetProperty("backfillMetrics")
                    .GetProperty("missingBoundingBoxBlocks").GetInt64()
                    .Should().Be(0, "every translated block shown in the demo must have a usable PDF bounding box");
            }

            using (var translatedPdf = PdfPigDocument.Open(translatedPath))
            {
                translatedPdf.NumberOfPages.Should().BeGreaterThan(0);
                var pageText = translatedPdf.GetPages().Select(page => page.Text).ToArray();
                pageText.Should().Contain(text => text.Contains(TranslatedNeedle, StringComparison.Ordinal));
            }

            if (keepArtifacts)
            {
                File.Copy(inputPath, Path.Combine(runDirectory, "overlay-real-paper-source.pdf"), overwrite: true);
                File.Copy(result.BilingualOutputPath!, Path.Combine(runDirectory, "overlay-real-paper-bilingual.pdf"), overwrite: true);
                File.WriteAllText(Path.Combine(runDirectory, "overlay-real-paper-quality-report.json"), result.QualityReport!);
            }

            _output.WriteLine($"Input PDF: {inputPath}");
            _output.WriteLine($"Translated PDF: {translatedPath}");
            _output.WriteLine($"Worker state: {result.State}; translated chunks: {result.SucceededChunks}/{result.TotalChunks}");
            _output.WriteLine($"Quality report: {result.QualityReport}");

            edgeLauncher = LaunchEdgePdfViewer(profileDirectory, translatedPath);
            var edgeWindow = Retry.WhileNull(
                () => FindVisiblePdfWindow(translatedPath),
                TimeSpan.FromSeconds(45),
                TimeSpan.FromMilliseconds(250)).Result;
            edgeWindow.Should().NotBeNull("the generated PDF must be opened in a visible isolated Edge Guest window");
            edgeWindow!.Title.Should().Contain(Path.GetFileName(translatedPath));
            TryBringWindowToFront(edgeWindow);
            if (!string.IsNullOrWhiteSpace(handshakeDirectory))
            {
                WaitForRenderedPdfPage(edgeWindow!, TimeSpan.FromSeconds(60));

                // Give the recorder a few seconds of the finished page instead of a loading spinner.
                Thread.Sleep(6000);
            }

            SignalRecordingFinishedAndWait(handshakeDirectory);
        }
        finally
        {
            if (edgeLauncher is not null)
            {
                try
                {
                    if (!edgeLauncher.HasExited)
                    {
                        edgeLauncher.Kill(entireProcessTree: true);
                        edgeLauncher.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Edge's initial launcher may hand off to its isolated browser process and exit.
                }
                edgeLauncher.Dispose();
            }

            if (!keepArtifacts && Directory.Exists(runDirectory))
            {
                try { Directory.Delete(runDirectory, recursive: true); }
                catch (IOException) { }
            }

            try { if (Directory.Exists(profileDirectory)) Directory.Delete(profileDirectory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void PrepareLongDocumentScreen(Window window)
    {
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        window.SetForeground();
        var sourceLanguage = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SourceLangCombo")),
            TimeSpan.FromSeconds(10)).Result;
        sourceLanguage.Should().NotBeNull("the main window must finish loading before recording starts");

        var modeButton = window.FindFirstDescendant(cf => cf.ByAutomationId("ModeMenuButton"));
        modeButton.Should().NotBeNull("the mode menu must be available for the export demo");
        modeButton!.Patterns.Invoke.Pattern.Invoke();
        var longDocItem = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("ModeLongDocItem")),
            TimeSpan.FromSeconds(5)).Result;
        longDocItem.Should().NotBeNull("the Long Document mode must be available");
        longDocItem!.Click();
        Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("LongDocSourceLangCombo")),
            TimeSpan.FromSeconds(10)).Result
            .Should().NotBeNull("the Long Document screen must be visible before recording starts");
        window.Focus();
    }

    private static void SignalRecordingReadyAndWait(AppLauncher app, Window window, string? handshakeDirectory)
    {
        if (string.IsNullOrWhiteSpace(handshakeDirectory)) return;
        using var process = Process.GetProcessById(app.Application.ProcessId);
        Directory.CreateDirectory(handshakeDirectory);
        File.WriteAllText(Path.Combine(handshakeDirectory, "ready.json"), JsonSerializer.Serialize(new
        {
            pid = app.Application.ProcessId,
            hwnd = window.Properties.NativeWindowHandle.Value.ToInt64(),
            exePath = process.MainModule!.FileName,
            windowTitle = window.Title
        }));
        Retry.WhileFalse(
            () => File.Exists(Path.Combine(handshakeDirectory, "continue")),
            TimeSpan.FromSeconds(90),
            TimeSpan.FromMilliseconds(100),
            throwOnTimeout: true).Success.Should().BeTrue("the recorder must start before PDF export begins");
    }

    private static void SignalRecordingFinishedAndWait(string? handshakeDirectory)
    {
        if (string.IsNullOrWhiteSpace(handshakeDirectory)) return;
        File.WriteAllText(Path.Combine(handshakeDirectory, "finished"), DateTime.UtcNow.ToString("O"));
        Retry.WhileFalse(
            () => File.Exists(Path.Combine(handshakeDirectory, "capture-stopped")),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(100),
            throwOnTimeout: true).Success.Should().BeTrue("the recorder must finish before the PDF viewer is closed");
    }

    private static Process LaunchEdgePdfViewer(string profileDirectory, string pdfPath)
    {
        var edgePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        if (!File.Exists(edgePath))
        {
            edgePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe");
        }
        File.Exists(edgePath).Should().BeTrue("Microsoft Edge is required to display the recorded PDF export");

        var startInfo = new ProcessStartInfo
        {
            FileName = edgePath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(edgePath) ?? Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add($"--user-data-dir={profileDirectory}");
        startInfo.ArgumentList.Add("--guest");
        startInfo.ArgumentList.Add("--disable-extensions");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(pdfPath);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the isolated Edge PDF viewer.");
    }

    private static PdfWindow? FindVisiblePdfWindow(string pdfPath)
    {
        var expectedFileName = Path.GetFileName(pdfPath);
        foreach (var process in Process.GetProcessesByName("msedge"))
        {
            using (process)
            {
                try
                {
                    var hwnd = process.MainWindowHandle;
                    var title = process.MainWindowTitle;
                    if (hwnd == IntPtr.Zero ||
                        !title.Contains(expectedFileName, StringComparison.OrdinalIgnoreCase) ||
                        !NativeWindow.IsWindowVisible(hwnd))
                    {
                        continue;
                    }

                    var guestTitle = title.Contains("[Guest]", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("[访客]", StringComparison.OrdinalIgnoreCase);
                    if (!guestTitle) return null;
                    return new PdfWindow(hwnd, title, process.Id);
                }
                catch (InvalidOperationException)
                {
                    // The process may exit between enumeration and title inspection.
                }
            }
        }

        return null;
    }

    private static void TryBringWindowToFront(PdfWindow window)
    {
        NativeWindow.SetForegroundWindow(window.Handle);
        Thread.Sleep(500);
        var foreground = NativeWindow.GetForegroundWindow();
        _ = NativeWindow.GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        foreground.Should().Be(window.Handle, "the visible PDF must be the recorded foreground window");
        ((int)foregroundProcessId).Should().Be(window.ProcessId, "the foreground window must belong to the isolated Edge Guest process");
    }

    /// <summary>
    /// Waits until the Edge PDF viewer has actually painted the document, and fails when it never
    /// does. Finding the window and bringing it to the front is not enough: the viewer shows a
    /// loading spinner for several seconds, and a recording that stops there shows no translated
    /// page at all - publishing such a GIF would misrepresent the export.
    /// Two independent signals are accepted: the accessible text of the document, or a PDF content
    /// area whose pixels stopped changing (a spinner keeps moving) and that carries page ink.
    /// </summary>
    private static void WaitForRenderedPdfPage(PdfWindow window, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastSignature = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            if (PdfViewerExposesText(window, TranslatedNeedle))
            {
                return;
            }

            // A content area that repeats itself two seconds later is a settled page, not a spinner.
            // Reviewer measurements on real recordings: a page showing the translation has ~0.15 %
            // dark pixels and ~94 % white paper in the content area, a loading frame has 0 % dark and
            // ~2 % white, so both ratios are needed - ink alone would pass a nearly empty viewer and
            // paper alone would pass a blank page.
            var (inkFraction, whiteFraction, signature) = SnapshotWindow(window);
            if (inkFraction > 0.0005 && whiteFraction > 0.5 && signature.Length > 0 && signature == lastSignature)
            {
                return;
            }

            lastSignature = signature;
            Thread.Sleep(2000);
        }

        throw new InvalidOperationException(
            $"The Edge PDF viewer never showed a rendered page within {timeout.TotalSeconds:0} s, so the " +
            "recording would only contain the loading screen.");
    }

    private static bool PdfViewerExposesText(PdfWindow window, string needle)
    {
        try
        {
            using var automation = new FlaUI.UIA3.UIA3Automation();
            var element = automation.FromHandle(window.Handle);
            foreach (var text in element.FindAllDescendants(
                cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text)))
            {
                string name;
                try { name = text.Name ?? string.Empty; }
                catch (Exception) { continue; }
                if (name.Contains(needle, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Accessibility can be unavailable or slow; the pixel check below still applies.
        }

        return false;
    }

    /// <summary>
    /// Samples only the PDF content area - the browser toolbar, tab strip and window borders are
    /// excluded, because they are dark and static even while the document is still loading, which
    /// would make a spinner look like a finished page. Returns how much of that area is ink plus a
    /// coarse signature of the sampled luminance, so two captures can be compared without storing
    /// images.
    /// </summary>
    private static (double InkFraction, double WhiteFraction, string Signature) SnapshotWindow(PdfWindow window)
    {
        int left, top, width, height;
        double dpiScale;
        using (new PerMonitorDpiScope())
        {
            if (!NativeWindow.GetWindowRect(window.Handle, out var rect))
            {
                return (0, 0, string.Empty);
            }

            left = rect.Left;
            top = rect.Top;
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            var dpi = NativeWindow.GetDpiForWindow(window.Handle);
            dpiScale = dpi == 0 ? 1.0 : dpi / 96.0;
        }

        if (width <= 0 || height <= 0)
        {
            return (0, 0, string.Empty);
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(new Point(left, top), Point.Empty, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        // Chrome: tab strip plus toolbar is roughly 110 logical pixels; the page sits inside the
        // remaining area with a margin of its own, so stay well inside that box.
        var contentLeft = (int)Math.Round(bitmap.Width * 0.10);
        var contentRight = (int)Math.Round(bitmap.Width * 0.90);
        var contentTop = Math.Min(bitmap.Height - 1, (int)Math.Round(110 * dpiScale));
        var contentBottom = Math.Min(bitmap.Height, (int)Math.Round(bitmap.Height - 30 * dpiScale));
        if (contentRight <= contentLeft || contentBottom <= contentTop)
        {
            return (0, 0, string.Empty);
        }

        var signature = new StringBuilder();
        long ink = 0;
        long paper = 0;
        long total = 0;
        for (var y = contentTop; y < contentBottom; y += 7)
        {
            for (var x = contentLeft; x < contentRight; x += 7)
            {
                var pixel = bitmap.GetPixel(x, y);
                var luminance = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
                total++;
                if (luminance < 110)
                {
                    ink++;
                }
                else if (luminance > 220)
                {
                    paper++;
                }

                if ((x - contentLeft) % 231 < 7)
                {
                    signature.Append((luminance >> 4).ToString("x1"));
                }
            }
        }

        if (total == 0)
        {
            return (0, 0, signature.ToString());
        }

        return ((double)ink / total, (double)paper / total, signature.ToString());
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".gitmodules")) &&
                Directory.Exists(Path.Combine(directory.FullName, "lib", "PdfPig")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root containing the PdfPig submodule.");
    }

    private static Task<TranslationResult> TranslateOfflinePaperAsync(
        TranslationRequest request,
        string serviceId,
        CancellationToken cancellationToken)
    {
        const string openTag = "[[EQ_SOFT]]";
        const string closeTag = "[[/EQ_SOFT]]";
        var preserved = Regex.Matches(
                request.Text,
                @"\[\[EQ_SOFT\]\].*?\[\[/EQ_SOFT\]\]|\{v\d+\}",
                RegexOptions.Singleline)
            .Select(match => match.Value.StartsWith(openTag, StringComparison.Ordinal)
                ? match.Value.Substring(openTag.Length, match.Value.Length - openTag.Length - closeTag.Length)
                : match.Value)
            .ToArray();
        var metadataText = request.Text
            .Replace(openTag, string.Empty, StringComparison.Ordinal)
            .Replace(closeTag, string.Empty, StringComparison.Ordinal)
            .Trim();
        var preserveMetadata = metadataText.Length <= 80 &&
            (metadataText.Contains("Craig Macdonald", StringComparison.Ordinal) ||
             metadataText.Contains("Nicola Tonellotto", StringComparison.Ordinal) ||
             metadataText.Contains("University of Glasgow, UK", StringComparison.Ordinal) ||
             metadataText.Contains("University of Pisa, Italy", StringComparison.Ordinal) ||
             metadataText.Contains("@glasgow.ac.uk", StringComparison.Ordinal) ||
             metadataText.Contains("@unipi.it", StringComparison.Ordinal));
        var addMetricOperand = Regex.IsMatch(metadataText, @"2\{v\d+\}") ||
            (metadataText.Contains("2×", StringComparison.Ordinal) && preserved.Contains("×", StringComparer.Ordinal));
        var translated = preserveMetadata
            ? request.Text
            : "这是固定的中文导出演示译文。 " +
              (addMetricOperand ? "2" : string.Empty) +
              string.Join(" ", preserved);
        return Task.FromResult(new TranslationResult
        {
            OriginalText = request.Text,
            TranslatedText = translated,
            ServiceName = serviceId,
            DetectedLanguage = request.FromLanguage,
            TargetLanguage = request.ToLanguage,
        });
    }

    private sealed record PdfWindow(IntPtr Handle, string Title, int ProcessId);

    private static class NativeWindow
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
