using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Auth;
using ExamProctoring.Domain.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExamProctoring.Infrastructure.Persistence;

// Startup seeding (§6.8, D20): the three roles and, while no admin exists, the first admin from the Seed settings.
// Safe to run any number of times, and from several API instances at once: everything runs in one transaction that
// first takes a SQL Server application lock, so a second instance waits and then finds the work already done.
public sealed class DataSeeder(
    AppDbContext db,
    RoleManager<IdentityRole<Guid>> roleManager,
    UserManager<ApplicationUser> userManager,
    IOptions<AdminSeedOptions> seedOptions,
    IClock clock,
    ILogger<DataSeeder> logger)
{
    // The seed settings carry no name; FullName is a required column (§5.2).
    public const string AdminFullName = "Administrator";

    private const string SeedLockResource = "ExamProctoring.Seeding";
    private const int SeedLockTimeoutMilliseconds = 60_000;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AcquireSeedLockAsync(cancellationToken);

        await SeedRolesAsync();
        await SeedFirstAdminAsync();

        await transaction.CommitAsync(cancellationToken);
    }

    // Held until the transaction ends. Serializes seeding across API instances.
    private async Task AcquireSeedLockAsync(CancellationToken cancellationToken)
    {
        var result = (await db.Database.SqlQuery<int>(
                $"DECLARE @result int; EXEC @result = sp_getapplock @Resource = {SeedLockResource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = {SeedLockTimeoutMilliseconds}; SELECT @result AS [Value]")
            .ToListAsync(cancellationToken)).Single();

        if (result < 0)
            throw new InvalidOperationException($"Could not obtain the seeding lock (sp_getapplock returned {result}).");
    }

    private async Task SeedRolesAsync()
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

    // D20. With an admin already present, the Seed settings are ignored and nothing changes. Without one, the
    // settings must be present and valid, or startup fails with a message that never contains the password.
    private async Task SeedFirstAdminAsync()
    {
        if ((await userManager.GetUsersInRoleAsync(RoleNames.Admin)).Count > 0)
            return;

        var settings = seedOptions.Value;
        var email = settings.AdminEmail?.Trim();
        var password = settings.AdminPassword;

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "No admin account exists, and Seed:AdminEmail and Seed:AdminPassword are not both configured. " +
                "Set them in User Secrets (development) or as the Seed__AdminEmail and Seed__AdminPassword environment variables.");
        }

        if (!EmailRules.IsValid(email))
            throw new InvalidOperationException("Seed:AdminEmail is not a valid email address.");

        var admin = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = AdminFullName,
            AccountStatus = AccountStatus.Active,
            CreatedAt = clock.UtcNow,
            CreatedById = null,
            MustChangePassword = false,
        };

        // The §6.5 policy as configured on Identity. The messages describe the rule, never the password.
        var policyErrors = new List<string>();
        foreach (var passwordValidator in userManager.PasswordValidators)
        {
            var result = await passwordValidator.ValidateAsync(userManager, admin, password);
            policyErrors.AddRange(result.Errors.Select(e => e.Description));
        }

        if (policyErrors.Count > 0)
            throw new InvalidOperationException($"Seed:AdminPassword does not meet the password policy: {string.Join(" ", policyErrors)}");

        if (await userManager.FindByEmailAsync(email) is not null)
            throw new InvalidOperationException("Seed:AdminEmail is already used by another account.");

        var created = await userManager.CreateAsync(admin, password);
        if (!created.Succeeded)
            throw new InvalidOperationException($"Could not create the first admin account: {string.Join(", ", created.Errors.Select(e => e.Code))}");

        var roleAdded = await userManager.AddToRoleAsync(admin, RoleNames.Admin);
        if (!roleAdded.Succeeded)
            throw new InvalidOperationException($"Could not assign the {RoleNames.Admin} role: {string.Join(", ", roleAdded.Errors.Select(e => e.Code))}");

        logger.LogInformation("Created the first admin account from the Seed settings.");
    }
}
