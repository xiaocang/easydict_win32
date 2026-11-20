using System.Runtime.CompilerServices;
using Easydict.WinUI.Services.DocumentExport;

namespace Easydict.WinUI.Tests;

/// <summary>
/// PdfSharpCore locks its global font resolver the first time any font is created and cannot be
/// replaced afterwards. A test that writes a fixture PDF with Arial therefore decides how every
/// later export resolves fonts, and Chinese text would silently render as missing glyphs that
/// extraction reports as punctuation only. Installing the production resolver at assembly load
/// keeps the tests on the same font path the app uses.
/// </summary>
internal static class PdfFontBootstrap
{
    private const string SimplifiedChineseFontFile = "NotoSansSC-Regular.ttf";

    [ModuleInitializer]
    internal static void Initialize()
    {
        CjkFontResolver.EnsureInitialized();

        var fontPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict",
            "Fonts",
            SimplifiedChineseFontFile);

        if (File.Exists(fontPath))
        {
            CjkFontResolver.RegisterFont(CjkFontResolver.NotoSansSC, fontPath);
        }
    }
}
