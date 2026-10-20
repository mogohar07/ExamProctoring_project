using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
