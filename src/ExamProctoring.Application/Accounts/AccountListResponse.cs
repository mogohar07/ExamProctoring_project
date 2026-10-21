namespace ExamProctoring.Application.Accounts;

// 200 body of GET /api/admin/accounts (D23). Page and PageSize are the values used; TotalCount counts every matching
// account, not only this page.
public sealed record AccountListResponse(IReadOnlyList<AccountSummaryResponse> Items, int Page, int PageSize, int TotalCount);
