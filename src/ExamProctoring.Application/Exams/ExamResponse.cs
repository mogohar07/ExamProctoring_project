using ExamProctoring.Domain.Exams;

namespace ExamProctoring.Application.Exams;

// An exam as instructors see it (D28): exactly these fields. EndsAt and Phase are calculated on read; RowVersion and
// InstructorId are never returned.
public sealed record ExamResponse(
    Guid Id,
    string Title,
    string? Description,
    DateTimeOffset StartsAt,
    int DurationMinutes,
    DateTimeOffset EndsAt,
    string Status,
    string Phase,
    int WarningLimit,
    DateTimeOffset? PublishedAt)
{
    internal static ExamResponse From(Exam exam, DateTimeOffset now) => new(
        exam.Id,
        exam.Title,
        exam.Description,
        exam.StartsAt,
        exam.DurationMinutes,
        exam.EndsAt,
        exam.Status.ToString(),
        exam.PhaseAt(now).ToString(),
        exam.WarningLimit,
        exam.PublishedAt);
}
