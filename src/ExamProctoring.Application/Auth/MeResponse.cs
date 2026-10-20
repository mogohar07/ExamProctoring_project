namespace ExamProctoring.Application.Auth;

// GET /api/auth/me (§6.9): the caller's own account. No password data, security stamp or lockout details.
public sealed record MeResponse(Guid Id, string FullName, string Email, string Role, bool MustChangePassword);
