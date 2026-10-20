using FluentValidation;

namespace ExamProctoring.Application.Auth;

// Field rules for sign-up (§2.2, §6.4). The password policy itself (§6.5) is checked by AuthService with
// Identity's configured password validators, so that policy is defined in one place only.
public sealed class RegisterStudentRequestValidator : AbstractValidator<RegisterStudentRequest>
{
    public const int FullNameMaxLength = 150;

    public RegisterStudentRequestValidator()
    {
        RuleFor(r => r.FullName)
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Full name is required.")
            .Must(name => name is null || name.Trim().Length <= FullNameMaxLength)
                .WithMessage($"Full name must be at most {FullNameMaxLength} characters.")
            .Must(name => name is null || !name.Any(char.IsControl))
                .WithMessage("Full name must not contain control characters.");

        RuleFor(r => r.Email)
            .Must(email => !string.IsNullOrWhiteSpace(email)).WithMessage("Email is required.")
            .Must(email => string.IsNullOrWhiteSpace(email) || EmailRules.IsValid(email))
                .WithMessage("Email is not a valid email address.");

        RuleFor(r => r.Password)
            .NotEmpty().WithMessage("Password is required.");

        RuleFor(r => r.ConfirmPassword)
            .Equal(r => r.Password).WithMessage("Passwords do not match.");
    }
}
