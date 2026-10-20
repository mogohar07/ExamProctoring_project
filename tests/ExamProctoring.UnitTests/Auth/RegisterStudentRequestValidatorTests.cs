using ExamProctoring.Application.Auth;

namespace ExamProctoring.UnitTests.Auth;

// Field rules for sign-up (§2.2, §6.4). The password policy (§6.5) is enforced by Identity and covered by integration tests.
public sealed class RegisterStudentRequestValidatorTests
{
    private readonly RegisterStudentRequestValidator _validator = new();

    private static RegisterStudentRequest Valid() => new("Sara Ahmed", "sara@example.com", "Abcdefg1", "Abcdefg1");

    [Fact]
    public void ValidRequest_HasNoErrors() => Assert.True(_validator.Validate(Valid()).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sara\u0007Ahmed")]
    public void InvalidFullName_IsRejected(string? fullName) =>
        AssertOnlyFieldFails(Valid() with { FullName = fullName }, nameof(RegisterStudentRequest.FullName));

    [Fact]
    public void FullName_IsLimitedTo150CharactersAfterTrimming()
    {
        Assert.True(_validator.Validate(Valid() with { FullName = "  " + new string('a', 150) + "  " }).IsValid);
        AssertOnlyFieldFails(Valid() with { FullName = new string('a', 151) }, nameof(RegisterStudentRequest.FullName));
    }

    [Theory]
    [InlineData("محمد عبد الله")]
    [InlineData("O'Brien-Smith")]
    [InlineData("José García")]
    public void FullName_HasNoFormatRule(string fullName) =>
        Assert.True(_validator.Validate(Valid() with { FullName = fullName }).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a b@example.com")]
    public void InvalidEmail_IsRejected(string? email) =>
        AssertOnlyFieldFails(Valid() with { Email = email }, nameof(RegisterStudentRequest.Email));

    [Fact]
    public void MissingEmail_ReportsOnlyTheRequiredMessage()
    {
        var errors = _validator.Validate(Valid() with { Email = "" }).Errors;

        Assert.Equal("Email is required.", Assert.Single(errors).ErrorMessage);
    }

    [Fact]
    public void MissingPassword_IsRejected() =>
        AssertOnlyFieldFails(Valid() with { Password = "", ConfirmPassword = "" }, nameof(RegisterStudentRequest.Password));

    [Fact]
    public void ConfirmPasswordMismatch_IsRejected() =>
        AssertOnlyFieldFails(Valid() with { ConfirmPassword = "Different1" }, nameof(RegisterStudentRequest.ConfirmPassword));

    private void AssertOnlyFieldFails(RegisterStudentRequest request, string field)
    {
        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.All(result.Errors, e => Assert.Equal(field, e.PropertyName));
    }
}
