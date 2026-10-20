using FluentValidation;

namespace ExamProctoring.Application.Auth;

// Both fields are required and the new password must differ from the current one (§6.4: also from a temporary
// password). The password policy itself (§6.5) is checked by AuthService with Identity's configured validators.
public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(r => r.CurrentPassword)
            .NotEmpty().WithMessage("Current password is required.");

        RuleFor(r => r.NewPassword)
            .NotEmpty().WithMessage("New password is required.")
            .NotEqual(r => r.CurrentPassword).When(r => !string.IsNullOrEmpty(r.CurrentPassword))
            .WithMessage("The new password must be different from the current password.");
    }
}
