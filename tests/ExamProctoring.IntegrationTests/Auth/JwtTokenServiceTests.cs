using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ExamProctoring.IntegrationTests.Auth;

// Access and refresh token creation (§6.6) and the access-token validation rules (§4.6).
// No database or API host: the service is created directly with a fixed clock.
public sealed partial class JwtTokenServiceTests
{
    // Random for this test run and stable within it; no key in source or configuration files.
    private static readonly JwtOptions Settings = new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
    };

    private static readonly ApplicationUser User = new()
    {
        Id = Guid.NewGuid(),
        Email = "sara@example.com",
        UserName = "sara@example.com",
        FullName = "Sara Ahmed",
        SecurityStamp = Guid.NewGuid().ToString("N"),
    };

    private static readonly JsonWebTokenHandler Handler = new();

    [Fact]
    public void AccessToken_CarriesExactlyTheDocumentedClaims()
    {
        var token = Service().CreateAccessToken(User, RoleNames.Student).Token;

        var jwt = new JsonWebToken(token);
        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal("JWT", jwt.Typ);
        Assert.Equal(
            new[] { "aud", "email", "exp", "iat", "iss", "jti", "name", "nbf", "role", "sstamp", "sub" },
            jwt.Claims.Select(c => c.Type).Distinct().Order());
        Assert.Equal(User.Id.ToString(), jwt.Subject);
        Assert.Equal(User.Email, Claim(jwt, AuthClaimTypes.Email));
        Assert.Equal(User.FullName, Claim(jwt, AuthClaimTypes.Name));
        Assert.Equal(RoleNames.Student, Claim(jwt, AuthClaimTypes.Role));
        Assert.Equal(User.SecurityStamp, Claim(jwt, AuthClaimTypes.SecurityStamp));
        Assert.Equal(Settings.Issuer, jwt.Issuer);
        Assert.Equal(Settings.Audience, Assert.Single(jwt.Audiences));
    }

    [Fact]
    public void AccessToken_IsValidFor15Minutes()
    {
        var now = DateTimeOffset.UtcNow;

        var accessToken = Service(now).CreateAccessToken(User, RoleNames.Student);

        var jwt = new JsonWebToken(accessToken.Token);
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.ValidTo - jwt.IssuedAt);
        Assert.Equal(jwt.ValidTo, accessToken.ExpiresAt.UtcDateTime);
        Assert.Equal(now.ToUnixTimeSeconds(), new DateTimeOffset(jwt.IssuedAt).ToUnixTimeSeconds());
    }

    [Fact]
    public void EachAccessToken_HasItsOwnTokenId()
    {
        var service = Service();

        var ids = Enumerable.Range(0, 20)
            .Select(_ => Claim(new JsonWebToken(service.CreateAccessToken(User, RoleNames.Student).Token), AuthClaimTypes.TokenId));

        Assert.Equal(20, ids.Distinct().Count());
    }

    [Fact]
    public async Task AccessToken_PassesValidation_WithSubjectAsNameAndTheRoleClaim()
    {
        var token = Service().CreateAccessToken(User, RoleNames.Instructor).Token;

        var result = await ValidateAsync(token);

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(User.Id.ToString(), result.ClaimsIdentity.Name);
        Assert.True(new System.Security.Claims.ClaimsPrincipal(result.ClaimsIdentity).IsInRole(RoleNames.Instructor));
    }

    [Fact]
    public async Task Validation_Fails_WithAnotherSigningKey()
    {
        var token = Service().CreateAccessToken(User, RoleNames.Student).Token;
        var otherKey = new JwtOptions
        {
            Issuer = Settings.Issuer,
            Audience = Settings.Audience,
            SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
        };

        var result = await Handler.ValidateTokenAsync(token, JwtTokenValidation.CreateParameters(otherKey));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("other-issuer", null)]
    [InlineData(null, "other-audience")]
    public async Task Validation_Fails_ForAnotherIssuerOrAudience(string? issuer, string? audience)
    {
        var token = Service().CreateAccessToken(User, RoleNames.Student).Token;
        var parameters = JwtTokenValidation.CreateParameters(Settings);
        parameters.ValidIssuer = issuer ?? Settings.Issuer;
        parameters.ValidAudience = audience ?? Settings.Audience;

        var result = await Handler.ValidateTokenAsync(token, parameters);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        // Issued 16 minutes ago: expired one minute ago, beyond the 30 s clock skew.
        var token = Service(DateTimeOffset.UtcNow.AddMinutes(-16)).CreateAccessToken(User, RoleNames.Student).Token;

        var result = await ValidateAsync(token);

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenExpiredException>(result.Exception);
    }

    [Fact]
    public async Task TokenNotYetValid_IsRejected()
    {
        var token = Service(DateTimeOffset.UtcNow.AddMinutes(5)).CreateAccessToken(User, RoleNames.Student).Token;

        var result = await ValidateAsync(token);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task TokenSignedWithAnotherAlgorithm_IsRejected()
    {
        var hs512 = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Settings.Issuer,
            Audience = Settings.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { [AuthClaimTypes.Subject] = User.Id.ToString() },
            SigningCredentials = new SigningCredentials(JwtTokenValidation.CreateSigningKey(Settings), SecurityAlgorithms.HmacSha512),
        });

        var result = await ValidateAsync(hs512);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task UnsignedToken_IsRejected()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var unsigned = Handler.CreateToken(
            $$"""{"sub":"{{User.Id}}","role":"Admin","iss":"{{Settings.Issuer}}","aud":"{{Settings.Audience}}","exp":{{exp}}}""");

        var result = await ValidateAsync(unsigned);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task TokenWithAChangedRole_IsRejected()
    {
        var parts = Service().CreateAccessToken(User, RoleNames.Student).Token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
        Assert.Contains("\"role\":\"Student\"", payload);
        parts[1] = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.Replace("\"role\":\"Student\"", "\"role\":\"Admin\"")));

        var result = await ValidateAsync(string.Join('.', parts));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AccessToken_RequiresEmailSecurityStampAndRole()
    {
        var service = Service();

        Assert.Throws<InvalidOperationException>(() => service.CreateAccessToken(Copy(email: null), RoleNames.Student));
        Assert.Throws<InvalidOperationException>(() => service.CreateAccessToken(Copy(securityStamp: null), RoleNames.Student));
        Assert.Throws<ArgumentException>(() => service.CreateAccessToken(User, " "));
    }

    [Fact]
    public void RefreshToken_Is32RandomBytesAsBase64Url()
    {
        var refreshToken = Service().CreateRefreshToken();

        Assert.Matches(Base64UrlOf32Bytes(), refreshToken.Token);
        Assert.Equal(32, Base64Url.DecodeFromChars(refreshToken.Token).Length);
    }

    [Fact]
    public void RefreshTokens_AreUnique()
    {
        var service = Service();

        var tokens = Enumerable.Range(0, 1000).Select(_ => service.CreateRefreshToken().Token).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RefreshTokenHash_IsTheLowercaseHexSha256OfTheToken()
    {
        var service = Service();
        var refreshToken = service.CreateRefreshToken();

        Assert.Matches(LowercaseHex64(), refreshToken.Hash);
        Assert.Equal(refreshToken.Hash, service.HashRefreshToken(refreshToken.Token));
        Assert.NotEqual(refreshToken.Token, refreshToken.Hash);
        // Known SHA-256 test vector.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", service.HashRefreshToken("abc"));
    }

    [Fact]
    public void RefreshToken_ExpiresAfter7Days()
    {
        var now = DateTimeOffset.UtcNow;

        var refreshToken = Service(now).CreateRefreshToken();

        Assert.Equal(now.AddDays(7), refreshToken.ExpiresAt);
    }

    [Fact]
    public void TokenRecords_DoNotPrintTheirTokens()
    {
        var service = Service();
        var accessToken = service.CreateAccessToken(User, RoleNames.Student);
        var refreshToken = service.CreateRefreshToken();

        Assert.DoesNotContain(accessToken.Token, accessToken.ToString());
        Assert.DoesNotContain(refreshToken.Token, refreshToken.ToString());
        Assert.DoesNotContain(refreshToken.Hash, refreshToken.ToString());
    }

    private static JwtTokenService Service(DateTimeOffset? now = null) =>
        new(Options.Create(Settings), new FixedClock(now ?? DateTimeOffset.UtcNow));

    private static Task<TokenValidationResult> ValidateAsync(string token) =>
        Handler.ValidateTokenAsync(token, JwtTokenValidation.CreateParameters(Settings));

    private static string Claim(JsonWebToken jwt, string type) => jwt.Claims.Single(c => c.Type == type).Value;

    private static ApplicationUser Copy(string? email = "sara@example.com", string? securityStamp = "stamp") => new()
    {
        Id = User.Id,
        Email = email,
        UserName = User.UserName,
        FullName = User.FullName,
        SecurityStamp = securityStamp,
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex Base64UrlOf32Bytes();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex LowercaseHex64();

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
