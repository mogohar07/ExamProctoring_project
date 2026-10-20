namespace ExamProctoring.Application.Auth;

// POST /api/auth/logout (D3): the refresh token whose session (token family) ends.
// Nullable so that a missing field is reported as validation_failed.
public sealed record LogoutRequest(string? RefreshToken)
{
    // Keeps the token out of logs and debugger output.
    public override string ToString() => nameof(LogoutRequest);
}
