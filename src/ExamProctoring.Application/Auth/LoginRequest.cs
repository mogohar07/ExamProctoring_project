namespace ExamProctoring.Application.Auth;

// POST /api/auth/login (§6.4). Nullable so that a missing field is reported as validation_failed.
public sealed record LoginRequest(string? Email, string? Password);
