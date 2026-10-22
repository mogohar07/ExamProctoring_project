using ExamProctoring.Application.Exams;

namespace ExamProctoring.UnitTests.Exams;

// Exam create and update fields (§5.2, D25): title required, at most 200 characters after trimming; description at
// most 2000; start time required with an explicit offset; duration 1-360 minutes. Whether the start time is in the
// future is checked by the service, not here.
public sealed class ExamRequestValidatorTests
{
    private const string ValidStartsAt = "2026-11-01T09:00:00Z";

    private readonly ExamRequestValidator _validator = new();

    [Fact]
    public void ValidRequest_IsValid() =>
        Assert.True(_validator.Validate(Request()).IsValid);

    [Fact]
    public void NoDescription_IsValid() =>
        Assert.True(_validator.Validate(Request(description: null)).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_Missing_IsRejected(string? title) =>
        AssertOnlyFieldFails(Request(title: title), nameof(ExamRequest.Title), "Title is required.");

    [Fact]
    public void Title_Of200Characters_AfterTrimming_IsValid() =>
        Assert.True(_validator.Validate(Request(title: "  " + new string('t', 200) + "  ")).IsValid);

    [Fact]
    public void Title_Over200Characters_IsRejected() =>
        AssertOnlyFieldFails(Request(title: new string('t', 201)), nameof(ExamRequest.Title), "Title must be at most 200 characters.");

    [Fact]
    public void Description_Of2000Characters_AfterTrimming_IsValid() =>
        Assert.True(_validator.Validate(Request(description: " " + new string('d', 2000) + " ")).IsValid);

    [Fact]
    public void Description_Over2000Characters_IsRejected() =>
        AssertOnlyFieldFails(Request(description: new string('d', 2001)), nameof(ExamRequest.Description), "Description must be at most 2000 characters.");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void StartsAt_Missing_IsRejected(string? startsAt) =>
        AssertOnlyFieldFails(Request(startsAt: startsAt), nameof(ExamRequest.StartsAt), "Start time is required.");

    [Theory]
    [InlineData("2026-11-01T09:00:00Z")]
    [InlineData("2026-11-01T11:00:00+02:00")]
    [InlineData("2026-11-01T04:00:00-05:00")]
    [InlineData("2026-11-01T09:00Z")]
    [InlineData("2026-11-01T09:00:00.1234567Z")]
    public void StartsAt_WithAnOffset_IsValid(string startsAt) =>
        Assert.True(_validator.Validate(Request(startsAt: startsAt)).IsValid);

    [Theory]
    [InlineData("2026-11-01T09:00:00")]
    [InlineData("2026-11-01")]
    [InlineData("2026-11-01 09:00:00Z")]
    [InlineData("01/11/2026 09:00 +00:00")]
    [InlineData("tomorrow")]
    [InlineData("2026-13-01T09:00:00Z")]
    [InlineData("2026-02-30T09:00:00Z")]
    [InlineData("2026-11-01T09:00:00+25:00")]
    [InlineData("0001-01-01T00:00:00+02:00")]
    [InlineData("9999-12-31T23:00:00-02:00")]
    public void StartsAt_WithoutAnOffset_OrNotADateTime_IsRejected(string startsAt) =>
        AssertOnlyFieldFails(Request(startsAt: startsAt), nameof(ExamRequest.StartsAt),
            "Start time must be a date and time with a time-zone offset, for example 2026-11-01T09:00:00Z or 2026-11-01T11:00:00+02:00.");

    // Later start times would leave no room to calculate the end time.
    [Theory]
    [InlineData("9999-12-31T18:00:00Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    public void StartsAt_TooCloseToTheLatestDate_IsRejected(string startsAt) =>
        AssertOnlyFieldFails(Request(startsAt: startsAt), nameof(ExamRequest.StartsAt), "Start time is too far in the future.");

    [Fact]
    public void StartsAt_JustEarlyEnoughForTheLongestExam_IsValid() =>
        Assert.True(_validator.Validate(Request(startsAt: "9999-12-31T17:59:59Z")).IsValid);

    [Fact]
    public void DurationMinutes_Missing_IsRejected() =>
        AssertOnlyFieldFails(Request(durationMinutes: null), nameof(ExamRequest.DurationMinutes), "Duration is required.");

    [Theory]
    [InlineData(1)]
    [InlineData(90)]
    [InlineData(360)]
    public void DurationMinutes_From1To360_IsValid(int durationMinutes) =>
        Assert.True(_validator.Validate(Request(durationMinutes: durationMinutes)).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(361)]
    [InlineData(int.MaxValue)]
    public void DurationMinutes_Outside1To360_IsRejected(int durationMinutes) =>
        AssertOnlyFieldFails(Request(durationMinutes: durationMinutes), nameof(ExamRequest.DurationMinutes), "Duration must be between 1 and 360 minutes.");

    [Fact]
    public void EmptyRequest_ReportsEveryRequiredField()
    {
        var errors = _validator.Validate(new ExamRequest(null, null, null, null)).Errors;

        Assert.Equal(
            new[] { nameof(ExamRequest.Title), nameof(ExamRequest.StartsAt), nameof(ExamRequest.DurationMinutes) },
            errors.Select(e => e.PropertyName));
    }

    private static ExamRequest Request(
        string? title = "Midterm", string? description = "Chapters 1-4", string? startsAt = ValidStartsAt, int? durationMinutes = 90) =>
        new(title, description, startsAt, durationMinutes);

    private void AssertOnlyFieldFails(ExamRequest request, string field, string message)
    {
        var error = Assert.Single(_validator.Validate(request).Errors);
        Assert.Equal(field, error.PropertyName);
        Assert.Equal(message, error.ErrorMessage);
    }
}
