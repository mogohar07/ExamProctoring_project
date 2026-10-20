using ExamProctoring.Domain.Users;
using ExamProctoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ExamProctoring.IntegrationTests;

// Identity registration and policies (§4.6, §6.5), resolved from the real API host.
[Collection(ApiCollection.Name)]
public sealed class IdentityConfigurationTests(ApiFactory factory)
{
    [Fact]
    public void IdentityOptions_MatchTheArchitecture()
    {
        var options = factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;

        Assert.Equal(IdentitySchemaVersions.Version1, options.Stores.SchemaVersion);

        Assert.Equal(8, options.Password.RequiredLength);
        Assert.True(options.Password.RequireUppercase);
        Assert.True(options.Password.RequireLowercase);
        Assert.True(options.Password.RequireDigit);
        Assert.False(options.Password.RequireNonAlphanumeric);

        Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Lockout.DefaultLockoutTimeSpan);
        Assert.True(options.Lockout.AllowedForNewUsers);

        Assert.True(options.User.RequireUniqueEmail);
        Assert.Equal(string.Empty, options.User.AllowedUserNameCharacters);
        Assert.False(options.SignIn.RequireConfirmedEmail);
    }

    [Fact]
    public void IdentityServices_AreRegistered()
    {
        using var scope = factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>());
    }

    [Fact]
    public async Task Model_HasNoPasskeyTable_AndMatchesTheMigrations()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Null(db.Model.FindEntityType(typeof(IdentityUserPasskey<Guid>)));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("Abcdefg1", true)]
    [InlineData("Abcdef1", false)]   // 7 characters
    [InlineData("abcdefg1", false)]  // no upper-case letter
    [InlineData("ABCDEFG1", false)]  // no lower-case letter
    [InlineData("Abcdefgh", false)]  // no digit
    public async Task PasswordPolicy_IsEnforced(string password, bool expectedValid)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "policy@example.com", Email = "policy@example.com" };

        var results = new List<IdentityResult>();
        foreach (var validator in userManager.PasswordValidators)
            results.Add(await validator.ValidateAsync(userManager, user, password));

        Assert.Equal(expectedValid, results.All(r => r.Succeeded));
    }

    [Fact]
    public async Task CreatingASecondAccountWithTheSameEmail_InAnyLetterCase_IsRejected()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var first = await userManager.CreateAsync(NewUser("Same.Person@Example.com"), "Abcdefg1");
        var second = await userManager.CreateAsync(NewUser("same.person@example.COM"), "Abcdefg1");

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Contains(second.Errors, e => e.Code is "DuplicateEmail" or "DuplicateUserName");
    }

    [Theory]
    [InlineData("o'brien@university.edu")]   // apostrophe, outside Identity's default user-name characters
    [InlineData("josé.garcía@universidad.es")] // non-ASCII letters in the local part
    [InlineData("324341896@sha.edu.eg")]      // any domain; the local part is not an ID (D11)
    public async Task ValidEmails_AreAccepted_AsEmailAndUserName(string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await userManager.CreateAsync(NewUser(email), "Abcdefg1");

        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Code)));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("two@at@example.com")]
    public async Task InvalidEmails_AreRejected(string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await userManager.CreateAsync(NewUser(email), "Abcdefg1");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "InvalidEmail");
    }

    [Theory]
    [InlineData("O'Neil@Example.com", "o'neil@example.COM")]
    [InlineData("José@Universidad.es", "JOSÉ@UNIVERSIDAD.ES")]
    public async Task DuplicateEmails_DifferingOnlyInLetterCase_AreRejected(string first, string second)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        Assert.True((await userManager.CreateAsync(NewUser(first), "Abcdefg1")).Succeeded);
        var duplicate = await userManager.CreateAsync(NewUser(second), "Abcdefg1");

        Assert.False(duplicate.Succeeded);
        Assert.Contains(duplicate.Errors, e => e.Code is "DuplicateEmail" or "DuplicateUserName");
    }

    [Fact]
    public async Task NewAccounts_HaveLockoutEnabled()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = NewUser("lockout@example.com");

        Assert.True((await userManager.CreateAsync(user, "Abcdefg1")).Succeeded);
        Assert.True(await userManager.GetLockoutEnabledAsync(user));
    }

    private static ApplicationUser NewUser(string email) => new()
    {
        UserName = email,
        Email = email,
        FullName = "Test User",
        CreatedAt = DateTimeOffset.UtcNow
    };
}
