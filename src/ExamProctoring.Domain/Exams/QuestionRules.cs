namespace ExamProctoring.Domain.Exams;

// Option rules enforced in the Domain rather than the database (§5.3).
public static class QuestionRules
{
    // An MCQ has exactly one correct option; True/False is a two-option MCQ (§2.2).
    public static bool HasExactlyOneCorrectOption(IEnumerable<QuestionOption> options) =>
        options.Count(option => option.IsCorrect) == 1;

    // Options belong to MCQs only, so an Essay has none (§5.2).
    public static bool HasNoOptions(IEnumerable<QuestionOption> options) =>
        !options.Any();

    // The rule for the question's type. An unknown type has no valid options.
    public static bool OptionsAreValid(QuestionType type, IEnumerable<QuestionOption> options) => type switch
    {
        QuestionType.Mcq => HasExactlyOneCorrectOption(options),
        QuestionType.Essay => HasNoOptions(options),
        _ => false,
    };
}
