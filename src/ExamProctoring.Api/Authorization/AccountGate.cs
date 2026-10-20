using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace ExamProctoring.Api.Authorization;

// §6.1 check 2, the account gate. Runs after every successful JWT validation: one primary-key lookup of the
// account's status, security stamp and forced-password-change flag. A missing or deactivated account, or a
// security stamp that has changed since the token was issued, fails authentication (401 token_invalid).
public static class AccountGate
{
    private static readonly object PasswordChangeRequiredKey = new();

    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var securityStamp = principal?.FindFirst(AuthClaimTypes.SecurityStamp)?.Value;
        if (!Guid.TryParse(principal?.FindFirst(AuthClaimTypes.Subject)?.Value, out var userId) || string.IsNullOrEmpty(securityStamp))
        {
            context.Fail("The access token has no valid subject or security stamp.");
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<IAppDbContext>();
        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.AccountStatus, u.SecurityStamp, u.MustChangePassword })
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

        if (account is null
            || account.AccountStatus != AccountStatus.Active
            || !string.Equals(account.SecurityStamp, securityStamp, StringComparison.Ordinal))
        {
            context.Fail("The account is not active or the access token is no longer valid.");
            return;
        }

        // Kept per request on the server, not as a claim, so a token can never switch it off.
        if (account.MustChangePassword)
            context.HttpContext.Items[PasswordChangeRequiredKey] = true;
    }

    public static bool IsPasswordChangeRequired(HttpContext httpContext) =>
        httpContext.Items.ContainsKey(PasswordChangeRequiredKey);
}
