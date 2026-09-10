using System.Security.Cryptography;
using System.Text;

namespace Easydict.TranslationService.Services.Auth;

/// <summary>
/// RFC 7636 (PKCE) helpers: random code verifiers / CSRF state values and the S256 code
/// challenge derived from a verifier. All values are unpadded base64url so they can be
/// placed in a query string without further escaping.
/// </summary>
public static class PkceCodeChallenge
{
    /// <summary>The only challenge method supported (<c>code_challenge_method=S256</c>).</summary>
    public const string Method = "S256";

    /// <summary>32 random bytes encode to 43 base64url characters (RFC 7636 §4.1 minimum).</summary>
    private const int RandomByteCount = 32;

    /// <summary>Create a fresh high-entropy code verifier.</summary>
    public static string CreateVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(RandomByteCount));

    /// <summary>Create a fresh opaque <c>state</c> value (the CSRF token for one sign-in attempt).</summary>
    public static string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(RandomByteCount));

    /// <summary><c>base64url(SHA256(ASCII(verifier)))</c> per RFC 7636 §4.2.</summary>
    public static string ComputeChallenge(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);
        return Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    /// <summary>Base64url without padding (RFC 4648 §5). .NET 8 has no built-in encoder.</summary>
    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

/// <summary>
/// The secrets for a single sign-in attempt. Create a fresh session per attempt and never
/// reuse one: the authorization code is bound to this verifier and state.
/// </summary>
public sealed record PkceSession(string CodeVerifier, string CodeChallenge, string State)
{
    public static PkceSession Create()
    {
        var verifier = PkceCodeChallenge.CreateVerifier();
        return new PkceSession(
            verifier,
            PkceCodeChallenge.ComputeChallenge(verifier),
            PkceCodeChallenge.CreateState());
    }
}
