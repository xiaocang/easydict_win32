using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Easydict.TranslationService.Security;

/// <summary>
/// Manages encrypted secrets using AES-128 CBC encryption.
/// Ports the macOS encryption approach for API key storage.
/// </summary>
public static class SecretKeyManager
{
    private const string ResourceName = "Easydict.TranslationService.Resources.EncryptedSecrets.json";
    private static readonly Lazy<Dictionary<string, string>> CachedSecrets = new(LoadSecrets);
    private static readonly Lazy<byte[]> DerivedKey = new(DeriveKey);

    /// <summary>
    /// Gets a decrypted secret by key name.
    /// </summary>
    /// <param name="key">The secret key name (e.g., "builtInGLMAPIKey").</param>
    /// <returns>The decrypted secret value, or null if not found.</returns>
    public static string? GetSecret(string key)
    {
        if (CachedSecrets.Value.TryGetValue(key, out var encryptedValue))
        {
            try
            {
                return DecryptAES(encryptedValue);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Decrypts a base64-encoded AES encrypted string.
    /// Uses AES-128 CBC with PKCS7 padding, matching the macOS implementation.
    /// </summary>
    /// <param name="base64Encrypted">Base64-encoded encrypted data.</param>
    /// <returns>Decrypted plaintext string.</returns>
    public static string DecryptAES(string base64Encrypted)
    {
        var encryptedBytes = Convert.FromBase64String(base64Encrypted);
        var key = DerivedKey.Value;

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = key; // Same as key per macOS implementation
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
        return Encoding.UTF8.GetString(decryptedBytes);
    }

    /// <summary>
    /// Encrypts a plaintext string using AES-128 CBC.
    /// Used by the encryption helper tool.
    /// </summary>
    /// <param name="plaintext">The plaintext to encrypt.</param>
    /// <returns>Base64-encoded encrypted data.</returns>
    public static string EncryptAES(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var key = DerivedKey.Value;

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = key; // Same as key per macOS implementation
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var encryptedBytes = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);
        return Convert.ToBase64String(encryptedBytes);
    }

    /// <summary>
    /// Derives the AES key from the assembly name.
    /// Uses SHA256 hash, taking first 16 characters of hex string as key bytes.
    /// This matches the macOS approach: SHA256(BundleName).prefix(16).
    /// </summary>
    private static byte[] DeriveKey()
    {
        // Use the assembly name as the key derivation source
        var assemblyName = typeof(SecretKeyManager).Assembly.GetName().Name
            ?? "Easydict.TranslationService";

        // SHA256 hash of assembly name
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(assemblyName));

        // Convert to lowercase hex string and take first 16 characters
        var hexString = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var keyString = hexString[..16];

        // Use the 16 ASCII characters as key bytes (AES-128)
        return Encoding.UTF8.GetBytes(keyString);
    }

    /// <summary>
    /// Loads encrypted secrets from embedded resource.
    /// </summary>
    private static Dictionary<string, string> LoadSecrets()
    {
        var assembly = typeof(SecretKeyManager).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);

        if (stream == null)
        {
            return new Dictionary<string, string>();
        }

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? new Dictionary<string, string>();
    }
}
