using ExamProctoring.Application.Accounts;

namespace ExamProctoring.UnitTests.Accounts;

// Account list parameters (D23): role Student or Instructor, status Active or Deactivated, page from 1, pageSize 1-100.
// Invalid values are rejected, never adjusted.
public sealed class AccountListQueryValidatorTests
{
    private readonly AccountListQueryValidator _validator = new();

    [Fact]
    public void NoParameters_IsValid() =>
        Assert.True(_validator.Validate(new AccountListQuery(null, null, null, null)).IsValid);

    [Theory]
    [InlineData("Student")]
    [InlineData("Instructor")]
    [InlineData("student")]
    [InlineData("INSTRUCTOR")]
    public void Role_StudentOrInstructor_InAnyCase_IsValid(string role) =>
        Assert.True(_validator.Validate(new AccountListQuery(role, null, null, null)).IsValid);

    [Theory]
    [InlineData("Admin")]
    [InlineData("admin")]
    [InlineData("Teacher")]
    [InlineData(" Student")]
    [InlineData("")]
    public void Role_AnythingElse_IsRejected(string role) =>
        AssertOnlyFieldFails(new AccountListQuery(role, null, null, null), nameof(AccountListQuery.Role), "Role must be Student or Instructor.");

    [Theory]
    [InlineData("Active")]
    [InlineData("Deactivated")]
    [InlineData("active")]
    [InlineData("DEACTIVATED")]
    public void Status_ActiveOrDeactivated_InAnyCase_IsValid(string status) =>
        Assert.True(_validator.Validate(new AccountListQuery(null, status, null, null)).IsValid);

    [Theory]
    [InlineData("Locked")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("")]
    public void Status_AnythingElse_IsRejected(string status) =>
        AssertOnlyFieldFails(new AccountListQuery(null, status, null, null), nameof(AccountListQuery.Status), "Status must be Active or Deactivated.");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Page_FromOne_IsValid(int page) =>
        Assert.True(_validator.Validate(new AccountListQuery(null, null, page, null)).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Page_BelowOne_IsRejected(int page) =>
        AssertOnlyFieldFails(new AccountListQuery(null, null, page, null), nameof(AccountListQuery.Page), "Page must be 1 or greater.");

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(100)]
    public void PageSize_From1To100_IsValid(int pageSize) =>
        Assert.True(_validator.Validate(new AccountListQuery(null, null, null, pageSize)).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void PageSize_Outside1To100_IsRejected(int pageSize) =>
        AssertOnlyFieldFails(new AccountListQuery(null, null, null, pageSize), nameof(AccountListQuery.PageSize), "Page size must be between 1 and 100.");

    private void AssertOnlyFieldFails(AccountListQuery query, string field, string message)
    {
        var error = Assert.Single(_validator.Validate(query).Errors);
        Assert.Equal(field, error.PropertyName);
        Assert.Equal(message, error.ErrorMessage);
    }
}
