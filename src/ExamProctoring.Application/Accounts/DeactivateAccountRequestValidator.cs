using FluentValidation;

namespace ExamProctoring.Application.Accounts;

// D22: the reason is required, trimmed, 1-500 characters.
public sealed class DeactivateAccountRequestValidator : AbstractValidator<DeactivateAccountRequest>
{
    public DeactivateAccountRequestValidator()
    {
        RuleFor(r => r.Reason)
            .Must(reason => !string.IsNullOrWhiteSpace(reason)).WithMessage("A reason is required.")
            .WithinReasonLength();
    }
}
