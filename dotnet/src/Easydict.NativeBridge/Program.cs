using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.NativeBridge;

/// <summary>
/// Minimal Native Messaging host for Chrome/Firefox browser extensions.
///
/// Protocol: stdin/stdout with 4-byte little-endian length prefix per message (JSON).
/// On receiving any message, signals the Easydict named event to trigger OCR capture.
///
/// Deployed to: %LocalAppData%\Easydict\browser-bridge\easydict-native-bridge.exe
/// Registered via: HKCU\Software\{Google\Chrome|Mozilla}\NativeMessagingHosts\com.easydict.bridge
/// </summary>
public static class Program
{
    /// <summary>
    /// Must match the event name in Easydict.WinUI.Program.OcrTranslateEventName.
    /// </summary>
    private const string OcrTranslateEventName = @"Local\Easydict-OcrTranslate";

    static void Main()
    {
        // Native Messaging hosts read from stdin in a loop.
        // The browser keeps the process alive as long as the extension port is open.
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        while (true)
        {
            // Read 4-byte length prefix (little-endian uint32)
            var lengthBytes = new byte[4];
            var bytesRead = ReadExact(stdin, lengthBytes, 4);
            if (bytesRead < 4)
                break; // stdin closed — browser disconnected

            var messageLength = BitConverter.ToUInt32(lengthBytes, 0);
            if (messageLength == 0 || messageLength > 1024 * 1024)
                break; // Sanity check: max 1MB

            // Read the JSON message body
            var messageBytes = new byte[messageLength];
            bytesRead = ReadExact(stdin, messageBytes, (int)messageLength);
            if (bytesRead < (int)messageLength)
                break;

            // Parse action (optional — any message triggers OCR for simplicity)
            var action = "ocr-translate";
            try
            {
                using var doc = JsonDocument.Parse(messageBytes);
                if (doc.RootElement.TryGetProperty("action", out var actionProp))
                    action = actionProp.GetString() ?? action;
            }
            catch { /* Ignore parse errors — default to ocr-translate */ }

            // Signal the running Easydict app
            var success = false;
            if (action == "ocr-translate")
            {
                success = SignalOcrTranslate();
            }

            // Send response
            var response = JsonSerializer.SerializeToUtf8Bytes(
                new BridgeResponse(success, action),
                BridgeJsonContext.Default.BridgeResponse);
            WriteMessage(stdout, response);
        }
    }

    private static bool SignalOcrTranslate()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(OcrTranslateEventName);
            evt.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Easydict is not running
            return false;
        }
    }

    private static int ReadExact(Stream stream, byte[] buffer, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0)
                return totalRead; // EOF
            totalRead += read;
        }
        return totalRead;
    }

    private static void WriteMessage(Stream stream, byte[] message)
    {
        var lengthBytes = BitConverter.GetBytes((uint)message.Length);
        stream.Write(lengthBytes, 0, 4);
        stream.Write(message, 0, message.Length);
        stream.Flush();
    }
}

/// <summary>
/// Response sent back to the browser extension via native messaging.
/// </summary>
internal sealed record BridgeResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("action")] string Action);

/// <summary>
/// Source-generated JSON context for trimming-safe serialization.
/// </summary>
[JsonSerializable(typeof(BridgeResponse))]
internal sealed partial class BridgeJsonContext : JsonSerializerContext;
