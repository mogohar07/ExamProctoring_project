using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExamProctoring.IntegrationTests.TestSupport;

public sealed record Session(string AccessToken, string RefreshToken);

// Calls to the auth endpoints and direct reads of users and refresh tokens, shared by the session tests.
public sealed class AuthApi(WebApplicationFactory<Program> host)
{
    public const string Password = "Abcdefg1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<ApplicationUser> CreateUserAsync(
        string role, bool mustChangePassword = false, DateTimeOffset? temporaryPasswordExpiresAt = null)
    {
        var email = $"session.{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = "Session Test",
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

    public async Task<Session> LoginAsync(ApplicationUser user, string password = Password)
    {
        var response = await PostLoginAsync(user.Email!, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SessionFromAsync(response);
    }

    public Task<HttpResponseMessage> PostLoginAsync(string email, string password) =>
        PostJsonAsync("/api/auth/login", new { email, password });

    public Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        PostJsonAsync("/api/auth/refresh", new { refreshToken });

    public Task<HttpResponseMessage> LogoutAsync(string? accessToken, string? refreshToken) =>
        PostJsonAsync("/api/auth/logout", new { refreshToken }, accessToken);

    public Task<HttpResponseMessage> ChangePasswordAsync(string? accessToken, string? currentPassword, string? newPassword) =>
        PostJsonAsync("/api/auth/change-password", new { currentPassword, newPassword }, accessToken);

    public Task<HttpResponseMessage> GetAsync(string path, string accessToken)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.GetAsync(path, Ct);
    }

    public Task<HttpResponseMessage> PostJsonAsync(string path, object body, string? accessToken = null)
    {
        var client = host.CreateClient();
        if (accessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.PostAsJsonAsync(path, body, Ct);
    }

    public Task<HttpResponseMessage> PostRawJsonAsync(string path, string json, string? accessToken = null)
    {
        var client = host.CreateClient();
        if (accessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), Ct);
    }

    public static async Task<Session> SessionFromAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return new Session(
            json.RootElement.GetProperty("accessToken").GetString()!,
            json.RootElement.GetProperty("refreshToken").GetString()!);
    }

    public async Task WithUserManagerAsync(Func<UserManager<ApplicationUser>, Task> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    public Task UpdateUserAsync(Guid userId, Action<ApplicationUser> change) =>
        WithUserManagerAsync(async userManager =>
        {
            var user = (await userManager.FindByIdAsync(userId.ToString()))!;
            change(user);
            Assert.True((await userManager.UpdateAsync(user)).Succeeded);
        });

    public async Task<ApplicationUser> FindUserAsync(Guid userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .SingleAsync(u => u.Id == userId, Ct);
    }

    public async Task<RefreshToken> TokenRowAsync(string refreshToken)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var hash = Hash(refreshToken);
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens.AsNoTracking()
            .SingleAsync(t => t.TokenHash == hash, Ct);
    }

    public async Task<List<RefreshToken>> TokensOfUserAsync(Guid userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync(Ct);
    }

    public static async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem;
    }

    public static string Hash(string refreshToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
}
