namespace ExamProctoring.Application.Exams.Import;

// 200 body of POST /api/exams/import/docx. Nothing in it is stored: the instructor reviews the questions and the app
// saves them with POST /api/exams/{examId}/questions, which takes the same question fields.
public sealed record DocxImportResponse(
    ImportSummary Summary,
    IReadOnlyList<ImportedQuestion> Questions,
    IReadOnlyList<string> UnassignedText,
    IReadOnlyList<UnsupportedContentItem> UnsupportedContent);

public sealed record ImportSummary(int Found, int Ready, int NeedsAttention, int NotImported);

// One question found in the document. Type, Text, Marks, ModelAnswer and Options use the QuestionRequest names and
// formats. Status is Ready, NeedsAttention or NotImported; Type is null only for NotImported.
public sealed record ImportedQuestion(
    int Index,
    string Status,
    string? Type,
    string Text,
    decimal? Marks,
    string? ModelAnswer,
    IReadOnlyList<ImportedOption> Options,
    IReadOnlyList<ImportIssue> Issues,
    string SourceText);

public sealed record ImportedOption(string Text, bool IsCorrect);

// Code is stable (the apps translate by it); Message is English.
public sealed record ImportIssue(string Code, string Message);

// QuestionIndex is null for content outside any question.
public sealed record UnsupportedContentItem(int? QuestionIndex, string Kind);

public static class ImportStatus
{
    public const string Ready = "Ready";
    public const string NeedsAttention = "NeedsAttention";
    public const string NotImported = "NotImported";
}
