using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Exams;

namespace ExamProctoring.UnitTests.Exams;

// D28: the phase is calculated from the time, for every status. D25: at most 6 hours. D26: warning limit 20 by default.
public sealed class ExamPhaseTests
{
    private static readonly DateTimeOffset StartsAt = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly Exam NinetyMinutes = new() { StartsAt = StartsAt, DurationMinutes = 90 };

    [Fact]
    public void BeforeStartsAt_IsUpcoming()
    {
        Assert.Equal(ExamPhase.Upcoming, NinetyMinutes.PhaseAt(StartsAt.AddDays(-1)));
        Assert.Equal(ExamPhase.Upcoming, NinetyMinutes.PhaseAt(StartsAt.AddTicks(-1)));
    }

    [Fact]
    public void FromStartsAt_UntilEndsAt_IsOpen()
    {
        Assert.Equal(ExamPhase.Open, NinetyMinutes.PhaseAt(StartsAt));
        Assert.Equal(ExamPhase.Open, NinetyMinutes.PhaseAt(StartsAt.AddMinutes(45)));
        Assert.Equal(ExamPhase.Open, NinetyMinutes.PhaseAt(NinetyMinutes.EndsAt.AddTicks(-1)));
    }

    [Fact]
    public void FromEndsAt_IsClosed()
    {
        Assert.Equal(ExamPhase.Closed, NinetyMinutes.PhaseAt(NinetyMinutes.EndsAt));
        Assert.Equal(ExamPhase.Closed, NinetyMinutes.PhaseAt(NinetyMinutes.EndsAt.AddYears(1)));
    }

    [Fact]
    public void ComparesInstants_NotLocalClockTimes() =>
        // 10:00 at +02:00 is 08:00 UTC, an hour before the start.
        Assert.Equal(ExamPhase.Upcoming, NinetyMinutes.PhaseAt(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.FromHours(2))));

    [Theory]
    [InlineData(ExamStatus.Draft)]
    [InlineData(ExamStatus.Published)]
    [InlineData(ExamStatus.Cancelled)]
    public void DoesNotDependOnTheStatus(ExamStatus status)
    {
        var exam = new Exam { StartsAt = StartsAt, DurationMinutes = 90, Status = status };

        Assert.Equal(ExamPhase.Open, exam.PhaseAt(StartsAt.AddMinutes(1)));
    }

    [Fact]
    public void MaxDuration_IsSixHours() =>
        Assert.Equal(360, Exam.MaxDurationMinutes);

    [Fact]
    public void ConfiguredWarningLimit_Defaults_To20_InTheRange1To20()
    {
        Assert.Equal(20, new ExamOptions().WarningLimit);
        Assert.Equal(1, ExamOptions.MinWarningLimit);
        Assert.Equal(20, ExamOptions.MaxWarningLimit);
    }
}
