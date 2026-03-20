using System.Text;
using FluentAssertions;
using MDict.Csharp.Utils;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for MDict type-2 encryption support (Salsa20/8 + RIPEMD-128).
/// </summary>
[Trait("Category", "WinUI")]
public class MdxEncryptionTests
{
    // ---- RIPEMD-128 tests ----

    [Fact]
    public void Ripemd128_EmptyInput_ProducesKnownDigest()
    {
        // Known RIPEMD-128 digest for empty string
        var digest = Ripemd128.ComputeHash([]);
        digest.Should().HaveCount(16);
        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        hex.Should().Be("cdf26213a150dc3ecb610f18f6b38b46");
    }

    [Fact]
    public void Ripemd128_Abc_ProducesValidDigest()
    {
        var digest = Ripemd128.ComputeHash(Encoding.UTF8.GetBytes("abc"));
        digest.Should().HaveCount(16);
        // Verify consistency (same input produces same output)
        var digest2 = Ripemd128.ComputeHash(Encoding.UTF8.GetBytes("abc"));
        digest.Should().BeEquivalentTo(digest2);
    }

    [Fact]
    public void Ripemd128_DifferentInputs_ProduceDifferentDigests()
    {
        var d1 = Ripemd128.ComputeHash(Encoding.UTF8.GetBytes("hello"));
        var d2 = Ripemd128.ComputeHash(Encoding.UTF8.GetBytes("world"));
        d1.Should().NotBeEquivalentTo(d2);
    }

    [Fact]
    public void Ripemd128_Utf16Le_ProducesConsistentDigest()
    {
        // Email hashing uses UTF-16LE, verify consistency
        var emailBytes = Encoding.Unicode.GetBytes("test@example.com");
        var d1 = Ripemd128.ComputeHash(emailBytes);
        var d2 = Ripemd128.ComputeHash(emailBytes);
        d1.Should().BeEquivalentTo(d2);
        d1.Should().HaveCount(16);
    }

    [Fact]
    public void Ripemd128_OutputIsAlways16Bytes()
    {
        // Test with various input sizes
        foreach (var len in new[] { 0, 1, 15, 16, 17, 64, 100, 256 })
        {
            var input = new byte[len];
            for (int i = 0; i < len; i++) input[i] = (byte)(i & 0xFF);
            var digest = Ripemd128.ComputeHash(input);
            digest.Should().HaveCount(16, $"input length {len}");
        }
    }

    // ---- Salsa20/8 tests ----

    [Fact]
    public void SalsaDecrypt_ReturnsTransformedData()
    {
        // Salsa20 is a stream cipher — encrypting and decrypting are the same operation.
        // Verify that the output is different from input (not a no-op stub).
        var key = new byte[16]; // zero key
        var data = Encoding.UTF8.GetBytes("hello world test");

        var encrypted = Utils.SalsaDecrypt(data, key);
        encrypted.Should().NotBeEquivalentTo(data, "SalsaDecrypt should transform the data");
    }

    [Fact]
    public void SalsaDecrypt_IsInvertible()
    {
        // Salsa20 XOR cipher: encrypt(encrypt(data)) = data
        var key = new byte[16];
        key[0] = 0x42;
        key[5] = 0xAB;

        var original = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
        var encrypted = Utils.SalsaDecrypt(original, key);
        var decrypted = Utils.SalsaDecrypt(encrypted, key);

        decrypted.Should().BeEquivalentTo(original, "double Salsa20 should return original data");
    }

    [Fact]
    public void SalsaDecrypt_DifferentKeys_ProduceDifferentOutput()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var key1 = new byte[16];
        var key2 = new byte[16];
        key2[0] = 0xFF;

        var out1 = Utils.SalsaDecrypt(data, key1);
        var out2 = Utils.SalsaDecrypt(data, key2);

        out1.Should().NotBeEquivalentTo(out2, "different keys should produce different output");
    }

    [Fact]
    public void SalsaDecrypt_EmptyData_ReturnsEmpty()
    {
        var key = new byte[16];
        var result = Utils.SalsaDecrypt([], key);
        result.Should().BeEmpty();
    }

    [Fact]
    public void SalsaDecrypt_32ByteKey_Works()
    {
        // Salsa20 supports both 16-byte and 32-byte keys
        var key = new byte[32];
        key[0] = 0x01;
        key[31] = 0xFF;

        var data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var encrypted = Utils.SalsaDecrypt(data, key);
        var decrypted = Utils.SalsaDecrypt(encrypted, key);

        decrypted.Should().BeEquivalentTo(data);
    }

    // ---- DecryptRegcodeByEmail tests ----

    [Fact]
    public void DecryptRegcodeByEmail_ProducesConsistentKey()
    {
        // Regcode must be 16 or 32 bytes for Salsa20 (used as data, key is 16-byte RIPEMD digest)
        var regcode = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var email = "user@example.com";

        var key1 = Utils.DecryptRegcodeByEmail(regcode, email);
        var key2 = Utils.DecryptRegcodeByEmail(regcode, email);

        key1.Should().BeEquivalentTo(key2);
        key1.Should().HaveCount(regcode.Length);
    }

    [Fact]
    public void DecryptRegcodeByEmail_DifferentEmails_ProduceDifferentKeys()
    {
        var regcode = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

        var key1 = Utils.DecryptRegcodeByEmail(regcode, "alice@example.com");
        var key2 = Utils.DecryptRegcodeByEmail(regcode, "bob@example.com");

        key1.Should().NotBeEquivalentTo(key2);
    }

    [Fact]
    public void DecryptRegcodeByEmail_UsesUtf16LeEncoding()
    {
        // Verify the email digest uses UTF-16LE (Encoding.Unicode) not UTF-8
        var regcode = new byte[16];
        var email = "test@test.com";

        // Manually compute what the function should do:
        // 1. emailBytes = Encoding.Unicode.GetBytes(email) — UTF-16LE
        // 2. emailDigest = RIPEMD128(emailBytes)
        // 3. result = Salsa20(regcode, emailDigest)
        var expectedEmailBytes = Encoding.Unicode.GetBytes(email);
        var expectedDigest = Ripemd128.ComputeHash(expectedEmailBytes);
        var expectedResult = Utils.SalsaDecrypt(regcode, expectedDigest);

        var actual = Utils.DecryptRegcodeByEmail(regcode, email);
        actual.Should().BeEquivalentTo(expectedResult);
    }

    // ---- DecryptRegcodeByDeviceId tests ----

    [Fact]
    public void DecryptRegcodeByDeviceId_ProducesConsistentKey()
    {
        var regcode = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var deviceId = Encoding.UTF8.GetBytes("device-123");

        var key1 = Utils.DecryptRegcodeByDeviceId(regcode, deviceId);
        var key2 = Utils.DecryptRegcodeByDeviceId(regcode, deviceId);

        key1.Should().BeEquivalentTo(key2);
    }

    [Fact]
    public void DecryptRegcodeByDeviceId_DifferentDeviceIds_ProduceDifferentKeys()
    {
        var regcode = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };

        var key1 = Utils.DecryptRegcodeByDeviceId(regcode, Encoding.UTF8.GetBytes("device-1"));
        var key2 = Utils.DecryptRegcodeByDeviceId(regcode, Encoding.UTF8.GetBytes("device-2"));

        key1.Should().NotBeEquivalentTo(key2);
    }

    // ---- Full key derivation round-trip test ----

    [Fact]
    public void FullKeyDerivation_EncryptThenDecryptHeader_RestoresOriginal()
    {
        // Simulate the full type-2 encryption flow:
        // 1. Derive encrypt_key from regcode + email (regcode must be 16 bytes for Salsa20 key)
        // 2. Encrypt a header block with that key
        // 3. Decrypt it back and verify we get the original

        // Use a 16-byte regcode (realistic size for MDict registration codes)
        var regcode = new byte[16];
        for (int i = 0; i < 16; i++) regcode[i] = (byte)(0x30 + i);
        var email = "user@dictionary.com";

        // Step 1: Derive the encryption key (same as what the MDX creator would do)
        // encrypt_key = Salsa20(regcode, RIPEMD128(email_utf16le))
        // encrypt_key is 16 bytes (same length as regcode input)
        var encryptKey = Utils.DecryptRegcodeByEmail(regcode, email);
        encryptKey.Should().HaveCount(16, "encrypt key should be same length as regcode");

        // Step 2: Create a fake 40-byte key header block
        var originalHeader = new byte[40];
        for (int i = 0; i < 40; i++) originalHeader[i] = (byte)(i * 3 + 7);

        // Step 3: "Encrypt" the header (Salsa20 is symmetric)
        var encryptedHeader = Utils.SalsaDecrypt(originalHeader, encryptKey);

        // Step 4: "Decrypt" using the same key derivation
        var decryptedHeader = Utils.SalsaDecrypt(encryptedHeader, encryptKey);

        decryptedHeader.Should().BeEquivalentTo(originalHeader,
            "full encrypt→decrypt round-trip should restore the original header");
    }

    [Fact]
    public void FullKeyDerivation_EncryptedHeaderDiffersFromOriginal()
    {
        var regcode = new byte[16];
        regcode[0] = 0xDE; regcode[1] = 0xAD;
        var email = "test@example.com";

        var encryptKey = Utils.DecryptRegcodeByEmail(regcode, email);

        var header = new byte[40];
        for (int i = 0; i < 40; i++) header[i] = (byte)i;

        var encrypted = Utils.SalsaDecrypt(header, encryptKey);
        encrypted.Should().NotBeEquivalentTo(header, "encrypted header should differ from original");
    }
}
