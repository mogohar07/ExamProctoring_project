using ExamProctoring.Application.Accounts;

namespace ExamProctoring.UnitTests.Accounts;

// Status-change reasons (D22): required and 1-500 characters after trimming to deactivate; optional to reactivate.
public sealed class AccountStatusRequestValidatorTests
{
    private readonly DeactivateAccountRequestValidator _deactivate = new();
    private readonly ReactivateAccountRequestValidator _reactivate = new();

    [Theory]
    [InlineData("Left the university")]
    [InlineData("x")]
    [InlineData("  Suspended pending review  ")]
    [InlineData("تم الإيقاف مؤقتا")]
    public void Deactivate_WithAReason_IsValid(string reason) =>
        Assert.True(_deactivate.Validate(new DeactivateAccountRequest(reason)).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Deactivate_WithoutAReason_IsRejected(string? reason) =>
        AssertReasonFails(_deactivate.Validate(new DeactivateAccountRequest(reason)), "A reason is required.");

    [Fact]
    public void Deactivate_ReasonIsLimitedTo500CharactersAfterTrimming()
    {
        Assert.True(_deactivate.Validate(new DeactivateAccountRequest("  " + new string('r', 500) + "  ")).IsValid);
        AssertReasonFails(_deactivate.Validate(new DeactivateAccountRequest(new string('r', 501))),
            "The reason must be at most 500 characters.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Appeal accepted")]
    public void Reactivate_ReasonIsOptional(string? reason) =>
        Assert.True(_reactivate.Validate(new ReactivateAccountRequest(reason)).IsValid);

    [Fact]
    public void Reactivate_ReasonIsLimitedTo500CharactersAfterTrimming()
    {
        Assert.True(_reactivate.Validate(new ReactivateAccountRequest("  " + new string('r', 500) + "  ")).IsValid);
        AssertReasonFails(_reactivate.Validate(new ReactivateAccountRequest(new string('r', 501))),
            "The reason must be at most 500 characters.");
    }

    [Fact]
    public void TemporaryPasswordResponse_DoesNotPrintThePassword()
    {
        var response = new TemporaryPasswordResponse("Secret-Temp-42", DateTimeOffset.UtcNow);

        Assert.DoesNotContain("Secret-Temp-42", response.ToString());
    }

    private static void AssertReasonFails(FluentValidation.Results.ValidationResult result, string message)
    {
        var error = Assert.Single(result.Errors);
        Assert.Equal("Reason", error.PropertyName);
        Assert.Equal(message, error.ErrorMessage);
    }
}
