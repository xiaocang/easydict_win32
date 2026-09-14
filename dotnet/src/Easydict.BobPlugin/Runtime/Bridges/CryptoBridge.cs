using System.Security.Cryptography;

namespace Easydict.BobPlugin.Runtime.Bridges;

/// <summary>
/// Hashing and symmetric crypto for the bundled crypto-js shim. Everything is expressed as
/// base64 in and base64 out so the JS side never handles byte arrays.
/// </summary>
internal static class CryptoBridge
{
    /// <summary>Hash base64 input with md5, sha1, sha256 or sha512.</summary>
    public static string Hash(string algorithm, string base64Input)
    {
        var data = DataCodec.FromBase64(base64Input);
        var hash = algorithm?.ToLowerInvariant() switch
        {
            "md5" => MD5.HashData(data),
            "sha1" => SHA1.HashData(data),
            "sha256" => SHA256.HashData(data),
            "sha512" => SHA512.HashData(data),
            _ => throw new NotSupportedException($"Unsupported hash algorithm '{algorithm}'.")
        };

        return DataCodec.BytesToBase64(hash);
    }

    /// <summary>HMAC of base64 input with a base64 key.</summary>
    public static string Hmac(string algorithm, string base64Key, string base64Input)
    {
        var key = DataCodec.FromBase64(base64Key);
        var data = DataCodec.FromBase64(base64Input);
        var hash = algorithm?.ToLowerInvariant() switch
        {
            "md5" => HMACMD5.HashData(key, data),
            "sha1" => HMACSHA1.HashData(key, data),
            "sha256" => HMACSHA256.HashData(key, data),
            "sha512" => HMACSHA512.HashData(key, data),
            _ => throw new NotSupportedException($"Unsupported HMAC algorithm '{algorithm}'.")
        };

        return DataCodec.BytesToBase64(hash);
    }

    /// <summary>
    /// AES-CBC with PKCS7 padding and an explicit key and IV. Passphrase-derived keys (crypto-js's
    /// OpenSSL-compatible KDF) are deliberately unsupported; the shim raises a clear error instead.
    /// </summary>
    public static string Aes(bool encrypt, string base64Key, string base64Iv, string base64Data)
    {
        var key = DataCodec.FromBase64(base64Key);
        var iv = DataCodec.FromBase64(base64Iv);
        var data = DataCodec.FromBase64(base64Data);

        if (key.Length is not (16 or 24 or 32))
        {
            throw new NotSupportedException($"AES key must be 16, 24 or 32 bytes; got {key.Length}.");
        }

        if (iv.Length != 16)
        {
            throw new NotSupportedException($"AES-CBC IV must be 16 bytes; got {iv.Length}.");
        }

        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var transformed = encrypt
            ? aes.EncryptCbc(data, iv, PaddingMode.PKCS7)
            : aes.DecryptCbc(data, iv, PaddingMode.PKCS7);

        return DataCodec.BytesToBase64(transformed);
    }
}
