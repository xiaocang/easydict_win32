using System.Diagnostics;
using PdfSharpCore.Fonts;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Custom font resolver for PdfSharpCore that supports CJK fonts loaded from disk
/// and system fonts (Arial, Consolas) from the Windows Fonts directory.
/// Once registered as GlobalFontSettings.FontResolver, PdfSharpCore routes ALL
/// font resolution through this class — returning null does NOT fall back to
/// platform resolution, so we must handle system fonts ourselves.
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

    // Maps family name → face name (includes aliases for internal TTF names from variable fonts)
    private static readonly Dictionary<string, string> FamilyToFace = new(StringComparer.OrdinalIgnoreCase)
    {
        [NotoSansSC] = FaceNotoSansSC,
        [NotoSansTC] = FaceNotoSansTC,
        [NotoSansJP] = FaceNotoSansJP,
        [NotoSansKR] = FaceNotoSansKR,
        // Aliases: the NotoSansCJK variable fonts report these internal family names
        ["Noto Sans CJK SC"] = FaceNotoSansSC,
        ["Noto Sans CJK TC"] = FaceNotoSansTC,
        ["Noto Sans CJK JP"] = FaceNotoSansJP,
        ["Noto Sans CJK KR"] = FaceNotoSansKR,
    };

    // System CJK font family names (Windows built-in)
    public const string MicrosoftYaHei = "Microsoft YaHei";
    public const string MicrosoftJhengHei = "Microsoft JhengHei";
    public const string YuGothic = "Yu Gothic";
    public const string MalgunGothic = "Malgun Gothic";

    // System font file names in %WINDIR%\Fonts, keyed by face name
    private static readonly Dictionary<string, string> SystemFontFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial#R"] = "arial.ttf",
        ["Arial#B"] = "arialbd.ttf",
        ["Arial#I"] = "ariali.ttf",
        ["Arial#BI"] = "arialbi.ttf",
        ["Consolas#R"] = "consola.ttf",
        ["Consolas#B"] = "consolab.ttf",
        ["Consolas#I"] = "consolai.ttf",
        ["Consolas#BI"] = "consolabi.ttf",
        // System CJK fonts (Windows built-in)
        ["Microsoft YaHei#R"] = "msyh.ttc",
        ["Microsoft YaHei#B"] = "msyhbd.ttc",
        ["Microsoft JhengHei#R"] = "msjh.ttc",
        ["Microsoft JhengHei#B"] = "msjhbd.ttc",
        ["Yu Gothic#R"] = "yugothm.ttc",
        ["Yu Gothic#B"] = "yugothb.ttc",
        ["Malgun Gothic#R"] = "malgun.ttf",
        ["Malgun Gothic#B"] = "malgunbd.ttf",
    };

    private static readonly string SystemFontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

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
            string? fontPath;
            lock (FontFilePaths)
            {
                FontFilePaths.TryGetValue(faceName, out fontPath);
            }

            // Validate that the font file actually exists on disk (#17)
            if (fontPath != null && File.Exists(fontPath))
            {
                // CJK fonts typically don't have bold/italic variants in the file we download,
                // so we always return the regular face and let PdfSharpCore simulate bold/italic
                return new FontResolverInfo(faceName, isBold, isItalic);
            }
        }

        // Try system fonts (Arial, Consolas, system CJK) — once this resolver is registered,
        // PdfSharpCore does NOT fall back to platform resolution on null return.
        var systemFace = MakeSystemFaceName(familyName, isBold, isItalic);
        if (SystemFontFiles.ContainsKey(systemFace))
        {
            return new FontResolverInfo(systemFace);
        }

        // Try system CJK fonts as fallback before Arial (for unregistered CJK family requests)
        foreach (var cjkFamily in new[] { MicrosoftYaHei, MicrosoftJhengHei, YuGothic, MalgunGothic })
        {
            var cjkFace = MakeSystemFaceName(cjkFamily, isBold, isItalic);
            if (SystemFontFiles.TryGetValue(cjkFace, out var cjkFile))
            {
                var cjkPath = Path.Combine(SystemFontsDir, cjkFile);
                if (File.Exists(cjkPath))
                {
                    Debug.WriteLine($"[CjkFontResolver] Unknown font '{familyName}', falling back to system CJK font '{cjkFamily}'");
                    return new FontResolverInfo(cjkFace);
                }
            }
        }

        // Fall back to Arial for unknown fonts
        var fallback = MakeSystemFaceName("Arial", isBold, isItalic);
        if (SystemFontFiles.ContainsKey(fallback))
        {
            Debug.WriteLine($"[CjkFontResolver] Unknown font '{familyName}', falling back to Arial");
            return new FontResolverInfo(fallback);
        }

        return null;
    }

    public byte[]? GetFont(string faceName)
    {
        // Check CJK fonts first
        string? fontPath;
        lock (FontFilePaths)
        {
            FontFilePaths.TryGetValue(faceName, out fontPath);
        }

        if (fontPath != null && File.Exists(fontPath))
        {
            Debug.WriteLine($"[CjkFontResolver] Loading CJK font data from: {fontPath}");
            return File.ReadAllBytes(fontPath);
        }

        // Check system fonts
        if (SystemFontFiles.TryGetValue(faceName, out var fileName))
        {
            var fullPath = Path.Combine(SystemFontsDir, fileName);
            if (File.Exists(fullPath))
            {
                Debug.WriteLine($"[CjkFontResolver] Loading system font from: {fullPath}");
                return File.ReadAllBytes(fullPath);
            }
        }

        // PdfSharpCore requires non-null for any face returned by ResolveTypeface.
        // Fall back to Arial regular as a last resort (#12).
        var arialPath = Path.Combine(SystemFontsDir, "arial.ttf");
        if (File.Exists(arialPath))
        {
            Debug.WriteLine($"[CjkFontResolver] Font '{faceName}' not found, falling back to Arial");
            return File.ReadAllBytes(arialPath);
        }

        Debug.WriteLine($"[CjkFontResolver] CRITICAL: No font data available for '{faceName}' and Arial fallback missing");
        return null;
    }

    private static string MakeSystemFaceName(string familyName, bool isBold, bool isItalic)
    {
        var suffix = (isBold, isItalic) switch
        {
            (true, true) => "#BI",
            (true, false) => "#B",
            (false, true) => "#I",
            _ => "#R"
        };
        return $"{familyName}{suffix}";
    }

    public string DefaultFontName => "Arial";
}
