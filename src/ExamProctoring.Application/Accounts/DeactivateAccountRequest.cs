namespace ExamProctoring.Application.Accounts;

// POST /api/admin/accounts/{id}/deactivate (§6.4, D22). The reason is required.
public sealed record DeactivateAccountRequest(string? Reason);
