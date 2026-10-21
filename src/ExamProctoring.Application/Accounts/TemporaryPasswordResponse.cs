namespace ExamProctoring.Application.Accounts;

// 200 body of POST /api/admin/accounts/{id}/temporary-password (§6.4, D18, D19): the only time the password is shown.
public sealed record TemporaryPasswordResponse(string TemporaryPassword, DateTimeOffset TemporaryPasswordExpiresAt)
{
    // Keeps the temporary password out of logs and debugger output.
    public override string ToString() =>
        $"TemporaryPasswordResponse {{ TemporaryPasswordExpiresAt = {TemporaryPasswordExpiresAt:O} }}";
}
