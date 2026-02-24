using System.Diagnostics;
using PdfSharpCore.Fonts;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Custom font resolver for PdfSharpCore that supports CJK fonts loaded from disk.
/// Falls back to the default platform font resolver for non-CJK fonts.
/// Must be registered via <c>GlobalFontSettings.FontResolver</c> before any PDF operations.
/// </summary>
internal sealed class CjkFontResolver : IFontResolver
{
    // CJK font family names used by PdfExportService.PickFont
    public const string NotoSansSC = "Noto Sans SC";
    public const string NotoSansTC = "Noto Sans TC";
    public const string NotoSansJP = "Noto Sans JP";
    public const string NotoSansKR = "Noto Sans KR";

    // Internal face names for font data lookup
    private const string FaceNotoSansSC = "NotoSansSC#R";
    private const string FaceNotoSansTC = "NotoSansTC#R";
    private const string FaceNotoSansJP = "NotoSansJP#R";
    private const string FaceNotoSansKR = "NotoSansKR#R";

    private static readonly object InitLock = new();
    private static bool _initialized;

    // Maps font file paths keyed by face name
    private static readonly Dictionary<string, string> FontFilePaths = new(StringComparer.OrdinalIgnoreCase);

    // Maps family name → face name
    private static readonly Dictionary<string, string> FamilyToFace = new(StringComparer.OrdinalIgnoreCase)
    {
        [NotoSansSC] = FaceNotoSansSC,
        [NotoSansTC] = FaceNotoSansTC,
        [NotoSansJP] = FaceNotoSansJP,
        [NotoSansKR] = FaceNotoSansKR,
    };

    /// <summary>
    /// Registers a CJK font file for use in PDF rendering.
    /// Must be called before the font is used in XFont construction.
    /// </summary>
    public static void RegisterFont(string familyName, string fontFilePath)
    {
        if (!FamilyToFace.TryGetValue(familyName, out var faceName))
        {
            Debug.WriteLine($"[CjkFontResolver] Unknown CJK family: {familyName}");
            return;
        }

        lock (FontFilePaths)
        {
            FontFilePaths[faceName] = fontFilePath;
        }

        Debug.WriteLine($"[CjkFontResolver] Registered {familyName} → {fontFilePath}");
    }

    /// <summary>
    /// Ensures this resolver is registered as the global font resolver.
    /// Safe to call multiple times; only registers once.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            try
            {
                GlobalFontSettings.FontResolver = new CjkFontResolver();
                _initialized = true;
                Debug.WriteLine("[CjkFontResolver] Registered as global font resolver.");
            }
            catch (InvalidOperationException ex)
            {
                // Font resolver was already set (possibly by a previous call)
                Debug.WriteLine($"[CjkFontResolver] Could not set font resolver: {ex.Message}");
                _initialized = true;
            }
        }
    }

    /// <summary>
    /// Returns whether a CJK font family has been registered and its file exists.
    /// </summary>
    public static bool IsFontRegistered(string familyName)
    {
        if (!FamilyToFace.TryGetValue(familyName, out var faceName))
            return false;

        lock (FontFilePaths)
        {
            return FontFilePaths.TryGetValue(faceName, out var path) && File.Exists(path);
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Check if this is a registered CJK family
        if (FamilyToFace.TryGetValue(familyName, out var faceName))
        {
            bool hasFile;
            lock (FontFilePaths)
            {
                hasFile = FontFilePaths.ContainsKey(faceName);
            }

            if (hasFile)
            {
                // CJK fonts typically don't have bold/italic variants in the file we download,
                // so we always return the regular face and let PdfSharpCore simulate bold/italic
                return new FontResolverInfo(faceName, isBold, isItalic);
            }
        }

        // Fall through to platform resolver for system fonts (Arial, Consolas, etc.)
        return PlatformFontResolver.ResolveTypeface(familyName, isBold, isItalic);
    }

    public byte[]? GetFont(string faceName)
    {
        string? fontPath;
        lock (FontFilePaths)
        {
            FontFilePaths.TryGetValue(faceName, out fontPath);
        }

        if (fontPath != null && File.Exists(fontPath))
        {
            Debug.WriteLine($"[CjkFontResolver] Loading font data from: {fontPath}");
            return File.ReadAllBytes(fontPath);
        }

        // Not a CJK font face - return null to let PdfSharpCore try other resolvers
        return null;
    }
}
