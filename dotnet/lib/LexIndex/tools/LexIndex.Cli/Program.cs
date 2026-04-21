using LexIndex;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: LexIndex.Cli <input-keys.txt> <output-index.bin>");
    return 1;
}

var inputPath = args[0];
var outputPath = args[1];

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return 1;
}

await using var output = File.Create(outputPath);
await LexIndexBuilder.BuildAsync(File.ReadLines(inputPath), output);
Console.WriteLine($"Built index: {outputPath}");
return 0;
