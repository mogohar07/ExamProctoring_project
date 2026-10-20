using ExamProctoring.Domain.Users;

namespace ExamProctoring.Application.Abstractions;

// Access and refresh tokens (§6.6). Implemented by JwtTokenService in Infrastructure.
public interface ITokenService
{
    // A signed JWT for this user and their single role, valid for Jwt:AccessTokenMinutes.
    AccessToken CreateAccessToken(ApplicationUser user, string role);

    // A new random refresh token, the hash to store, and its expiry (Jwt:RefreshTokenDays). Only the hash is stored.
    GeneratedRefreshToken CreateRefreshToken();

    // The stored form (TokenHash) of a refresh token presented by a client.
    string HashRefreshToken(string refreshToken);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt)
{
    // Keeps the token out of logs and debugger output.
    public override string ToString() => $"AccessToken {{ ExpiresAt = {ExpiresAt:O} }}";
}

public sealed record GeneratedRefreshToken(string Token, string Hash, DateTimeOffset ExpiresAt)
{
    // Keeps the token out of logs and debugger output.
    public override string ToString() => $"GeneratedRefreshToken {{ ExpiresAt = {ExpiresAt:O} }}";
}
