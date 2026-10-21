using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Identity;
using ExamProctoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ExamProctoring.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    // SQL Server 2022. Keeps JSON columns as nvarchar(max); level 170 would need SQL Server 2025 (§4.6).
    public const int SqlServerCompatibilityLevel = 160;

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // The connection string is read when the DbContext is first created, not at registration,
        // so configuration added later (User Secrets, environment variables, test overrides) is honoured.
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not configured. " +
                    "In development, set it with: dotnet user-secrets set \"ConnectionStrings:Default\" \"...\" --project src/ExamProctoring.Api");

            options.UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(SqlServerCompatibilityLevel));
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        // AddIdentityCore, not AddIdentity: no cookie schemes; the API uses JWT bearer tokens (§4.6).
        services.AddIdentityCore<ApplicationUser>(IdentityConfiguration.Configure)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager();

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<DataSeeder>();
        services.AddOptions<AdminSeedOptions>().BindConfiguration(AdminSeedOptions.SectionName);

        // Account settings (§6.8, D18): validated before the API starts (Program.cs) and again on start.
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName)
            .Validate(options => options.TemporaryPasswordHours > 0, "Auth:TemporaryPasswordHours must be greater than 0.")
            .ValidateOnStart();

        // JWT settings (§6.8): bound from "Jwt", validated before the API starts (Program.cs) and again on start.
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddSingleton<ITokenService, JwtTokenService>();

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database");

        return services;
    }
}
