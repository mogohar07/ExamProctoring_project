using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Persistence;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ExamProctoring.IntegrationTests;

// POST /api/auth/login (§6.4, §6.5, §6.6, D3, D7), covering the login items of §6.10.
[Collection(ApiCollection.Name)]
public sealed class LoginTests(ApiFactory factory)
{
    private const string Password = "Abcdefg1";
    private const string WrongPassword = "Wrong-Password1";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Success ----

    [Fact]
    public async Task ValidLogin_Returns200_WithTheAgreedResponse()
    {
        var user = await CreateUserAsync(RoleNames.Student);

        var response = await LoginAsync(user.Email!, Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(
            new[] { "tokenType", "accessToken", "accessTokenExpiresAt", "refreshToken", "refreshTokenExpiresAt", "mustChangePassword", "user" },
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Bearer", root.GetProperty("tokenType").GetString());
        Assert.False(root.GetProperty("mustChangePassword").GetBoolean());
        var userJson = root.GetProperty("user");
        Assert.Equal(new[] { "id", "fullName", "email", "role" }, userJson.EnumerateObject().Select(p => p.Name));
        Assert.Equal(user.Id, userJson.GetProperty("id").GetGuid());
        Assert.Equal(user.FullName, userJson.GetProperty("fullName").GetString());
        Assert.Equal(user.Email, userJson.GetProperty("email").GetString());
        Assert.Equal(RoleNames.Student, userJson.GetProperty("role").GetString());

        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain(user.PasswordHash!, body);
        Assert.DoesNotContain(user.SecurityStamp!, body);

        // The access token works on a protected endpoint.
        var me = await GetMeAsync(root.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Theory]
    [InlineData(RoleNames.Student)]
    [InlineData(RoleNames.Instructor)]
    [InlineData(RoleNames.Admin)]
    public async Task AccessToken_CarriesTheUsersClaims_AndLasts15Minutes(string role)
    {
        var user = await CreateUserAsync(role);

        using var json = await LoginJsonAsync(user.Email!, Password);

        var jwt = new JsonWebToken(json.RootElement.GetProperty("accessToken").GetString()!);
        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal(user.Email, Claim(jwt, AuthClaimTypes.Email));
        Assert.Equal(user.FullName, Claim(jwt, AuthClaimTypes.Name));
        Assert.Equal(role, Assert.Single(jwt.Claims, c => c.Type == AuthClaimTypes.Role).Value);
        Assert.Equal(user.SecurityStamp, Claim(jwt, AuthClaimTypes.SecurityStamp));
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.ValidTo - jwt.IssuedAt);
        Assert.Equal(jwt.ValidTo, json.RootElement.GetProperty("accessTokenExpiresAt").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(role, json.RootElement.GetProperty("user").GetProperty("role").GetString());
    }

    [Fact]
    public async Task RefreshToken_IsStoredOnlyAsItsHash_AndLasts7Days()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var before = DateTimeOffset.UtcNow;

        using var json = await LoginJsonAsync(user.Email!, Password);

        var refreshToken = json.RootElement.GetProperty("refreshToken").GetString()!;
        var expiresAt = json.RootElement.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset();
        var row = Assert.Single(await RefreshTokensOfAsync(user.Id));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken))), row.TokenHash);
        Assert.NotEqual(refreshToken, row.TokenHash);
        Assert.NotEqual(Guid.Empty, row.FamilyId);
        Assert.Null(row.RevokedAt);
        Assert.Null(row.ReplacedByTokenId);
        Assert.Equal(expiresAt, row.ExpiresAt);
        Assert.InRange(row.ExpiresAt, before.AddDays(7), DateTimeOffset.UtcNow.AddDays(7));
    }

    [Fact]
    public async Task EachLogin_StartsItsOwnTokenFamily()
    {
        var user = await CreateUserAsync(RoleNames.Student);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email!, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email!, Password)).StatusCode);

        var rows = await RefreshTokensOfAsync(user.Id);
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].FamilyId, rows[1].FamilyId);
    }

    [Fact]
    public async Task Login_SetsLastLoginAt()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var before = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email!, Password)).StatusCode);

        var stored = await FindUserAsync(user.Id);
        Assert.NotNull(stored.LastLoginAt);
        Assert.InRange(stored.LastLoginAt.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task EmailMatching_IgnoresLetterCaseAndSurroundingSpaces()
    {
        var user = await CreateUserAsync(RoleNames.Student);

        var response = await LoginAsync($"  {user.Email!.ToUpperInvariant()}  ", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NewlyRegisteredStudent_CanLogInImmediately()
    {
        var email = $"new.{Guid.NewGuid():N}@example.com";
        var register = await factory.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { fullName = "New Student", email, password = Password, confirmPassword = Password }, Ct);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        using var json = await LoginJsonAsync(email, Password);

        Assert.Equal(RoleNames.Student, json.RootElement.GetProperty("user").GetProperty("role").GetString());
        Assert.False(json.RootElement.GetProperty("mustChangePassword").GetBoolean());
    }

    // ---- Invalid credentials ----

    [Fact]
    public async Task UnknownEmailAndWrongPassword_GetIdenticalResponses()
    {
        var user = await CreateUserAsync(RoleNames.Student);

        var unknown = await LoginAsync($"nobody.{Guid.NewGuid():N}@example.com", Password);
        var wrong = await LoginAsync(user.Email!, WrongPassword);

        var unknownProblem = await AssertProblemAsync(unknown, HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        var wrongProblem = await AssertProblemAsync(wrong, HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        Assert.Equal(WithoutTraceId(unknownProblem), WithoutTraceId(wrongProblem));
    }

    [Fact]
    public async Task MalformedEmail_IsAnUnknownAccount_NotAValidationError() =>
        await AssertProblemAsync(await LoginAsync("not-an-email", Password), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);

    [Theory]
    [InlineData(null, Password, "email")]
    [InlineData("   ", Password, "email")]
    [InlineData("someone@example.com", null, "password")]
    [InlineData("someone@example.com", "", "password")]
    public async Task MissingFields_Return400ValidationFailed(string? email, string? password, string field)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password }, Ct);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task MalformedJson_Returns400ValidationFailed()
    {
        var response = await factory.CreateClient().PostAsync("/api/auth/login",
            new StringContent("{ not json", Encoding.UTF8, "application/json"), Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    // ---- Lockout (§6.5: 5 failures → 15 minutes) ----

    [Fact]
    public async Task FifthWrongPassword_LocksTheAccount_AndEvenTheRightPasswordIsRefused()
    {
        var user = await CreateUserAsync(RoleNames.Student);

        for (var attempt = 1; attempt <= 4; attempt++)
            await AssertProblemAsync(await LoginAsync(user.Email!, WrongPassword), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);

        await AssertProblemAsync(await LoginAsync(user.Email!, WrongPassword), HttpStatusCode.Unauthorized, ErrorCodes.AccountLocked);
        await AssertProblemAsync(await LoginAsync(user.Email!, Password), HttpStatusCode.Unauthorized, ErrorCodes.AccountLocked);

        var stored = await FindUserAsync(user.Id);
        Assert.NotNull(stored.LockoutEnd);
        Assert.InRange(stored.LockoutEnd.Value, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(15).AddSeconds(5));
        Assert.Empty(await RefreshTokensOfAsync(user.Id));
    }

    [Fact]
    public async Task AfterTheLockoutEnds_TheRightPasswordWorks()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        await WithUserManagerAsync(async userManager =>
        {
            var stored = (await userManager.FindByIdAsync(user.Id.ToString()))!;
            Assert.True((await userManager.SetLockoutEndDateAsync(stored, DateTimeOffset.UtcNow.AddMinutes(-1))).Succeeded);
        });

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email!, Password)).StatusCode);
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsTheFailureCount()
    {
        var user = await CreateUserAsync(RoleNames.Student);

        for (var attempt = 1; attempt <= 3; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(user.Email!, WrongPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email!, Password)).StatusCode);
        Assert.Equal(0, (await FindUserAsync(user.Id)).AccessFailedCount);

        // Four more failures are still below the limit of five.
        for (var attempt = 1; attempt <= 4; attempt++)
            await AssertProblemAsync(await LoginAsync(user.Email!, WrongPassword), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
    }

    // ---- Account state, revealed only after a correct password ----

    [Fact]
    public async Task DeactivatedAccount_Gets403_OnlyWithTheRightPassword()
    {
        var user = await CreateUserAsync(RoleNames.Student, status: AccountStatus.Deactivated);

        await AssertProblemAsync(await LoginAsync(user.Email!, WrongPassword), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        await AssertProblemAsync(await LoginAsync(user.Email!, Password), HttpStatusCode.Forbidden, ErrorCodes.AccountDeactivated);
        Assert.Empty(await RefreshTokensOfAsync(user.Id));
    }

    [Fact]
    public async Task ValidTemporaryPassword_LogsIn_WithMustChangePassword()
    {
        var instructor = await CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddHours(71));

        using var json = await LoginJsonAsync(instructor.Email!, Password);

        Assert.True(json.RootElement.GetProperty("mustChangePassword").GetBoolean());

        // Only the endpoints allowed during a forced password change work with this token (§6.3).
        var me = await GetMeAsync(json.RootElement.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task ExpiredTemporaryPassword_Gets403_OnlyWithTheRightPassword()
    {
        var instructor = await CreateUserAsync(RoleNames.Instructor, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await AssertProblemAsync(await LoginAsync(instructor.Email!, WrongPassword), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        await AssertProblemAsync(await LoginAsync(instructor.Email!, Password), HttpStatusCode.Forbidden, ErrorCodes.TemporaryPasswordExpired);
        Assert.Empty(await RefreshTokensOfAsync(instructor.Id));
    }

    [Fact]
    public async Task AccountWithoutARole_IsRefused_WithoutIssuingTokens()
    {
        var user = await CreateUserAsync(role: null);

        var response = await LoginAsync(user.Email!, Password);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await RefreshTokensOfAsync(user.Id));
    }

    // ---- Limits and anonymous access ----

    [Fact]
    public async Task MoreThanTenRequestsPerMinute_FromOneIp_Returns429()
    {
        const string ip = "203.0.113.20";

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync("nobody@example.com", WrongPassword, ip)).StatusCode);

        var limited = await LoginAsync("nobody@example.com", WrongPassword, ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync("nobody@example.com", WrongPassword, "203.0.113.21")).StatusCode);
    }

    [Fact]
    public async Task LoginEndpoint_Has16KBBodyLimit_AndRegisterHasNone()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var login = endpoints.Single(e => e.RoutePattern.RawText == "api/auth/login");
        var register = endpoints.Single(e => e.RoutePattern.RawText == "api/auth/register");

        Assert.Equal(16 * 1024, login.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
        Assert.Null(register.Metadata.GetMetadata<IRequestSizeLimitMetadata>());
    }

    // The in-memory test server does not enforce body limits, so this test runs the API on real Kestrel.
    [Fact]
    public async Task OnKestrel_BodyOver16KB_Returns413ProblemDetails_AndBodyUnder16KB_IsProcessed()
    {
        await using var kestrel = new KestrelApiFactory();
        kestrel.UseKestrel(0);
        kestrel.StartServer();
        var client = kestrel.CreateClient();

        var tooLarge = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "someone@example.com", password = new string('a', 17 * 1024) }, Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal("application/problem+json", tooLarge.Content.Headers.ContentType?.MediaType);

        var underLimit = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "someone@example.com", password = new string('a', 15 * 1024) }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, underLimit.StatusCode);
    }

    [Fact]
    public async Task Register_HasNoBodyLimit_OfItsOwn()
    {
        // 20 KB is refused by login; sign-up still reads it and answers with its normal validation error.
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            fullName = new string('a', 20 * 1024),
            email = $"big.{Guid.NewGuid():N}@example.com",
            password = Password,
            confirmPassword = Password,
        }, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Login_IsAnonymous_EvenWithAnInvalidBearerToken()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = user.Email, password = Password }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-jwt");

        var response = await factory.CreateClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Helpers ----

    private async Task<ApplicationUser> CreateUserAsync(
        string? role,
        AccountStatus status = AccountStatus.Active,
        bool mustChangePassword = false,
        DateTimeOffset? temporaryPasswordExpiresAt = null)
    {
        var email = $"{role?.ToLowerInvariant() ?? "norole"}.{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = "Login Test",
            AccountStatus = status,
            MustChangePassword = mustChangePassword,
            TemporaryPasswordExpiresAt = temporaryPasswordExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await WithUserManagerAsync(async userManager =>
        {
            Assert.True((await userManager.CreateAsync(user, Password)).Succeeded);
            if (role is not null)
                Assert.True((await userManager.AddToRoleAsync(user, role)).Succeeded);
        });
        return user;
    }

    private async Task WithUserManagerAsync(Func<UserManager<ApplicationUser>, Task> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    private async Task<ApplicationUser> FindUserAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Ct);
    }

    private async Task<List<RefreshToken>> RefreshTokensOfAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RefreshTokens.AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync(Ct);
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password, string? clientIp = null)
    {
        var client = factory.CreateClient();
        if (clientIp is not null)
            client.DefaultRequestHeaders.Add(ApiFactory.ClientIpHeader, clientIp);
        return client.PostAsJsonAsync("/api/auth/login", new { email, password }, Ct);
    }

    private async Task<JsonDocument> LoginJsonAsync(string email, string password)
    {
        var response = await LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private Task<HttpResponseMessage> GetMeAsync(string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return client.GetAsync("/api/auth/me", Ct);
    }

    private async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    private static string WithoutTraceId(JsonDocument problem) =>
        string.Join(";", problem.RootElement.EnumerateObject().Where(p => p.Name != "traceId").Select(p => $"{p.Name}={p.Value}"));

    private static string Claim(JsonWebToken jwt, string type) => jwt.Claims.Single(c => c.Type == type).Value;

}
