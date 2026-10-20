using ExamProctoring.Domain.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace ExamProctoring.Infrastructure.Persistence;

// Startup seeding (§6.8). Safe to run any number of times: it only creates what is missing.
// For now it creates the three roles only; the first admin account is added in the admin step.
public sealed class DataSeeder(RoleManager<IdentityRole<Guid>> roleManager, ILogger<DataSeeder> logger)
{
    public async Task SeedAsync()
    {
        foreach (var roleName in RoleNames.All)
        {
            if (await roleManager.RoleExistsAsync(roleName))
                continue;

            var result = await roleManager.CreateAsync(new IdentityRole<Guid>(roleName));
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not create the {roleName} role: {string.Join(", ", result.Errors.Select(e => e.Code))}");
            }

            logger.LogInformation("Created role {Role}", roleName);
        }
    }
}
