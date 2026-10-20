using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ExamProctoring.Infrastructure.Identity;

// §6.6. Access tokens: HS256 JWTs created with JsonWebTokenHandler (not the legacy JwtSecurityTokenHandler, §4.6).
// Refresh tokens: 32 random bytes as Base64Url; only their SHA-256 hash is stored.
public sealed class JwtTokenService : ITokenService
{
    private const int RefreshTokenBytes = 32;

    private readonly JwtOptions _options;
    private readonly IClock _clock;
    private readonly SigningCredentials _signingCredentials;
    private readonly JsonWebTokenHandler _handler = new();

    public JwtTokenService(IOptions<JwtOptions> options, IClock clock)
    {
        _options = options.Value;
        _clock = clock;
        _signingCredentials = new SigningCredentials(JwtTokenValidation.CreateSigningKey(_options), SecurityAlgorithms.HmacSha256);
    }

    public AccessToken CreateAccessToken(ApplicationUser user, string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (string.IsNullOrEmpty(user.Email) || string.IsNullOrEmpty(user.SecurityStamp))
            throw new InvalidOperationException("An access token needs the user's email and security stamp.");

        var issuedAt = _clock.UtcNow;

        // The token stores whole seconds; the returned expiry is exactly the token's "exp".
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(issuedAt.AddMinutes(_options.AccessTokenMinutes).ToUnixTimeSeconds());

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _signingCredentials,
            Claims = new Dictionary<string, object>
            {
                [AuthClaimTypes.Subject] = user.Id.ToString(),
                [AuthClaimTypes.Email] = user.Email,
                [AuthClaimTypes.Name] = user.FullName,
                [AuthClaimTypes.Role] = role,
                [AuthClaimTypes.SecurityStamp] = user.SecurityStamp,
                [AuthClaimTypes.TokenId] = Guid.NewGuid().ToString(),
            },
        });

        return new AccessToken(token, expiresAt);
    }

    public GeneratedRefreshToken CreateRefreshToken()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenBytes));
        return new GeneratedRefreshToken(token, HashRefreshToken(token), _clock.UtcNow.AddDays(_options.RefreshTokenDays));
    }

    // SHA-256 as 64 lowercase hex characters: the format of RefreshToken.TokenHash, char(64).
    public string HashRefreshToken(string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
    }
}
