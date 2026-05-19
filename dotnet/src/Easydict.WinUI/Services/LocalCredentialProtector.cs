using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Easydict.WinUI.Services;

/// <summary>
/// Protects credentials stored in settings.json with Windows DPAPI.
/// The current default is per-user protection; the payload records its scope so future
/// shared-machine storage can coexist without changing settings call sites.
/// </summary>
internal static class LocalCredentialProtector
{
    private const string ProtectedValuePrefix = "edcred1:";
    private const string LegacyProtectedValuePrefix = "edloc1:";
    private const string KeyPurpose = "Easydict.WinUI.LocalSettingsCredentialKey.v1";
    private const string DpapiPurpose = "Easydict.WinUI.LocalSettingsCredential.v2";
    private const string UserScopeName = "user";
    private const string MachineScopeName = "machine";
    internal const string MachineIdFileName = "machine-id";
    private const string LegacyMachineIdFileName = "local-machine-id";
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private static readonly Lazy<string> MachineId = new(GetMachineId);
    private const CredentialProtectionScope DefaultProtectionScope = CredentialProtectionScope.CurrentUser;

    internal enum CredentialProtectionScope
    {
        CurrentUser,
        LocalMachine,
    }

    public static bool IsProtected(string? value)
    {
        return IsDpapiProtected(value) || IsLegacyProtected(value);
    }

    public static string Protect(string plaintext)
    {
        return Protect(plaintext, DefaultProtectionScope);
    }

    internal static string Protect(string plaintext, CredentialProtectionScope scope)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var protectedBytes = WindowsDataProtection.Protect(
                plaintextBytes,
                GetDpapiOptionalEntropy(scope),
                scope);

            return $"{ProtectedValuePrefix}{GetScopeName(scope)}:{Convert.ToBase64String(protectedBytes)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    internal static string ProtectLegacy(string plaintext, string machineId)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSizeBytes];
            var key = DeriveKey(machineId);
            try
            {
                using var aes = new AesGcm(key, TagSizeBytes);
                aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, GetAssociatedData());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            var payload = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, payload, 0, NonceSizeBytes);
            Buffer.BlockCopy(tag, 0, payload, NonceSizeBytes, TagSizeBytes);
            Buffer.BlockCopy(ciphertext, 0, payload, NonceSizeBytes + TagSizeBytes, ciphertext.Length);

            return LegacyProtectedValuePrefix + Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public static bool TryUnprotect(string protectedValue, out string plaintext)
    {
        if (TryUnprotectDpapi(protectedValue, out plaintext))
        {
            return true;
        }

        return IsLegacyProtected(protectedValue) &&
            TryUnprotectLegacy(protectedValue, MachineId.Value, out plaintext);
    }

    internal static string? UnprotectOrReturnPlaintext(
        string? storedValue,
        out bool needsMigration,
        out bool decryptFailed)
    {
        needsMigration = false;
        decryptFailed = false;

        if (string.IsNullOrEmpty(storedValue))
        {
            return null;
        }

        if (TryUnprotectDpapi(storedValue, out var plaintext))
        {
            return string.IsNullOrEmpty(plaintext) ? null : plaintext;
        }

        if (IsLegacyProtected(storedValue) &&
            TryUnprotectLegacy(storedValue, MachineId.Value, out plaintext))
        {
            needsMigration = true;
            return string.IsNullOrEmpty(plaintext) ? null : plaintext;
        }

        if (IsProtected(storedValue))
        {
            decryptFailed = true;
            return null;
        }

        needsMigration = true;
        return storedValue;
    }

    internal static string? UnprotectOrReturnPlaintext(
        string? storedValue,
        string machineId,
        out bool needsMigration,
        out bool decryptFailed)
    {
        needsMigration = false;
        decryptFailed = false;

        if (string.IsNullOrEmpty(storedValue))
        {
            return null;
        }

        if (TryUnprotectDpapi(storedValue, out var plaintext))
        {
            return string.IsNullOrEmpty(plaintext) ? null : plaintext;
        }

        if (IsLegacyProtected(storedValue) &&
            TryUnprotectLegacy(storedValue, machineId, out plaintext))
        {
            needsMigration = true;
            return string.IsNullOrEmpty(plaintext) ? null : plaintext;
        }

        if (IsProtected(storedValue))
        {
            decryptFailed = true;
            return null;
        }

        needsMigration = true;
        return storedValue;
    }

    internal static bool TryUnprotectLegacy(string protectedValue, string machineId, out string plaintext)
    {
        plaintext = string.Empty;
        if (!IsLegacyProtected(protectedValue))
        {
            return false;
        }

        try
        {
            var payload = Convert.FromBase64String(protectedValue[LegacyProtectedValuePrefix.Length..]);
            if (payload.Length <= NonceSizeBytes + TagSizeBytes)
            {
                return false;
            }

            var nonce = payload[..NonceSizeBytes];
            var tag = payload[NonceSizeBytes..(NonceSizeBytes + TagSizeBytes)];
            var ciphertext = payload[(NonceSizeBytes + TagSizeBytes)..];
            var plaintextBytes = new byte[ciphertext.Length];
            try
            {
                var key = DeriveKey(machineId);
                try
                {
                    using var aes = new AesGcm(key, TagSizeBytes);
                    aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, GetAssociatedData());
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }

                plaintext = Encoding.UTF8.GetString(plaintextBytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryUnprotectDpapi(string protectedValue, out string plaintext)
    {
        plaintext = string.Empty;
        if (!TryParseDpapiProtectedValue(protectedValue, out var scope, out var payload))
        {
            return false;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(payload);
            var plaintextBytes = WindowsDataProtection.Unprotect(
                protectedBytes,
                GetDpapiOptionalEntropy(scope),
                scope);

            try
            {
                plaintext = Encoding.UTF8.GetString(plaintextBytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsDpapiProtected(string? value)
    {
        return value?.StartsWith(ProtectedValuePrefix, StringComparison.Ordinal) == true;
    }

    private static bool IsLegacyProtected(string? value)
    {
        return value?.StartsWith(LegacyProtectedValuePrefix, StringComparison.Ordinal) == true;
    }

    private static bool TryParseDpapiProtectedValue(
        string protectedValue,
        out CredentialProtectionScope scope,
        out string payload)
    {
        scope = DefaultProtectionScope;
        payload = string.Empty;

        if (!IsDpapiProtected(protectedValue))
        {
            return false;
        }

        var rest = protectedValue[ProtectedValuePrefix.Length..];
        var separator = rest.IndexOf(':');
        if (separator <= 0 || separator == rest.Length - 1)
        {
            return false;
        }

        if (!TryParseScopeName(rest[..separator], out scope))
        {
            return false;
        }

        payload = rest[(separator + 1)..];
        return true;
    }

    private static bool TryParseScopeName(string scopeName, out CredentialProtectionScope scope)
    {
        scope = scopeName switch
        {
            UserScopeName => CredentialProtectionScope.CurrentUser,
            MachineScopeName => CredentialProtectionScope.LocalMachine,
            _ => DefaultProtectionScope,
        };

        return scopeName is UserScopeName or MachineScopeName;
    }

    private static string GetScopeName(CredentialProtectionScope scope)
    {
        return scope switch
        {
            CredentialProtectionScope.CurrentUser => UserScopeName,
            CredentialProtectionScope.LocalMachine => MachineScopeName,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
    }

    private static byte[] GetDpapiOptionalEntropy(CredentialProtectionScope scope)
    {
        return Encoding.UTF8.GetBytes($"{DpapiPurpose}:{GetScopeName(scope)}");
    }

    private static byte[] DeriveKey(string machineId)
    {
        var keyMaterial = Encoding.UTF8.GetBytes($"{KeyPurpose}:{machineId}");
        try
        {
            return SHA256.HashData(keyMaterial);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial);
        }
    }

    private static byte[] GetAssociatedData()
    {
        return Encoding.UTF8.GetBytes(KeyPurpose);
    }

    private static string GetMachineId()
    {
        return GetOrCreatePersistedMachineId(GetEasydictDataDirectory());
    }

    private static string? GetMachineGuidHash()
    {
        try
        {
            var machineGuid = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                null) as string;
            if (!string.IsNullOrWhiteSpace(machineGuid))
            {
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machineGuid)))
                    .ToLowerInvariant();
            }
        }
        catch
        {
            // Fall back to the app-local machine id below.
        }

        return null;
    }

    internal static string GetOrCreatePersistedMachineId(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, MachineIdFileName);
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }

            var legacyPath = Path.Combine(directory, LegacyMachineIdFileName);
            if (File.Exists(legacyPath))
            {
                var legacy = File.ReadAllText(legacyPath).Trim();
                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    File.WriteAllText(path, legacy);
                    return legacy;
                }
            }

            // Seed with the previous derivation input so credentials encrypted before
            // this file existed remain readable after upgrade.
            var created = GetMachineGuidHash() ?? Guid.NewGuid().ToString("N");
            File.WriteAllText(path, created);
            return created;
        }
        catch
        {
            return GetMachineGuidHash() ?? Environment.MachineName;
        }
    }

    private static string GetEasydictDataDirectory()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataPath, "Easydict");
    }

    private static class WindowsDataProtection
    {
        private const int CryptProtectUiForbidden = 0x1;
        private const int CryptProtectLocalMachine = 0x4;

        public static byte[] Protect(
            byte[] plaintext,
            byte[] optionalEntropy,
            CredentialProtectionScope scope)
        {
            return Execute(
                plaintext,
                optionalEntropy,
                GetFlags(scope, protect: true),
                protect: true);
        }

        public static byte[] Unprotect(
            byte[] protectedBytes,
            byte[] optionalEntropy,
            CredentialProtectionScope scope)
        {
            return Execute(
                protectedBytes,
                optionalEntropy,
                GetFlags(scope, protect: false),
                protect: false);
        }

        private static int GetFlags(CredentialProtectionScope scope, bool protect)
        {
            var flags = CryptProtectUiForbidden;
            if (protect && scope == CredentialProtectionScope.LocalMachine)
            {
                flags |= CryptProtectLocalMachine;
            }

            return flags;
        }

        private static byte[] Execute(
            byte[] input,
            byte[] optionalEntropy,
            int flags,
            bool protect)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows DPAPI is only available on Windows.");
            }

            var inputBlob = DataBlob.FromBytes(input);
            var entropyBlob = DataBlob.FromBytes(optionalEntropy);
            var outputBlob = default(DataBlob);

            try
            {
                var success = protect
                    ? CryptProtectData(
                        ref inputBlob,
                        DpapiPurpose,
                        ref entropyBlob,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        flags,
                        out outputBlob)
                    : CryptUnprotectData(
                        ref inputBlob,
                        IntPtr.Zero,
                        ref entropyBlob,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        flags,
                        out outputBlob);

                if (!success)
                {
                    throw new CryptographicException(Marshal.GetHRForLastWin32Error());
                }

                return outputBlob.ToBytes();
            }
            finally
            {
                inputBlob.FreeHGlobal(clear: protect);
                entropyBlob.FreeHGlobal(clear: true);
                outputBlob.LocalFree(clear: !protect);
            }
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string? dataDescription,
            ref DataBlob optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr dataDescription,
            ref DataBlob optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;

            public static DataBlob FromBytes(byte[] bytes)
            {
                if (bytes.Length == 0)
                {
                    return default;
                }

                var blob = new DataBlob
                {
                    cbData = bytes.Length,
                    pbData = Marshal.AllocHGlobal(bytes.Length),
                };

                Marshal.Copy(bytes, 0, blob.pbData, bytes.Length);
                return blob;
            }

            public readonly byte[] ToBytes()
            {
                if (cbData == 0 || pbData == IntPtr.Zero)
                {
                    return [];
                }

                var bytes = new byte[cbData];
                Marshal.Copy(pbData, bytes, 0, cbData);
                return bytes;
            }

            public readonly void FreeHGlobal(bool clear = false)
            {
                if (pbData != IntPtr.Zero)
                {
                    if (clear)
                    {
                        Clear();
                    }

                    Marshal.FreeHGlobal(pbData);
                }
            }

            public readonly void LocalFree(bool clear = false)
            {
                if (pbData != IntPtr.Zero)
                {
                    if (clear)
                    {
                        Clear();
                    }

                    _ = WindowsDataProtection.LocalFree(pbData);
                }
            }

            private readonly void Clear()
            {
                if (cbData <= 0 || pbData == IntPtr.Zero)
                {
                    return;
                }

                Marshal.Copy(new byte[cbData], 0, pbData, cbData);
            }
        }
    }
}
