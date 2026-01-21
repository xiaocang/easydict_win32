using Easydict.TranslationService.Security;

if (args.Length == 0)
{
    Console.WriteLine("Usage: dotnet run --project tools/EncryptSecret -- <secret>");
    Console.WriteLine();
    Console.WriteLine("Encrypts a secret using the same AES encryption as SecretKeyManager.");
    Console.WriteLine("The output can be added to EncryptedSecrets.json.");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  dotnet run --project tools/EncryptSecret -- \"my-api-key\"");
    return 1;
}

var plaintext = args[0];
var encrypted = SecretKeyManager.EncryptAES(plaintext);

Console.WriteLine("Plaintext: " + plaintext);
Console.WriteLine("Encrypted: " + encrypted);
Console.WriteLine();
Console.WriteLine("Add to EncryptedSecrets.json:");
Console.WriteLine($"  \"keyName\": \"{encrypted}\"");

// Verify by decrypting
var decrypted = SecretKeyManager.DecryptAES(encrypted);
if (decrypted == plaintext)
{
    Console.WriteLine();
    Console.WriteLine("Verification: OK (decryption successful)");
}
else
{
    Console.WriteLine();
    Console.WriteLine("Verification: FAILED (decryption mismatch)");
    return 1;
}

return 0;
