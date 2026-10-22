namespace ExamProctoring.Application.Exams;

// POST /api/exams and PUT /api/exams/{id} (§7, D25, D27). An update replaces all four fields. The warning limit is
// not part of the request; the server sets it from the general setting (D26).
public sealed record ExamRequest(string? Title, string? Description, string? StartsAt, int? DurationMinutes);
