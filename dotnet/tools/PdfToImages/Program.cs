using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Easydict.TranslationService.LongDocument;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

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

    var file = await StorageFile.GetFileFromPathAsync(options.InputPdf);
    var document = await PdfDocument.LoadFromFileAsync(file);
    var pageCount = checked((int)document.PageCount);
    var selectedPages = ResolveSelectedPages(options.PageSelection, pageCount);
    var pagesToRender = selectedPages ?? Enumerable.Range(1, pageCount).ToArray();

    var effectiveDpi = scale * 72.0;
    Console.WriteLine($"Input PDF : {options.InputPdf}");
    Console.WriteLine($"Output dir: {outputDir}");
    Console.WriteLine($"Pages     : {FormatPageSummary(selectedPages, pageCount)}");
    Console.WriteLine($"Format    : {options.Format}");
    Console.WriteLine($"Scale     : {scale:F2} ({effectiveDpi:F0} DPI)");
    Console.WriteLine();

    for (var i = 0; i < pagesToRender.Count; i++)
    {
        var pageNumber = pagesToRender[i];
        var pageIndex = pageNumber - 1;
        var outputPath = Path.Combine(outputDir, $"{baseName}_p{pageNumber:D4}.{options.Format}");
        using var page = document.GetPage((uint)pageIndex);
        await RenderPageAsync(page, outputPath, options.Format, scale);
        Console.WriteLine($"[{i + 1}/{pagesToRender.Count}] {outputPath}");
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

static async Task RenderPageAsync(PdfPage page, string outputPath, string format, double scale)
{
    using var renderedStream = new InMemoryRandomAccessStream();
    var renderOptions = new PdfPageRenderOptions
    {
        DestinationWidth = checked((uint)Math.Ceiling(page.Size.Width * scale)),
    };
    await page.RenderToStreamAsync(renderedStream, renderOptions);
    renderedStream.Seek(0);

    var decoder = await BitmapDecoder.CreateAsync(renderedStream);
    using var encodedStream = new InMemoryRandomAccessStream();
    var encoderId = format == "jpg" ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;
    var encoder = await BitmapEncoder.CreateAsync(encoderId, encodedStream);
    var pixelData = await decoder.GetPixelDataAsync(
        BitmapPixelFormat.Rgba8,
        BitmapAlphaMode.Ignore,
        new BitmapTransform(),
        ExifOrientationMode.IgnoreExifOrientation,
        ColorManagementMode.DoNotColorManage);
    var pixels = pixelData.DetachPixelData();

    encoder.SetPixelData(
        BitmapPixelFormat.Rgba8,
        BitmapAlphaMode.Ignore,
        decoder.PixelWidth,
        decoder.PixelHeight,
        96,
        96,
        pixels);
    await encoder.FlushAsync();

    encodedStream.Seek(0);
    using var reader = new DataReader(encodedStream);
    await reader.LoadAsync(checked((uint)encodedStream.Size));
    var output = new byte[checked((int)encodedStream.Size)];
    reader.ReadBytes(output);
    await File.WriteAllBytesAsync(outputPath, output);
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
    {
        // PageRangeParser.Parse returns null for three distinct cases:
        //   1. null/empty input        → "all pages"
        //   2. input == "all"          → "all pages"
        //   3. input parsed but matched nothing in range → invalid
        // Only the first two should fall through as "no selection = all pages".
        var isAllPagesRequest = string.IsNullOrWhiteSpace(pageSelection)
            || pageSelection.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
        if (!isAllPagesRequest)
            throw new ArgumentException(
                $"Page selection '{pageSelection}' is invalid or does not match any page in this PDF.");
        return null;
    }

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
