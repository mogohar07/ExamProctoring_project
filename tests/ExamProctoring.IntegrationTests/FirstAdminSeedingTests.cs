using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure;
using ExamProctoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExamProctoring.IntegrationTests;

// First-admin seeding (§6.8, D20). The shared test host is seeded at startup with a random admin password per run.
// Cases that need a database without any admin use their own, freshly migrated database, deleted afterwards.
[Collection(ApiCollection.Name)]
public sealed class FirstAdminSeedingTests(ApiFactory factory)
{
    private static readonly string ValidPassword = "Aa1" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- The shared test host (an admin already exists) ----

    [Fact]
    public async Task SeededAdmin_ExistsOnce_WithTheDocumentedState_AndCanLogIn()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var admin = await userManager.FindByEmailAsync(ApiFactory.SeedAdminEmail);

        Assert.NotNull(admin);
        Assert.Equal(new[] { RoleNames.Admin }, await userManager.GetRolesAsync(admin));
        Assert.Equal(AccountStatus.Active, admin.AccountStatus);
        Assert.False(admin.MustChangePassword);
        Assert.Null(admin.TemporaryPasswordExpiresAt);
        Assert.Null(admin.CreatedById);
        Assert.Equal(DataSeeder.AdminFullName, admin.FullName);
        Assert.NotEqual(ApiFactory.SeedAdminPassword, admin.PasswordHash);

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = ApiFactory.SeedAdminEmail, password = ApiFactory.SeedAdminPassword }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync(Ct));
        Assert.Equal(RoleNames.Admin, json.RootElement.GetProperty("user").GetProperty("role").GetString());
        Assert.False(json.RootElement.GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task RunningTheSeederAgain_ChangesNothing()
    {
        var before = await SeedAdminSnapshotAsync(factory.Services);
        var adminsBefore = await AdminCountAsync(factory.Services);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync(Ct);
        }

        Assert.Equal(before, await SeedAdminSnapshotAsync(factory.Services));
        Assert.Equal(adminsBefore, await AdminCountAsync(factory.Services));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("not-an-email", "weak")]
    [InlineData("another.admin@examproctoring.test", "Another-Valid-Passw0rd")]
    public async Task WithAnAdminPresent_TheSeedSettingsAreIgnored_AndStartupSucceeds(string email, string password)
    {
        var before = await SeedAdminSnapshotAsync(factory.Services);
        var adminsBefore = await AdminCountAsync(factory.Services);

        await using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:AdminEmail", email);
            builder.UseSetting("Seed:AdminPassword", password);
        });
        _ = host.Services; // starts the host, which runs the seeder

        Assert.Equal(before, await SeedAdminSnapshotAsync(factory.Services));
        Assert.Equal(adminsBefore, await AdminCountAsync(factory.Services));
    }

    // ---- A database without any admin ----

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "password")]
    [InlineData("seed@examproctoring.test", null)]
    [InlineData("  ", "password")]
    public async Task NoAdmin_AndMissingSettings_FailsClearly_AndCreatesNothing(string? email, string? passwordKind)
    {
        await using var database = await EmptyDatabase.CreateAsync();
        var password = passwordKind is null ? null : ValidPassword;
        await using var services = BuildServices(database.ConnectionString, email, password);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(services));

        Assert.Contains("Seed:AdminEmail and Seed:AdminPassword are not both configured", error.Message);
        Assert.DoesNotContain(ValidPassword, error.Message);
        Assert.Equal(0, await UserCountAsync(services));
    }

    [Fact]
    public async Task NoAdmin_AndAnInvalidEmail_FailsClearly()
    {
        await using var database = await EmptyDatabase.CreateAsync();
        await using var services = BuildServices(database.ConnectionString, "not-an-email", ValidPassword);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(services));

        Assert.Equal("Seed:AdminEmail is not a valid email address.", error.Message);
        Assert.Equal(0, await UserCountAsync(services));
    }

    [Theory]
    [InlineData("short1A")]
    [InlineData("nouppercase1")]
    [InlineData("NoDigitsHere")]
    public async Task NoAdmin_AndAWeakPassword_FailsWithoutShowingThePassword(string weakPassword)
    {
        await using var database = await EmptyDatabase.CreateAsync();
        await using var services = BuildServices(database.ConnectionString, "seed@examproctoring.test", weakPassword);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(services));

        Assert.StartsWith("Seed:AdminPassword does not meet the password policy", error.Message);
        Assert.DoesNotContain(weakPassword, error.Message);
        Assert.Equal(0, await UserCountAsync(services));
    }

    [Fact]
    public async Task NoAdmin_AndAnEmailUsedByAnotherAccount_FailsAndLeavesThatAccountAlone()
    {
        await using var database = await EmptyDatabase.CreateAsync();
        const string email = "taken@examproctoring.test";
        await using var services = BuildServices(database.ConnectionString, email, ValidPassword);
        await using (var scope = services.CreateAsyncScope())
        {
            var student = new ApplicationUser { UserName = email, Email = email, FullName = "Existing Student", CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(student, ValidPassword)).Succeeded);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(services));

        Assert.Equal("Seed:AdminEmail is already used by another account.", error.Message);
        Assert.Equal(0, await AdminCountAsync(services));
        Assert.Equal(1, await UserCountAsync(services));
    }

    [Fact]
    public async Task NoAdmin_AndValidSettings_CreatesExactlyOneAdmin_AndLaterSettingsAreIgnored()
    {
        await using var database = await EmptyDatabase.CreateAsync();
        await using (var first = BuildServices(database.ConnectionString, "first@examproctoring.test", ValidPassword))
            await SeedAsync(first);
        await using var second = BuildServices(database.ConnectionString, "second@examproctoring.test", ValidPassword);

        await SeedAsync(second);

        Assert.Equal(1, await AdminCountAsync(second));
        await using var scope = second.CreateAsyncScope();
        var admin = Assert.Single(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().GetUsersInRoleAsync(RoleNames.Admin));
        Assert.Equal("first@examproctoring.test", admin.Email);
        Assert.False(admin.MustChangePassword);
        Assert.Equal(AccountStatus.Active, admin.AccountStatus);
        var roles = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Roles.Select(r => r.Name).OrderBy(n => n).ToListAsync(Ct);
        Assert.Equal(new[] { RoleNames.Admin, RoleNames.Instructor, RoleNames.Student }, roles);
    }

    [Fact]
    public async Task ConcurrentSeeding_FromSeveralInstances_CreatesExactlyOneAdminAndEachRoleOnce()
    {
        await using var database = await EmptyDatabase.CreateAsync();
        var instances = Enumerable.Range(1, 4)
            .Select(i => BuildServices(database.ConnectionString, $"instance{i}@examproctoring.test", ValidPassword))
            .ToList();
        try
        {
            await Task.WhenAll(instances.Select(SeedAsync));

            Assert.Equal(1, await AdminCountAsync(instances[0]));
            Assert.Equal(1, await UserCountAsync(instances[0]));
            await using var scope = instances[0].CreateAsyncScope();
            var roles = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Roles.Select(r => r.Name).OrderBy(n => n).ToListAsync(Ct);
            Assert.Equal(new[] { RoleNames.Admin, RoleNames.Instructor, RoleNames.Student }, roles);
        }
        finally
        {
            foreach (var instance in instances)
                await instance.DisposeAsync();
        }
    }

    [Fact]
    public async Task NoAdmin_AndMissingSettings_TheApiRefusesToStart()
    {
        await using var database = await EmptyDatabase.CreateAsync();
        await using var api = new EmptyDatabaseApiFactory(database.ConnectionString);

        var exception = Record.Exception(() => api.Services);

        Assert.NotNull(exception);
        Assert.Contains("Seed:AdminEmail and Seed:AdminPassword are not both configured", AllMessages(exception));
    }

    // ---- Helpers ----

    private sealed record AdminSnapshot(Guid Id, string? PasswordHash, string? SecurityStamp, string? ConcurrencyStamp, AccountStatus Status);

    private static async Task<AdminSnapshot> SeedAdminSnapshotAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var admin = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(ApiFactory.SeedAdminEmail))!;
        return new AdminSnapshot(admin.Id, admin.PasswordHash, admin.SecurityStamp, admin.ConcurrencyStamp, admin.AccountStatus);
    }

    private static async Task<int> AdminCountAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().GetUsersInRoleAsync(RoleNames.Admin)).Count;
    }

    private static async Task<int> UserCountAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync(Ct);
    }

    private static async Task SeedAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync(Ct);
    }

    // The Infrastructure services alone, as one API instance would have them, with in-memory settings.
    private static ServiceProvider BuildServices(string connectionString, string? adminEmail, string? adminPassword)
    {
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString };
        if (adminEmail is not null)
            settings["Seed:AdminEmail"] = adminEmail;
        if (adminPassword is not null)
            settings["Seed:AdminPassword"] = adminPassword;

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddLogging();
        services.AddInfrastructure();
        return services.BuildServiceProvider(validateScopes: true);
    }

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

    // A migrated database with no users and no roles, deleted when disposed.
    private sealed class EmptyDatabase : IAsyncDisposable
    {
        private EmptyDatabase(string connectionString) => ConnectionString = connectionString;

        public string ConnectionString { get; }

        public static async Task<EmptyDatabase> CreateAsync()
        {
            var builder = new SqlConnectionStringBuilder(ApiFactory.TestConnectionString) { InitialCatalog = "ExamProctoring_SeedTests" };
            var database = new EmptyDatabase(builder.ConnectionString);
            await using var db = database.CreateContext();
            await db.Database.EnsureDeletedAsync(Ct);
            await db.Database.MigrateAsync(Ct);
            return database;
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await using var db = CreateContext();
            await db.Database.EnsureDeletedAsync();
        }

        private AppDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(ConnectionString, sql => sql.UseCompatibilityLevel(DependencyInjection.SqlServerCompatibilityLevel))
                .Options);
    }

    // The API pointed at a database without an admin, with the Seed settings explicitly empty.
    private sealed class EmptyDatabaseApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
            builder.UseSetting("Seed:AdminEmail", "");
            builder.UseSetting("Seed:AdminPassword", "");
        }
    }
}
