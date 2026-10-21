using ExamProctoring.Application.Abstractions;
using ExamProctoring.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace ExamProctoring.Application.Accounts;

// Session changes shared by authentication and account administration (§6.3).
// Session changes for one account are serialized: each operation takes the account row's update lock as the first
// write of its transaction (this holds across API instances), and every security-stamp change revokes the user's
// still-active refresh tokens in the same transaction.
public sealed class AccountSessions(IAppDbContext db)
{
    // Takes the account row's update lock for the rest of the current transaction (a no-op update), optionally only
    // if the account is still active with the given security stamp. False when that condition no longer holds.
    public async Task<bool> LockAccountAsync(Guid userId, CancellationToken cancellationToken, string? expectedSecurityStamp = null)
    {
        var accounts = db.Users.Where(u => u.Id == userId);
        if (expectedSecurityStamp is not null)
            accounts = accounts.Where(u => u.SecurityStamp == expectedSecurityStamp && u.AccountStatus == AccountStatus.Active);

        return await accounts.ExecuteUpdateAsync(
            setters => setters.SetProperty(u => u.SecurityStamp, u => u.SecurityStamp), cancellationToken) == 1;
    }

    // Ends one session: every still-active token of the family.
    public Task<int> RevokeFamilyAsync(Guid familyId, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        RevokeActiveTokensAsync(db.RefreshTokens.Where(t => t.FamilyId == familyId), reason, now, cancellationToken);

    // Ends every session of the user, as every security-stamp change must (§6.3).
    public Task<int> RevokeAllForUserAsync(Guid userId, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        RevokeActiveTokensAsync(db.RefreshTokens.Where(t => t.UserId == userId), reason, now, cancellationToken);

    // Already-revoked rows keep their original reason and replacement link, so a rotated token stays
    // recognisable as one (D14).
    private static Task<int> RevokeActiveTokensAsync(
        IQueryable<RefreshToken> tokens, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        tokens.Where(t => t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.RevokedReason, reason), cancellationToken);
}
