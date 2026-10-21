namespace ExamProctoring.Application.Accounts;

// 201 body for instructor creation (§6.4). The only time the temporary password is ever shown (D18).
public sealed record CreateInstructorResponse(
    Guid Id,
    string FullName,
    string Email,
    string Role,
    string TemporaryPassword,
    DateTimeOffset TemporaryPasswordExpiresAt)
{
    // Keeps the temporary password out of logs and debugger output.
    public override string ToString() => $"CreateInstructorResponse {{ Id = {Id}, Role = {Role} }}";
}
