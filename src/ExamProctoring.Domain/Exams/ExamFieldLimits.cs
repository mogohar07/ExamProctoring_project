namespace ExamProctoring.Domain.Exams;

// Maximum text lengths for exams and questions (§5.2; the other limits decided 2026-10-21), shared by the database
// columns and the request validators. Lengths count UTF-16 code units, as nvarchar(n) does.
public static class ExamFieldLimits
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int QuestionTextMaxLength = 4000;
    public const int ModelAnswerMaxLength = 4000;
    public const int OptionTextMaxLength = 1000;
}
