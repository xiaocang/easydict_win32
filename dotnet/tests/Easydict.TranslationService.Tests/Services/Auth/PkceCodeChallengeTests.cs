using System.Text.RegularExpressions;
using Easydict.TranslationService.Services.Auth;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services.Auth;

public class PkceCodeChallengeTests
{
    private static readonly Regex Base64UrlPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    [Fact]
    public void ComputeChallenge_MatchesRfc7636AppendixBVector()
    {
        // RFC 7636 Appendix B: verifier → S256 challenge.
        var challenge = PkceCodeChallenge.ComputeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        challenge.Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Fact]
    public void CreateVerifier_Is43UnpaddedBase64UrlChars()
    {
        var verifier = PkceCodeChallenge.CreateVerifier();

        verifier.Should().HaveLength(43);
        verifier.Should().MatchRegex(Base64UrlPattern.ToString());
        verifier.Should().NotContain("=");
    }

    [Fact]
    public void CreateState_Is43UnpaddedBase64UrlChars()
    {
        var state = PkceCodeChallenge.CreateState();

        state.Should().HaveLength(43);
        state.Should().MatchRegex(Base64UrlPattern.ToString());
    }

    [Fact]
    public void CreateVerifier_ProducesDifferentValuesEachCall()
    {
        PkceCodeChallenge.CreateVerifier().Should().NotBe(PkceCodeChallenge.CreateVerifier());
        PkceCodeChallenge.CreateState().Should().NotBe(PkceCodeChallenge.CreateState());
    }

    [Fact]
    public void Base64UrlEncode_UsesUrlAlphabetAndStripsPadding()
    {
        // 0xFB 0xFF → standard base64 "+/8=" → base64url "-_8".
        PkceCodeChallenge.Base64UrlEncode(new byte[] { 0xFB, 0xFF }).Should().Be("-_8");
    }

    [Fact]
    public void PkceSession_Create_ChallengeMatchesVerifier()
    {
        var session = PkceSession.Create();

        session.CodeChallenge.Should().Be(PkceCodeChallenge.ComputeChallenge(session.CodeVerifier));
        session.State.Should().NotBe(session.CodeVerifier);
    }

    [Fact]
    public void ComputeChallenge_RejectsEmptyVerifier()
    {
        var act = () => PkceCodeChallenge.ComputeChallenge("");

        act.Should().Throw<ArgumentException>();
    }
}
