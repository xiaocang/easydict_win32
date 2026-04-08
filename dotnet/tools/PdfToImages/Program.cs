using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Easydict.TranslationService.LongDocument;
using MuPDF.NET;

if (args.Length == 0 || args.Any(static arg => arg is "-h" or "--help"))
{
    PrintUsage();
    return 0;
}

try
{
    var options = ParseArgs(args);

    if (!File.Exists(options.InputPdf))
        throw new FileNotFoundException($"Input PDF not found: {options.InputPdf}");

    var outputDir = options.OutputDir ?? BuildDefaultOutputDir(options.InputPdf);
    Directory.CreateDirectory(outputDir);

    var baseName = Path.GetFileNameWithoutExtension(options.InputPdf);
    var scale = options.Scale ?? options.Dpi / 72.0;

    if (scale <= 0)
        throw new ArgumentOutOfRangeException(nameof(options.Scale), "Scale must be greater than 0.");

    var document = new Document(options.InputPdf);
    try
    {
        var selectedPages = ResolveSelectedPages(options.PageSelection, document.PageCount);
        var pagesToRender = selectedPages ?? Enumerable.Range(1, document.PageCount).ToArray();

        Console.WriteLine($"Input PDF : {options.InputPdf}");
        Console.WriteLine($"Output dir: {outputDir}");
        Console.WriteLine($"Pages     : {FormatPageSummary(selectedPages, document.PageCount)}");
        Console.WriteLine($"Format    : {options.Format}");
        Console.WriteLine($"Scale     : {scale:F2} ({options.Dpi:F0} DPI)");
        Console.WriteLine();

        for (var i = 0; i < pagesToRender.Count; i++)
        {
            var pageNumber = pagesToRender[i];
            var pageIndex = pageNumber - 1;
            var outputPath = Path.Combine(outputDir, $"{baseName}_p{pageNumber:D4}.{options.Format}");

            var page = document[pageIndex];
            var pixmap = page.GetPixmap(new Matrix((float)scale, (float)scale));
            pixmap.Save(outputPath, options.Format);

            Console.WriteLine($"[{i + 1}/{pagesToRender.Count}] {outputPath}");
        }
    }
    finally
    {
        document.Close();
    }

    Console.WriteLine();
    Console.WriteLine("Done.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static Options ParseArgs(IReadOnlyList<string> args)
{
    string? inputPdf = null;
    string? outputDir = null;
    double dpi = 144;
    double? scale = null;
    var format = "png";
    string? pageSelection = null;

    for (var i = 0; i < args.Count; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--input":
            case "-i":
                inputPdf = ReadValue(args, ref i, arg);
                break;
            case "--output-dir":
            case "-o":
                outputDir = ReadValue(args, ref i, arg);
                break;
            case "--dpi":
                dpi = ParsePositiveDouble(ReadValue(args, ref i, arg), arg);
                break;
            case "--scale":
                scale = ParsePositiveDouble(ReadValue(args, ref i, arg), arg);
                break;
            case "--format":
            case "-f":
                format = NormalizeFormat(ReadValue(args, ref i, arg));
                break;
            case "--page":
                pageSelection = NormalizeSinglePage(ReadValue(args, ref i, arg), arg);
                break;
            case "--page-range":
            case "--pages":
                pageSelection = ReadValue(args, ref i, arg);
                break;
            default:
                if (arg.StartsWith("-", StringComparison.Ordinal))
                    throw new ArgumentException($"Unknown argument: {arg}");

                inputPdf ??= arg;
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(inputPdf))
        throw new ArgumentException("Input PDF is required.");

    return new Options(
        InputPdf: Path.GetFullPath(inputPdf),
        OutputDir: string.IsNullOrWhiteSpace(outputDir) ? null : Path.GetFullPath(outputDir),
        Dpi: dpi,
        Scale: scale,
        Format: format,
        PageSelection: pageSelection);
}

static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
{
    if (index + 1 >= args.Count)
        throw new ArgumentException($"Missing value for {option}.");

    index++;
    return args[index];
}

static double ParsePositiveDouble(string value, string option)
{
    if (!double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var result) || result <= 0)
        throw new ArgumentException($"{option} must be a positive number.");

    return result;
}

static string NormalizeFormat(string value) =>
    value.ToLowerInvariant() switch
    {
        "png" => "png",
        "jpg" => "jpg",
        "jpeg" => "jpg",
        _ => throw new ArgumentException("Only png and jpg are supported.")
    };

static string NormalizeSinglePage(string value, string option)
{
    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pageNumber) || pageNumber < 1)
        throw new ArgumentException($"{option} must be an integer >= 1.");

    return pageNumber.ToString(CultureInfo.InvariantCulture);
}

static IReadOnlyList<int>? ResolveSelectedPages(string? pageSelection, int totalPages)
{
    var parsed = PageRangeParser.Parse(pageSelection, totalPages);
    if (parsed is null)
        return null;

    if (parsed.Count == 0)
        throw new ArgumentException($"Page selection '{pageSelection}' does not match any page in this PDF.");

    return parsed.OrderBy(p => p).ToArray();
}

static string FormatPageSummary(IReadOnlyList<int>? selectedPages, int totalPages)
{
    if (selectedPages is null)
        return $"{totalPages} (all)";

    return $"{selectedPages.Count} selected ({string.Join(", ", selectedPages)})";
}

static string BuildDefaultOutputDir(string inputPdf)
{
    var sourceDir = Path.GetDirectoryName(inputPdf) ?? Directory.GetCurrentDirectory();
    var baseName = Path.GetFileNameWithoutExtension(inputPdf);
    return Path.Combine(sourceDir, $"{baseName}_pages");
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project dotnet/tools/PdfToImages -- --input <file.pdf> [--output-dir <dir>] [--dpi 144] [--format png]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -i, --input        Input PDF path. Positional input path is also supported.");
    Console.WriteLine("  -o, --output-dir   Output directory. Default: <pdf-name>_pages");
    Console.WriteLine("      --dpi          Target DPI. Default: 144");
    Console.WriteLine("      --scale        Render scale. Overrides DPI if provided.");
    Console.WriteLine("  -f, --format       png or jpg. Default: png");
    Console.WriteLine("      --page         Single page to export, e.g. 2.");
    Console.WriteLine("      --page-range   Page range to export, e.g. 1-3,5.");
}

sealed record Options(
    string InputPdf,
    string? OutputDir,
    double Dpi,
    double? Scale,
    string Format,
    string? PageSelection);
