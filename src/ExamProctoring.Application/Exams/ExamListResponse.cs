namespace ExamProctoring.Application.Exams;

// 200 body of GET /api/exams (D29), with the same shape as the account list (D23). TotalCount counts every matching
// exam, not only this page.
public sealed record ExamListResponse(IReadOnlyList<ExamResponse> Items, int Page, int PageSize, int TotalCount);
