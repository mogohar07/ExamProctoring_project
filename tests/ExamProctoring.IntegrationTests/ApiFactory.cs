using ExamProctoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExamProctoring.IntegrationTests;

// Hosts the API in memory against a dedicated test database that is recreated for each test run (§4.5).
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Local Windows authentication, no password. Override with EXAMPROCTORING_TEST_CONNECTION if needed.
    private static readonly string TestConnectionString =
        Environment.GetEnvironmentVariable("EXAMPROCTORING_TEST_CONNECTION")
        ?? "Server=localhost;Database=ExamProctoring_Tests;Trusted_Connection=True;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", TestConnectionString);
    }

    public async ValueTask InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }
    // Disposal comes from WebApplicationFactory, which already implements IAsyncDisposable.
}
