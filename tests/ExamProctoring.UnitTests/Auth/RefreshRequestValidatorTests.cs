using ExamProctoring.Application.Auth;

namespace ExamProctoring.UnitTests.Auth;

// The refresh token is required and at most 256 characters; its format is not checked (§6.6, approved plan).
public sealed class RefreshRequestValidatorTests
{
    private readonly RefreshRequestValidator _validator = new();

    [Theory]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE")]
    [InlineData("not a refresh token")]
    [InlineData("a")]
    public void AnyNonEmptyTokenUpTo256Characters_IsValid(string token) =>
        Assert.True(_validator.Validate(new RefreshRequest(token)).IsValid);

    [Fact]
    public void TokenOfExactly256Characters_IsValid() =>
        Assert.True(_validator.Validate(new RefreshRequest(new string('x', 256))).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingToken_IsRejected(string? token) =>
        Assert.False(_validator.Validate(new RefreshRequest(token)).IsValid);

    [Fact]
    public void TokenLongerThan256Characters_IsRejected() =>
        Assert.False(_validator.Validate(new RefreshRequest(new string('x', 257))).IsValid);

    [Fact]
    public void RequestDoesNotPrintTheToken() =>
        Assert.DoesNotContain("secret-token-value", new RefreshRequest("secret-token-value").ToString());
}
