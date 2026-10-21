using FluentValidation;

namespace ExamProctoring.Application.Auth;

// Field rules for sign-up (§2.2, §6.4). The password policy itself (§6.5) is checked by AuthService with
// Identity's configured password validators, so that policy is defined in one place only.
public sealed class RegisterStudentRequestValidator : AbstractValidator<RegisterStudentRequest>
{
    public const int FullNameMaxLength = AccountFieldRules.FullNameMaxLength;

    public RegisterStudentRequestValidator()
    {
        RuleFor(r => r.FullName).ValidFullName();

        RuleFor(r => r.Email).ValidEmail();

        RuleFor(r => r.Password)
            .NotEmpty().WithMessage("Password is required.");

        RuleFor(r => r.ConfirmPassword)
            .Equal(r => r.Password).WithMessage("Passwords do not match.");
    }
}
