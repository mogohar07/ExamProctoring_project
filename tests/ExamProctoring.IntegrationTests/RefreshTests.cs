using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Identity;
using ExamProctoring.Infrastructure.Persistence;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ExamProctoring.IntegrationTests;

// POST /api/auth/refresh (§6.6, D4–D7): rotation, replay detection, family revocation and concurrency.
// Sessions start through the real login endpoint. Expiry is tested by moving the host's clock (TestClock), not by sleeping.
[Collection(ApiCollection.Name)]
public sealed class RefreshTests(ApiFactory factory)
{
    private const string Password = "Abcdefg1";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Successful rotation ----

    [Fact]
    public async Task Refresh_Returns200_WithTheLoginResponseShape_AndNewTokens()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var session = await LoginAsync(user);

        var response = await RefreshAsync(session.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(
            new[] { "tokenType", "accessToken", "accessTokenExpiresAt", "refreshToken", "refreshTokenExpiresAt", "mustChangePassword", "user" },
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Bearer", root.GetProperty("tokenType").GetString());
        Assert.False(root.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(new[] { "id", "fullName", "email", "role" }, root.GetProperty("user").EnumerateObject().Select(p => p.Name));
        Assert.Equal(user.Id, root.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal(RoleNames.Student, root.GetProperty("user").GetProperty("role").GetString());

        var newRefreshToken = root.GetProperty("refreshToken").GetString()!;
        Assert.NotEqual(session.RefreshToken, newRefreshToken);
        Assert.Equal(32, Base64Url.DecodeFromChars(newRefreshToken).Length);
        Assert.DoesNotContain(Hash(newRefreshToken), body);
        Assert.DoesNotContain(user.SecurityStamp!, body);

        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(root.GetProperty("accessToken").GetString()!)).StatusCode);
    }

    [Theory]
    [InlineData(RoleNames.Student)]
    [InlineData(RoleNames.Instructor)]
    [InlineData(RoleNames.Admin)]
    public async Task NewAccessToken_CarriesTheCurrentClaims_AndLasts15Minutes(string role)
    {
        var user = await CreateUserAsync(role);
        var session = await LoginAsync(user);

        using var json = await RefreshJsonAsync(session.RefreshToken);

        var jwt = new JsonWebToken(json.RootElement.GetProperty("accessToken").GetString()!);
        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal(role, Assert.Single(jwt.Claims, c => c.Type == AuthClaimTypes.Role).Value);
        Assert.Equal(user.Email, Claim(jwt, AuthClaimTypes.Email));
        Assert.Equal(user.FullName, Claim(jwt, AuthClaimTypes.Name));
        Assert.Equal((await FindUserAsync(user.Id)).SecurityStamp, Claim(jwt, AuthClaimTypes.SecurityStamp));
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.ValidTo - jwt.IssuedAt);
        Assert.Equal(jwt.ValidTo, json.RootElement.GetProperty("accessTokenExpiresAt").GetDateTimeOffset().UtcDateTime);
    }

    [Fact]
    public async Task Rotation_RevokesTheOldToken_AndLinksAReplacementInTheSameFamily_ExpiringSevenDaysFromTheClock()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var session = await LoginAsync(user);
        var before = await TokenRowAsync(session.RefreshToken);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        JsonDocument json;
        using (factory.Clock.FreezeAt(now))
            json = await RefreshJsonAsync(session.RefreshToken);

        using (json)
        {
            var newRefreshToken = json.RootElement.GetProperty("refreshToken").GetString()!;
            var old = await TokenRowAsync(session.RefreshToken);
            var replacement = await TokenRowAsync(newRefreshToken);

            Assert.Equal(now, old.RevokedAt);
            Assert.Equal(RefreshTokenRevocationReasons.Rotated, old.RevokedReason);
            Assert.Equal(replacement.Id, old.ReplacedByTokenId);
            Assert.Equal(before.ExpiresAt, old.ExpiresAt);

            Assert.Equal(before.FamilyId, replacement.FamilyId);
            Assert.Equal(user.Id, replacement.UserId);
            Assert.Equal(Hash(newRefreshToken), replacement.TokenHash);
            Assert.Matches("^[0-9a-f]{64}$", replacement.TokenHash);
            Assert.Equal(now, replacement.CreatedAt);
            Assert.Equal(now.AddDays(7), replacement.ExpiresAt);
            Assert.Equal(now.AddDays(7), json.RootElement.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset());
            Assert.Null(replacement.RevokedAt);
            Assert.Null(replacement.ReplacedByTokenId);
        }
    }

    [Fact]
    public async Task Lifetime_IsRolling_EachRefreshGivesAnotherSevenDays()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var first = (await LoginAsync(user)).RefreshToken;

        string second;
        using (factory.Clock.Advance(TimeSpan.FromDays(6)))
            second = await RefreshTokenFromAsync(first);

        // 12 days after login: the login token would have expired, but the token from day 6 is still valid.
        using (factory.Clock.Advance(TimeSpan.FromDays(12)))
        {
            var third = await RefreshTokenFromAsync(second);
            Assert.Equal((await TokenRowAsync(second)).Id, (await TokenRowAsync(first)).ReplacedByTokenId);
            Assert.Equal((await TokenRowAsync(third)).Id, (await TokenRowAsync(second)).ReplacedByTokenId);
        }
    }

    // ---- Replay detection and family revocation (D4, D5) ----

    [Fact]
    public async Task ReusingARotatedToken_Returns401RefreshTokenReused_AndRevokesTheFamilysActiveTokens()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var first = (await LoginAsync(user)).RefreshToken;
        var second = await RefreshTokenFromAsync(first);

        await AssertProblemAsync(await RefreshAsync(first), HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenReused);

        // The newest token of the family is revoked too, and cannot be used any more.
        var newest = await TokenRowAsync(second);
        Assert.NotNull(newest.RevokedAt);
        Assert.Equal(RefreshTokenRevocationReasons.ReuseDetected, newest.RevokedReason);
        Assert.Null(newest.ReplacedByTokenId);
        await AssertProblemAsync(await RefreshAsync(second), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

        // The rotated token keeps its rotation record, and the replay created no new token.
        var old = await TokenRowAsync(first);
        Assert.Equal(RefreshTokenRevocationReasons.Rotated, old.RevokedReason);
        Assert.Equal(newest.Id, old.ReplacedByTokenId);
        Assert.Equal(2, (await FamilyAsync(old.FamilyId)).Count);
    }

    [Fact]
    public async Task ReuseDetection_EndsOnlyThatSession_NotTheUsersOtherSessions()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var phone = (await LoginAsync(user)).RefreshToken;
        var laptop = (await LoginAsync(user)).RefreshToken;
        await RefreshTokenFromAsync(phone);

        await AssertProblemAsync(await RefreshAsync(phone), HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenReused);

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(laptop)).StatusCode);
    }

    [Fact]
    public async Task TokenRevokedWithoutAReplacement_Returns401TokenInvalid_NotReuse()
    {
        // As logout and password change will revoke tokens (Step 4e): revoked, with no replacement.
        var user = await CreateUserAsync(RoleNames.Student);
        var token = (await LoginAsync(user)).RefreshToken;
        await RevokeAsync(token, "logout");

        await AssertProblemAsync(await RefreshAsync(token), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

        var row = await TokenRowAsync(token);
        Assert.Equal("logout", row.RevokedReason);
        Assert.Single(await FamilyAsync(row.FamilyId));
    }

    [Fact]
    public async Task TenConcurrentRefreshes_OfTheSameToken_AtMostOneSucceeds_AndTheFamilyIsRevoked()
    {
        // Three independent rounds, each with ten simultaneous requests carrying the same token.
        for (var round = 0; round < 3; round++)
        {
            var user = await CreateUserAsync(RoleNames.Student);
            var token = (await LoginAsync(user)).RefreshToken;
            var familyId = (await TokenRowAsync(token)).FamilyId;

            var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => RefreshAsync(token)));

            Assert.All(responses, r => Assert.True((int)r.StatusCode < 500, $"Unexpected {(int)r.StatusCode}"));
            var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            foreach (var loser in responses.Where(r => r != winner))
                await AssertProblemAsync(loser, HttpStatusCode.Unauthorized, ErrorCodes.RefreshTokenReused);

            // Exactly one replacement survived (the losers' replacements were rolled back), and the replays
            // revoked it, so the whole family is closed.
            var family = await FamilyAsync(familyId);
            Assert.Equal(2, family.Count);
            Assert.All(family, t => Assert.NotNull(t.RevokedAt));
            var original = family.Single(t => t.TokenHash == Hash(token));
            var replacement = family.Single(t => t.Id != original.Id);
            Assert.Equal(replacement.Id, original.ReplacedByTokenId);
            Assert.Equal(RefreshTokenRevocationReasons.ReuseDetected, replacement.RevokedReason);

            using var winnerJson = JsonDocument.Parse(await winner.Content.ReadAsStringAsync(Ct));
            var winnerToken = winnerJson.RootElement.GetProperty("refreshToken").GetString()!;
            Assert.Equal(replacement.TokenHash, Hash(winnerToken));
            await AssertProblemAsync(await RefreshAsync(winnerToken), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        }
    }

    // ---- Invalid tokens ----

    [Theory]
    [InlineData("random")]
    [InlineData("not a refresh token")]
    [InlineData("a")]
    [InlineData("max-length")]
    public async Task UnknownOrMalformedToken_Returns401TokenInvalid(string kind)
    {
        var token = kind switch
        {
            "random" => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
            "max-length" => new string('x', 256),
            _ => kind,
        };

        await AssertProblemAsync(await RefreshAsync(token), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
    }

    [Fact]
    public async Task ExpiredToken_Returns401TokenInvalid_AndIsNotRotated()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var token = (await LoginAsync(user)).RefreshToken;

        using (factory.Clock.Advance(TimeSpan.FromDays(7)))
            await AssertProblemAsync(await RefreshAsync(token), HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

        var row = await TokenRowAsync(token);
        Assert.Null(row.RevokedAt);
        Assert.Single(await FamilyAsync(row.FamilyId));
    }

    [Fact]
    public async Task TokenJustBeforeItsExpiry_StillRefreshes()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var token = (await LoginAsync(user)).RefreshToken;

        using (factory.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1)))
            Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(token)).StatusCode);
    }

    // ---- Account rules ----

    [Fact]
    public async Task DeactivatedAccount_Gets403_AndTheTokenIsNotConsumed()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var token = (await LoginAsync(user)).RefreshToken;
        await UpdateUserAsync(user.Id, u => u.AccountStatus = AccountStatus.Deactivated);

        await AssertProblemAsync(await RefreshAsync(token), HttpStatusCode.Forbidden, ErrorCodes.AccountDeactivated);

        var row = await TokenRowAsync(token);
        Assert.Null(row.RevokedAt);
        Assert.Single(await FamilyAsync(row.FamilyId));
    }

    [Fact]
    public async Task ExpiredTemporaryPassword_Gets403_WhileAValidOneStillRefreshes()
    {
        var instructor = await CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddHours(72));
        var token = (await LoginAsync(instructor)).RefreshToken;

        string next;
        using (factory.Clock.Advance(TimeSpan.FromHours(1)))
        {
            using var json = await RefreshJsonAsync(token);
            Assert.True(json.RootElement.GetProperty("mustChangePassword").GetBoolean());
            next = json.RootElement.GetProperty("refreshToken").GetString()!;
        }

        using (factory.Clock.Advance(TimeSpan.FromHours(73)))
            await AssertProblemAsync(await RefreshAsync(next), HttpStatusCode.Forbidden, ErrorCodes.TemporaryPasswordExpired);

        Assert.Null((await TokenRowAsync(next)).RevokedAt);
    }

    [Fact]
    public async Task ChangedSecurityStamp_InvalidatesOldAccessTokens_AndRefreshIssuesTokensWithTheNewStamp()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var session = await LoginAsync(user);
        await WithUserManagerAsync(async userManager =>
            Assert.True((await userManager.UpdateSecurityStampAsync((await userManager.FindByIdAsync(user.Id.ToString()))!)).Succeeded));

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(session.AccessToken)).StatusCode);

        using var json = await RefreshJsonAsync(session.RefreshToken);
        var newAccessToken = json.RootElement.GetProperty("accessToken").GetString()!;
        var newStamp = (await FindUserAsync(user.Id)).SecurityStamp;
        Assert.NotEqual(user.SecurityStamp, newStamp);
        Assert.Equal(newStamp, Claim(new JsonWebToken(newAccessToken), AuthClaimTypes.SecurityStamp));
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(newAccessToken)).StatusCode);
    }

    [Fact]
    public async Task AccountWithoutARole_Fails_WithoutConsumingTheToken()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var token = (await LoginAsync(user)).RefreshToken;
        await WithUserManagerAsync(async userManager =>
            Assert.True((await userManager.RemoveFromRoleAsync((await userManager.FindByIdAsync(user.Id.ToString()))!, RoleNames.Student)).Succeeded));

        var response = await RefreshAsync(token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var row = await TokenRowAsync(token);
        Assert.Null(row.RevokedAt);
        Assert.Single(await FamilyAsync(row.FamilyId));
    }

    // ---- Request validation, anonymity and limits ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-long")]
    public async Task MissingOrOverlongToken_Returns400ValidationFailed(string? token)
    {
        if (token == "too-long")
            token = new string('x', 257);

        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = token }, Ct);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("refreshToken", out _));
    }

    [Fact]
    public async Task MalformedJson_Returns400ValidationFailed()
    {
        var response = await factory.CreateClient().PostAsync("/api/auth/refresh",
            new StringContent("{ not json", Encoding.UTF8, "application/json"), Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("expired")]
    public async Task Refresh_IsAnonymous_EvenWithAnInvalidOrExpiredAccessToken(string kind)
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var token = (await LoginAsync(user)).RefreshToken;
        var bearer = kind == "garbage"
            ? "not-a-jwt"
            : new JwtTokenService(Options.Create(factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value),
                    new FixedClock(DateTimeOffset.UtcNow.AddHours(-1)))
                .CreateAccessToken(await FindUserAsync(user.Id), RoleNames.Student).Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken = token }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task MoreThanThirtyRequestsPerMinute_FromOneIp_Returns429()
    {
        const string ip = "203.0.113.40";
        var unknown = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(unknown, ip)).StatusCode);

        var limited = await RefreshAsync(unknown, ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(unknown, "203.0.113.41")).StatusCode);
    }

    [Fact]
    public void BodyLimits_Are16KBOnRefreshAndLogin_AndNoneOnRegister()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        long? Limit(string route) =>
            endpoints.Single(e => e.RoutePattern.RawText == route).Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;

        Assert.Equal(16 * 1024, Limit("api/auth/refresh"));
        Assert.Equal(16 * 1024, Limit("api/auth/login"));
        Assert.Null(Limit("api/auth/register"));
    }

    // The in-memory test server does not enforce body limits, so this runs the API on real Kestrel.
    [Fact]
    public async Task OnKestrel_RefreshBodyOver16KB_Returns413_AndBodyUnder16KB_IsProcessed()
    {
        await using var kestrel = new KestrelApiFactory();
        kestrel.UseKestrel(0);
        kestrel.StartServer();
        var client = kestrel.CreateClient();

        var tooLarge = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = new string('x', 17 * 1024) }, Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal("application/problem+json", tooLarge.Content.Headers.ContentType?.MediaType);

        // Under the limit, the body is read and validated normally (the token is longer than 256 characters).
        var underLimit = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = new string('x', 15 * 1024) }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, underLimit.StatusCode);
    }

    // ---- Helpers ----

    private sealed record Session(string AccessToken, string RefreshToken);

    private async Task<ApplicationUser> CreateUserAsync(
        string role, bool mustChangePassword = false, DateTimeOffset? temporaryPasswordExpiresAt = null)
    {
        var email = $"refresh.{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = "Refresh Test",
            AccountStatus = AccountStatus.Active,
            MustChangePassword = mustChangePassword,
            TemporaryPasswordExpiresAt = temporaryPasswordExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await WithUserManagerAsync(async userManager =>
        {
            Assert.True((await userManager.CreateAsync(user, Password)).Succeeded);
            Assert.True((await userManager.AddToRoleAsync(user, role)).Succeeded);
        });
        return user;
    }

    private async Task<Session> LoginAsync(ApplicationUser user)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return new Session(
            json.RootElement.GetProperty("accessToken").GetString()!,
            json.RootElement.GetProperty("refreshToken").GetString()!);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken, string? clientIp = null)
    {
        var client = factory.CreateClient();
        if (clientIp is not null)
            client.DefaultRequestHeaders.Add(ApiFactory.ClientIpHeader, clientIp);
        return client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken }, Ct);
    }

    private async Task<JsonDocument> RefreshJsonAsync(string refreshToken)
    {
        var response = await RefreshAsync(refreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<string> RefreshTokenFromAsync(string refreshToken)
    {
        using var json = await RefreshJsonAsync(refreshToken);
        return json.RootElement.GetProperty("refreshToken").GetString()!;
    }

    private Task<HttpResponseMessage> GetMeAsync(string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return client.GetAsync("/api/auth/me", Ct);
    }

    private async Task WithUserManagerAsync(Func<UserManager<ApplicationUser>, Task> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    private Task UpdateUserAsync(Guid userId, Action<ApplicationUser> change) =>
        WithUserManagerAsync(async userManager =>
        {
            var user = (await userManager.FindByIdAsync(userId.ToString()))!;
            change(user);
            Assert.True((await userManager.UpdateAsync(user)).Succeeded);
        });

    private async Task<ApplicationUser> FindUserAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == userId, Ct);
    }

    private async Task<RefreshToken> TokenRowAsync(string refreshToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var hash = Hash(refreshToken);
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.TokenHash == hash, Ct);
    }

    private async Task<List<RefreshToken>> FamilyAsync(Guid familyId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens.AsNoTracking()
            .Where(t => t.FamilyId == familyId).ToListAsync(Ct);
    }

    // Marks a token revoked the way logout and password change will: no replacement link.
    private async Task RevokeAsync(string refreshToken, string reason)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var hash = Hash(refreshToken);
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens
            .Where(t => t.TokenHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow).SetProperty(t => t.RevokedReason, reason), Ct);
    }

    private async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    private static string Hash(string refreshToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    private static string Claim(JsonWebToken jwt, string type) => jwt.Claims.Single(c => c.Type == type).Value;
}
