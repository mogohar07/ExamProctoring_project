namespace ExamProctoring.Application.Common;

// An expected failure that the API turns into a ProblemDetails response. FieldErrors is set for validation_failed.
public sealed record AppError(string Code, string Message, IReadOnlyDictionary<string, string[]>? FieldErrors = null);

// The outcome of an application service call: a value, or an expected failure (not an exception).
public sealed class Result<T>
{
    private Result(T? value, AppError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public AppError? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(AppError error) => new(default, error);
}
