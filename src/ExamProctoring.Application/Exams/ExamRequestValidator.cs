using ExamProctoring.Domain.Exams;
using FluentValidation;

namespace ExamProctoring.Application.Exams;

// Field rules for creating and updating an exam (§5.2, D25). Whether the start time is in the future is checked by
// the service, because on update it only applies when the start time changes.
public sealed class ExamRequestValidator : AbstractValidator<ExamRequest>
{
    public ExamRequestValidator()
    {
        RuleFor(r => r.Title)
            .Must(title => !string.IsNullOrWhiteSpace(title)).WithMessage("Title is required.")
            .Must(title => title is null || title.Trim().Length <= ExamFieldLimits.TitleMaxLength)
                .WithMessage($"Title must be at most {ExamFieldLimits.TitleMaxLength} characters.");

        RuleFor(r => r.Description)
            .Must(description => description is null || description.Trim().Length <= ExamFieldLimits.DescriptionMaxLength)
                .WithMessage($"Description must be at most {ExamFieldLimits.DescriptionMaxLength} characters.");

        RuleFor(r => r.StartsAt)
            .Must(startsAt => !string.IsNullOrWhiteSpace(startsAt)).WithMessage("Start time is required.")
            .Must(startsAt => string.IsNullOrWhiteSpace(startsAt) || ExamStartTime.TryParse(startsAt, out _))
                .WithMessage($"Start time must be a date and time with a time-zone offset, for example {ExamStartTime.FormatExample}.")
            .Must(startsAt => !ExamStartTime.TryParse(startsAt, out var utc) || utc <= ExamStartTime.Latest)
                .WithMessage("Start time is too far in the future.");

        RuleFor(r => r.DurationMinutes)
            .Must(duration => duration is not null).WithMessage("Duration is required.")
            .Must(duration => duration is null or >= 1 and <= Exam.MaxDurationMinutes)
                .WithMessage($"Duration must be between 1 and {Exam.MaxDurationMinutes} minutes.");
    }
}
