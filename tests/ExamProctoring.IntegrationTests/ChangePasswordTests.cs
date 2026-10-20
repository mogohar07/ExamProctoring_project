using System.Net;
using System.Text.Json;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.IdentityModel.JsonWebTokens;
using static ExamProctoring.IntegrationTests.TestSupport.AuthApi;

namespace ExamProctoring.IntegrationTests;

// POST /api/auth/change-password (§6.3, §6.4, §6.5, D7, D9) and its effect on every session of the user.
[Collection(ApiCollection.Name)]
public sealed class ChangePasswordTests(ApiFactory factory)
{
    private const string NewPassword = "Newpassw0rd";
    private const string WrongPassword = "Wrong-Password1";

    private readonly AuthApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Success ----

    [Fact]
    public async Task Success_ReturnsANewSession_AndUpdatesTheAccount()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        HttpResponseMessage response;
        using (factory.Clock.FreezeAt(now))
            response = await _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            new[] { "tokenType", "accessToken", "accessTokenExpiresAt", "refreshToken", "refreshTokenExpiresAt", "mustChangePassword", "user" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.False(json.RootElement.GetProperty("mustChangePassword").GetBoolean());
        Assert.DoesNotContain(NewPassword, body);
        Assert.DoesNotContain(Password, body);

        var stored = await _api.FindUserAsync(user.Id);
        Assert.NotEqual(user.SecurityStamp, stored.SecurityStamp);
        Assert.NotEqual(user.PasswordHash, stored.PasswordHash);
        Assert.Equal(now, stored.PasswordChangedAt);
        Assert.False(stored.MustChangePassword);
        Assert.Null(stored.TemporaryPasswordExpiresAt);
        Assert.Equal(0, stored.AccessFailedCount);

        // The new tokens carry the new security stamp and work.
        var newSession = await SessionFromAsync(response);
        Assert.Equal(stored.SecurityStamp, new JsonWebToken(newSession.AccessToken).Claims.Single(c => c.Type == AuthClaimTypes.SecurityStamp).Value);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/auth/me", newSession.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(newSession.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Success_EndsEveryOldCredentialAndSession()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var phone = await _api.LoginAsync(user);
        var laptop = await _api.LoginAsync(user);
        var rotatedPhone = await SessionFromAsync(await _api.RefreshAsync(phone.RefreshToken));

        var response = await _api.ChangePasswordAsync(laptop.AccessToken, Password, NewPassword);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newSession = await SessionFromAsync(response);

        // Old access tokens fail on their next request (new security stamp).
        await AssertProblemAsync(await _api.GetAsync("/api/auth/me", laptop.AccessToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        await AssertProblemAsync(await _api.GetAsync("/api/auth/me", rotatedPhone.AccessToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

        // Every active refresh token was revoked; none can get new tokens.
        await AssertProblemAsync(await _api.RefreshAsync(laptop.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        await AssertProblemAsync(await _api.RefreshAsync(rotatedPhone.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        // The rotated token keeps its record, so presenting it again is still a replay (D5).
        await AssertProblemAsync(await _api.RefreshAsync(phone.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenReused);

        var tokens = await _api.TokensOfUserAsync(user.Id);
        var active = Assert.Single(tokens, t => t.RevokedAt is null);
        Assert.Equal(Hash(newSession.RefreshToken), active.TokenHash);
        Assert.Equal(RefreshTokenRevocationReasons.PasswordChanged, (await _api.TokenRowAsync(laptop.RefreshToken)).RevokedReason);
        Assert.Equal(RefreshTokenRevocationReasons.PasswordChanged, (await _api.TokenRowAsync(rotatedPhone.RefreshToken)).RevokedReason);
        Assert.Equal(RefreshTokenRevocationReasons.Rotated, (await _api.TokenRowAsync(phone.RefreshToken)).RevokedReason);

        // Only the new password works.
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, Password), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        Assert.Equal(HttpStatusCode.OK, (await _api.PostLoginAsync(user.Email!, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task OtherUsersSessions_AreUnaffected()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var other = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);
        var otherSession = await _api.LoginAsync(other);

        Assert.Equal(HttpStatusCode.OK, (await _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/auth/me", otherSession.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(otherSession.RefreshToken)).StatusCode);
    }

    // ---- Forced password change and temporary passwords (§6.3, D7) ----

    [Fact]
    public async Task ForcedPasswordChange_UnlocksTheAccount()
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddHours(72));
        var session = await _api.LoginAsync(instructor);
        await AssertProblemAsync(await _api.GetAsync("/test-only/authorization/instructor", session.AccessToken),
            HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired);

        var response = await _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newSession = await SessionFromAsync(response);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/test-only/authorization/instructor", newSession.AccessToken)).StatusCode);
        var stored = await _api.FindUserAsync(instructor.Id);
        Assert.False(stored.MustChangePassword);
        Assert.Null(stored.TemporaryPasswordExpiresAt);
    }

    [Fact]
    public async Task NewPassword_MustDifferFromTheTemporaryOne()
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddHours(72));
        var session = await _api.LoginAsync(instructor);

        using var problem = await AssertProblemAsync(await _api.ChangePasswordAsync(session.AccessToken, Password, Password),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("newPassword", out _));
        Assert.True((await _api.FindUserAsync(instructor.Id)).MustChangePassword);
    }

    [Fact]
    public async Task ExpiredTemporaryPassword_Gets403_AndNothingChanges()
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddHours(72));
        var session = await _api.LoginAsync(instructor);

        using (factory.Clock.Advance(TimeSpan.FromHours(73)))
        {
            await AssertProblemAsync(await _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword),
                HttpStatusCode.Forbidden, ErrorCodes.TemporaryPasswordExpired);
        }

        var stored = await _api.FindUserAsync(instructor.Id);
        Assert.Equal(instructor.PasswordHash, stored.PasswordHash);
        Assert.Equal(instructor.SecurityStamp, stored.SecurityStamp);
        Assert.Equal(0, stored.AccessFailedCount);
        Assert.True(stored.MustChangePassword);
    }

    // ---- Validation ----

    [Theory]
    [InlineData(null, NewPassword, "currentPassword")]
    [InlineData("", NewPassword, "currentPassword")]
    [InlineData(Password, null, "newPassword")]
    [InlineData(Password, "", "newPassword")]
    [InlineData(Password, Password, "newPassword")]   // must differ from the current password
    [InlineData(Password, "Short1A", "newPassword")]  // fewer than 8 characters
    [InlineData(Password, "nouppercase1", "newPassword")]
    [InlineData(Password, "NOLOWERCASE1", "newPassword")]
    [InlineData(Password, "NoDigitsHere", "newPassword")]
    public async Task InvalidRequest_Returns400_AndDoesNotCountAsAFailedAttempt(string? current, string? @new, string field)
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        using var problem = await AssertProblemAsync(await _api.ChangePasswordAsync(session.AccessToken, current, @new),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        var stored = await _api.FindUserAsync(user.Id);
        Assert.Equal(user.PasswordHash, stored.PasswordHash);
        Assert.Equal(0, stored.AccessFailedCount);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/auth/me", session.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task MalformedJson_Returns400ValidationFailed()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        await AssertProblemAsync(await _api.PostRawJsonAsync("/api/auth/change-password", "{ not json", session.AccessToken),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-jwt")]
    public async Task ChangePassword_RequiresAValidAccessToken(string? accessToken) =>
        await AssertProblemAsync(await _api.ChangePasswordAsync(accessToken, Password, NewPassword),
            HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

    // ---- Wrong current password and lockout (D9, §6.5) ----

    [Fact]
    public async Task WrongCurrentPassword_Returns400OnCurrentPassword_AndCountsAsAFailedAttempt()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        using var problem = await AssertProblemAsync(await _api.ChangePasswordAsync(session.AccessToken, WrongPassword, NewPassword),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("currentPassword", out _));
        var stored = await _api.FindUserAsync(user.Id);
        Assert.Equal(1, stored.AccessFailedCount);
        Assert.Equal(user.PasswordHash, stored.PasswordHash);
        Assert.Equal(user.SecurityStamp, stored.SecurityStamp);
        Assert.Null((await _api.TokenRowAsync(session.RefreshToken)).RevokedAt);
    }

    [Fact]
    public async Task FifthWrongCurrentPassword_LocksTheAccount_With403AccountLocked()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await AssertProblemAsync(await _api.ChangePasswordAsync(session.AccessToken, WrongPassword, NewPassword),
                HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        }

        await AssertProblemAsync(await _api.ChangePasswordAsync(session.AccessToken, WrongPassword, NewPassword),
            HttpStatusCode.Forbidden, ErrorCodes.AccountLocked);
        await AssertProblemAsync(await _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword),
            HttpStatusCode.Forbidden, ErrorCodes.AccountLocked);

        // The password did not change, login is locked too, and (decision 2) the lockout alone does not end the
        // existing session: the access token and the refresh token still work.
        Assert.Equal(user.PasswordHash, (await _api.FindUserAsync(user.Id)).PasswordHash);
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, Password), HttpStatusCode.Unauthorized, ErrorCodes.AccountLocked);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/auth/me", session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(session.RefreshToken)).StatusCode);
    }
}
