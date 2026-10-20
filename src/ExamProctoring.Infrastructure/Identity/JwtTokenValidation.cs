using ExamProctoring.Application.Common;
using Microsoft.IdentityModel.Tokens;

namespace ExamProctoring.Infrastructure.Identity;

// The rules every access token must pass (§4.6): our issuer and audience, an unexpired lifetime with 30 s
// clock skew, and an HS256 signature with our key. Used by the token tests now and by the JwtBearer scheme later.
public static class JwtTokenValidation
{
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    public static SymmetricSecurityKey CreateSigningKey(JwtOptions options) => new(options.GetSigningKeyBytes());

    public static TokenValidationParameters CreateParameters(JwtOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ClockSkew = ClockSkew,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = CreateSigningKey(options),
        RequireSignedTokens = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        NameClaimType = AuthClaimTypes.Subject,
        RoleClaimType = AuthClaimTypes.Role,
    };
}
