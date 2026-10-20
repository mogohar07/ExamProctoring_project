namespace ExamProctoring.Application.Auth;

// Student sign-up (§6.4, D12): exactly these four fields. There is no role or university field, so an extra
// JSON property such as "role" or "universityId" is never bound.
// Nullable so that a missing field is reported by the validator as validation_failed.
public sealed record RegisterStudentRequest(string? FullName, string? Email, string? Password, string? ConfirmPassword);
