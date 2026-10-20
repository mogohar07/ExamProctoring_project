namespace ExamProctoring.Application.Auth;

// POST /api/auth/refresh (§6.6, D2). Nullable so that a missing field is reported as validation_failed.
public sealed record RefreshRequest(string? RefreshToken)
{
    // Keeps the token out of logs and debugger output.
    public override string ToString() => nameof(RefreshRequest);
}
