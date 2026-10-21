using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExamProctoring.IntegrationTests;

// POST /api/auth/register (§6.4), covering the sign-up items of §6.10.
[Collection(ApiCollection.Name)]
public sealed class StudentSignUpTests(ApiFactory factory)
{
    private const string Password = "Abcdefg1";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ValidRequest_Returns201_WithOnlyIdFullNameEmailAndRole()
    {
        var email = UniqueEmail("sara");

        var response = await RegisterAsync(new { fullName = "  Sara Ahmed  ", email = $"  {email}  ", password = Password, confirmPassword = Password });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(new[] { "id", "fullName", "email", "role" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Sara Ahmed", json.RootElement.GetProperty("fullName").GetString());
        Assert.Equal(email, json.RootElement.GetProperty("email").GetString());
        Assert.Equal(RoleNames.Student, json.RootElement.GetProperty("role").GetString());
        Assert.DoesNotContain(Password, body);

        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.Equal(json.RootElement.GetProperty("id").GetGuid(), user.Id);
        Assert.Equal(email, user.Email);
        Assert.Equal(email, user.UserName);
        Assert.Equal(AccountStatus.Active, user.AccountStatus);
        Assert.False(user.MustChangePassword);
        Assert.Null(user.CreatedById);
        Assert.True(user.LockoutEnabled);
        Assert.NotEqual(Password, user.PasswordHash);
        Assert.Equal(new[] { RoleNames.Student }, await userManager.GetRolesAsync(user));
    }

    [Fact]
    public void ConfirmPassword_HasNoColumn()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var userColumns = db.Model.FindEntityType(typeof(ApplicationUser))!.GetProperties().Select(p => p.Name);

        Assert.DoesNotContain("ConfirmPassword", userColumns);
    }

    [Theory]
    [InlineData(null, "valid", Password, Password, "fullName")]
    [InlineData("   ", "valid", Password, Password, "fullName")]
    [InlineData("toolong", "valid", Password, Password, "fullName")]
    [InlineData("Sara Ahmed", "not-an-email", Password, Password, "email")]
    [InlineData("Sara Ahmed", "a b@example.com", Password, Password, "email")]
    [InlineData("Sara Ahmed", null, Password, Password, "email")]
    [InlineData("Sara Ahmed", "valid", "abcdefgh", "abcdefgh", "password")]
    [InlineData("Sara Ahmed", "valid", "Abc1", "Abc1", "password")]
    [InlineData("Sara Ahmed", "valid", null, null, "password")]
    [InlineData("Sara Ahmed", "valid", Password, "Different1", "confirmPassword")]
    public async Task InvalidRequest_Returns400ValidationFailed_AndCreatesNothing(
        string? fullName, string? email, string? password, string? confirmPassword, string invalidField)
    {
        if (fullName == "toolong")
            fullName = new string('a', 151);
        if (email == "valid")
            email = UniqueEmail("invalid-case");

        var response = await RegisterAsync(new { fullName, email, password, confirmPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await ReadProblemAsync(response);
        Assert.Equal("validation_failed", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(invalidField, out _));
        if (email is not null)
            Assert.Equal(0, await CountUsersAsync(email));
    }

    [Fact]
    public async Task MalformedJson_Returns400ValidationFailed()
    {
        var response = await factory.CreateClient().PostAsync("/api/auth/register",
            new StringContent("{ not json", Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await ReadProblemAsync(response);
        Assert.Equal("validation_failed", problem.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{0}@sha.edu.eg")]          // digits only; the local part is never read as an ID (D11)
    [InlineData("name.{0}@gmail.com")]
    [InlineData("o'brien.{0}@university.edu")]
    [InlineData("josé.{0}@universidad.es")]
    public async Task EmailsFromAnyDomain_AreAccepted(string pattern)
    {
        var email = string.Format(pattern, Random.Shared.NextInt64(100_000_000, 999_999_999));

        var response = await RegisterAsync(new { fullName = "Any Domain", email, password = Password, confirmPassword = Password });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("SPACES")]
    public async Task SameEmail_InAnotherLetterCaseOrWithSurroundingSpaces_Returns409(string variant)
    {
        var email = UniqueEmail("dup");
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(Valid(email))).StatusCode);

        var second = variant == "UPPER" ? email.ToUpperInvariant() : $"  {email}  ";
        var response = await RegisterAsync(Valid(second));

        await AssertDuplicateAsync(response);
        Assert.Equal(1, await CountUsersAsync(email));
    }

    [Fact]
    public async Task LookAlikeEmail_WithAZeroWidthSpace_IsRejectedAsInvalid()
    {
        var email = UniqueEmail("lookalike");
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(Valid(email))).StatusCode);

        var lookAlike = email.Replace("@", "​@");
        var response = await RegisterAsync(Valid(lookAlike));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await ReadProblemAsync(response);
        Assert.Equal("validation_failed", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("email", out _));
        Assert.Equal(0, await CountUsersAsync(lookAlike));
    }

    [Fact]
    public async Task InternationalEmail_IsAccepted()
    {
        var email = $"محمد.{Random.Shared.Next(100_000, 999_999)}@مثال.مصر";

        var response = await RegisterAsync(Valid(email));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(email, json.RootElement.GetProperty("email").GetString());
    }

    [Theory]
    [InlineData(RoleNames.Admin, AccountStatus.Active)]
    [InlineData(RoleNames.Instructor, AccountStatus.Active)]
    [InlineData(RoleNames.Student, AccountStatus.Deactivated)]
    public async Task EmailOfAnExistingAccount_InAnyRoleOrStatus_Returns409(string role, AccountStatus status)
    {
        var email = UniqueEmail("existing");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var existing = new ApplicationUser
            {
                UserName = email, Email = email, FullName = "Existing", AccountStatus = status, CreatedAt = DateTimeOffset.UtcNow,
            };
            Assert.True((await userManager.CreateAsync(existing, Password)).Succeeded);
            Assert.True((await userManager.AddToRoleAsync(existing, role)).Succeeded);
        }

        await AssertDuplicateAsync(await RegisterAsync(Valid(email)));
    }

    [Fact]
    public async Task ConcurrentSignUps_WithTheSameEmail_CreateExactlyOneAccount()
    {
        var email = UniqueEmail("race");

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => RegisterAsync(Valid(email))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await CountUsersAsync(email));
    }

    [Fact]
    public async Task ExtraFields_AreIgnored_AndTheRoleIsAlwaysStudent()
    {
        var email = UniqueEmail("extra");

        var response = await RegisterAsync(new
        {
            fullName = "Extra Fields", email, password = Password, confirmPassword = Password,
            role = RoleNames.Admin, universityId = "324341896",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(RoleNames.Student, json.RootElement.GetProperty("role").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal(new[] { RoleNames.Student }, await userManager.GetRolesAsync((await userManager.FindByEmailAsync(email))!));
    }

    [Fact]
    public async Task FailureAfterTheUserIsInserted_LeavesNoAccountBehind()
    {
        // Make the role assignment fail by renaming the Student role for the duration of this test.
        // Tests in this collection run one at a time, so no other test sees the renamed role.
        var email = UniqueEmail("rollback");
        await RenameRoleAsync(RoleNames.Student, "StudentRenamedForTest");
        try
        {
            var response = await RegisterAsync(Valid(email));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(0, await CountUsersAsync(email));
        }
        finally
        {
            await RenameRoleAsync("StudentRenamedForTest", RoleNames.Student);
        }
    }

    [Fact]
    public async Task MoreThanFiveRequestsPerMinute_FromOneIp_Returns429()
    {
        const string ip = "203.0.113.7";
        var invalidBody = new { fullName = "", email = "", password = "", confirmPassword = "" };

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(invalidBody, ip)).StatusCode);

        var limited = await RegisterAsync(invalidBody, ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);

        // Another address is not affected.
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(invalidBody, "203.0.113.8")).StatusCode);
    }

    [Fact]
    public async Task RoleSeeding_CreatesTheThreeRoles_AndCanRunAgain()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync(Ct);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roles = await db.Roles.Select(r => r.Name).OrderBy(n => n).ToListAsync(Ct);
        Assert.Equal(new[] { RoleNames.Admin, RoleNames.Instructor, RoleNames.Student }, roles);
    }

    private static object Valid(string email) =>
        new { fullName = "Test Student", email, password = Password, confirmPassword = Password };

    private static string UniqueEmail(string prefix) => $"{prefix}.{Guid.NewGuid():N}@example.com";

    private Task<HttpResponseMessage> RegisterAsync(object body, string? clientIp = null)
    {
        var client = factory.CreateClient();
        if (clientIp is not null)
            client.DefaultRequestHeaders.Add(ApiFactory.ClientIpHeader, clientIp);
        return client.PostAsJsonAsync("/api/auth/register", body, Ct);
    }

    private async Task<JsonDocument> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task AssertDuplicateAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = await ReadProblemAsync(response);
        Assert.Equal("duplicate_account", problem.RootElement.GetProperty("code").GetString());
    }

    private async Task<int> CountUsersAsync(string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalized = email.Trim().ToUpperInvariant();
        return await db.Users.CountAsync(u => u.NormalizedEmail == normalized, Ct);
    }

    private async Task RenameRoleAsync(string from, string to)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = await roleManager.FindByNameAsync(from) ?? throw new InvalidOperationException($"Role {from} not found.");
        Assert.True((await roleManager.SetRoleNameAsync(role, to)).Succeeded);
        Assert.True((await roleManager.UpdateAsync(role)).Succeeded);
    }
}
