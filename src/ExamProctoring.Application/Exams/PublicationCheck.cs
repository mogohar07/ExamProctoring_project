using ExamProctoring.Domain.Exams;

namespace ExamProctoring.Application.Exams;

// D34: before an exam is published, the whole exam is checked against the question rules (D31, D32) and the MCQ
// maximum in force now, so a question written under a higher maximum must be corrected first. D36 (P2): the answer
// describes the first blocking problem, in position order.
public static class PublicationCheck
{
    // The first problem that blocks publication, or null when every question is valid.
    public static string? FirstProblem(
        IReadOnlyList<Question> questions, IReadOnlyList<QuestionOption> options, int maxMcqOptions)
    {
        if (questions.Count == 0)
            return "The exam has no questions.";

        foreach (var question in questions.OrderBy(q => q.Position))
        {
            var label = $"Question {question.Position}";
            var own = options.Where(o => o.QuestionId == question.Id).ToList();

            var text = question.Text.Trim();
            if (text.Length == 0)
                return $"{label} has no text.";
            if (text.Length > ExamFieldLimits.QuestionTextMaxLength)
                return $"{label} has more than {ExamFieldLimits.QuestionTextMaxLength} characters of text.";
            if (!QuestionRules.MarksAreValid(question.Marks))
                return $"{label} has invalid marks.";
            if (question.ModelAnswer?.Trim().Length > ExamFieldLimits.ModelAnswerMaxLength)
                return $"{label} has a model answer longer than {ExamFieldLimits.ModelAnswerMaxLength} characters.";

            switch (question.Type)
            {
                case QuestionType.Essay:
                    if (!QuestionRules.HasNoOptions(own))
                        return $"{label} is an essay question and must not have options.";
                    break;

                case QuestionType.Mcq:
                    if (own.Any(o => o.Text.Trim().Length == 0))
                        return $"{label} has an option without text.";
                    if (own.Any(o => o.Text.Trim().Length > ExamFieldLimits.OptionTextMaxLength))
                        return $"{label} has an option longer than {ExamFieldLimits.OptionTextMaxLength} characters.";
                    if (!QuestionRules.HasEnoughOptions(own))
                        return $"{label} needs at least {QuestionRules.MinMcqOptions} options.";
                    if (!QuestionRules.HasAtMostOptions(own, maxMcqOptions))
                        return $"{label} has more options than the current maximum of {maxMcqOptions}.";
                    if (!QuestionRules.HasExactlyOneCorrectOption(own))
                        return $"{label} needs exactly one correct option.";
                    if (!QuestionRules.HasDistinctOptionTexts(own))
                        return $"{label} repeats an option text.";
                    break;

                default:
                    return $"{label} has an unknown type.";
            }
        }

        return null;
    }
}
