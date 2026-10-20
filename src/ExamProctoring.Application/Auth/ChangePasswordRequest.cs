namespace ExamProctoring.Application.Auth;

// POST /api/auth/change-password (§6.4): the current password and the new one. No confirmation field.
// Nullable so that a missing field is reported as validation_failed.
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword)
{
    // Keeps the passwords out of logs and debugger output.
    public override string ToString() => nameof(ChangePasswordRequest);
}
