using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Identity;
using ExamProctoring.Infrastructure.Persistence;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using static ExamProctoring.IntegrationTests.TestSupport.AuthApi;

namespace ExamProctoring.IntegrationTests;

// Session changes are atomic and serialized per account (decision 1, Step 4e): a session can never survive a
// password change or a logout because of a request running at the same moment, and a failure part-way through a
// password change leaves nothing changed.
//
// Each race test holds a database transaction that plays the other operation, waits until the API request is
// actually blocked by it (read from sys.dm_exec_requests, not timed sleeps), then commits.
[Collection(ApiCollection.Name)]
public sealed class SessionAtomicityTests(ApiFactory factory)
{
    private const string NewPassword = "Newpassw0rd";

    private readonly AuthApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RefreshDuringAPasswordChange_Fails_AndCreatesNoToken()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);
        var familyId = (await _api.TokenRowAsync(session.RefreshToken)).FamilyId;

        // A password change has written the new security stamp but has not committed yet.
        await using var passwordChange = await HeldTransaction.BeginAsync(factory);
        await passwordChange.Db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")), Ct);

        var refresh = _api.RefreshAsync(session.RefreshToken);
        await passwordChange.WaitUntilItBlocksAnotherRequestAsync(factory);
        await passwordChange.CommitAsync();

        await AssertProblemAsync(await refresh, HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        Assert.Single(await _api.TokensOfUserAsync(user.Id), t => t.FamilyId == familyId);
    }

    [Fact]
    public async Task LoginDuringAPasswordChange_Fails_AndCreatesNoSession()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);

        await using var passwordChange = await HeldTransaction.BeginAsync(factory);
        await passwordChange.Db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")), Ct);

        var login = _api.PostLoginAsync(user.Email!, Password);
        await passwordChange.WaitUntilItBlocksAnotherRequestAsync(factory);
        await passwordChange.CommitAsync();

        await AssertProblemAsync(await login, HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        Assert.Empty(await _api.TokensOfUserAsync(user.Id));
    }

    [Fact]
    public async Task LogoutDuringARefresh_WaitsForIt_AndRevokesTheTokenItCreated()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        // A refresh of this session holds the account lock and has added its replacement token, not yet committed.
        await using var refresh = await HeldTransaction.BeginAsync(factory);
        var replacementHash = await refresh.AddRefreshInProgressAsync(user.Id, (await _api.TokenRowAsync(session.RefreshToken)).FamilyId);

        var logout = _api.LogoutAsync(session.AccessToken, session.RefreshToken);
        await refresh.WaitUntilItBlocksAnotherRequestAsync(factory);
        await refresh.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await logout).StatusCode);
        var replacement = Assert.Single(await _api.TokensOfUserAsync(user.Id), t => t.TokenHash == replacementHash);
        Assert.Equal(RefreshTokenRevocationReasons.Logout, replacement.RevokedReason);
    }

    [Fact]
    public async Task PasswordChangeDuringARefresh_WaitsForIt_AndRevokesTheTokenItCreated()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        await using var refresh = await HeldTransaction.BeginAsync(factory);
        var replacementHash = await refresh.AddRefreshInProgressAsync(user.Id, (await _api.TokenRowAsync(session.RefreshToken)).FamilyId);

        var change = _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword);
        await refresh.WaitUntilItBlocksAnotherRequestAsync(factory);
        await refresh.CommitAsync();

        var response = await change;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newSession = await SessionFromAsync(response);
        var tokens = await _api.TokensOfUserAsync(user.Id);
        Assert.Equal(RefreshTokenRevocationReasons.PasswordChanged, tokens.Single(t => t.TokenHash == replacementHash).RevokedReason);
        Assert.Equal(Hash(newSession.RefreshToken), Assert.Single(tokens, t => t.RevokedAt is null).TokenHash);
    }

    [Fact]
    public async Task DeactivationDuringARefresh_WaitsForIt_AndRevokesTheTokenItCreated()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        await using var refresh = await HeldTransaction.BeginAsync(factory);
        var replacementHash = await refresh.AddRefreshInProgressAsync(user.Id, (await _api.TokenRowAsync(session.RefreshToken)).FamilyId);

        var deactivate = _api.PostJsonAsync($"/api/admin/accounts/{user.Id}/deactivate", new { reason = "Suspended" }, admin.AccessToken);
        await refresh.WaitUntilItBlocksAnotherRequestAsync(factory);
        await refresh.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await deactivate).StatusCode);
        var tokens = await _api.TokensOfUserAsync(user.Id);
        Assert.Equal(RefreshTokenRevocationReasons.AccountDeactivated, tokens.Single(t => t.TokenHash == replacementHash).RevokedReason);
        Assert.DoesNotContain(tokens, t => t.RevokedAt is null);
    }

    [Fact]
    public async Task TemporaryPasswordDuringALogin_WaitsForIt_AndRevokesTheSessionItCreated()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Instructor);

        // A login holds the account lock and has added the first token of its new session, not yet committed.
        await using var login = await HeldTransaction.BeginAsync(factory);
        var newSessionHash = await login.AddRefreshInProgressAsync(user.Id, Guid.NewGuid());

        var issue = _api.PostWithoutBodyAsync($"/api/admin/accounts/{user.Id}/temporary-password", admin.AccessToken);
        await login.WaitUntilItBlocksAnotherRequestAsync(factory);
        await login.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await issue).StatusCode);
        var token = Assert.Single(await _api.TokensOfUserAsync(user.Id));
        Assert.Equal(newSessionHash, token.TokenHash);
        Assert.Equal(RefreshTokenRevocationReasons.TemporaryPasswordIssued, token.RevokedReason);
    }

    [Fact]
    public async Task DeactivationDuringAnotherDeactivation_WaitsForIt_AndReturns409()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);

        // Another deactivation of the same account has saved its changes but has not committed yet.
        await using var otherDeactivation = await HeldTransaction.BeginAsync(factory);
        await otherDeactivation.Db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.AccountStatus, AccountStatus.Deactivated)
                .SetProperty(u => u.StatusReason, "First")
                .SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N"))
                .SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid().ToString()), Ct);

        var deactivate = _api.PostJsonAsync($"/api/admin/accounts/{user.Id}/deactivate", new { reason = "Second" }, admin.AccessToken);
        await otherDeactivation.WaitUntilItBlocksAnotherRequestAsync(factory);
        await otherDeactivation.CommitAsync();

        // The second one reads the account only after the first has finished, so it sees the new status.
        await AssertProblemAsync(await deactivate, HttpStatusCode.Conflict, ErrorCodes.AccountStatusUnchanged);
        Assert.Equal("First", (await _api.FindUserAsync(user.Id)).StatusReason);
    }

    [Fact]
    public async Task RefreshDuringADeactivation_Fails_AndCreatesNoToken()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);
        var familyId = (await _api.TokenRowAsync(session.RefreshToken)).FamilyId;

        // A deactivation has written the new status and security stamp but has not committed yet.
        await using var deactivation = await HeldTransaction.BeginAsync(factory);
        await deactivation.Db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.AccountStatus, AccountStatus.Deactivated)
                .SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")), Ct);

        var refresh = _api.RefreshAsync(session.RefreshToken);
        await deactivation.WaitUntilItBlocksAnotherRequestAsync(factory);
        await deactivation.CommitAsync();

        await AssertProblemAsync(await refresh, HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        Assert.Single(await _api.TokensOfUserAsync(user.Id), t => t.FamilyId == familyId);
    }

    // ---- Change-password overlapping an admin action, with both real requests in flight ----
    //
    // The test locks one of the user's refresh tokens. The first request takes the account row lock, writes its changes
    // and then stops where it revokes the user's sessions; the second request is started and waits for the account
    // row. Committing the test's transaction lets both finish, in that order.

    [Fact]
    public async Task ChangePasswordDuringATemporaryPassword_Returns401_AndOnlyTheTemporaryPasswordWorks()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        await using var hold = await HeldTransaction.BeginAsync(factory);
        await hold.LockRefreshTokenAsync((await _api.TokenRowAsync(session.RefreshToken)).Id);
        var issue = _api.PostWithoutBodyAsync($"/api/admin/accounts/{user.Id}/temporary-password", admin.AccessToken);
        await WaitUntilRequestsAreBlockedAsync(1);
        var change = _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword);
        await WaitUntilRequestsAreBlockedAsync(2);
        await hold.CommitAsync();

        var temporaryPassword = await TemporaryPasswordFromAsync(await issue);
        await AssertTokenInvalidAsync(await change);
        var stored = await _api.FindUserAsync(user.Id);
        Assert.True(stored.MustChangePassword);
        Assert.Null(stored.PasswordChangedAt);
        var tokens = await _api.TokensOfUserAsync(user.Id);
        Assert.Equal(RefreshTokenRevocationReasons.TemporaryPasswordIssued, Assert.Single(tokens).RevokedReason);

        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, NewPassword), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, Password), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        Assert.Equal(HttpStatusCode.OK, (await _api.PostLoginAsync(user.Email!, temporaryPassword)).StatusCode);
    }

    [Fact]
    public async Task TemporaryPasswordDuringAChangePassword_WaitsForIt_AndEndsTheNewSession()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        await using var hold = await HeldTransaction.BeginAsync(factory);
        await hold.LockRefreshTokenAsync((await _api.TokenRowAsync(session.RefreshToken)).Id);
        var change = _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword);
        await WaitUntilRequestsAreBlockedAsync(1);
        var issue = _api.PostWithoutBodyAsync($"/api/admin/accounts/{user.Id}/temporary-password", admin.AccessToken);
        await WaitUntilRequestsAreBlockedAsync(2);
        await hold.CommitAsync();

        var changed = await change;
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var newSession = await SessionFromAsync(changed);
        var temporaryPassword = await TemporaryPasswordFromAsync(await issue);

        // The temporary password came second: it ends the session the password change created.
        await AssertTokenInvalidAsync(await _api.GetAsync("/api/auth/me", newSession.AccessToken));
        await AssertTokenInvalidAsync(await _api.RefreshAsync(newSession.RefreshToken));
        var tokens = await _api.TokensOfUserAsync(user.Id);
        Assert.DoesNotContain(tokens, t => t.RevokedAt is null);
        Assert.Equal(RefreshTokenRevocationReasons.TemporaryPasswordIssued,
            tokens.Single(t => t.TokenHash == Hash(newSession.RefreshToken)).RevokedReason);

        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, NewPassword), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        Assert.Equal(HttpStatusCode.OK, (await _api.PostLoginAsync(user.Email!, temporaryPassword)).StatusCode);
    }

    [Fact]
    public async Task ChangePasswordDuringADeactivation_Returns401_AndNoSessionSurvives()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Instructor);
        var session = await _api.LoginAsync(user);

        await using var hold = await HeldTransaction.BeginAsync(factory);
        await hold.LockRefreshTokenAsync((await _api.TokenRowAsync(session.RefreshToken)).Id);
        var deactivate = _api.PostJsonAsync($"/api/admin/accounts/{user.Id}/deactivate", new { reason = "Suspended" }, admin.AccessToken);
        await WaitUntilRequestsAreBlockedAsync(1);
        var change = _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword);
        await WaitUntilRequestsAreBlockedAsync(2);
        await hold.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await deactivate).StatusCode);
        await AssertTokenInvalidAsync(await change);
        var stored = await _api.FindUserAsync(user.Id);
        Assert.Equal(AccountStatus.Deactivated, stored.AccountStatus);
        Assert.Equal(user.PasswordHash, stored.PasswordHash);
        Assert.Null(stored.PasswordChangedAt);
        Assert.Equal(RefreshTokenRevocationReasons.AccountDeactivated, Assert.Single(await _api.TokensOfUserAsync(user.Id)).RevokedReason);
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, Password), HttpStatusCode.Forbidden, ErrorCodes.AccountDeactivated);
    }

    [Fact]
    public async Task DeactivationDuringAChangePassword_WaitsForIt_AndEndsTheNewSession()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Instructor);
        var session = await _api.LoginAsync(user);

        await using var hold = await HeldTransaction.BeginAsync(factory);
        await hold.LockRefreshTokenAsync((await _api.TokenRowAsync(session.RefreshToken)).Id);
        var change = _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword);
        await WaitUntilRequestsAreBlockedAsync(1);
        var deactivate = _api.PostJsonAsync($"/api/admin/accounts/{user.Id}/deactivate", new { reason = "Suspended" }, admin.AccessToken);
        await WaitUntilRequestsAreBlockedAsync(2);
        await hold.CommitAsync();

        var changed = await change;
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var newSession = await SessionFromAsync(changed);
        Assert.Equal(HttpStatusCode.NoContent, (await deactivate).StatusCode);

        await AssertTokenInvalidAsync(await _api.GetAsync("/api/auth/me", newSession.AccessToken));
        await AssertTokenInvalidAsync(await _api.RefreshAsync(newSession.RefreshToken));
        var tokens = await _api.TokensOfUserAsync(user.Id);
        Assert.DoesNotContain(tokens, t => t.RevokedAt is null);
        Assert.Equal(RefreshTokenRevocationReasons.AccountDeactivated,
            tokens.Single(t => t.TokenHash == Hash(newSession.RefreshToken)).RevokedReason);

        // The password change itself was kept; the account is deactivated.
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, NewPassword), HttpStatusCode.Forbidden, ErrorCodes.AccountDeactivated);
        await AssertProblemAsync(await _api.PostLoginAsync(user.Email!, Password), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
    }

    [Fact]
    public async Task ChangePasswordDuringADeactivationAndReactivation_Returns401_AndOnlyANewLoginWorks()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        // The deactivation goes first; the password change and the reactivation both wait for it. Whichever of the two
        // runs next, the password change finds its access token's session already ended.
        await using var hold = await HeldTransaction.BeginAsync(factory);
        await hold.LockRefreshTokenAsync((await _api.TokenRowAsync(session.RefreshToken)).Id);
        var deactivate = _api.PostJsonAsync($"/api/admin/accounts/{user.Id}/deactivate", new { reason = "Suspended" }, admin.AccessToken);
        await WaitUntilRequestsAreBlockedAsync(1);
        var change = _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword);
        await WaitUntilRequestsAreBlockedAsync(2);
        var reactivate = _api.PostWithoutBodyAsync($"/api/admin/accounts/{user.Id}/reactivate", admin.AccessToken);
        await WaitUntilRequestsAreBlockedAsync(3);
        await hold.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await deactivate).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reactivate).StatusCode);
        await AssertTokenInvalidAsync(await change);
        var stored = await _api.FindUserAsync(user.Id);
        Assert.Equal(AccountStatus.Active, stored.AccountStatus);
        Assert.Equal(user.PasswordHash, stored.PasswordHash);
        Assert.DoesNotContain(await _api.TokensOfUserAsync(user.Id), t => t.RevokedAt is null);
        await AssertTokenInvalidAsync(await _api.GetAsync("/api/auth/me", session.AccessToken));
        await AssertTokenInvalidAsync(await _api.RefreshAsync(session.RefreshToken));

        Assert.Equal(HttpStatusCode.OK, (await _api.PostLoginAsync(user.Email!, Password)).StatusCode);
    }

    [Fact]
    public async Task ReactivatingAnActiveAccountDuringAChangePassword_Returns409_AndTheNewSessionSurvives()
    {
        var admin = await _api.AdminLoginAsync();
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        await using var hold = await HeldTransaction.BeginAsync(factory);
        await hold.LockRefreshTokenAsync((await _api.TokenRowAsync(session.RefreshToken)).Id);
        var change = _api.ChangePasswordAsync(session.AccessToken, Password, NewPassword);
        await WaitUntilRequestsAreBlockedAsync(1);
        var reactivate = _api.PostWithoutBodyAsync($"/api/admin/accounts/{user.Id}/reactivate", admin.AccessToken);
        await WaitUntilRequestsAreBlockedAsync(2);
        await hold.CommitAsync();

        var changed = await change;
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var newSession = await SessionFromAsync(changed);
        await AssertProblemAsync(await reactivate, HttpStatusCode.Conflict, ErrorCodes.AccountStatusUnchanged);

        // Nothing was changed by the reactivation, so the new session goes on working.
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/auth/me", newSession.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(newSession.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task FailureInsideAPasswordChange_RollsEverythingBack()
    {
        var user = await _api.CreateUserAsync(RoleNames.Student);
        var session = await _api.LoginAsync(user);

        // The same API, except that creating the new session's refresh token fails: the last step inside the
        // transaction, after the new password, the new stamp and the revocation of every session.
        await using var failingHost = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ITokenService>(sp => new FailingRefreshTokenService(ActivatorUtilities.CreateInstance<JwtTokenService>(sp)))));
        var client = failingHost.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var response = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = NewPassword }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(NewPassword, await response.Content.ReadAsStringAsync(Ct));
        var stored = await _api.FindUserAsync(user.Id);
        Assert.Equal(user.PasswordHash, stored.PasswordHash);
        Assert.Equal(user.SecurityStamp, stored.SecurityStamp);
        Assert.Null(stored.PasswordChangedAt);
        Assert.Single(await _api.TokensOfUserAsync(user.Id));
        Assert.Null((await _api.TokenRowAsync(session.RefreshToken)).RevokedAt);

        // The old password, access token and session all still work.
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/auth/me", session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(session.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.PostLoginAsync(user.Email!, Password)).StatusCode);
    }

    [Fact]
    public void BodyLimits_Are16KBOnTheFourTokenEndpoints_AndNoneOnRegister()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        long? Limit(string route) =>
            endpoints.Single(e => e.RoutePattern.RawText == route).Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;

        Assert.Equal(16 * 1024, Limit("api/auth/login"));
        Assert.Equal(16 * 1024, Limit("api/auth/refresh"));
        Assert.Equal(16 * 1024, Limit("api/auth/logout"));
        Assert.Equal(16 * 1024, Limit("api/auth/change-password"));
        Assert.Null(Limit("api/auth/register"));
    }

    // The in-memory test server does not enforce body limits, so this runs the API on real Kestrel.
    [Fact]
    public async Task OnKestrel_LogoutAndChangePasswordBodiesOver16KB_Return413()
    {
        await using var kestrel = new KestrelApiFactory();
        kestrel.UseKestrel(0);
        kestrel.StartServer();
        var kestrelApi = new AuthApi(kestrel);
        var user = await kestrelApi.CreateUserAsync(RoleNames.Student);
        var accessToken = kestrel.Services.GetRequiredService<ITokenService>().CreateAccessToken(await kestrelApi.FindUserAsync(user.Id), RoleNames.Student).Token;
        var big = new string('x', 17 * 1024);

        var logout = await kestrelApi.LogoutAsync(accessToken, big);
        var change = await kestrelApi.ChangePasswordAsync(accessToken, big, NewPassword);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, logout.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, change.StatusCode);
        Assert.Equal("application/problem+json", change.Content.Headers.ContentType?.MediaType);

        // Under the limit, the body is read and handled normally.
        Assert.Equal(HttpStatusCode.BadRequest, (await kestrelApi.LogoutAsync(accessToken, new string('x', 15 * 1024))).StatusCode);
    }

    // 401 token_invalid: what the account gate answers for an access token whose session has ended.
    private static async Task AssertTokenInvalidAsync(HttpResponseMessage response)
    {
        using var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
    }

    private static async Task<string> TemporaryPasswordFromAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("temporaryPassword").GetString()!;
    }

    // Returns once the given number of requests in the test database are waiting for a lock, so the requests started
    // so far have each reached the point where they wait and their order is fixed.
    private async Task WaitUntilRequestsAreBlockedAsync(int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var monitor = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            var blocked = (await monitor.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id <> 0 AND database_id = DB_ID()")
                .ToListAsync(Ct)).Single();
            if (blocked >= count)
                return;
            if (timeout.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException($"Expected {count} waiting request(s), found {blocked}.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
    }

    // A database transaction opened by the test on its own connection, standing in for another request in progress.
    private sealed class HeldTransaction : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private readonly IDbContextTransaction _transaction;
        private readonly short _sessionId;

        private HeldTransaction(AsyncServiceScope scope, AppDbContext db, IDbContextTransaction transaction, short sessionId)
        {
            _scope = scope;
            Db = db;
            _transaction = transaction;
            _sessionId = sessionId;
        }

        public AppDbContext Db { get; }

        public static async Task<HeldTransaction> BeginAsync(ApiFactory factory)
        {
            var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transaction = await db.Database.BeginTransactionAsync(Ct);
            var sessionId = (await db.Database.SqlQuery<short>($"SELECT @@SPID AS [Value]").ToListAsync(Ct)).Single();
            return new HeldTransaction(scope, db, transaction, sessionId);
        }

        // What a refresh does inside its transaction: lock the account row and add the replacement token.
        public async Task<string> AddRefreshInProgressAsync(Guid userId, Guid familyId)
        {
            await Db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, u => u.SecurityStamp), Ct);
            var hash = Hash(Guid.NewGuid().ToString("N"));
            Db.RefreshTokens.Add(new RefreshToken
            {
                UserId = userId,
                TokenHash = hash,
                FamilyId = familyId,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            });
            await Db.SaveChangesAsync(Ct);
            return hash;
        }

        // Locks one refresh token row (a no-op update). A request that revokes the user's sessions stops there, with the
        // account row already locked and its other changes written but not committed.
        public Task LockRefreshTokenAsync(Guid tokenId) =>
            Db.RefreshTokens.Where(t => t.Id == tokenId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedReason, t => t.RevokedReason), Ct);

        // Returns once another database session is waiting on a lock held by this transaction.
        public async Task WaitUntilItBlocksAnotherRequestAsync(ApiFactory factory)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var monitor = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                var blocked = (await monitor.Database
                    .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id = {_sessionId}")
                    .ToListAsync(Ct)).Single();
                if (blocked > 0)
                    return;
                if (timeout.Elapsed > TimeSpan.FromSeconds(15))
                    throw new TimeoutException("The API request never waited for the test's transaction.");
                await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
            }
        }

        public Task CommitAsync() => _transaction.CommitAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _scope.DisposeAsync();
        }
    }

    // Lets everything work except creating a refresh token, to fail a password change at its last step.
    private sealed class FailingRefreshTokenService(ITokenService inner) : ITokenService
    {
        public AccessToken CreateAccessToken(ApplicationUser user, string role) => inner.CreateAccessToken(user, role);

        public GeneratedRefreshToken CreateRefreshToken() => throw new InvalidOperationException("Injected failure for a test.");

        public string HashRefreshToken(string refreshToken) => inner.HashRefreshToken(refreshToken);
    }
}
