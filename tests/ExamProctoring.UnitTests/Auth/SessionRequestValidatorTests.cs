using ExamProctoring.Application.Auth;

namespace ExamProctoring.UnitTests.Auth;

// Field rules for logout and change-password. The password policy itself is enforced by Identity (integration tests).
public sealed class SessionRequestValidatorTests
{
    private readonly LogoutRequestValidator _logout = new();
    private readonly ChangePasswordRequestValidator _changePassword = new();

    [Theory]
    [InlineData("a")]
    [InlineData("not a refresh token")]
    public void Logout_AcceptsAnyTokenUpTo256Characters(string token) =>
        Assert.True(_logout.Validate(new LogoutRequest(token)).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Logout_RejectsAMissingToken(string? token) =>
        Assert.False(_logout.Validate(new LogoutRequest(token)).IsValid);

    [Fact]
    public void Logout_RejectsATokenLongerThan256Characters()
    {
        Assert.True(_logout.Validate(new LogoutRequest(new string('x', 256))).IsValid);
        Assert.False(_logout.Validate(new LogoutRequest(new string('x', 257))).IsValid);
    }

    [Fact]
    public void ChangePassword_AcceptsTwoDifferentPasswords() =>
        Assert.True(_changePassword.Validate(new ChangePasswordRequest("Abcdefg1", "Newpassw0rd")).IsValid);

    [Theory]
    [InlineData(null, "Newpassw0rd", nameof(ChangePasswordRequest.CurrentPassword))]
    [InlineData("", "Newpassw0rd", nameof(ChangePasswordRequest.CurrentPassword))]
    [InlineData("Abcdefg1", null, nameof(ChangePasswordRequest.NewPassword))]
    [InlineData("Abcdefg1", "", nameof(ChangePasswordRequest.NewPassword))]
    [InlineData("Abcdefg1", "Abcdefg1", nameof(ChangePasswordRequest.NewPassword))]
    public void ChangePassword_RejectsMissingOrUnchangedPasswords(string? current, string? @new, string field)
    {
        var result = _changePassword.Validate(new ChangePasswordRequest(current, @new));

        Assert.False(result.IsValid);
        Assert.All(result.Errors, e => Assert.Equal(field, e.PropertyName));
    }

    [Fact]
    public void Requests_DoNotPrintTheirSecrets()
    {
        Assert.DoesNotContain("secret-token", new LogoutRequest("secret-token").ToString());
        var change = new ChangePasswordRequest("Old-secret1", "New-secret1").ToString();
        Assert.DoesNotContain("Old-secret1", change);
        Assert.DoesNotContain("New-secret1", change);
    }
}
