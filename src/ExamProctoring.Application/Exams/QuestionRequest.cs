using ExamProctoring.Domain.Exams;

namespace ExamProctoring.Application.Exams;

// POST /api/exams/{id}/questions and PUT /api/questions/{id} (D31-D33): the complete question with all its options.
// An update replaces the whole question; options are stored in the order given. The exam, the position and the ids are
// never taken from the request.
public sealed record QuestionRequest(
    string? Type,
    string? Text,
    decimal? Marks,
    string? ModelAnswer,
    IReadOnlyList<QuestionOptionRequest?>? Options)
{
    // The type, or null when the value is not one of the type names in any letter case (numbers are not accepted).
    internal static QuestionType? ParseType(string? value) =>
        Enum.GetValues<QuestionType>()
            .Where(type => string.Equals(type.ToString(), value, StringComparison.OrdinalIgnoreCase))
            .Select(type => (QuestionType?)type)
            .FirstOrDefault();
}

// One MCQ option. A missing IsCorrect means false.
public sealed record QuestionOptionRequest(string? Text, bool? IsCorrect);
