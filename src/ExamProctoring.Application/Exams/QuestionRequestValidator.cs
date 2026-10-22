using System.Globalization;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Domain.Exams;
using FluentValidation;

namespace ExamProctoring.Application.Exams;

// Field rules for creating and replacing a question (D31, D32, D35). Texts are checked after trimming. The MCQ maximum
// is the configured value in force now, and only the question being written is checked against it (D34).
public sealed class QuestionRequestValidator : AbstractValidator<QuestionRequest>
{
    public QuestionRequestValidator(IExamSettings settings)
    {
        RuleFor(r => r.Type)
            .Must(type => !string.IsNullOrWhiteSpace(type)).WithMessage("Type is required.")
            .Must(type => string.IsNullOrWhiteSpace(type) || QuestionRequest.ParseType(type) is not null)
                .WithMessage("Type must be Mcq or Essay.");

        RuleFor(r => r.Text)
            .Must(text => !string.IsNullOrWhiteSpace(text)).WithMessage("Text is required.")
            .Must(text => text is null || text.Trim().Length <= ExamFieldLimits.QuestionTextMaxLength)
                .WithMessage($"Text must be at most {ExamFieldLimits.QuestionTextMaxLength} characters.");

        // One message at a time: the first rule the value breaks.
        RuleFor(r => r.Marks)
            .Cascade(CascadeMode.Stop)
            .Must(marks => marks is not null).WithMessage("Marks are required.")
            .Must(marks => marks > 0).WithMessage("Marks must be greater than 0.")
            .Must(marks => marks <= QuestionRules.MaxMarks).WithMessage(string.Create(CultureInfo.InvariantCulture, $"Marks must be at most {QuestionRules.MaxMarks}."))
            .Must(marks => QuestionRules.MarksAreValid(marks!.Value))
                .WithMessage($"Marks can have at most {QuestionRules.MarksDecimalPlaces} decimal places.");

        RuleFor(r => r.ModelAnswer)
            .Must(answer => answer is null || answer.Trim().Length <= ExamFieldLimits.ModelAnswerMaxLength)
                .WithMessage($"Model answer must be at most {ExamFieldLimits.ModelAnswerMaxLength} characters.");

        When(r => QuestionRequest.ParseType(r.Type) == QuestionType.Mcq, () =>
        {
            RuleFor(r => r.Options)
                .Must(options => QuestionRules.HasEnoughOptions(AsOptions(options)))
                    .WithMessage($"An MCQ question needs at least {QuestionRules.MinMcqOptions} options.")
                .Must(options => QuestionRules.HasAtMostOptions(AsOptions(options), settings.MaxMcqOptions))
                    .WithMessage(_ => $"An MCQ question can have at most {settings.MaxMcqOptions} options.")
                .Must(options => QuestionRules.HasExactlyOneCorrectOption(AsOptions(options)))
                    .WithMessage("An MCQ question needs exactly one correct option.")
                // Blank texts are reported on the option itself, so only the others are compared here.
                .Must(options => QuestionRules.HasDistinctOptionTexts(AsOptions(options).Where(o => o.Text.Length > 0)))
                    .WithMessage("Options must not repeat the same text.");

            RuleForEach(r => r.Options)
                .NotNull().WithMessage("Option is required.")
                .ChildRules(option => option.RuleFor(o => o!.Text)
                    .Must(text => !string.IsNullOrWhiteSpace(text)).WithMessage("Option text is required.")
                    .Must(text => text is null || text.Trim().Length <= ExamFieldLimits.OptionTextMaxLength)
                        .WithMessage($"Option text must be at most {ExamFieldLimits.OptionTextMaxLength} characters."));
        });

        When(r => QuestionRequest.ParseType(r.Type) == QuestionType.Essay, () =>
            RuleFor(r => r.Options)
                .Must(options => options is null || options.Count == 0)
                    .WithMessage("An Essay question has no options."));
    }

    // The request's options as the Domain rules see them: trimmed text, a missing IsCorrect as false. Missing options are
    // left out here; they are reported on their own.
    private static List<QuestionOption> AsOptions(IReadOnlyList<QuestionOptionRequest?>? options) =>
        (options ?? [])
            .OfType<QuestionOptionRequest>()
            .Select(option => new QuestionOption { Text = option.Text?.Trim() ?? string.Empty, IsCorrect = option.IsCorrect ?? false })
            .ToList();
}
