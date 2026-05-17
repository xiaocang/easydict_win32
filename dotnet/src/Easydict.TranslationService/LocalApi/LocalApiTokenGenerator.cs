using System.Security.Cryptography;

namespace Easydict.TranslationService.LocalApi;

/// <summary>
/// Generates bearer tokens for the local API.
/// Format: <c>sk-edt-</c> + 32 base32url chars (20 random bytes, ~160 bits of entropy).
/// </summary>
public static class LocalApiTokenGenerator
{
    public const string Prefix = "sk-edt-";

    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[20];
        RandomNumberGenerator.Fill(bytes);
        return Prefix + Base32Encode(bytes);
    }

    private static string Base32Encode(ReadOnlySpan<byte> input)
    {
        // RFC 4648 base32 without padding, lowercase. 20 bytes → 32 chars.
        var output = new char[(input.Length * 8 + 4) / 5];
        var buffer = 0;
        var bits = 0;
        var idx = 0;
        foreach (var b in input)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output[idx++] = Base32Alphabet[(buffer >> bits) & 0x1F];
            }
        }
        if (bits > 0)
        {
            output[idx++] = Base32Alphabet[(buffer << (5 - bits)) & 0x1F];
        }
        return new string(output, 0, idx);
    }
}
