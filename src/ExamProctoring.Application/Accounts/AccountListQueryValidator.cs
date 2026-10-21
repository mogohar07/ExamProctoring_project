using FluentValidation;

namespace ExamProctoring.Application.Accounts;

// D23: role Student or Instructor; status Active or Deactivated; page from 1; pageSize 1-100. Invalid values are
// rejected, never adjusted.
public sealed class AccountListQueryValidator : AbstractValidator<AccountListQuery>
{
    public AccountListQueryValidator()
    {
        RuleFor(q => q.Role)
            .Must(role => role is null || AccountListQuery.ParseRole(role) is not null)
            .WithMessage("Role must be Student or Instructor.");

        RuleFor(q => q.Status)
            .Must(status => status is null || AccountListQuery.ParseStatus(status) is not null)
            .WithMessage("Status must be Active or Deactivated.");

        RuleFor(q => q.Page)
            .Must(page => page is null or >= 1)
            .WithMessage("Page must be 1 or greater.");

        RuleFor(q => q.PageSize)
            .Must(pageSize => pageSize is null or >= 1 and <= AccountListQuery.MaxPageSize)
            .WithMessage($"Page size must be between 1 and {AccountListQuery.MaxPageSize}.");
    }
}
