namespace ExamProctoring.Application.Accounts;

// POST /api/admin/accounts/{id}/reactivate (§6.4, D22). The reason is optional, and the body may be left out.
public sealed record ReactivateAccountRequest(string? Reason);
