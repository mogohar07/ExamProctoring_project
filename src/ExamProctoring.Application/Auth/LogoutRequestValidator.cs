using FluentValidation;

namespace ExamProctoring.Application.Auth;

// Same rules as refresh: required and at most 256 characters; the format is not checked.
public sealed class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator()
    {
        RuleFor(r => r.RefreshToken)
            .NotEmpty().WithMessage("Refresh token is required.")
            .MaximumLength(RefreshRequestValidator.MaxRefreshTokenLength)
            .WithMessage($"Refresh token must be at most {RefreshRequestValidator.MaxRefreshTokenLength} characters.");
    }
}
