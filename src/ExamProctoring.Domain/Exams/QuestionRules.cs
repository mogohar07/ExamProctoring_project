namespace ExamProctoring.Domain.Exams;

// Question and option rules enforced in the Domain rather than the database (§5.3, D31-D32).
public static class QuestionRules
{
    // D32: an MCQ has at least two options. The maximum is a setting (Exams:MaxMcqOptions), not a fixed rule.
    public const int MinMcqOptions = 2;

    // D35 (S6): the largest value the decimal(6,2) Marks column holds.
    public const decimal MaxMarks = 9999.99m;

    // D31: marks have at most two decimal places.
    public const int MarksDecimalPlaces = 2;

    // An MCQ has exactly one correct option; True/False is a two-option MCQ (§2.2).
    public static bool HasExactlyOneCorrectOption(IEnumerable<QuestionOption> options) =>
        options.Count(option => option.IsCorrect) == 1;

    // D32: at least two options.
    public static bool HasEnoughOptions(IEnumerable<QuestionOption> options) =>
        options.Count() >= MinMcqOptions;

    // D32: at most the configured maximum, checked when the question is written (and, from Step 2.4, at publication).
    public static bool HasAtMostOptions(IEnumerable<QuestionOption> options, int maxOptions) =>
        options.Count() <= maxOptions;

    // D32: no two options with the same text, compared after trimming and ignoring letter case (ordinal, no Unicode
    // normalization).
    public static bool HasDistinctOptionTexts(IEnumerable<QuestionOption> options)
    {
        var texts = options.Select(option => option.Text.Trim()).ToList();
        return texts.Distinct(StringComparer.OrdinalIgnoreCase).Count() == texts.Count;
    }

    // Options belong to MCQs only, so an Essay has none (§5.2).
    public static bool HasNoOptions(IEnumerable<QuestionOption> options) =>
        !options.Any();

    // The fixed option rules for the question's type (the configured maximum is checked separately). An unknown type
    // has no valid options.
    public static bool OptionsAreValid(QuestionType type, IReadOnlyCollection<QuestionOption> options) => type switch
    {
        QuestionType.Mcq => HasEnoughOptions(options) && HasExactlyOneCorrectOption(options) && HasDistinctOptionTexts(options),
        QuestionType.Essay => HasNoOptions(options),
        _ => false,
    };

    // D31, D35: greater than zero, at most 9999.99, at most two decimal places (1.5 and 1.500 are allowed, 1.005 is not).
    public static bool MarksAreValid(decimal marks) =>
        marks > 0 && marks <= MaxMarks && decimal.Round(marks, MarksDecimalPlaces) == marks;
}
