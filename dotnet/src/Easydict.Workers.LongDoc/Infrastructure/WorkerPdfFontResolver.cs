using PdfSharpCore.Fonts;

namespace Easydict.Workers.LongDoc.Infrastructure;

internal sealed class WorkerPdfFontResolver : IFontResolver
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    private static readonly string SystemFontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static readonly Dictionary<string, string> SystemFontFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial#R"] = "arial.ttf",
        ["Arial#B"] = "arialbd.ttf",
        ["Arial#I"] = "ariali.ttf",
        ["Arial#BI"] = "arialbi.ttf",
        ["Microsoft YaHei#R"] = "msyh.ttc",
        ["Microsoft YaHei#B"] = "msyhbd.ttc",
        ["Microsoft JhengHei#R"] = "msjh.ttc",
        ["Microsoft JhengHei#B"] = "msjhbd.ttc",
        ["Yu Gothic#R"] = "yugothm.ttc",
        ["Yu Gothic#B"] = "yugothb.ttc",
        ["Malgun Gothic#R"] = "malgun.ttf",
        ["Malgun Gothic#B"] = "malgunbd.ttf",
    };

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                GlobalFontSettings.FontResolver = new WorkerPdfFontResolver();
            }
            catch (InvalidOperationException)
            {
                // PdfSharpCore allows only one global resolver. If another test or host path
                // registered one already, keep it rather than failing worker PDF export.
            }

            _initialized = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var requested = MakeFaceName(familyName, isBold, isItalic);
        if (SystemFontFiles.TryGetValue(requested, out var fileName) &&
            File.Exists(Path.Combine(SystemFontsDir, fileName)))
        {
            return new FontResolverInfo(requested);
        }

        foreach (var cjkFamily in new[] { "Microsoft YaHei", "Microsoft JhengHei", "Yu Gothic", "Malgun Gothic" })
        {
            var cjkFace = MakeFaceName(cjkFamily, isBold, isItalic);
            if (SystemFontFiles.TryGetValue(cjkFace, out var cjkFile) &&
                File.Exists(Path.Combine(SystemFontsDir, cjkFile)))
            {
                return new FontResolverInfo(cjkFace);
            }
        }

        var arialFace = MakeFaceName("Arial", isBold, isItalic);
        return SystemFontFiles.ContainsKey(arialFace)
            ? new FontResolverInfo(arialFace)
            : null;
    }

    public byte[]? GetFont(string faceName)
    {
        if (SystemFontFiles.TryGetValue(faceName, out var fileName))
        {
            var path = Path.Combine(SystemFontsDir, fileName);
            if (File.Exists(path))
            {
                return File.ReadAllBytes(path);
            }
        }

        var fallback = Path.Combine(SystemFontsDir, "arial.ttf");
        return File.Exists(fallback) ? File.ReadAllBytes(fallback) : null;
    }

    public string DefaultFontName => "Arial";

    private static string MakeFaceName(string familyName, bool isBold, bool isItalic)
    {
        var suffix = (isBold, isItalic) switch
        {
            (true, true) => "#BI",
            (true, false) => "#B",
            (false, true) => "#I",
            _ => "#R",
        };

        return $"{familyName}{suffix}";
    }
}
