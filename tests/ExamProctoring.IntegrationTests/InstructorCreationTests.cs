using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExamProctoring.Application.Accounts;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static ExamProctoring.IntegrationTests.TestSupport.AuthApi;

namespace ExamProctoring.IntegrationTests;

// POST /api/admin/instructors (§6.4, D9, D18, D21). The admin is the one seeded at startup (D20).
[Collection(ApiCollection.Name)]
public sealed partial class InstructorCreationTests(ApiFactory factory)
{
    private const string Path = "/api/admin/instructors";
    private const string NewPassword = "Newpassw0rd";

    private readonly AuthApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Success ----

    [Fact]
    public async Task Success_Returns201_WithTheTemporaryPasswordOnce_AndStoresTheInstructor()
    {
        var admin = await AdminSessionAsync();
        var email = UniqueEmail();
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        HttpResponseMessage response;
        using (factory.Clock.FreezeAt(now))
            response = await _api.PostJsonAsync(Path, new { fullName = "  Dr. Mona Hassan  ", email = $"  {email}  " }, admin.AccessToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(
            new[] { "id", "fullName", "email", "role", "temporaryPassword", "temporaryPasswordExpiresAt" },
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Dr. Mona Hassan", root.GetProperty("fullName").GetString());
        Assert.Equal(email, root.GetProperty("email").GetString());
        Assert.Equal(RoleNames.Instructor, root.GetProperty("role").GetString());
        Assert.Equal(now.AddHours(72), root.GetProperty("temporaryPasswordExpiresAt").GetDateTimeOffset());
        var temporaryPassword = root.GetProperty("temporaryPassword").GetString()!;
        Assert.Matches(TemporaryPasswordFormat(), temporaryPassword);

        var stored = await _api.FindUserAsync(root.GetProperty("id").GetGuid());
        Assert.Equal(email, stored.Email);
        Assert.Equal(email, stored.UserName);
        Assert.Equal("Dr. Mona Hassan", stored.FullName);
        Assert.Equal(AccountStatus.Active, stored.AccountStatus);
        Assert.True(stored.MustChangePassword);
        Assert.Equal(now.AddHours(72), stored.TemporaryPasswordExpiresAt);
        Assert.Equal(now, stored.CreatedAt);
        Assert.Equal(await SeedAdminIdAsync(), stored.CreatedById);
        Assert.True(stored.LockoutEnabled);

        // Only the hash is stored, and it matches the password shown once.
        Assert.NotEqual(temporaryPassword, stored.PasswordHash);
        Assert.DoesNotContain(stored.PasswordHash!, body);
        Assert.DoesNotContain(stored.SecurityStamp!, body);
        await _api.WithUserManagerAsync(async userManager =>
        {
            var user = (await userManager.FindByIdAsync(stored.Id.ToString()))!;
            Assert.True(await userManager.CheckPasswordAsync(user, temporaryPassword));
            Assert.Equal(new[] { RoleNames.Instructor }, await userManager.GetRolesAsync(user));
        });
    }

    [Fact]
    public async Task NewInstructor_LogsIn_MustChangeThePassword_ThenHasNormalAccess()
    {
        var created = await CreateInstructorAsync(UniqueEmail());

        var login = await _api.PostLoginAsync(created.Email, created.TemporaryPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using (var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync(Ct)))
        {
            Assert.True(json.RootElement.GetProperty("mustChangePassword").GetBoolean());
            Assert.Equal(RoleNames.Instructor, json.RootElement.GetProperty("user").GetProperty("role").GetString());
        }
        var session = await SessionFromAsync(login);
        await AssertProblemAsync(await _api.GetAsync("/test-only/authorization/instructor", session.AccessToken),
            HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired);

        // The new password must differ from the temporary one.
        await AssertProblemAsync(await _api.ChangePasswordAsync(session.AccessToken, created.TemporaryPassword, created.TemporaryPassword),
            HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        var changed = await _api.ChangePasswordAsync(session.AccessToken, created.TemporaryPassword, NewPassword);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var newSession = await SessionFromAsync(changed);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/test-only/authorization/instructor", newSession.AccessToken)).StatusCode);
        var stored = await _api.FindUserAsync(created.Id);
        Assert.False(stored.MustChangePassword);
        Assert.Null(stored.TemporaryPasswordExpiresAt);
    }

    [Fact]
    public async Task AfterTheConfiguredExpiry_TheTemporaryPasswordNoLongerLogsIn()
    {
        var created = await CreateInstructorAsync(UniqueEmail());

        using (factory.Clock.Advance(TimeSpan.FromHours(72) + TimeSpan.FromMinutes(1)))
        {
            await AssertProblemAsync(await _api.PostLoginAsync(created.Email, created.TemporaryPassword),
                HttpStatusCode.Forbidden, ErrorCodes.TemporaryPasswordExpired);
        }
    }

    [Fact]
    public async Task ExpiryComesFrom_AuthTemporaryPasswordHours()
    {
        var admin = await AdminSessionAsync();
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:TemporaryPasswordHours", "24"));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        HttpResponseMessage response;
        using (factory.Clock.FreezeAt(now))
            response = await client.PostAsJsonAsync(Path, new { fullName = "Day Instructor", email = UniqueEmail() }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(now.AddHours(24), json.RootElement.GetProperty("temporaryPasswordExpiresAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void InvalidTemporaryPasswordHours_PreventsStartup(string hours)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:TemporaryPasswordHours", hours));

        var exception = Record.Exception(() => host.Services);

        Assert.NotNull(exception);
        Assert.Contains("Auth:TemporaryPasswordHours must be greater than 0.", AllMessages(exception));
    }

    [Fact]
    public async Task ExtraFields_AreIgnored_AndTheAccountIsAlwaysAnInstructor()
    {
        var admin = await AdminSessionAsync();
        var email = UniqueEmail();

        var response = await _api.PostJsonAsync(Path, new
        {
            fullName = "Extra Fields",
            email,
            role = RoleNames.Admin,
            mustChangePassword = false,
            accountStatus = "Deactivated",
            createdById = Guid.NewGuid(),
            password = "Chosen-Passw0rd",
        }, admin.AccessToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(RoleNames.Instructor, json.RootElement.GetProperty("role").GetString());
        var stored = await _api.FindUserAsync(json.RootElement.GetProperty("id").GetGuid());
        Assert.True(stored.MustChangePassword);
        Assert.Equal(AccountStatus.Active, stored.AccountStatus);
        Assert.Equal(await SeedAdminIdAsync(), stored.CreatedById);
        await AssertProblemAsync(await _api.PostLoginAsync(email, "Chosen-Passw0rd"), HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        await _api.WithUserManagerAsync(async userManager =>
            Assert.Equal(new[] { RoleNames.Instructor }, await userManager.GetRolesAsync((await userManager.FindByEmailAsync(email))!)));
    }

    [Fact]
    public async Task EachInstructor_GetsADifferentTemporaryPassword()
    {
        var first = await CreateInstructorAsync(UniqueEmail());
        var second = await CreateInstructorAsync(UniqueEmail());

        Assert.NotEqual(first.TemporaryPassword, second.TemporaryPassword);
    }

    // ---- Validation and uniqueness ----

    [Theory]
    [InlineData(null, "valid", "fullName")]
    [InlineData("   ", "valid", "fullName")]
    [InlineData("toolong", "valid", "fullName")]
    [InlineData("Mona\u0007Hassan", "valid", "fullName")]
    [InlineData("Mona Hassan", null, "email")]
    [InlineData("Mona Hassan", "not-an-email", "email")]
    [InlineData("Mona Hassan", "a b@example.com", "email")]
    [InlineData("Mona Hassan", "mona​@example.com", "email")]
    public async Task InvalidRequest_Returns400ValidationFailed_AndCreatesNothing(string? fullName, string? email, string field)
    {
        var admin = await AdminSessionAsync();
        if (fullName == "toolong")
            fullName = new string('a', 151);
        if (email == "valid")
            email = UniqueEmail();

        var before = await UserCountAsync();
        using var problem = await AssertProblemAsync(
            await _api.PostJsonAsync(Path, new { fullName, email }, admin.AccessToken), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(before, await UserCountAsync());
    }

    [Fact]
    public async Task MalformedJson_Returns400ValidationFailed()
    {
        var admin = await AdminSessionAsync();

        await AssertProblemAsync(await _api.PostRawJsonAsync(Path, "{ not json", admin.AccessToken), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("student")]
    [InlineData("student-upper-case")]
    [InlineData("student-with-spaces")]
    [InlineData("deactivated-student")]
    [InlineData("instructor")]
    [InlineData("seeded-admin")]
    public async Task EmailOfAnyExistingAccount_Returns409DuplicateAccount(string existing)
    {
        var admin = await AdminSessionAsync();
        string email;
        if (existing == "seeded-admin")
        {
            email = ApiFactory.SeedAdminEmail;
        }
        else
        {
            var user = await _api.CreateUserAsync(existing == "instructor" ? RoleNames.Instructor : RoleNames.Student);
            if (existing == "deactivated-student")
                await _api.UpdateUserAsync(user.Id, u => u.AccountStatus = AccountStatus.Deactivated);
            email = existing switch
            {
                "student-upper-case" => user.Email!.ToUpperInvariant(),
                "student-with-spaces" => $"  {user.Email}  ",
                _ => user.Email!,
            };
        }

        var before = await UserCountAsync();
        await AssertProblemAsync(await _api.PostJsonAsync(Path, new { fullName = "Duplicate", email }, admin.AccessToken),
            HttpStatusCode.Conflict, ErrorCodes.DuplicateAccount);
        Assert.Equal(before, await UserCountAsync());
    }

    [Fact]
    public async Task ConcurrentRequests_WithTheSameEmail_CreateExactlyOneInstructor()
    {
        var admin = await AdminSessionAsync();
        var email = UniqueEmail();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _api.PostJsonAsync(Path, new { fullName = "Race Instructor", email }, admin.AccessToken)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
            await AssertProblemAsync(loser, HttpStatusCode.Conflict, ErrorCodes.DuplicateAccount);
        Assert.Single(await AllUsersAsync(), u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FailureAfterTheUserIsInserted_LeavesNoAccountBehind()
    {
        // Make the role assignment fail by renaming the Instructor role for the duration of this test.
        // Tests in this collection run one at a time, so no other test sees the renamed role.
        var admin = await AdminSessionAsync();
        var email = UniqueEmail();
        await RenameRoleAsync(RoleNames.Instructor, "InstructorRenamedForTest");
        try
        {
            var response = await _api.PostJsonAsync(Path, new { fullName = "Rollback", email }, admin.AccessToken);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await _api.WithUserManagerAsync(async userManager => Assert.Null(await userManager.FindByEmailAsync(email)));
        }
        finally
        {
            await RenameRoleAsync("InstructorRenamedForTest", RoleNames.Instructor);
        }
    }

    // ---- Authorization ----

    [Fact]
    public async Task WithoutAToken_Returns401TokenInvalid() =>
        await AssertProblemAsync(await _api.PostJsonAsync(Path, new { fullName = "No Token", email = UniqueEmail() }),
            HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);

    [Theory]
    [InlineData(RoleNames.Student)]
    [InlineData(RoleNames.Instructor)]
    public async Task NonAdmins_Get403_AndNothingIsCreated(string role)
    {
        var user = await _api.CreateUserAsync(role);
        var session = await _api.LoginAsync(user);
        var email = UniqueEmail();

        var response = await _api.PostJsonAsync(Path, new { fullName = "Not Allowed", email }, session.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _api.WithUserManagerAsync(async userManager => Assert.Null(await userManager.FindByEmailAsync(email)));
    }

    [Fact]
    public async Task AnAdminWithAPendingPasswordChange_Gets403PasswordChangeRequired()
    {
        var admin = await _api.CreateUserAsync(RoleNames.Admin, mustChangePassword: true,
            temporaryPasswordExpiresAt: DateTimeOffset.UtcNow.AddHours(72));
        var session = await _api.LoginAsync(admin);

        await AssertProblemAsync(await _api.PostJsonAsync(Path, new { fullName = "Blocked", email = UniqueEmail() }, session.AccessToken),
            HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired);
    }

    // ---- Helpers ----

    private sealed record CreatedInstructor(Guid Id, string Email, string TemporaryPassword);

    private async Task<Session> AdminSessionAsync()
    {
        var response = await _api.PostLoginAsync(ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SessionFromAsync(response);
    }

    private async Task<CreatedInstructor> CreateInstructorAsync(string email)
    {
        var admin = await AdminSessionAsync();
        var response = await _api.PostJsonAsync(Path, new { fullName = "Test Instructor", email }, admin.AccessToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return new CreatedInstructor(
            json.RootElement.GetProperty("id").GetGuid(),
            json.RootElement.GetProperty("email").GetString()!,
            json.RootElement.GetProperty("temporaryPassword").GetString()!);
    }

    private async Task<Guid> SeedAdminIdAsync()
    {
        Guid id = default;
        await _api.WithUserManagerAsync(async userManager => id = (await userManager.FindByEmailAsync(ApiFactory.SeedAdminEmail))!.Id);
        return id;
    }

    private async Task<int> UserCountAsync() => (await AllUsersAsync()).Count;

    private async Task<List<ApplicationUser>> AllUsersAsync()
    {
        List<ApplicationUser> users = [];
        await _api.WithUserManagerAsync(userManager =>
        {
            users = [.. userManager.Users];
            return Task.CompletedTask;
        });
        return users;
    }

    private async Task RenameRoleAsync(string from, string to)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = await roleManager.FindByNameAsync(from) ?? throw new InvalidOperationException($"Role {from} not found.");
        Assert.True((await roleManager.SetRoleNameAsync(role, to)).Succeeded);
        Assert.True((await roleManager.UpdateAsync(role)).Succeeded);
    }

    private static string UniqueEmail() => $"instructor.{Guid.NewGuid():N}@example.com";

    private static string AllMessages(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
                messages.AddRange(aggregate.InnerExceptions.Select(e => e.Message));
        }

        return string.Join(Environment.NewLine, messages);
    }

    // 16 characters from the approved alphabet (no 0, O, 1, l, I).
    [GeneratedRegex("^[A-HJ-NP-Za-km-z2-9]{16}$")]
    private static partial Regex TemporaryPasswordFormat();
}
