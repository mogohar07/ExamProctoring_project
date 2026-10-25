namespace ExamProctoring.Application.Exams;

// POST /api/exams/with-questions (owner decision 2026-10-25): the exam details and the reviewed questions (for example
// from the Word import) saved together as a new Draft exam. The exam fields are those of ExamRequest and each question
// is a QuestionRequest; the same rules apply. Marks may be missing (null), because the exam is a Draft.
public sealed record DraftExamRequest(
    string? Title,
    string? Description,
    string? StartsAt,
    int? DurationMinutes,
    IReadOnlyList<QuestionRequest?>? Questions)
{
    // The same limit as the Word import (ExamImportService.MaxQuestions).
    public const int MaxQuestions = 500;

    internal ExamRequest ToExamRequest() => new(Title, Description, StartsAt, DurationMinutes);
}

// 201 body of POST /api/exams/with-questions: the new Draft exam and its questions in position order.
public sealed record DraftExamResponse(ExamResponse Exam, IReadOnlyList<QuestionResponse> Questions);
