using ExamProctoring.Application.Auth;

namespace ExamProctoring.UnitTests.Auth;

// Login only requires both fields; the email format is not checked (§6.4).
public sealed class LoginRequestValidatorTests
{
    private readonly LoginRequestValidator _validator = new();

    [Theory]
    [InlineData("sara@example.com")]
    [InlineData("not-an-email")]
    [InlineData("  SARA@EXAMPLE.COM  ")]
    public void AnyNonEmptyEmail_WithAPassword_IsValid(string email) =>
        Assert.True(_validator.Validate(new LoginRequest(email, "anything")).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingEmail_IsRejected(string? email) =>
        AssertOnlyFieldFails(new LoginRequest(email, "anything"), nameof(LoginRequest.Email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingPassword_IsRejected(string? password) =>
        AssertOnlyFieldFails(new LoginRequest("sara@example.com", password), nameof(LoginRequest.Password));

    private void AssertOnlyFieldFails(LoginRequest request, string field)
    {
        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.All(result.Errors, e => Assert.Equal(field, e.PropertyName));
    }
}
