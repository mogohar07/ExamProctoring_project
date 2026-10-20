namespace ExamProctoring.Application.Common;

// Stable codes returned in the ProblemDetails "code" extension (§6.7). Each is added when first used.
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string DuplicateAccount = "duplicate_account";
    public const string TokenInvalid = "token_invalid";
    public const string PasswordChangeRequired = "password_change_required";
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountLocked = "account_locked";
    public const string AccountDeactivated = "account_deactivated";
    public const string TemporaryPasswordExpired = "temporary_password_expired";
    public const string RefreshTokenReused = "refresh_token_reused";
}
