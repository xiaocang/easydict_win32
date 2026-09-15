using System.Text;

namespace Easydict.BobPlugin.Runtime.Bridges;

/// <summary>
/// Encoding helpers shared by the host bridges. Binary data crosses the host/JS boundary as
/// base64 strings so no JsValue ever has to be built from C#.
/// </summary>
internal static class DataCodec
{
    public static string Utf8ToBase64(string? text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? string.Empty));

    public static string Base64ToUtf8(string? base64)
        => Encoding.UTF8.GetString(FromBase64(base64));

    public static string BytesToBase64(byte[] bytes)
        => Convert.ToBase64String(bytes);

    public static byte[] FromBase64(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return [];
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    public static string Base64ToHex(string? base64)
        => Convert.ToHexString(FromBase64(base64)).ToLowerInvariant();

    public static string HexToBase64(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return string.Empty;
        }

        var cleaned = hex.Trim();
        if (cleaned.Length % 2 != 0)
        {
            cleaned = "0" + cleaned;
        }

        try
        {
            return Convert.ToBase64String(Convert.FromHexString(cleaned));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    public static string Latin1ToBase64(string? text)
        => Convert.ToBase64String(Encoding.Latin1.GetBytes(text ?? string.Empty));

    public static string Base64ToLatin1(string? base64)
        => Encoding.Latin1.GetString(FromBase64(base64));
}
