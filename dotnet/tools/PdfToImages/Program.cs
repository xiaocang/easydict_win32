using System.Globalization;
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
        Console.WriteLine($"Input PDF : {options.InputPdf}");
        Console.WriteLine($"Output dir: {outputDir}");
        Console.WriteLine($"Pages     : {document.PageCount}");
        Console.WriteLine($"Format    : {options.Format}");
        Console.WriteLine($"Scale     : {scale:F2} ({options.Dpi:F0} DPI)");
        Console.WriteLine();

        for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            var pageNumber = pageIndex + 1;
            var outputPath = Path.Combine(outputDir, $"{baseName}_p{pageNumber:D4}.{options.Format}");

            var page = document[pageIndex];
            var pixmap = page.GetPixmap(new Matrix((float)scale, (float)scale));
            pixmap.Save(outputPath, options.Format);

            Console.WriteLine($"[{pageNumber}/{document.PageCount}] {outputPath}");
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
        Format: format);
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
    if (!double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var result) &&
        !double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out result) ||
        result <= 0)
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
}

sealed record Options(
    string InputPdf,
    string? OutputDir,
    double Dpi,
    double? Scale,
    string Format);
