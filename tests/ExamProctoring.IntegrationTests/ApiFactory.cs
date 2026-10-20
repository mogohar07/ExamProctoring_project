using System.Net;
using ExamProctoring.Infrastructure;
using ExamProctoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    private static readonly string TestConnectionString =
        Environment.GetEnvironmentVariable("EXAMPROCTORING_TEST_CONNECTION")
        ?? "Server=localhost;Database=ExamProctoring_Tests;Trusted_Connection=True;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", TestConnectionString);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestClientIpStartupFilter>());
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
