using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure;
using ExamProctoring.Infrastructure.Persistence;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ExamProctoring.IntegrationTests.TestSupport.AuthApi;

namespace ExamProctoring.IntegrationTests;

// GET /api/admin/accounts and GET /api/admin/accounts/{id} (§6.9, D21, D23). These run on their own database
// (AccountListHost) and start each test with only the seeded admin, so counts and order can be checked exactly.
[Collection(ApiCollection.Name)]
public sealed class AccountListTests(AccountListHost host) : IClassFixture<AccountListHost>
{
    private const string ListPath = "/api/admin/accounts";

    private static readonly string[] ItemFields =
    [
        "id", "fullName", "email", "role", "accountStatus", "mustChangePassword", "temporaryPasswordExpiresAt",
        "isLockedOut", "createdAt", "lastLoginAt", "statusChangedAt", "statusReason",
    ];

    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly AuthApi _api = new(host);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- List: which accounts, order, shape ----

    [Fact]
    public async Task List_ReturnsOnlyStudentsAndInstructors_NewestFirst_WithExactlyTheD23Fields()
    {
        await host.RemoveAllButTheSeededAdminAsync();
        var oldestStudent = await CreateAccountAsync(RoleNames.Student, BaseTime);
        var instructor = await CreateAccountAsync(RoleNames.Instructor, BaseTime.AddMinutes(1));
        var otherAdmin = await CreateAccountAsync(RoleNames.Admin, BaseTime.AddMinutes(2));
        var newestStudent = await CreateAccountAsync(RoleNames.Student, BaseTime.AddMinutes(3));

        var response = await ListAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = await JsonAsync(response);
        var root = json.RootElement;
        Assert.Equal(new[] { "items", "page", "pageSize", "totalCount" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(20, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(new[] { newestStudent.Id, instructor.Id, oldestStudent.Id }, Ids(root));
        Assert.All(root.GetProperty("items").EnumerateArray(), item => Assert.Equal(ItemFields, item.EnumerateObject().Select(p => p.Name)));

        // Neither the extra admin nor the seeded admin (the caller) is ever listed.
        Assert.DoesNotContain(otherAdmin.Id, Ids(root));
        Assert.DoesNotContain(await _api.SeedAdminIdAsync(), Ids(root));
    }

    [Fact]
    public async Task List_WithNoStudentsOrInstructors_ReturnsAnEmptyPage()
    {
        await host.RemoveAllButTheSeededAdminAsync();

        using var json = await JsonAsync(await ListAsync());

        Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(0, json.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task List_ItemValues_ComeFromTheAccount()
    {
        await host.RemoveAllButTheSeededAdminAsync();
        var adminId = await _api.SeedAdminIdAsync();
        var plain = await CreateAccountAsync(RoleNames.Student, BaseTime);
        var deactivated = await CreateAccountAsync(RoleNames.Instructor, BaseTime.AddMinutes(1), u =>
        {
            u.AccountStatus = AccountStatus.Deactivated;
            u.StatusChangedAt = BaseTime.AddDays(1);
            u.StatusChangedById = adminId;
            u.StatusReason = "Left the university";
        });
        var temporary = await CreateAccountAsync(RoleNames.Instructor, BaseTime.AddMinutes(2), u =>
        {
            u.MustChangePassword = true;
            u.TemporaryPasswordExpiresAt = BaseTime.AddHours(72);
        });
        var locked = await CreateAccountAsync(RoleNames.Student, BaseTime.AddMinutes(3), u => u.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15));
        var lockExpired = await CreateAccountAsync(RoleNames.Student, BaseTime.AddMinutes(4), u => u.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(-1));
        var loggedIn = await CreateAccountAsync(RoleNames.Student, BaseTime.AddMinutes(5), u => u.LastLoginAt = BaseTime.AddDays(2));

        using var json = await JsonAsync(await ListAsync());
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());

        var item = items[plain.Id];
        Assert.Equal(plain.FullName, item.GetProperty("fullName").GetString());
        Assert.Equal(plain.Email, item.GetProperty("email").GetString());
        Assert.Equal(RoleNames.Student, item.GetProperty("role").GetString());
        Assert.Equal("Active", item.GetProperty("accountStatus").GetString());
        Assert.False(item.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("temporaryPasswordExpiresAt").ValueKind);
        Assert.False(item.GetProperty("isLockedOut").GetBoolean());
        Assert.Equal(BaseTime, item.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("lastLoginAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("statusChangedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("statusReason").ValueKind);

        item = items[deactivated.Id];
        Assert.Equal(RoleNames.Instructor, item.GetProperty("role").GetString());
        Assert.Equal("Deactivated", item.GetProperty("accountStatus").GetString());
        Assert.Equal(BaseTime.AddDays(1), item.GetProperty("statusChangedAt").GetDateTimeOffset());
        Assert.Equal("Left the university", item.GetProperty("statusReason").GetString());

        item = items[temporary.Id];
        Assert.True(item.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(BaseTime.AddHours(72), item.GetProperty("temporaryPasswordExpiresAt").GetDateTimeOffset());

        Assert.True(items[locked.Id].GetProperty("isLockedOut").GetBoolean());
        Assert.False(items[lockExpired.Id].GetProperty("isLockedOut").GetBoolean());
        Assert.Equal(BaseTime.AddDays(2), items[loggedIn.Id].GetProperty("lastLoginAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task List_ReflectsRealAdminActions_AndALogin()
    {
        await host.RemoveAllButTheSeededAdminAsync();
        var user = await CreateAccountAsync(RoleNames.Student, BaseTime);
        await _api.LoginAsync(user);
        var admin = await AdminTokenAsync();

        Assert.Equal(HttpStatusCode.NoContent,
            (await _api.PostJsonAsync($"/api/admin/accounts/{user.Id}/deactivate", new { reason = "Suspended" }, admin)).StatusCode);

        using var json = await JsonAsync(await ListAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        var stored = await _api.FindUserAsync(user.Id);
        Assert.Equal("Deactivated", item.GetProperty("accountStatus").GetString());
        Assert.Equal("Suspended", item.GetProperty("statusReason").GetString());
        Assert.Equal(stored.StatusChangedAt, item.GetProperty("statusChangedAt").GetDateTimeOffset());
        Assert.Equal(stored.LastLoginAt, item.GetProperty("lastLoginAt").GetDateTimeOffset());
    }

    // ---- List: filters ----

    [Theory]
    [InlineData("?role=Student", "student-active,student-deactivated")]
    [InlineData("?role=Instructor", "instructor-active,instructor-deactivated")]
    [InlineData("?status=Active", "student-active,instructor-active")]
    [InlineData("?status=Deactivated", "student-deactivated,instructor-deactivated")]
    [InlineData("?role=Student&status=Deactivated", "student-deactivated")]
    [InlineData("?role=Instructor&status=Active", "instructor-active")]
    [InlineData("?role=student&status=deactivated", "student-deactivated")]
    [InlineData("?role=INSTRUCTOR", "instructor-active,instructor-deactivated")]
    [InlineData("?role=&status=", "student-active,student-deactivated,instructor-active,instructor-deactivated")]
    public async Task List_Filters_ByRoleAndStatus(string query, string expected)
    {
        await host.RemoveAllButTheSeededAdminAsync();
        var accounts = new Dictionary<string, ApplicationUser>
        {
            ["student-active"] = await CreateAccountAsync(RoleNames.Student, BaseTime.AddMinutes(4)),
            ["student-deactivated"] = await CreateAccountAsync(RoleNames.Student, BaseTime.AddMinutes(3), u => u.AccountStatus = AccountStatus.Deactivated),
            ["instructor-active"] = await CreateAccountAsync(RoleNames.Instructor, BaseTime.AddMinutes(2)),
            ["instructor-deactivated"] = await CreateAccountAsync(RoleNames.Instructor, BaseTime.AddMinutes(1), u => u.AccountStatus = AccountStatus.Deactivated),
        };
        await CreateAccountAsync(RoleNames.Admin, BaseTime.AddMinutes(5));

        using var json = await JsonAsync(await ListAsync(query));

        var expectedIds = expected.Split(',').Select(name => accounts[name].Id).ToArray();
        Assert.Equal(expectedIds, Ids(json.RootElement));
        Assert.Equal(expectedIds.Length, json.RootElement.GetProperty("totalCount").GetInt32());
    }

    // ---- List: paging ----

    [Fact]
    public async Task List_Pages_With20ByDefault_AndTheRequestedPageSize()
    {
        await host.RemoveAllButTheSeededAdminAsync();
        var newestFirst = await CreateAccountsAsync(25);

        using (var json = await JsonAsync(await ListAsync()))
        {
            Assert.Equal(newestFirst.Take(20), Ids(json.RootElement));
            Assert.Equal(25, json.RootElement.GetProperty("totalCount").GetInt32());
        }

        using (var json = await JsonAsync(await ListAsync("?page=2")))
        {
            Assert.Equal(newestFirst.Skip(20), Ids(json.RootElement));
            Assert.Equal(2, json.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(20, json.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal(25, json.RootElement.GetProperty("totalCount").GetInt32());
        }

        using (var json = await JsonAsync(await ListAsync("?pageSize=100")))
        {
            Assert.Equal(newestFirst, Ids(json.RootElement));
            Assert.Equal(100, json.RootElement.GetProperty("pageSize").GetInt32());
        }
    }

    [Fact]
    public async Task List_PagesTogether_ContainEveryAccountOnce()
    {
        await host.RemoveAllButTheSeededAdminAsync();
        var newestFirst = await CreateAccountsAsync(25);

        var pages = new List<Guid>();
        foreach (var page in new[] { 1, 2, 3 })
        {
            using var json = await JsonAsync(await ListAsync($"?page={page}&pageSize=10"));
            Assert.Equal(page == 3 ? 5 : 10, json.RootElement.GetProperty("items").GetArrayLength());
            pages.AddRange(Ids(json.RootElement));
        }

        Assert.Equal(newestFirst, pages);
    }

    [Theory]
    [InlineData("?page=4&pageSize=10")]
    [InlineData("?page=26&pageSize=1")]
    [InlineData("?page=2147483647&pageSize=100")]
    public async Task List_PagePastTheEnd_IsEmpty_WithTheRealTotalCount(string query)
    {
        await host.RemoveAllButTheSeededAdminAsync();
        await CreateAccountsAsync(25);

        var response = await ListAsync(query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(25, json.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task List_AccountsCreatedAtTheSameMoment_ArePagedWithoutOverlap()
    {
        await host.RemoveAllButTheSeededAdminAsync();
        var first = await CreateAccountAsync(RoleNames.Student, BaseTime);
        var second = await CreateAccountAsync(RoleNames.Instructor, BaseTime);
        var third = await CreateAccountAsync(RoleNames.Student, BaseTime);

        var seen = new List<Guid>();
        foreach (var page in new[] { 1, 2, 3 })
        {
            using var json = await JsonAsync(await ListAsync($"?page={page}&pageSize=1"));
            seen.Add(Assert.Single(Ids(json.RootElement)));
        }

        Assert.Equal(new[] { first.Id, second.Id, third.Id }.Order(), seen.Order());

        // The same request gives the same order every time.
        using var all = await JsonAsync(await ListAsync());
        Assert.Equal(seen, Ids(all.RootElement));
    }

    // ---- List: invalid parameters ----

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?page=-1", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?pageSize=-5", "pageSize")]
    [InlineData("?role=Admin", "role")]
    [InlineData("?role=admin", "role")]
    [InlineData("?role=Teacher", "role")]
    [InlineData("?role=%20Student", "role")]
    [InlineData("?status=Locked", "status")]
    [InlineData("?status=0", "status")]
    [InlineData("?status=1", "status")]
    public async Task List_InvalidParameter_Returns400ValidationFailed_OnThatField(string query, string field)
    {
        using var problem = await AssertProblemAsync(await ListAsync(query), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.Equal(new[] { field }, problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("?page=abc")]
    [InlineData("?pageSize=1.5")]
    [InlineData("?page=99999999999")]
    public async Task List_NonNumericOrOversizedPaging_Returns400ValidationFailed(string query) =>
        await AssertProblemAsync(await ListAsync(query), HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

    // ---- Detail ----

    [Theory]
    [InlineData(RoleNames.Student)]
    [InlineData(RoleNames.Instructor)]
    public async Task Detail_Returns200_WithTheSameFieldsAndValuesAsTheListItem(string role)
    {
        await host.RemoveAllButTheSeededAdminAsync();
        var account = await CreateAccountAsync(role, BaseTime, u =>
        {
            u.AccountStatus = AccountStatus.Deactivated;
            u.StatusChangedAt = BaseTime.AddDays(1);
            u.StatusReason = "Suspended";
            u.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10);
        });

        var response = await _api.GetAsync($"{ListPath}/{account.Id}", await AdminTokenAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var detail = await JsonAsync(response);
        Assert.Equal(ItemFields, detail.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(account.Id, detail.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(role, detail.RootElement.GetProperty("role").GetString());
        Assert.True(detail.RootElement.GetProperty("isLockedOut").GetBoolean());

        using var list = await JsonAsync(await ListAsync());
        var listItem = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(listItem.GetRawText(), detail.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("another admin")]
    [InlineData("own account")]
    [InlineData("unknown id")]
    [InlineData("not a guid")]
    public async Task Detail_OfAnAdminOwnUnknownOrNonGuidId_Returns404AccountNotFound(string target)
    {
        var id = target switch
        {
            "another admin" => (await CreateAccountAsync(RoleNames.Admin, BaseTime)).Id.ToString(),
            "own account" => (await _api.SeedAdminIdAsync()).ToString(),
            "unknown id" => Guid.NewGuid().ToString(),
            _ => "not-a-guid",
        };

        var response = await _api.GetAsync($"{ListPath}/{id}", await AdminTokenAsync());

        await AssertProblemAsync(response, HttpStatusCode.NotFound, ErrorCodes.AccountNotFound);
    }

    // ---- Secrets ----

    [Fact]
    public async Task ListAndDetail_NeverContainPasswordHashesStampsTokensOrTemporaryPasswords()
    {
        await host.RemoveAllButTheSeededAdminAsync();
        var admin = await AdminTokenAsync();
        var student = await CreateAccountAsync(RoleNames.Student, BaseTime);
        var instructor = await CreateAccountAsync(RoleNames.Instructor, BaseTime.AddMinutes(1));
        var session = await _api.LoginAsync(student);
        var issued = await _api.PostWithoutBodyAsync($"/api/admin/accounts/{instructor.Id}/temporary-password", admin);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        using var issuedJson = await JsonAsync(issued);
        var temporaryPassword = issuedJson.RootElement.GetProperty("temporaryPassword").GetString()!;

        var secrets = new List<string> { temporaryPassword, session.AccessToken, session.RefreshToken, Hash(session.RefreshToken) };
        foreach (var id in new[] { student.Id, instructor.Id })
        {
            var stored = await _api.FindUserAsync(id);
            secrets.AddRange([stored.PasswordHash!, stored.SecurityStamp!, stored.ConcurrencyStamp!]);
        }

        var bodies = new[]
        {
            await (await ListAsync()).Content.ReadAsStringAsync(Ct),
            await (await _api.GetAsync($"{ListPath}/{student.Id}", admin)).Content.ReadAsStringAsync(Ct),
            await (await _api.GetAsync($"{ListPath}/{instructor.Id}", admin)).Content.ReadAsStringAsync(Ct),
        };

        foreach (var body in bodies)
        {
            Assert.Contains("\"id\"", body);
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, body));
        }
    }

    // ---- Access ----

    [Theory]
    [InlineData(RoleNames.Student, ListPath)]
    [InlineData(RoleNames.Instructor, ListPath)]
    [InlineData(RoleNames.Student, ListPath + "/{id}")]
    [InlineData(RoleNames.Instructor, ListPath + "/{id}")]
    public async Task NonAdmins_Get403(string role, string path)
    {
        var caller = await CreateAccountAsync(role, BaseTime);
        var token = await TokenForAsync(caller.Id, role);

        var response = await _api.GetAsync(path.Replace("{id}", caller.Id.ToString()), token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(ListPath)]
    [InlineData(ListPath + "/{id}")]
    public async Task WithoutAToken_Returns401TokenInvalid(string path)
    {
        var account = await CreateAccountAsync(RoleNames.Student, BaseTime);

        var response = await host.CreateClient().GetAsync(path.Replace("{id}", account.Id.ToString()), Ct);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.TokenInvalid);
    }

    [Theory]
    [InlineData(ListPath)]
    [InlineData(ListPath + "/{id}")]
    public async Task AnAdminWithAPendingPasswordChange_Gets403PasswordChangeRequired(string path)
    {
        var otherAdmin = await CreateAccountAsync(RoleNames.Admin, BaseTime, u =>
        {
            u.MustChangePassword = true;
            u.TemporaryPasswordExpiresAt = DateTimeOffset.UtcNow.AddHours(72);
        });
        var student = await CreateAccountAsync(RoleNames.Student, BaseTime);
        var token = await TokenForAsync(otherAdmin.Id, RoleNames.Admin);

        var response = await _api.GetAsync(path.Replace("{id}", student.Id.ToString()), token);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.PasswordChangeRequired);
    }

    // ---- Helpers ----

    private async Task<HttpResponseMessage> ListAsync(string query = "") =>
        await _api.GetAsync(ListPath + query, await AdminTokenAsync());

    private async Task<string> AdminTokenAsync() => await TokenForAsync(await _api.SeedAdminIdAsync(), RoleNames.Admin);

    // An access token issued the way login issues one, without logging in (login is rate limited per IP).
    private async Task<string> TokenForAsync(Guid userId, string role) =>
        host.Services.GetRequiredService<ITokenService>().CreateAccessToken(await _api.FindUserAsync(userId), role).Token;

    private async Task<ApplicationUser> CreateAccountAsync(string role, DateTimeOffset createdAt, Action<ApplicationUser>? change = null)
    {
        var user = await _api.CreateUserAsync(role);
        await _api.UpdateUserAsync(user.Id, u =>
        {
            u.CreatedAt = createdAt;
            change?.Invoke(u);
        });
        return await _api.FindUserAsync(user.Id);
    }

    // The given number of Students and Instructors, one minute apart; their ids newest first.
    private async Task<List<Guid>> CreateAccountsAsync(int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
            ids.Add((await CreateAccountAsync(i % 2 == 0 ? RoleNames.Student : RoleNames.Instructor, BaseTime.AddMinutes(i))).Id);

        ids.Reverse();
        return ids;
    }

    private static List<Guid> Ids(JsonElement listResponse) =>
        [.. listResponse.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
}

// The API on its own database, created for AccountListTests and deleted afterwards, seeded with the same admin as the
// shared test host. Each test first removes every account except the seeded admin. The database name is unique to
// this instance, so concurrent test runs never share, reset or delete each other's database.
public sealed class AccountListHost : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _connectionString =
        new SqlConnectionStringBuilder(ApiFactory.TestConnectionString)
        {
            InitialCatalog = $"ExamProctoring_AccountListTests_{Guid.NewGuid():N}",
        }.ConnectionString;

    private bool _databaseDeleted;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await using (var db = CreateContext())
                await db.Database.MigrateAsync(Ct);

            // Starts the API, which seeds the roles and the admin.
            _ = Services;
        }
        catch
        {
            // A failed start still removes the database this instance created.
            await DisposeAsync();
            throw;
        }
    }

    public async Task RemoveAllButTheSeededAdminAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seedAdminEmail = ApiFactory.SeedAdminEmail.ToUpperInvariant();
        await db.Users.Where(u => u.NormalizedEmail != seedAdminEmail).ExecuteDeleteAsync(Ct);
    }

    // Deletes only this instance's own database, once.
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_databaseDeleted)
            return;

        _databaseDeleted = true;
        using (var connection = new SqlConnection(_connectionString))
            SqlConnection.ClearPool(connection);

        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
        builder.UseSetting("Seed:AdminEmail", ApiFactory.SeedAdminEmail);
        builder.UseSetting("Seed:AdminPassword", ApiFactory.SeedAdminPassword);
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString, sql => sql.UseCompatibilityLevel(DependencyInjection.SqlServerCompatibilityLevel))
            .Options);
}
