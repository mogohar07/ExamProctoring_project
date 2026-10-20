namespace ExamProctoring.Application.Auth;

// Returned by login (and later by refresh and change-password) (§6.4, §6.6, decision D1).
// Tokens travel only in this JSON body; there are no cookies.
public sealed record TokenResponse(
    string TokenType,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    bool MustChangePassword,
    AuthUserResponse User)
{
    public const string BearerTokenType = "Bearer";

    // Keeps the tokens out of logs and debugger output.
    public override string ToString() =>
        $"TokenResponse {{ User = {User.Id}, MustChangePassword = {MustChangePassword}, AccessTokenExpiresAt = {AccessTokenExpiresAt:O} }}";
}

public sealed record AuthUserResponse(Guid Id, string FullName, string Email, string Role);
