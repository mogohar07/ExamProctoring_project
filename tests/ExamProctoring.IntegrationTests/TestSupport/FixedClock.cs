using ExamProctoring.Application.Abstractions;

namespace ExamProctoring.IntegrationTests.TestSupport;

// An IClock stopped at a given moment, for creating tokens that are already expired or not yet valid.
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;
}
