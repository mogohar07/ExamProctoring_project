using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.IntegrationTests.TestSupport;
using static ExamProctoring.IntegrationTests.TestSupport.AuthApi;

namespace ExamProctoring.IntegrationTests;

// Admin actions on existing accounts (§6.3, §6.4, D18, D19, D21, D22): POST /api/admin/accounts/{id}/temporary-password,
// /deactivate and /reactivate. The admin is the one seeded at startup (D20).
[Collection(ApiCollection.Name)]
public sealed partial class AccountAdministrationTests(ApiFactory factory)
{
    private const string NewPassword = "Newpassw0rd";

    private static readonly string[] Actions = ["temporary-password", "deactivate", "reactivate"];

    private readonly AuthApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Temporary password ----

    [Theory]
    [InlineData(RoleNames.Student)]
    [InlineData(RoleNames.Instructor)]
    public async Task TemporaryPassword_Returns200_WithThePasswordOnce_AndStoresOnlyItsHash(string role)
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(role);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        HttpResponseMessage response;
        using (factory.Clock.FreezeAt(now))
            response = await IssueTemporaryPasswordAsync(user.Id.ToString(), admin.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(new[] { "temporaryPassword", "temporaryPasswordExpiresAt" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(now.AddHours(72), json.RootElement.GetProperty("temporaryPasswordExpiresAt").GetDateTimeOffset());
        var temporaryPassword = json.RootElement.GetProperty("temporaryPassword").GetString()!;
        Assert.Matches(TemporaryPasswordFormat(), temporaryPassword);

        var stored = await _api.FindUserAsync(user.Id);
        Assert.True(stored.MustChangePassword);
        Assert.Equal(now.AddHours(72), stored.TemporaryPasswordExpiresAt);
        Assert.NotEqual(user.SecurityStamp, stored.SecurityStamp);
        Assert.NotEqual(temporaryPassword, stored.PasswordHash);
        Assert.DoesNotContain(stored.PasswordHash!, body);
        Assert.DoesNotContain(stored.SecurityStamp!, body);

        // Nothing else about the account changes: not its status, its status record or its role.
        Assert.Equal(AccountStatus.Active, stored.AccountStatus);
        Assert.Null(stored.StatusChangedAt);
        Assert.Null(stored.StatusChangedById);
        Assert.Null(stored.StatusReason);
        Assert.Equal(user.FullName, stored.FullName);
        Assert.Equal(user.Email, stored.Email);
        await _api.WithUserManagerAsync(async userManager =>
        {
            var account = (await userManager.FindByIdAsync(user.Id.ToString()))!;
            Assert.True(await userManager.CheckPasswordAsync(account, temporaryPassword));
            Assert.Equal(new[] { role }, await userManager.GetRolesAsync(account));
        });
    }

    [Fact]
    public async Task TemporaryPassword_EndsEverySession_ReplacesTheOldPassword_AndForcesAPasswordChange()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var first = await _api.LoginAsync(user);
        var second = await _api.LoginAsync(user);

        var temporaryPassword = await TemporaryPasswordFromAsync(await IssueTemporaryPasswordAsync(user.Id.ToString(), admin.AccessToken));

        // Old access tokens stop working on their next request, and every refresh token is revoked.
        await AssertProblemAsync(await _api.GetAsync("/api/auth/me", first.AccessToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        await AssertProblemAsync(await _api.RefreshAsync(first.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        await AssertProblemAsync(await _api.RefreshAsync(second.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        Assert.All(await _api.TokensOfUserAsync(user.Id),
            t => Assert.Equal(RefreshTokenRevocationReasons.TemporaryPasswordIssued, t.RevokedReason));

        // The old password no longer works; the temporary one does, limited to the password change.
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, Password), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        var login = await _api.PostLoginAsync(user.Email!, temporaryPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using (var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync(Ct)))
            Assert.True(json.RootElement.GetProperty("mustChangePassword").GetBoolean());
        var session = await SessionFromAsync(login);
        await AssertProblemAsync(await _api.GetAsync("/test-only/authorization/student", session.AccessToken),
            HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired);

        var changed = await _api.ChangePasswordAsync(session.AccessToken, temporaryPassword, NewPassword);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _api.GetAsync("/test-only/authorization/student", (await SessionFromAsync(changed)).AccessToken)).StatusCode);
    }

    [Fact]
    public async Task TemporaryPassword_ExpiresAfterTheConfiguredHours()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Instructor);

        var temporaryPassword = await TemporaryPasswordFromAsync(await IssueTemporaryPasswordAsync(user.Id.ToString(), admin.AccessToken));

        using (factory.Clock.Advance(TimeSpan.FromHours(72) + TimeSpan.FromMinutes(1)))
        {
            await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, temporaryPassword),
                HttpStatusCode.Forbidden, ErrorCodes.TemporaryPasswordExpired);
        }
    }

    [Fact]
    public async Task TemporaryPassword_ClearsALockout()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        await _api.UpdateUserAsync(user.Id, u =>
        {
            u.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15);
            u.AccessFailedCount = 3;
        });
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, Password), HttpStatusCode.Unauthorized, ErrorCodes.AccountLocked);

        var temporaryPassword = await TemporaryPasswordFromAsync(await IssueTemporaryPasswordAsync(user.Id.ToString(), admin.AccessToken));

        var stored = await _api.FindUserAsync(user.Id);
        Assert.Null(stored.LockoutEnd);
        Assert.Equal(0, stored.AccessFailedCount);
        Assert.Equal(HttpStatusCode.OK, (await _api.PostLoginAsync(user.Email!, temporaryPassword)).StatusCode);
    }

    [Fact]
    public async Task TemporaryPassword_OnADeactivatedAccount_LeavesItDeactivated_UntilItIsReactivated()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        Assert.Equal(HttpStatusCode.NoContent, (await DeactivateAsync(user.Id.ToString(), admin.AccessToken, "Suspended")).StatusCode);
        var deactivated = await _api.FindUserAsync(user.Id);

        var temporaryPassword = await TemporaryPasswordFromAsync(await IssueTemporaryPasswordAsync(user.Id.ToString(), admin.AccessToken));

        var stored = await _api.FindUserAsync(user.Id);
        Assert.Equal(AccountStatus.Deactivated, stored.AccountStatus);
        Assert.Equal(deactivated.StatusChangedAt, stored.StatusChangedAt);
        Assert.Equal("Suspended", stored.StatusReason);
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, temporaryPassword), HttpStatusCode.Forbidden, ErrorCodes.AccountDeactivated);

        Assert.Equal(HttpStatusCode.NoContent, (await ReactivateAsync(user.Id.ToString(), admin.AccessToken)).StatusCode);
        var login = await _api.PostLoginAsync(user.Email!, temporaryPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync(Ct));
        Assert.True(json.RootElement.GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task TemporaryPassword_IsDifferentEachTime()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);

        var first = await TemporaryPasswordFromAsync(await IssueTemporaryPasswordAsync(user.Id.ToString(), admin.AccessToken));
        var second = await TemporaryPasswordFromAsync(await IssueTemporaryPasswordAsync(user.Id.ToString(), admin.AccessToken));

        Assert.NotEqual(first, second);
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, first), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        Assert.Equal(HttpStatusCode.OK, (await _api.PostLoginAsync(user.Email!, second)).StatusCode);
    }

    // ---- Deactivation ----

    [Theory]
    [InlineData(RoleNames.Student)]
    [InlineData(RoleNames.Instructor)]
    public async Task Deactivate_Returns204_RecordsTheChange_AndEndsEverySession(string role)
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(role);
        var first = await _api.LoginAsync(user);
        var second = await _api.LoginAsync(user);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        HttpResponseMessage response;
        using (factory.Clock.FreezeAt(now))
            response = await DeactivateAsync(user.Id.ToString(), admin.AccessToken, "  Left the university  ");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
        var stored = await _api.FindUserAsync(user.Id);
        Assert.Equal(AccountStatus.Deactivated, stored.AccountStatus);
        Assert.Equal(now, stored.StatusChangedAt);
        Assert.Equal(await _api.SeedAdminIdAsync(), stored.StatusChangedById);
        Assert.Equal("Left the university", stored.StatusReason);
        Assert.NotEqual(user.SecurityStamp, stored.SecurityStamp);
        Assert.Equal(user.PasswordHash, stored.PasswordHash);

        // The existing access token fails on its next request, no session can be refreshed, and the right password
        // gets account_deactivated.
        await AssertProblemAsync(await _api.GetAsync("/api/auth/me", first.AccessToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        await AssertProblemAsync(await _api.RefreshAsync(first.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        await AssertProblemAsync(await _api.RefreshAsync(second.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        Assert.All(await _api.TokensOfUserAsync(user.Id), t => Assert.Equal(RefreshTokenRevocationReasons.AccountDeactivated, t.RevokedReason));
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, Password), HttpStatusCode.Forbidden, ErrorCodes.AccountDeactivated);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "reason": null }""")]
    [InlineData("""{ "reason": "" }""")]
    [InlineData("""{ "reason": "   " }""")]
    [InlineData("""{ "reason": "{501}" }""")]
    public async Task Deactivate_WithoutAValidReason_Returns400ValidationFailed_AndChangesNothing(string body)
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        var response = await _api.PostRawJsonAsync(ActionPath(user.Id.ToString(), "deactivate"),
            body.Replace("{501}", new string('r', 501)), admin.AccessToken);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("reason", out _));
        await AssertUnchangedAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/auth/me", session.AccessToken)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    public async Task Deactivate_WithAMissingOrMalformedBody_Returns400ValidationFailed(string body)
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);

        await AssertProblemAsync(await _api.PostRawJsonAsync(ActionPath(user.Id.ToString(), "deactivate"), body, admin.AccessToken),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        await AssertUnchangedAsync(user);
    }

    [Fact]
    public async Task Deactivate_AcceptsAReasonOf500CharactersAfterTrimming()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var reason = new string('r', 500);

        var response = await DeactivateAsync(user.Id.ToString(), admin.AccessToken, $"  {reason}  ");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(reason, (await _api.FindUserAsync(user.Id)).StatusReason);
    }

    [Fact]
    public async Task DeactivatingADeactivatedAccount_Returns409AccountStatusUnchanged_AndChangesNothing()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Instructor);
        Assert.Equal(HttpStatusCode.NoContent, (await DeactivateAsync(user.Id.ToString(), admin.AccessToken, "First reason")).StatusCode);
        var deactivated = await _api.FindUserAsync(user.Id);

        using (factory.Clock.Advance(TimeSpan.FromHours(1)))
        {
            await AssertProblemAsync(await DeactivateAsync(user.Id.ToString(), admin.AccessToken, "Second reason"),
                HttpStatusCode.Conflict, ErrorCodes.AccountStatusUnchanged);
        }

        await AssertUnchangedAsync(deactivated);
    }

    // ---- Reactivation ----

    [Fact]
    public async Task Reactivate_Returns204_RecordsTheChange_AndTheUserCanLogInAgain_ButOldSessionsStayEnded()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);
        Assert.Equal(HttpStatusCode.NoContent, (await DeactivateAsync(user.Id.ToString(), admin.AccessToken, "Suspended")).StatusCode);
        var deactivated = await _api.FindUserAsync(user.Id);
        var later = DateTimeOffset.UtcNow.AddHours(2);

        HttpResponseMessage response;
        using (factory.Clock.FreezeAt(later))
            response = await ReactivateAsync(user.Id.ToString(), admin.AccessToken, "  Appeal accepted  ");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await _api.FindUserAsync(user.Id);
        Assert.Equal(AccountStatus.Active, stored.AccountStatus);
        Assert.Equal(later, stored.StatusChangedAt);
        Assert.Equal(await _api.SeedAdminIdAsync(), stored.StatusChangedById);
        Assert.Equal("Appeal accepted", stored.StatusReason);
        Assert.NotEqual(deactivated.SecurityStamp, stored.SecurityStamp);

        // Tokens from before the deactivation stay invalid; a new login works.
        await AssertProblemAsync(await _api.GetAsync("/api/auth/me", session.AccessToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        await AssertProblemAsync(await _api.RefreshAsync(session.RefreshToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        var newSession = await _api.LoginAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/test-only/authorization/student", newSession.AccessToken)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{ "reason": null }""")]
    [InlineData("""{ "reason": "   " }""")]
    public async Task Reactivate_WithoutAReason_Returns204_AndRecordsNoReason(string? body)
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Instructor);
        Assert.Equal(HttpStatusCode.NoContent, (await DeactivateAsync(user.Id.ToString(), admin.AccessToken, "Suspended")).StatusCode);
        var path = ActionPath(user.Id.ToString(), "reactivate");

        var response = body is null
            ? await _api.PostWithoutBodyAsync(path, admin.AccessToken)
            : await _api.PostRawJsonAsync(path, body, admin.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await _api.FindUserAsync(user.Id);
        Assert.Equal(AccountStatus.Active, stored.AccountStatus);
        Assert.Null(stored.StatusReason);
        Assert.Equal(await _api.SeedAdminIdAsync(), stored.StatusChangedById);
    }

    [Fact]
    public async Task Reactivate_WithAReasonOver500Characters_Returns400ValidationFailed_AndChangesNothing()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        Assert.Equal(HttpStatusCode.NoContent, (await DeactivateAsync(user.Id.ToString(), admin.AccessToken, "Suspended")).StatusCode);
        var deactivated = await _api.FindUserAsync(user.Id);

        using var problem = await AssertProblemAsync(await ReactivateAsync(user.Id.ToString(), admin.AccessToken, new string('r', 501)),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("reason", out _));
        await AssertUnchangedAsync(deactivated);
    }

    [Fact]
    public async Task ReactivatingAnActiveAccount_Returns409AccountStatusUnchanged_AndChangesNothing()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        await AssertProblemAsync(await ReactivateAsync(user.Id.ToString(), admin.AccessToken, "Not needed"),
            HttpStatusCode.Conflict, ErrorCodes.AccountStatusUnchanged);

        await AssertUnchangedAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(session.RefreshToken)).StatusCode);
    }

    // ---- Targets and access (D21) ----

    public static TheoryData<string, string> ActionsOnUnmanagedTargets()
    {
        var data = new TheoryData<string, string>();
        foreach (var action in Actions)
        {
            foreach (var target in new[] { "another admin", "own account", "unknown id", "not a guid" })
                data.Add(action, target);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ActionsOnUnmanagedTargets))]
    public async Task AdminOwnOrUnknownTargets_Return404AccountNotFound_AndChangeNothing(string action, string target)
    {
        var admin = await _api.AdminLoginAsync();
        var otherAdmin = await _api.CreateUserAsync(RoleNames.Admin);
        var seedAdmin = await _api.FindUserAsync(await _api.SeedAdminIdAsync());
        var id = target switch
        {
            "another admin" => otherAdmin.Id.ToString(),
            "own account" => seedAdmin.Id.ToString(),
            "unknown id" => Guid.NewGuid().ToString(),
            _ => "not-a-guid",
        };

        await AssertProblemAsync(await SendAsync(action, id, admin.AccessToken), HttpStatusCode.NotFound, ErrorCodes.AccountNotFound);

        await AssertUnchangedAsync(otherAdmin);
        await AssertUnchangedAsync(seedAdmin);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/auth/me", admin.AccessToken)).StatusCode);
    }

    public static TheoryData<string, string> ActionsByNonAdmins()
    {
        var data = new TheoryData<string, string>();
        foreach (var action in Actions)
        {
            data.Add(action, RoleNames.Student);
            data.Add(action, RoleNames.Instructor);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ActionsByNonAdmins))]
    public async Task NonAdmins_Get403_AndChangeNothing(string action, string role)
    {
        var caller = await _api.CreateUserAsync(role);
        var session = await _api.LoginAsync(caller);
        var target = await _api.CreateUserAsync(RoleNames.Student);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(action, target.Id.ToString(), session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(action, caller.Id.ToString(), session.AccessToken)).StatusCode);

        await AssertUnchangedAsync(target);
        await AssertUnchangedAsync(caller);
    }

    [Theory]
    [InlineData("temporary-password")]
    [InlineData("deactivate")]
    [InlineData("reactivate")]
    public async Task WithoutAToken_Returns401TokenInvalid(string action)
    {
        var target = await _api.CreateUserAsync(RoleNames.Student);

        await AssertProblemAsync(await SendAsync(action, target.Id.ToString(), accessToken: null),
            HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        await AssertUnchangedAsync(target);
    }

    // ---- Helpers ----

    private static string ActionPath(string id, string action) => $"/api/admin/accounts/{id}/{action}";

    private Task<HttpResponseMessage> IssueTemporaryPasswordAsync(string id, string accessToken) =>
        _api.PostWithoutBodyAsync(ActionPath(id, "temporary-password"), accessToken);

    private Task<HttpResponseMessage> DeactivateAsync(string id, string accessToken, string? reason) =>
        _api.PostJsonAsync(ActionPath(id, "deactivate"), new { reason }, accessToken);

    private Task<HttpResponseMessage> ReactivateAsync(string id, string accessToken, string? reason = null) =>
        _api.PostJsonAsync(ActionPath(id, "reactivate"), new { reason }, accessToken);

    // Each action with a request that would succeed on a manageable target.
    private Task<HttpResponseMessage> SendAsync(string action, string id, string? accessToken) => action switch
    {
        "temporary-password" => _api.PostWithoutBodyAsync(ActionPath(id, action), accessToken),
        "deactivate" => _api.PostJsonAsync(ActionPath(id, action), new { reason = "Testing" }, accessToken),
        _ => _api.PostWithoutBodyAsync(ActionPath(id, action), accessToken),
    };

    private static async Task<string> TemporaryPasswordFromAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("temporaryPassword").GetString()!;
    }

    // The account, its credentials, its status record and its sessions are exactly as they were.
    private async Task AssertUnchangedAsync(ApplicationUser before)
    {
        var stored = await _api.FindUserAsync(before.Id);
        Assert.Equal(before.SecurityStamp, stored.SecurityStamp);
        Assert.Equal(before.PasswordHash, stored.PasswordHash);
        Assert.Equal(before.AccountStatus, stored.AccountStatus);
        Assert.Equal(before.MustChangePassword, stored.MustChangePassword);
        Assert.Equal(before.TemporaryPasswordExpiresAt, stored.TemporaryPasswordExpiresAt);
        Assert.Equal(before.StatusChangedAt, stored.StatusChangedAt);
        Assert.Equal(before.StatusChangedById, stored.StatusChangedById);
        Assert.Equal(before.StatusReason, stored.StatusReason);
        Assert.DoesNotContain(await _api.TokensOfUserAsync(before.Id), t =>
            t.RevokedReason is RefreshTokenRevocationReasons.TemporaryPasswordIssued
                or RefreshTokenRevocationReasons.AccountDeactivated
                or RefreshTokenRevocationReasons.AccountReactivated);
    }

    // 16 characters from the approved alphabet (no 0, O, 1, l, I).
    [GeneratedRegex("^[A-HJ-NP-Za-km-z2-9]{16}$")]
    private static partial Regex TemporaryPasswordFormat();
}
