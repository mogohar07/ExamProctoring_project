using FluentValidation;

namespace ExamProctoring.Application.Auth;

// The full-name and email rules of §2.2, shared by student sign-up and admin instructor creation.
public static class AccountFieldRules
{
    public const int FullNameMaxLength = 150;

    // Required; trimmed; at most 150 characters; no control characters; no format rule.
    public static IRuleBuilderOptions<T, string?> ValidFullName<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Full name is required.")
            .Must(name => name is null || name.Trim().Length <= FullNameMaxLength)
                .WithMessage($"Full name must be at most {FullNameMaxLength} characters.")
            .Must(name => name is null || !name.Any(char.IsControl))
                .WithMessage("Full name must not contain control characters.");

    // Required; the approved email format rule (EmailRules).
    public static IRuleBuilderOptions<T, string?> ValidEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(email => !string.IsNullOrWhiteSpace(email)).WithMessage("Email is required.")
            .Must(email => string.IsNullOrWhiteSpace(email) || EmailRules.IsValid(email))
                .WithMessage("Email is not a valid email address.");
}
