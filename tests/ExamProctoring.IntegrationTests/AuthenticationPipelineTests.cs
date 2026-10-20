using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Identity;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ExamProctoring.IntegrationTests;

// JWT authentication, the account gate and the authorization policies (§6.1, §6.7), plus GET /api/auth/me.
// Users are created directly with UserManager and tokens with the host's ITokenService (login comes in Step 4c).
[Collection(ApiCollection.Name)]
public sealed class AuthenticationPipelineTests(ApiFactory factory)
{
    private const string Me = "/api/auth/me";
    private const string Probe = "/test-only/authorization";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- GET /api/auth/me ----

    [Theory]
    [InlineData(RoleNames.Student)]
    [InlineData(RoleNames.Instructor)]
    [InlineData(RoleNames.Admin)]
    public async Task Me_WithAValidToken_ReturnsOnlyTheSafeFields_AndTheTokenRole(string role)
    {
        var user = await CreateUserAsync(role);

        var response = await GetAsync(Me, TokenFor(user, role));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            new[] { "id", "fullName", "email", "role", "mustChangePassword" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(user.Id, json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(user.FullName, json.RootElement.GetProperty("fullName").GetString());
        Assert.Equal(user.Email, json.RootElement.GetProperty("email").GetString());
        Assert.Equal(role, json.RootElement.GetProperty("role").GetString());
        Assert.False(json.RootElement.GetProperty("mustChangePassword").GetBoolean());
        Assert.DoesNotContain(user.SecurityStamp!, body);
    }

    [Fact]
    public async Task Me_WithoutAToken_Returns401TokenInvalid() => await AssertTokenInvalidAsync(await GetAsync(Me));

    [Theory]
    [InlineData("Bearer", "not-a-jwt")]
    [InlineData("Bearer", "a.b.c")]
    [InlineData("Bearer", "")]
    [InlineData("Basic", "dXNlcjpwYXNzd29yZA==")]
    public async Task Me_WithAMalformedTokenOrAnotherScheme_Returns401TokenInvalid(string scheme, string token) =>
        await AssertTokenInvalidAsync(await GetAsync(Me, token, scheme));

    [Fact]
    public async Task ExpiredToken_Returns401TokenInvalid()
    {
        var user = await CreateUserAsync(RoleNames.Student);

        // Issued 16 minutes ago, so it expired a minute ago (beyond the 30 s clock skew).
        var token = TokenServiceWith(_ => { }, DateTimeOffset.UtcNow.AddMinutes(-16)).CreateAccessToken(user, RoleNames.Student).Token;

        await AssertTokenInvalidAsync(await GetAsync(Me, token));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("key")]
    public async Task TokenWithAnotherIssuerAudienceOrKey_Returns401TokenInvalid(string changed)
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var service = TokenServiceWith(options =>
        {
            switch (changed)
            {
                case "issuer": options.Issuer = "someone-else"; break;
                case "audience": options.Audience = "another-app"; break;
                default: options.SigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)); break;
            }
        });

        await AssertTokenInvalidAsync(await GetAsync(Me, service.CreateAccessToken(user, RoleNames.Student).Token));
    }

    // ---- The account gate ----

    [Fact]
    public async Task TokenForAnAccountThatDoesNotExist_Returns401TokenInvalid()
    {
        var ghost = new ApplicationUser
        {
            Id = Guid.NewGuid(), Email = "ghost@example.com", FullName = "Ghost", SecurityStamp = Guid.NewGuid().ToString("N"),
        };

        await AssertTokenInvalidAsync(await GetAsync(Me, TokenFor(ghost, RoleNames.Student)));
    }

    [Fact]
    public async Task DeactivatingAnAccount_RejectsItsTokenOnTheNextRequest_AndReactivatingRestoresAccess()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var token = TokenFor(user, RoleNames.Student);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Me, token)).StatusCode);

        await UpdateUserAsync(user.Id, u => u.AccountStatus = AccountStatus.Deactivated);
        await AssertTokenInvalidAsync(await GetAsync(Me, token));

        await UpdateUserAsync(user.Id, u => u.AccountStatus = AccountStatus.Active);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Me, token)).StatusCode);
    }

    [Fact]
    public async Task ChangingTheSecurityStamp_RejectsOlderTokens_AndANewTokenWorks()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var oldToken = TokenFor(user, RoleNames.Student);

        ApplicationUser updated;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            updated = (await userManager.FindByIdAsync(user.Id.ToString()))!;
            Assert.True((await userManager.UpdateSecurityStampAsync(updated)).Succeeded);
        }

        await AssertTokenInvalidAsync(await GetAsync(Me, oldToken));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Me, TokenFor(updated, RoleNames.Student))).StatusCode);
    }

    [Fact]
    public async Task SignedTokenWithoutASecurityStamp_Returns401TokenInvalid()
    {
        var user = await CreateUserAsync(RoleNames.Student);
        var options = HostOptions();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>
            {
                [AuthClaimTypes.Subject] = user.Id.ToString(),
                [AuthClaimTypes.Role] = RoleNames.Student,
            },
            SigningCredentials = new SigningCredentials(JwtTokenValidation.CreateSigningKey(options), SecurityAlgorithms.HmacSha256),
        });

        await AssertTokenInvalidAsync(await GetAsync(Me, token));
    }

    // ---- Forced password change (§6.3) ----

    [Fact]
    public async Task PendingPasswordChange_AllowsMe_ButBlocksOtherEndpoints_UntilTheFlagIsCleared()
    {
        var instructor = await CreateUserAsync(RoleNames.Instructor, mustChangePassword: true);
        var token = TokenFor(instructor, RoleNames.Instructor);

        var me = await GetAsync(Me, token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using (var json = JsonDocument.Parse(await me.Content.ReadAsStringAsync(Ct)))
            Assert.True(json.RootElement.GetProperty("mustChangePassword").GetBoolean());

        await AssertProblemAsync(await GetAsync($"{Probe}/fallback", token), HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired);
        await AssertProblemAsync(await GetAsync($"{Probe}/instructor", token), HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired);

        // Clearing the flag (as change-password will) lifts the restriction for the same token.
        await UpdateUserAsync(instructor.Id, u => u.MustChangePassword = false);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync($"{Probe}/instructor", token)).StatusCode);
    }

    // ---- Policies ----

    [Theory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Instructor)]
    [InlineData(RoleNames.Student)]
    public async Task RolePolicies_AllowOnlyTheirRole(string role)
    {
        var user = await CreateUserAsync(role);
        var token = TokenFor(user, role);

        foreach (var (path, policyRole) in new[]
                 {
                     ("admin", RoleNames.Admin), ("instructor", RoleNames.Instructor), ("student", RoleNames.Student),
                 })
        {
            var response = await GetAsync($"{Probe}/{path}", token);
            if (policyRole == role)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            }
        }

        Assert.Equal(HttpStatusCode.OK, (await GetAsync($"{Probe}/fallback", token)).StatusCode);
    }

    [Fact]
    public async Task FallbackPolicy_RequiresAToken() => await AssertTokenInvalidAsync(await GetAsync($"{Probe}/fallback"));

    // ---- Public endpoints ----

    [Theory]
    [InlineData("none")]
    [InlineData("garbage")]
    [InlineData("expired")]
    public async Task PublicEndpoints_StayAnonymous_EvenWithAnInvalidToken(string tokenKind)
    {
        string? token = tokenKind switch
        {
            "garbage" => "not-a-jwt",
            "expired" => TokenServiceWith(_ => { }, DateTimeOffset.UtcNow.AddHours(-1))
                .CreateAccessToken(await CreateUserAsync(RoleNames.Student), RoleNames.Student).Token,
            _ => null,
        };

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/health", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/openapi/v1.json", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/swagger/index.html", token)).StatusCode);

        var email = $"public.{Guid.NewGuid():N}@example.com";
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new { fullName = "Public Test", email, password = "Abcdefg1", confirmPassword = "Abcdefg1" }),
        };
        if (token is not null)
            register.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        Assert.Equal(HttpStatusCode.Created, (await factory.CreateClient().SendAsync(register, Ct)).StatusCode);
    }

    [Fact]
    public void AnonymousEndpoints_AreExactlyTheDocumentedOnes()
    {
        var anonymous = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!.TrimStart('/'))
            .Order();

        // §6.7: register, login, refresh and the health check, plus the Development OpenAPI document.
        Assert.Equal(
            new[] { "api/auth/login", "api/auth/refresh", "api/auth/register", "health", "openapi/{documentName}.json" },
            anonymous);
    }

    [Fact]
    public async Task UnknownRoute_StillReturns404_WithAValidToken()
    {
        var user = await CreateUserAsync(RoleNames.Student);

        var response = await GetAsync("/api/does-not-exist", TokenFor(user, RoleNames.Student));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- Helpers ----

    private async Task<ApplicationUser> CreateUserAsync(string role, bool mustChangePassword = false)
    {
        var email = $"{role.ToLowerInvariant()}.{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = $"Test {role}",
            AccountStatus = AccountStatus.Active,
            MustChangePassword = mustChangePassword,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True((await userManager.CreateAsync(user, "Abcdefg1")).Succeeded);
        Assert.True((await userManager.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    private async Task UpdateUserAsync(Guid userId, Action<ApplicationUser> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await userManager.FindByIdAsync(userId.ToString()))!;
        change(user);
        Assert.True((await userManager.UpdateAsync(user)).Succeeded);
    }

    private string TokenFor(ApplicationUser user, string role) =>
        factory.Services.GetRequiredService<ITokenService>().CreateAccessToken(user, role).Token;

    private JwtOptions HostOptions() => factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

    // A token service with the host's settings, changed as given, and optionally a clock stopped at another moment.
    private JwtTokenService TokenServiceWith(Action<JwtOptions> change, DateTimeOffset? now = null)
    {
        var host = HostOptions();
        var options = new JwtOptions
        {
            Issuer = host.Issuer,
            Audience = host.Audience,
            SigningKey = host.SigningKey,
            AccessTokenMinutes = host.AccessTokenMinutes,
            RefreshTokenDays = host.RefreshTokenDays,
        };
        change(options);
        return new JwtTokenService(Options.Create(options), new FixedClock(now ?? DateTimeOffset.UtcNow));
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? token = null, string scheme = "Bearer")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {token}");
        return await factory.CreateClient().SendAsync(request, Ct);
    }

    private async Task AssertTokenInvalidAsync(HttpResponseMessage response)
    {
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    private async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }
}
