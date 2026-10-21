using FluentValidation;

namespace ExamProctoring.Application.Accounts;

// The status-change reason rules of D22, shared by deactivation (reason required) and reactivation (reason optional).
public static class StatusReasonRules
{
    // StatusReason is nvarchar(500) (§5.2).
    public const int MaxLength = 500;

    // At most 500 characters after trimming. A missing reason passes; deactivation adds its own required rule.
    public static IRuleBuilderOptions<T, string?> WithinReasonLength<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(reason => reason is null || reason.Trim().Length <= MaxLength)
            .WithMessage($"The reason must be at most {MaxLength} characters.");
}
