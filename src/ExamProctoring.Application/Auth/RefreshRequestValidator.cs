using FluentValidation;

namespace ExamProctoring.Application.Auth;

// The refresh token is required and at most 256 characters (approved plan). Its format is not checked here:
// a malformed value is simply an unknown token and gets token_invalid.
public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public const int MaxRefreshTokenLength = 256;

    public RefreshRequestValidator()
    {
        RuleFor(r => r.RefreshToken)
            .NotEmpty().WithMessage("Refresh token is required.")
            .MaximumLength(MaxRefreshTokenLength).WithMessage($"Refresh token must be at most {MaxRefreshTokenLength} characters.");
    }
}
