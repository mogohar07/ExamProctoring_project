namespace ExamProctoring.Application.Common;

// Stable codes returned in the ProblemDetails "code" extension (§6.7). Each is added when first used.
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string DuplicateAccount = "duplicate_account";
}
