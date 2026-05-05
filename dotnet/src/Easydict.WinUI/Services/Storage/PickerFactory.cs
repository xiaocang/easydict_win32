using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using NewPickers = Microsoft.Windows.Storage.Pickers;
using LegacyPickers = Windows.Storage.Pickers;

namespace Easydict.WinUI.Services.Storage;

/// <summary>
/// Centralizes picker construction so every call site uses the WinAppSDK 2.x
/// <c>Microsoft.Windows.Storage.Pickers</c> API with a per-scenario
/// <c>SettingsIdentifier</c> (the OS remembers the last folder per identifier).
///
/// Each call site passes a stable identifier that is unique to that scenario
/// (long-doc import vs MDX import vs bilingual export vs OCR output). That way
/// importing a 50MB PDF doesn't reset the folder the user picked for an MDX
/// dictionary, and vice versa.
///
/// The 1.x <c>Windows.Storage.Pickers</c> namespace required
/// <c>InitializeWithWindow.Initialize(picker, hwnd)</c> boilerplate. The 2.x
/// namespace takes the hwnd in the constructor; this helper hides both forms
/// and falls back to the legacy API if the new types aren't yet available at
/// runtime (e.g. WinAppRuntime not yet upgraded on the host).
/// </summary>
internal static class PickerFactory
{
    public static class SettingsIdentifiers
    {
        public const string LongDocImport = "Easydict.LongDoc.Import";
        public const string LongDocOutput = "Easydict.LongDoc.OutputFolder";
        public const string MdxImport = "Easydict.Mdx.Import";
        public const string MddAdd = "Easydict.Mdx.AddMdd";
        public const string BilingualExport = "Easydict.LongDoc.BilingualExport";
        public const string OcrOutput = "Easydict.Ocr.OutputFolder";
    }

    /// <summary>Pick a single file. Returns the absolute path or null if cancelled.</summary>
    public static async Task<string?> PickSingleFileAsync(
        Window window,
        string settingsIdentifier,
        IReadOnlyList<string> fileTypeFilter,
        string? title = null)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        try
        {
            var picker = new NewPickers.FileOpenPicker(windowId)
            {
                SettingsIdentifier = settingsIdentifier,
                CommitButtonText = title ?? string.Empty,
            };
            foreach (var ext in fileTypeFilter) picker.FileTypeFilter.Add(ext);

            var result = await picker.PickSingleFileAsync();
            return result?.Path;
        }
        catch (Exception ex) when (IsApiNotAvailable(ex))
        {
            Debug.WriteLine($"[PickerFactory] New picker API unavailable, falling back: {ex.Message}");
            return await LegacyPickSingleFileAsync(hwnd, fileTypeFilter);
        }
    }

    /// <summary>Pick multiple files. Returns paths or empty list if cancelled.</summary>
    public static async Task<IReadOnlyList<string>> PickMultipleFilesAsync(
        Window window,
        string settingsIdentifier,
        IReadOnlyList<string> fileTypeFilter,
        string? title = null)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        try
        {
            var picker = new NewPickers.FileOpenPicker(windowId)
            {
                SettingsIdentifier = settingsIdentifier,
                CommitButtonText = title ?? string.Empty,
            };
            foreach (var ext in fileTypeFilter) picker.FileTypeFilter.Add(ext);

            var results = await picker.PickMultipleFilesAsync();
            return results == null ? Array.Empty<string>() : results.Select(r => r.Path).ToList();
        }
        catch (Exception ex) when (IsApiNotAvailable(ex))
        {
            Debug.WriteLine($"[PickerFactory] New picker API unavailable, falling back: {ex.Message}");
            return await LegacyPickMultipleFilesAsync(hwnd, fileTypeFilter);
        }
    }

    /// <summary>
    /// Pick a save target. <paramref name="fileTypeChoices"/> maps display names
    /// (e.g. "Bilingual Markdown") to extension lists (e.g. [".md"]).
    /// </summary>
    public static async Task<string?> PickSaveFileAsync(
        Window window,
        string settingsIdentifier,
        IReadOnlyDictionary<string, IList<string>> fileTypeChoices,
        string? suggestedFileName = null,
        int initialFileTypeIndex = 0)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        try
        {
            var picker = new NewPickers.FileSavePicker(windowId)
            {
                SettingsIdentifier = settingsIdentifier,
                SuggestedFileName = suggestedFileName ?? string.Empty,
            };
            foreach (var kvp in fileTypeChoices) picker.FileTypeChoices.Add(kvp.Key, kvp.Value);
            if (initialFileTypeIndex > 0 && initialFileTypeIndex < fileTypeChoices.Count)
            {
                // FileSavePicker on the new namespace exposes a default-selected file type; if
                // the property name differs across point releases, the catch below preserves
                // the picker (with no preselection) rather than failing the whole save flow.
                try { picker.DefaultFileExtension = fileTypeChoices.ElementAt(initialFileTypeIndex).Value.FirstOrDefault() ?? string.Empty; }
                catch { /* surface absent in this build — ignore */ }
            }

            var result = await picker.PickSaveFileAsync();
            return result?.Path;
        }
        catch (Exception ex) when (IsApiNotAvailable(ex))
        {
            Debug.WriteLine($"[PickerFactory] New save picker API unavailable, falling back: {ex.Message}");
            return await LegacySaveFileAsync(hwnd, fileTypeChoices, suggestedFileName);
        }
    }

    /// <summary>Pick a folder. Returns the absolute path or null if cancelled.</summary>
    public static async Task<string?> PickFolderAsync(Window window, string settingsIdentifier)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        try
        {
            var picker = new NewPickers.FolderPicker(windowId)
            {
                SettingsIdentifier = settingsIdentifier,
            };
            var result = await picker.PickSingleFolderAsync();
            return result?.Path;
        }
        catch (Exception ex) when (IsApiNotAvailable(ex))
        {
            Debug.WriteLine($"[PickerFactory] New folder picker API unavailable, falling back: {ex.Message}");
            return await LegacyPickFolderAsync(hwnd);
        }
    }

    private static bool IsApiNotAvailable(Exception ex)
    {
        // The runtime throws TypeLoadException/MissingMethodException when the WinAppRuntime
        // installed on the host is older than what the app was built against. This is the
        // failure shape that justifies the legacy fallback below.
        return ex is TypeLoadException
            || ex is MissingMethodException
            || ex is MissingMemberException
            || ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80040154) };
    }

    private static async Task<string?> LegacyPickSingleFileAsync(IntPtr hwnd, IReadOnlyList<string> fileTypeFilter)
    {
        var picker = new LegacyPickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        foreach (var ext in fileTypeFilter) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static async Task<IReadOnlyList<string>> LegacyPickMultipleFilesAsync(IntPtr hwnd, IReadOnlyList<string> fileTypeFilter)
    {
        var picker = new LegacyPickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        foreach (var ext in fileTypeFilter) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        return files == null ? Array.Empty<string>() : files.Select(f => f.Path).ToList();
    }

    private static async Task<string?> LegacySaveFileAsync(IntPtr hwnd, IReadOnlyDictionary<string, IList<string>> fileTypeChoices, string? suggestedFileName)
    {
        var picker = new LegacyPickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        foreach (var kvp in fileTypeChoices) picker.FileTypeChoices.Add(kvp.Key, kvp.Value);
        if (!string.IsNullOrEmpty(suggestedFileName)) picker.SuggestedFileName = suggestedFileName;
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static async Task<string?> LegacyPickFolderAsync(IntPtr hwnd)
    {
        var picker = new LegacyPickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
