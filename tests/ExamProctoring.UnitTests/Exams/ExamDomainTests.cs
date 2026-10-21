using ExamProctoring.Domain.Exams;

namespace ExamProctoring.UnitTests.Exams;

// Exam defaults, the calculated end time and the shared text limits (§2.2, §5.2; limits decided 2026-10-21).
public sealed class ExamDomainTests
{
    [Fact]
    public void NewExam_IsADraft_WithWarningLimit3()
    {
        var exam = new Exam();

        Assert.Equal(ExamStatus.Draft, exam.Status);
        Assert.Equal(3, exam.WarningLimit);
        Assert.Equal(3, Exam.DefaultWarningLimit);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(90)]
    [InlineData(24 * 60)]
    public void EndsAt_IsStartsAtPlusDuration(int durationMinutes)
    {
        var startsAt = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var exam = new Exam { StartsAt = startsAt, DurationMinutes = durationMinutes };

        Assert.Equal(startsAt.AddMinutes(durationMinutes), exam.EndsAt);
    }

    [Fact]
    public void EndsAt_FollowsChangesToTheSchedule()
    {
        var exam = new Exam { StartsAt = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), DurationMinutes = 60 };

        exam.StartsAt = exam.StartsAt.AddDays(1);
        exam.DurationMinutes = 30;

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero), exam.EndsAt);
    }

    [Fact]
    public void TextLimits_AreTheApprovedValues()
    {
        Assert.Equal(200, ExamFieldLimits.TitleMaxLength);
        Assert.Equal(2000, ExamFieldLimits.DescriptionMaxLength);
        Assert.Equal(4000, ExamFieldLimits.QuestionTextMaxLength);
        Assert.Equal(4000, ExamFieldLimits.ModelAnswerMaxLength);
        Assert.Equal(1000, ExamFieldLimits.OptionTextMaxLength);
    }
}
