namespace ExamProctoring.Application.Exams.Import;

// Content of a Word document that the import cannot turn into question text (owner decision 2026-10-24).
public enum UnsupportedContentKind
{
    Image,
    Table,
    Equation,
    Other
}

// One item of a Word document in reading order: a line of text, or content that cannot be imported. UnsupportedText
// holds the readable text of unsupported content (for example the cells of a complex table), so it is never lost.
public sealed record DocxItem(string? Text, UnsupportedContentKind? Unsupported, IReadOnlyList<string> UnsupportedText)
{
    public static DocxItem Line(string text) => new(text, null, []);

    public static DocxItem UnsupportedContent(UnsupportedContentKind kind, IReadOnlyList<string>? text = null) =>
        new(null, kind, text ?? []);
}

public enum DocxReadFailure
{
    // Not a .docx at all (another format, a macro-enabled document or a template).
    Unsupported,

    // A .docx that cannot be read: corrupted, encrypted, over the package limits, or too slow to read.
    Unreadable
}

// The result of reading a document: its items, or why it could not be read. FailureMessage never contains document
// content.
public sealed record DocxReadResult(IReadOnlyList<DocxItem> Items, DocxReadFailure? Failure, string? FailureMessage)
{
    public static DocxReadResult Success(IReadOnlyList<DocxItem> items) => new(items, null, null);

    public static DocxReadResult Fail(DocxReadFailure failure, string message) => new([], failure, message);
}
