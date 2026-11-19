using System.Diagnostics;
using System.Runtime.InteropServices;
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
                pageText.Should().Contain(text => text.Contains("这是固定的中文导出演示译文。", StringComparison.Ordinal));
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
            Thread.Sleep(string.IsNullOrWhiteSpace(handshakeDirectory) ? 0 : 5000);

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
        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }
}
