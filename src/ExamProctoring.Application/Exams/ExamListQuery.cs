using ExamProctoring.Domain.Exams;

namespace ExamProctoring.Application.Exams;

// GET /api/exams?status=&page=&pageSize= (D29). Every value is optional; a missing one means no filter or the
// default. Status names match in any letter case.
public sealed record ExamListQuery(string? Status, int? Page, int? PageSize)
{
    public const int DefaultPageSize = 10;
    public const int MaxPageSize = 100;

    // The status, or null when the value is not one of the status names (numbers are not accepted).
    internal static ExamStatus? ParseStatus(string value) =>
        Enum.GetValues<ExamStatus>()
            .Where(status => string.Equals(status.ToString(), value, StringComparison.OrdinalIgnoreCase))
            .Select(status => (ExamStatus?)status)
            .FirstOrDefault();
}
