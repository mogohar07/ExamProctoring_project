using System.Net;
using System.Security.Cryptography;
using ExamProctoring.Application.Abstractions;
using ExamProctoring.Infrastructure;
using ExamProctoring.Infrastructure.Persistence;
using ExamProctoring.IntegrationTests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExamProctoring.IntegrationTests;

// Hosts the API in memory against a dedicated test database that is recreated for each test run (§4.5).
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Requests carrying this header use its value as the caller's IP address (for rate-limit tests).
    // Without it, every request gets a new address, so per-IP limits never interfere with other tests.
    public const string ClientIpHeader = "X-Test-Client-IP";

    // Local Windows authentication, no password. Override with EXAMPROCTORING_TEST_CONNECTION if needed.
    internal static readonly string TestConnectionString =
        Environment.GetEnvironmentVariable("EXAMPROCTORING_TEST_CONNECTION")
        ?? "Server=localhost;Database=ExamProctoring_Tests;Trusted_Connection=True;TrustServerCertificate=True";

    // A random JWT signing key for this test run: no key in source or configuration files, and the same key
    // for token creation and validation within the run. It replaces the developer's User Secrets key.
    private static readonly string TestSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    // The first admin seeded at startup (D20): a random password for this test run, nothing in source or configuration.
    internal const string SeedAdminEmail = "seed.admin@examproctoring.test";
    internal static readonly string SeedAdminPassword = "Aa1" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    // The API's clock for this test host. Tests can freeze it or move it forward for expiry checks.
    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", TestConnectionString);
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("Seed:AdminEmail", SeedAdminEmail);
        builder.UseSetting("Seed:AdminPassword", SeedAdminPassword);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestClientIpStartupFilter>());

        // Test-only endpoints (TestSupport/AuthorizationProbeController) that exercise the authorization policies.
        // They exist only in this test host, never in the API itself.
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(ApiFactory).Assembly);

            // The real time unless a test freezes or advances it (TestSupport/TestClock).
            services.AddSingleton<IClock>(Clock);
        });
    }

    public async ValueTask InitializeAsync()
    {
        // The database is recreated and migrated before the API starts, as in a real deployment:
        // the API seeds roles at startup and never applies migrations itself.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(TestConnectionString, sql => sql.UseCompatibilityLevel(DependencyInjection.SqlServerCompatibilityLevel))
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }

        // Starts the in-memory API host (and with it the startup seeding).
        _ = Services;
    }
    // Disposal comes from WebApplicationFactory, which already implements IAsyncDisposable.

    private sealed class TestClientIpStartupFilter : IStartupFilter
    {
        private static int _nextAddress = 0x0A000001; // 10.0.0.1

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress =
                    context.Request.Headers.TryGetValue(ClientIpHeader, out var value) && IPAddress.TryParse(value, out var address)
                        ? address
                        : new IPAddress(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(Interlocked.Increment(ref _nextAddress))));
                return nextMiddleware();
            });
            next(app);
        };
    }
}
