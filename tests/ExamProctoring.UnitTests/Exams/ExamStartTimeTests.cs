using ExamProctoring.Application.Exams;

namespace ExamProctoring.UnitTests.Exams;

// D25: start times are read with their offset and stored in UTC.
public sealed class ExamStartTimeTests
{
    [Theory]
    [InlineData("2026-11-01T09:00:00Z")]
    [InlineData("2026-11-01T11:00:00+02:00")]
    [InlineData("2026-11-01T04:00:00-05:00")]
    [InlineData("2026-11-01T14:30:00+05:30")]
    public void TryParse_ConvertsToUtc(string value)
    {
        Assert.True(ExamStartTime.TryParse(value, out var utc));

        Assert.Equal(TimeSpan.Zero, utc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void TryParse_KeepsFractionsOfASecond()
    {
        Assert.True(ExamStartTime.TryParse("2026-11-01T09:00:00.1234567Z", out var utc));

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero).AddTicks(1234567), utc);
    }

    [Fact]
    public void TryParse_CrossesTheDateLineIntoUtc()
    {
        Assert.True(ExamStartTime.TryParse("2026-11-01T01:00:00+03:00", out var utc));

        Assert.Equal(new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero), utc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-11-01T09:00:00")]
    [InlineData("2026-11-01T09:00:00z")]
    [InlineData("2026-11-01T09:00:00+0200")]
    [InlineData(" 2026-11-01T09:00:00Z")]
    [InlineData("2026-11-01T09:00:00Z ")]
    public void TryParse_RejectsValuesWithoutAnExplicitOffset(string? value) =>
        Assert.False(ExamStartTime.TryParse(value, out _));

    [Fact]
    public void Latest_LeavesRoomForTheLongestExam() =>
        Assert.Equal(DateTimeOffset.MaxValue.AddMinutes(-360), ExamStartTime.Latest);
}
