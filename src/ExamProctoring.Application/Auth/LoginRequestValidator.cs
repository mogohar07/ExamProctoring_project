using FluentValidation;

namespace ExamProctoring.Application.Auth;

// Login only requires both fields. The email format is deliberately not checked: a malformed address is simply
// an unknown account and gets the same invalid_credentials answer (§6.4).
public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.Email)
            .Must(email => !string.IsNullOrWhiteSpace(email)).WithMessage("Email is required.");

        RuleFor(r => r.Password)
            .NotEmpty().WithMessage("Password is required.");
    }
}
