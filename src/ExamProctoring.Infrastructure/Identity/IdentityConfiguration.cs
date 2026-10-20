using Microsoft.AspNetCore.Identity;

namespace ExamProctoring.Infrastructure.Identity;

// Identity options fixed by the architecture (§4.6, §6.5).
public static class IdentityConfiguration
{
    public static void Configure(IdentityOptions options)
    {
        // Original Identity schema, as created by the AddIdentityAndRefreshTokens migration.
        // Pinned so that a later migration never adds the passkey table (passkeys are not used, §4.6).
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version1;

        // At least 8 characters with an upper-case letter, a lower-case letter and a digit (§6.5).
        options.Password.RequiredLength = 8;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireDigit = true;
        options.Password.RequireNonAlphanumeric = false;

        // 5 failed attempts lock the account for 15 minutes, for every account (§6.5).
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;

        // Email is the login and is unique across every role (D12). The unique EmailIndex is the final guard.
        options.User.RequireUniqueEmail = true;

        // UserName is the email (§2.2), so the default user-name allow-list (a-z, A-Z, 0-9, -._@+) would reject
        // valid addresses such as o'brien@uni.edu. Empty turns that check off; the email format check that
        // RequireUniqueEmail enables (InvalidEmail) still runs on every create and update.
        options.User.AllowedUserNameCharacters = string.Empty;

        // No email verification in the current flow (§2.5).
        options.SignIn.RequireConfirmedEmail = false;
    }
}
