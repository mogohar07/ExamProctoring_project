using FluentValidation;

namespace ExamProctoring.Application.Accounts;

// D22: the reason is optional; when given, trimmed and at most 500 characters. A blank reason counts as none.
public sealed class ReactivateAccountRequestValidator : AbstractValidator<ReactivateAccountRequest>
{
    public ReactivateAccountRequestValidator()
    {
        RuleFor(r => r.Reason).WithinReasonLength();
    }
}
