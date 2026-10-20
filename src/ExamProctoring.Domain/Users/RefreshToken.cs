namespace ExamProctoring.Domain.Users;

// One refresh token in a rotation family (§6.6). Only the SHA-256 hash of the token is stored.
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    // SHA-256 of the token, 64 hex characters.
    public string TokenHash { get; set; } = string.Empty;

    // Shared by every token created by rotating the same login; revoked together on reuse or logout.
    public Guid FamilyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public Guid? ReplacedByTokenId { get; set; }
}
