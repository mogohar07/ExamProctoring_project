namespace ExamProctoring.Application.Abstractions;

// The current time in UTC (§2.6). An abstraction so that time-dependent rules can be tested.
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
