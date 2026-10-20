using System.Security.Cryptography;
using ExamProctoring.Application.Common;

namespace ExamProctoring.UnitTests.Auth;

// Jwt settings validation (§6.8, D12): a Base64 key that decodes to at least 32 bytes, and no key in any message.
public sealed class JwtOptionsTests
{
    private static string RandomKey(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    private static JwtOptions Valid(string? signingKey = null) => new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SigningKey = signingKey ?? RandomKey(64),
    };

    [Fact]
    public void ValidSettings_HaveNoErrors() => Assert.Empty(Valid().GetValidationErrors());

    [Fact]
    public void Defaults_Are15MinuteAccessAnd7DayRefreshTokens()
    {
        var options = new JwtOptions();

        Assert.Equal(15, options.AccessTokenMinutes);
        Assert.Equal(7, options.RefreshTokenDays);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingKey_IsRejected(string signingKey) => AssertOnlyKeyError(signingKey, "is not configured");

    [Theory]
    [InlineData("this is not base64!")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA-_")] // Base64Url characters are not standard Base64
    public void MalformedKey_IsRejected(string signingKey) => AssertOnlyKeyError(signingKey, "is not valid Base64");

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(31)]
    public void KeyShorterThan32Bytes_IsRejected(int bytes) =>
        AssertOnlyKeyError(RandomKey(bytes), $"decodes to {bytes} bytes; at least 32 are required");

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void KeyOfAtLeast32Bytes_IsAccepted(int bytes)
    {
        var options = Valid(RandomKey(bytes));

        Assert.Empty(options.GetValidationErrors());
        Assert.Equal(bytes, options.GetSigningKeyBytes().Length);
    }

    [Fact]
    public void KeyLength_IsMeasuredInDecodedBytes_NotCharacters()
    {
        // 31 and 32 bytes both encode to 44 Base64 characters; only the 32-byte key is long enough.
        var key31 = RandomKey(31);
        var key32 = RandomKey(32);
        Assert.Equal(key31.Length, key32.Length);

        Assert.NotEmpty(Valid(key31).GetValidationErrors());
        Assert.Empty(Valid(key32).GetValidationErrors());
    }

    [Fact]
    public void ErrorMessages_NeverContainTheKey()
    {
        foreach (var signingKey in new[] { RandomKey(16), RandomKey(31), "not-base64-but-secret-looking-value!" })
        {
            var errors = Valid(signingKey).GetValidationErrors();

            Assert.NotEmpty(errors);
            Assert.All(errors, error => Assert.DoesNotContain(signingKey, error));
        }
    }

    [Fact]
    public void MissingIssuerAudienceAndNonPositiveLifetimes_AreRejected()
    {
        var options = new JwtOptions { SigningKey = RandomKey(64), AccessTokenMinutes = 0, RefreshTokenDays = -1 };

        var errors = options.GetValidationErrors();

        Assert.Contains(errors, e => e.Contains("Jwt:Issuer"));
        Assert.Contains(errors, e => e.Contains("Jwt:Audience"));
        Assert.Contains(errors, e => e.Contains("Jwt:AccessTokenMinutes"));
        Assert.Contains(errors, e => e.Contains("Jwt:RefreshTokenDays"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void GetSigningKeyBytes_Throws_WhenTheKeyIsInvalid(string signingKey) =>
        Assert.Throws<InvalidOperationException>(() => Valid(signingKey).GetSigningKeyBytes());

    private static void AssertOnlyKeyError(string signingKey, string expected)
    {
        var error = Assert.Single(Valid(signingKey).GetValidationErrors());

        Assert.StartsWith("Jwt:SigningKey", error);
        Assert.Contains(expected, error);
    }
}
