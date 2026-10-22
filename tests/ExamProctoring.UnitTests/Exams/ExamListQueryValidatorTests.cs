using ExamProctoring.Application.Exams;

namespace ExamProctoring.UnitTests.Exams;

// Exam list parameters (D29): status Draft, Published or Cancelled in any letter case, page from 1, pageSize 1-100.
// Invalid values are rejected, never adjusted.
public sealed class ExamListQueryValidatorTests
{
    private readonly ExamListQueryValidator _validator = new();

    [Fact]
    public void NoParameters_IsValid() =>
        Assert.True(_validator.Validate(new ExamListQuery(null, null, null)).IsValid);

    [Theory]
    [InlineData("Draft")]
    [InlineData("Published")]
    [InlineData("Cancelled")]
    [InlineData("draft")]
    [InlineData("PUBLISHED")]
    [InlineData("cAnCeLlEd")]
    public void Status_AnExamStatus_InAnyCase_IsValid(string status) =>
        Assert.True(_validator.Validate(new ExamListQuery(status, null, null)).IsValid);

    [Theory]
    [InlineData("Canceled")]
    [InlineData("Open")]
    [InlineData("Upcoming")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData(" Draft")]
    [InlineData("")]
    public void Status_AnythingElse_IsRejected(string status) =>
        AssertOnlyFieldFails(new ExamListQuery(status, null, null), nameof(ExamListQuery.Status), "Status must be Draft, Published or Cancelled.");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Page_FromOne_IsValid(int page) =>
        Assert.True(_validator.Validate(new ExamListQuery(null, page, null)).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Page_BelowOne_IsRejected(int page) =>
        AssertOnlyFieldFails(new ExamListQuery(null, page, null), nameof(ExamListQuery.Page), "Page must be 1 or greater.");

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void PageSize_From1To100_IsValid(int pageSize) =>
        Assert.True(_validator.Validate(new ExamListQuery(null, null, pageSize)).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void PageSize_Outside1To100_IsRejected(int pageSize) =>
        AssertOnlyFieldFails(new ExamListQuery(null, null, pageSize), nameof(ExamListQuery.PageSize), "Page size must be between 1 and 100.");

    [Fact]
    public void DefaultPageSize_Is10() =>
        Assert.Equal(10, ExamListQuery.DefaultPageSize);

    private void AssertOnlyFieldFails(ExamListQuery query, string field, string message)
    {
        var error = Assert.Single(_validator.Validate(query).Errors);
        Assert.Equal(field, error.PropertyName);
        Assert.Equal(message, error.ErrorMessage);
    }
}
