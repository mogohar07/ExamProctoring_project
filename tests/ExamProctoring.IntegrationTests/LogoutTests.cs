using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.IntegrationTests.TestSupport;
using static ExamProctoring.IntegrationTests.TestSupport.AuthApi;

namespace ExamProctoring.IntegrationTests;

// POST /api/auth/logout (§6.6, §6.9, D3, D5, D8).
[Collection(ApiCollection.Name)]
public sealed class LogoutTests(ApiFactory factory)
{
    private readonly AuthApi _api = new(factory);

    [Fact]
    public async Task Logout_Returns204_AndEndsOnlyThatSession()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var phone = await _api.LoginAsync(user);
        var laptop = await _api.LoginAsync(user);

        var response = await _api.LogoutAsync(phone.AccessToken, phone.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
        var revoked = await _api.TokenRowAsync(phone.RefreshToken);
        Assert.NotNull(revoked.RevokedAt);
        Assert.Equal(RefreshTokenRevocationReasons.Logout, revoked.RevokedReason);
        Assert.Null(revoked.ReplacedByTokenId);

        await AssertProblemAsync(await _api.RefreshAsync(phone.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(laptop.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Logout_DoesNotEndTheCurrentAccessToken_WhichExpiresNaturally()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.LogoutAsync(session.AccessToken, session.RefreshToken)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/auth/me", session.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task LogoutWithTheNewestToken_AfterRotation_RevokesItAndKeepsTheRotationRecord()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var first = await _api.LoginAsync(user);
        var second = await SessionFromAsync(await _api.RefreshAsync(first.RefreshToken));

        Assert.Equal(HttpStatusCode.NoContent, (await _api.LogoutAsync(second.AccessToken, second.RefreshToken)).StatusCode);

        await AssertProblemAsync(await _api.RefreshAsync(second.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        // The older, rotated token is still a replay (D5), not an ordinary revoked token.
        await AssertProblemAsync(await _api.RefreshAsync(first.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenReused);
        Assert.Equal(RefreshTokenRevocationReasons.Rotated, (await _api.TokenRowAsync(first.RefreshToken)).RevokedReason);
        Assert.Equal(RefreshTokenRevocationReasons.Logout, (await _api.TokenRowAsync(second.RefreshToken)).RevokedReason);
    }

    [Fact]
    public async Task LogoutWithAnOlderRotatedToken_EndsTheWholeSession()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var first = await _api.LoginAsync(user);
        var second = await SessionFromAsync(await _api.RefreshAsync(first.RefreshToken));

        Assert.Equal(HttpStatusCode.NoContent, (await _api.LogoutAsync(second.AccessToken, first.RefreshToken)).StatusCode);

        await AssertProblemAsync(await _api.RefreshAsync(second.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        Assert.Equal(RefreshTokenRevocationReasons.Logout, (await _api.TokenRowAsync(second.RefreshToken)).RevokedReason);
    }

    [Fact]
    public async Task LogoutAfterReuseDetection_Returns204_AndKeepsTheReuseRecord()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var first = await _api.LoginAsync(user);
        var second = await SessionFromAsync(await _api.RefreshAsync(first.RefreshToken));
        await AssertProblemAsync(await _api.RefreshAsync(first.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenReused);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.LogoutAsync(second.AccessToken, second.RefreshToken)).StatusCode);

        Assert.Equal(RefreshTokenRevocationReasons.ReuseDetected, (await _api.TokenRowAsync(second.RefreshToken)).RevokedReason);
    }

    [Fact]
    public async Task LogoutTwice_IsHarmless_AndKeepsTheFirstRevocation()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.LogoutAsync(session.AccessToken, session.RefreshToken)).StatusCode);
        var firstRevocation = (await _api.TokenRowAsync(session.RefreshToken)).RevokedAt;

        using (factory.Clock.Advance(TimeSpan.FromMinutes(1)))
            Assert.Equal(HttpStatusCode.NoContent, (await _api.LogoutAsync(session.AccessToken, session.RefreshToken)).StatusCode);

        Assert.Equal(firstRevocation, (await _api.TokenRowAsync(session.RefreshToken)).RevokedAt);
    }

    [Theory]
    [InlineData("random")]
    [InlineData("not a refresh token")]
    public async Task LogoutWithAnUnknownToken_Returns204_AndChangesNothing(string kind)
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);
        var token = kind == "random" ? Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)) : kind;

        Assert.Equal(HttpStatusCode.NoContent, (await _api.LogoutAsync(session.AccessToken, token)).StatusCode);

        Assert.Null((await _api.TokenRowAsync(session.RefreshToken)).RevokedAt);
    }

    [Fact]
    public async Task LogoutWithAnotherUsersToken_Returns204_AndLeavesTheirSessionActive()
    {
        var caller = await _api.CreateUserAsync(RoleNames.Student);
        var other = await _api.CreateUserAsync(RoleNames.Student);
        var callerSession = await _api.LoginAsync(caller);
        var otherSession = await _api.LoginAsync(other);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.LogoutAsync(callerSession.AccessToken, otherSession.RefreshToken)).StatusCode);

        Assert.Null((await _api.TokenRowAsync(otherSession.RefreshToken)).RevokedAt);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(otherSession.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task LogoutWithAnExpiredRefreshToken_Returns204_AndRevokesIt()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        using (factory.Clock.Advance(TimeSpan.FromDays(8)))
            Assert.Equal(HttpStatusCode.NoContent, (await _api.LogoutAsync(session.AccessToken, session.RefreshToken)).StatusCode);

        Assert.Equal(RefreshTokenRevocationReasons.Logout, (await _api.TokenRowAsync(session.RefreshToken)).RevokedReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-jwt")]
    public async Task Logout_RequiresAValidAccessToken_AndThenRevokesNothing(string? accessToken)
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        await AssertProblemAsync(await _api.LogoutAsync(accessToken, session.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

        Assert.Null((await _api.TokenRowAsync(session.RefreshToken)).RevokedAt);
    }

    [Fact]
    public async Task Logout_IsAllowedDuringAForcedPasswordChange()
    {
        var instructor = await _api.CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddHours(72));
        var session = await _api.LoginAsync(instructor);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.LogoutAsync(session.AccessToken, session.RefreshToken)).StatusCode);
        Assert.Equal(RefreshTokenRevocationReasons.Logout, (await _api.TokenRowAsync(session.RefreshToken)).RevokedReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-long")]
    public async Task MissingOrOverlongRefreshToken_Returns400ValidationFailed(string? refreshToken)
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);
        if (refreshToken == "too-long")
            refreshToken = new string('x', 257);

        using var problem = await AssertProblemAsync(
            await _api.LogoutAsync(session.AccessToken, refreshToken), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("refreshToken", out _));
    }

    [Fact]
    public async Task MalformedJson_Returns400ValidationFailed()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        await AssertProblemAsync(
            await _api.PostRawJsonAsync("/api/auth/logout", "{ not json", session.AccessToken), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }
}
