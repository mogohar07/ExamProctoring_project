namespace ExamProctoring.Application.Accounts;

// One account in the admin account list and the account detail (D23): exactly these fields, nothing else. There is no
// field for a password hash, security stamp, concurrency stamp, failed-attempt count or refresh token.
public sealed record AccountSummaryResponse(
    Guid Id,
    string FullName,
    string Email,
    string Role,
    string AccountStatus,
    bool MustChangePassword,
    DateTimeOffset? TemporaryPasswordExpiresAt,
    bool IsLockedOut,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? StatusChangedAt,
    string? StatusReason);
