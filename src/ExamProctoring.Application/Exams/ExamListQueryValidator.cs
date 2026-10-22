using FluentValidation;

namespace ExamProctoring.Application.Exams;

// D29: status Draft, Published or Cancelled; page from 1; pageSize 1-100. Invalid values are rejected, never adjusted.
public sealed class ExamListQueryValidator : AbstractValidator<ExamListQuery>
{
    public ExamListQueryValidator()
    {
        RuleFor(q => q.Status)
            .Must(status => status is null || ExamListQuery.ParseStatus(status) is not null)
            .WithMessage("Status must be Draft, Published or Cancelled.");

        RuleFor(q => q.Page)
            .Must(page => page is null or >= 1)
            .WithMessage("Page must be 1 or greater.");

        RuleFor(q => q.PageSize)
            .Must(pageSize => pageSize is null or >= 1 and <= ExamListQuery.MaxPageSize)
            .WithMessage($"Page size must be between 1 and {ExamListQuery.MaxPageSize}.");
    }
}
