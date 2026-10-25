using ExamProctoring.Domain.Exams;

namespace ExamProctoring.Application.Exams;

// A question as its owning instructor sees it (D35, S1): exactly these fields, including the correct option and the
// model answer. An Essay has no options. Marks is null when not set yet (Draft only).
public sealed record QuestionResponse(
    Guid Id,
    Guid ExamId,
    string Type,
    string Text,
    decimal? Marks,
    string? ModelAnswer,
    int Position,
    IReadOnlyList<QuestionOptionResponse> Options)
{
    internal static QuestionResponse From(Question question, IEnumerable<QuestionOption> options) => new(
        question.Id,
        question.ExamId,
        question.Type.ToString(),
        question.Text,
        question.Marks,
        question.ModelAnswer,
        question.Position,
        options
            .OrderBy(option => option.Position)
            .Select(option => new QuestionOptionResponse(option.Id, option.Text, option.IsCorrect, option.Position))
            .ToList());
}

public sealed record QuestionOptionResponse(Guid Id, string Text, bool IsCorrect, int Position);

// 200 body of GET /api/exams/{id}/questions (D35, S2): every question of the exam in position order, not paged.
public sealed record QuestionListResponse(IReadOnlyList<QuestionResponse> Items);
