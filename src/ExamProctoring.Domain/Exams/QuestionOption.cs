namespace ExamProctoring.Domain.Exams;

// One option of an MCQ question (§5.2).
public class QuestionOption
{
    public Guid Id { get; set; }

    public Guid QuestionId { get; set; }

    public string Text { get; set; } = string.Empty;

    public bool IsCorrect { get; set; }

    // Order within the question; unique per question (§5.3).
    public int Position { get; set; }
}
