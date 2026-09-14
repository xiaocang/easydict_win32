using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Easydict.UIAutomation.Tests.Infrastructure;

/// <summary>A real, single-entry MDX/MDD dictionary with no network or user-settings dependency.</summary>
internal sealed class DictionaryRenderingFixture : IDisposable
{
    public const string Query = "no";
    public const string Definition = "Deterministic dictionary definition";
    public const string ServiceName = "UIA Dictionary";
    private const string ServiceId = "mdx::uia-dictionary";
    private readonly string? _previousSettings = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");

    public DictionaryRenderingFixture()
    {
        // Explorer/MSIX activation does not inherit the fixture's settings directory.
        var exePath = Environment.GetEnvironmentVariable("EASYDICT_EXE_PATH");
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("Set EASYDICT_EXE_PATH to the test app executable for isolated dictionary rendering tests.");

        var directory = Path.Combine(Path.GetTempPath(), "Easydict.DictionaryRendering.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dictionaryPath = Path.Combine(directory, "fixture.mdx");
        WriteDictionary(dictionaryPath, Query,
            $"<link rel=\"stylesheet\" href=\"fixture.css\"><h2>{Query}</h2><p>{Definition}</p>\0", Encoding.UTF8);
        // MDD resources select the production RawHtml/WebView rendering path.
        WriteDictionary(Path.ChangeExtension(dictionaryPath, ".mdd"), "\\fixture.css",
            "h2 { font-weight: bold; }", Encoding.Unicode);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            UILanguage = "en-US", AppTheme = "Light", CompactMode = false,
            MainWindowEnabledServices = new[] { ServiceId },
            MainWindowServiceEnabledQuery = new Dictionary<string, bool> { [ServiceId] = true },
            ImportedMdxDictionaries = new[] { new { ServiceId, DisplayName = ServiceName, FilePath = dictionaryPath, IsEncrypted = false } },
            EnableShowWindowHotkey = false, EnableTranslateSelectionHotkey = false,
            EnableShowMiniWindowHotkey = false, EnableShowFixedWindowHotkey = false,
            EnableOcrTranslateHotkey = false, EnableSilentOcrHotkey = false,
        }));
        Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", _previousSettings);

    // MDict v2: UTF-16 header, one key block, and one record block.
    // MDX keys use UTF-8; MDD resource keys use UTF-16LE.
    // Generating the tiny fixture keeps the entry and its format reviewable in source.
    private static void WriteDictionary(string path, string entry, string record, Encoding keyEncoding)
    {
        var encodingName = keyEncoding == Encoding.Unicode ? "UTF-16" : "UTF-8";
        var header = Encoding.Unicode.GetBytes($"<Dictionary GeneratedByEngineVersion=\"2.0\" RequiredEngineVersion=\"2.0\" Encoding=\"{encodingName}\" Encrypted=\"No\" Title=\"UIA Dictionary\"/>\0");
        var key = keyEncoding.GetBytes(entry + "\0");
        var keyData = new byte[8 + key.Length]; // Record offset is zero.
        key.CopyTo(keyData, 8);
        var keyBlock = Pack(keyData);
        var recordData = Encoding.UTF8.GetBytes(record);
        var recordBlock = Pack(recordData);

        using var info = new MemoryStream();
        WriteNumber(info, 1, 8);
        for (var i = 0; i < 2; i++) // First and last keys in the block.
        {
            WriteNumber(info, entry.Length, 2);
            info.Write(key);
        }
        WriteNumber(info, keyBlock.Length, 8);
        WriteNumber(info, keyData.Length, 8);
        var packedInfo = Pack(info.ToArray());

        using var keyHeader = new MemoryStream();
        foreach (var value in new long[] { 1, 1, info.Length, packedInfo.Length, keyBlock.Length })
            WriteNumber(keyHeader, value, 8);

        using var file = File.Create(path);
        WriteNumber(file, header.Length, 4);
        file.Write(header);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(checksum, Adler32(header));
        file.Write(checksum);
        file.Write(keyHeader.ToArray());
        WriteNumber(file, Adler32(keyHeader.ToArray()), 4);
        file.Write(packedInfo);
        file.Write(keyBlock);
        foreach (var value in new long[] { 1, 1, 16, recordBlock.Length, recordBlock.Length, recordData.Length })
            WriteNumber(file, value, 8);
        file.Write(recordBlock);
    }

    private static byte[] Pack(byte[] data)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 2, 0, 0, 0 }); // zlib compression marker.
        WriteNumber(output, Adler32(data), 4);
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(data);
        return output.ToArray();
    }

    private static void WriteNumber(Stream stream, long value, int width)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes[(8 - width)..]);
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
