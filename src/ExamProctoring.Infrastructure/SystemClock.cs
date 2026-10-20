using ExamProctoring.Application.Abstractions;

namespace ExamProctoring.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
