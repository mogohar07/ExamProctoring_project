using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExamProctoring.IntegrationTests.TestSupport;

// The API on a real Kestrel server, for behaviour the in-memory test server does not have (it ignores request
// body size limits). Uses the shared test database and its own random signing key. Call UseKestrel(0) and
// StartServer() before CreateClient().
public sealed class KestrelApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", ApiFactory.TestConnectionString);
        builder.UseSetting("Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
        builder.UseSetting("Seed:AdminEmail", ApiFactory.SeedAdminEmail);
        builder.UseSetting("Seed:AdminPassword", ApiFactory.SeedAdminPassword);
    }
}
