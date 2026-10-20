namespace ExamProctoring.Domain.Users;

// Values for RefreshToken.RevokedReason (nvarchar(50)), kept for auditing. Decisions never read this text:
// a rotated token is recognised by its ReplacedByTokenId. Each value is added when first used.
public static class RefreshTokenRevocationReasons
{
    // Replaced by a newer token of the same family on refresh.
    public const string Rotated = "rotated";

    // Revoked because an already-rotated token of the family was presented again.
    public const string ReuseDetected = "reuse_detected";
}
