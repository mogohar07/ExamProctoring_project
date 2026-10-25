namespace ExamProctoring.Domain.Exams;

// An exam-ready question, authored or (from Phase 3) approved from an AI suggestion (§5.2).
public class Question
{
    public Guid Id { get; set; }

    public Guid ExamId { get; set; }

    public QuestionType Type { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? ModelAnswer { get; set; }

    // decimal(6,2), greater than 0 (§5.1, §5.3). Null means not set yet (owner decision 2026-10-25): allowed only while
    // the exam is a Draft, for example a question imported from Word without marks; publishing requires valid marks.
    // Never replaced by a default.
    public decimal? Marks { get; set; }

    // Order within the exam; unique per exam (§5.3).
    public int Position { get; set; }
}
