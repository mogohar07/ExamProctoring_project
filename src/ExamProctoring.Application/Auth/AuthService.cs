using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Accounts;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExamProctoring.Application.Auth;

// Authentication use cases (§4.3): student sign-up, login, refresh, logout, change password and the caller's own account.
//
// Session changes for one account are serialized: login, refresh, logout and change-password each take the account
// row's update lock as the first write of their transaction (holds across API instances). Login and refresh also
// re-check the security stamp under that lock, so no session can be created or renewed with credentials that a
// concurrent password change has just replaced, and a logout or password change always sees every session.
public sealed class AuthService(
    UserManager<ApplicationUser> userManager,
    IAppDbContext db,
    IValidator<RegisterStudentRequest> validator,
    IValidator<LoginRequest> loginValidator,
    IValidator<RefreshRequest> refreshValidator,
    IValidator<LogoutRequest> logoutValidator,
    IValidator<ChangePasswordRequest> changePasswordValidator,
    ITokenService tokenService,
    AccountSessions sessions,
    IClock clock,
    ICurrentUser currentUser,
    ILogger<AuthService> logger)
{
    // Hash of a random password, verified for unknown emails so that they take as long as a wrong password.
    // Created once per process with the configured hasher (same algorithm and iteration count).
    private static string? _unknownUserPasswordHash;

    // POST /api/auth/login (§6.4). An unknown email and a wrong password get the same answer; the account's
    // status is revealed only after a correct password.
    public async Task<Result<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var fieldErrors = await ValidateFieldsAsync(loginValidator, request, cancellationToken);
        if (fieldErrors.Count > 0)
            return TokenRequestFailed(ErrorCodes.ValidationFailed, "One or more fields are invalid.", fieldErrors);

        var password = request.Password!;
        var user = await userManager.FindByEmailAsync(request.Email!.Trim());
        if (user is null)
        {
            userManager.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), UnknownUserPasswordHash(), password);
            return InvalidCredentials();
        }

        // A locked account stays locked even for the correct password (5 failures → 15 minutes, §6.5).
        if (await userManager.IsLockedOutAsync(user))
            return AccountLocked();

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            await userManager.AccessFailedAsync(user);
            return await userManager.IsLockedOutAsync(user) ? AccountLocked() : InvalidCredentials();
        }

        await userManager.ResetAccessFailedCountAsync(user);

        var now = clock.UtcNow;
        if (AccountStateFailure(user, now) is { } accountFailure)
            return accountFailure;

        var role = await SingleRoleAsync(user);

        // Every login starts its own session (token family, D3). Only the refresh token's hash is stored.
        var refreshToken = tokenService.CreateRefreshToken();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Sets LastLoginAt and takes the account row lock in one statement, only if the account is still active
            // with the security stamp read above. A direct update, so a concurrent failed-login count cannot make it fail.
            var stillCurrent = await db.Users
                .Where(u => u.Id == user.Id && u.SecurityStamp == user.SecurityStamp && u.AccountStatus == AccountStatus.Active)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LastLoginAt, now), cancellationToken);
            if (stillCurrent == 0)
            {
                // The password was changed (or the account deactivated) while this login was being checked.
                await transaction.RollbackAsync(CancellationToken.None);
                return InvalidCredentials();
            }

            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = refreshToken.Hash,
                FamilyId = Guid.NewGuid(),
                CreatedAt = now,
                ExpiresAt = refreshToken.ExpiresAt,
            });
            await db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        return Result<TokenResponse>.Success(CreateTokenResponse(user, role, refreshToken));
    }

    // POST /api/auth/refresh (§6.6). The refresh token is the credential. A valid token is rotated: it is revoked
    // and replaced by a new one in the same family, valid for another Jwt:RefreshTokenDays (rolling).
    // Replaying a token that was already rotated revokes the whole family (D4, D5); there is no grace period.
    public async Task<Result<TokenResponse>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        var fieldErrors = await ValidateFieldsAsync(refreshValidator, request, cancellationToken);
        if (fieldErrors.Count > 0)
            return TokenRequestFailed(ErrorCodes.ValidationFailed, "One or more fields are invalid.", fieldErrors);

        var now = clock.UtcNow;
        var tokenHash = tokenService.HashRefreshToken(request.RefreshToken!);
        var presented = await db.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        if (presented is null)
            return RefreshTokenInvalid();

        if (presented.RevokedAt is not null)
        {
            // ReplacedByTokenId, not the free-text reason, tells a rotated token from one revoked by logout or a
            // password change: only a rotated token presented again is a replay.
            return presented.ReplacedByTokenId is null
                ? RefreshTokenInvalid()
                : await RevokeFamilyAfterReuseAsync(presented, now);
        }

        if (presented.ExpiresAt <= now)
            return RefreshTokenInvalid();

        // Tokens are deleted with their user (cascade), so a missing user cannot normally happen.
        var user = await userManager.FindByIdAsync(presented.UserId.ToString());
        if (user is null)
            return RefreshTokenInvalid();
        if (AccountStateFailure(user, now) is { } accountFailure)
            return accountFailure;

        var role = await SingleRoleAsync(user);

        var refreshToken = tokenService.CreateRefreshToken();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (!await sessions.LockAccountAsync(user.Id, cancellationToken, expectedSecurityStamp: user.SecurityStamp))
            {
                // A password change (or deactivation) committed after the checks above: the session is over.
                await transaction.RollbackAsync(CancellationToken.None);
                return RefreshTokenInvalid();
            }

            var replacement = new RefreshToken
            {
                UserId = user.Id,
                TokenHash = refreshToken.Hash,
                FamilyId = presented.FamilyId,
                CreatedAt = now,
                ExpiresAt = refreshToken.ExpiresAt,
            };
            db.RefreshTokens.Add(replacement);
            await db.SaveChangesAsync(cancellationToken);

            // The rotation itself: one conditional UPDATE that only succeeds while the presented token is still
            // unrevoked. SQL Server re-checks the WHERE clause under the row lock, so of two concurrent requests
            // (even on different API instances) exactly one updates the row; the other gets 0 rows.
            var rotated = await db.RefreshTokens
                .Where(t => t.Id == presented.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.RevokedAt, now)
                    .SetProperty(t => t.RevokedReason, RefreshTokenRevocationReasons.Rotated)
                    .SetProperty(t => t.ReplacedByTokenId, replacement.Id), cancellationToken);

            if (rotated == 0)
            {
                // The token was revoked after it was read: undo our replacement and look at why. Rotated by another
                // request means it was used twice, a replay (D4: clients send one refresh at a time per session);
                // revoked by a logout or password change is an ordinary invalid token (D5).
                await transaction.RollbackAsync(CancellationToken.None);
                var current = await db.RefreshTokens.AsNoTracking()
                    .SingleOrDefaultAsync(t => t.Id == presented.Id, CancellationToken.None);
                return current?.ReplacedByTokenId is null
                    ? RefreshTokenInvalid()
                    : await RevokeFamilyAfterReuseAsync(current, now);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return Result<TokenResponse>.Success(CreateTokenResponse(user, role, refreshToken));
    }

    // POST /api/auth/logout (§6.6, §6.9, D3, D8). Ends one session: every still-active token of the given refresh
    // token's family is revoked. Other sessions of the user are untouched, and the access token used for this call
    // expires naturally (§6.6). Unknown tokens, another user's tokens and already revoked or expired tokens get the
    // same success, so the answer reveals nothing about the token.
    public async Task<Result<bool>> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        var fieldErrors = await ValidateFieldsAsync(logoutValidator, request, cancellationToken);
        if (fieldErrors.Count > 0)
            return Result<bool>.Failure(new AppError(ErrorCodes.ValidationFailed, "One or more fields are invalid.", fieldErrors));

        var userId = currentUser.UserId;
        var tokenHash = tokenService.HashRefreshToken(request.RefreshToken!);
        var familyId = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == tokenHash && t.UserId == userId)
            .Select(t => (Guid?)t.FamilyId)
            .SingleOrDefaultAsync(cancellationToken);

        if (familyId is not null)
        {
            var now = clock.UtcNow;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Waits for a refresh of this account that is in progress, so its new token is revoked too.
            await sessions.LockAccountAsync(userId, cancellationToken);
            await sessions.RevokeFamilyAsync(familyId.Value, RefreshTokenRevocationReasons.Logout, now, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        return Result<bool>.Success(true);
    }

    // POST /api/auth/change-password (§6.3, §6.4, §6.5, D7, D9). Requires the current password. On success, in one
    // transaction: the new password hash and a new security stamp (old access tokens stop working), the forced-change
    // flags cleared, every refresh token of the user revoked, and a new session whose tokens are returned.
    public async Task<Result<TokenResponse>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var fieldErrors = await ValidateFieldsAsync(changePasswordValidator, request, cancellationToken);

        // The account gate has just found this user; it is missing only if deleted in between.
        var user = await userManager.FindByIdAsync(currentUser.UserId.ToString());
        if (user is null)
            return TokenRequestFailed(ErrorCodes.TokenInvalid, "A valid access token is required.");

        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            var policyErrors = await PasswordPolicyErrorsAsync(user, request.NewPassword);
            if (policyErrors.Count > 0)
            {
                fieldErrors[nameof(ChangePasswordRequest.NewPassword)] =
                    [.. fieldErrors.GetValueOrDefault(nameof(ChangePasswordRequest.NewPassword), []), .. policyErrors];
            }
        }

        // An invalid request does not count as a failed password attempt.
        if (fieldErrors.Count > 0)
            return TokenRequestFailed(ErrorCodes.ValidationFailed, "One or more fields are invalid.", fieldErrors);

        if (await userManager.IsLockedOutAsync(user))
            return AccountLocked();

        var now = clock.UtcNow;
        if (AccountStateFailure(user, now) is { } accountFailure)
            return accountFailure;

        var role = await SingleRoleAsync(user);

        GeneratedRefreshToken refreshToken;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Verifies the current password, then saves the new hash and a new security stamp. This is the first write,
            // so it also takes the account row lock: a refresh or login in progress finishes first and is revoked below.
            var changed = await userManager.ChangePasswordAsync(user, request.CurrentPassword!, request.NewPassword!);
            if (!changed.Succeeded)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return await ChangePasswordFailedAsync(user, changed);
            }

            user.MustChangePassword = false;
            user.TemporaryPasswordExpiresAt = null;
            user.PasswordChangedAt = now;
            user.AccessFailedCount = 0;
            var updated = await userManager.UpdateAsync(user);
            if (!updated.Succeeded)
                throw new InvalidOperationException($"Could not update the account: {string.Join(", ", updated.Errors.Select(e => e.Code))}");

            // §6.3: a new security stamp always comes with revoking every session of the user.
            await sessions.RevokeAllForUserAsync(user.Id, RefreshTokenRevocationReasons.PasswordChanged, now, cancellationToken);

            refreshToken = tokenService.CreateRefreshToken();
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = refreshToken.Hash,
                FamilyId = Guid.NewGuid(),
                CreatedAt = now,
                ExpiresAt = refreshToken.ExpiresAt,
            });
            await db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        return Result<TokenResponse>.Success(CreateTokenResponse(user, role, refreshToken));
    }

    // GET /api/auth/me (§6.9). Read from the database by the token's subject; the role comes from the token,
    // since a user's single role is fixed when the account is created (§2.2).
    public async Task<Result<MeResponse>> GetMeAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var role = currentUser.Role;

        var me = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new MeResponse(u.Id, u.FullName, u.Email!, role, u.MustChangePassword))
            .SingleOrDefaultAsync(cancellationToken);

        // The account gate has just found this user; this only happens if the account was deleted in between.
        return me is null
            ? Result<MeResponse>.Failure(new AppError(ErrorCodes.TokenInvalid, "A valid access token is required."))
            : Result<MeResponse>.Success(me);
    }

    // §6.4. Creates an Active Student, or returns validation_failed / duplicate_account.
    public async Task<Result<RegisterStudentResponse>> RegisterStudentAsync(
        RegisterStudentRequest request, CancellationToken cancellationToken)
    {
        var fieldErrors = await ValidateAsync(request, cancellationToken);
        if (fieldErrors.Count > 0)
            return ValidationFailed(fieldErrors);

        var fullName = request.FullName!.Trim();
        var email = request.Email!.Trim();

        // Any existing account owns this email, whatever its role or status (Deactivated included).
        if (await EmailIsTakenAsync(email))
            return DuplicateAccount();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = fullName,
            AccountStatus = AccountStatus.Active,
            CreatedAt = clock.UtcNow,
            CreatedById = null,
            MustChangePassword = false,
        };

        // Identity saves the user and the role separately; one transaction makes a failed sign-up leave nothing behind.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var created = await userManager.CreateAsync(user, request.Password!);
            if (!created.Succeeded)
            {
                return created.Errors.Any(IsDuplicate)
                    ? DuplicateAccount()
                    : ValidationFailed(ToFieldErrors(created.Errors));
            }

            var roleAdded = await userManager.AddToRoleAsync(user, RoleNames.Student);
            if (!roleAdded.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not assign the {RoleNames.Student} role: {string.Join(", ", roleAdded.Errors.Select(e => e.Code))}");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);

            // Two sign-ups with the same email at the same moment both passed the check above, and the unique
            // NormalizedEmail / NormalizedUserName index rejected the second insert (§6.4). SQL Server reports
            // the violation only after the other insert has committed, so that account is visible here.
            if (await EmailIsTakenAsync(email))
                return DuplicateAccount();

            throw;
        }

        return Result<RegisterStudentResponse>.Success(
            new RegisterStudentResponse(user.Id, user.FullName, user.Email!, RoleNames.Student));
    }

    private async Task<Dictionary<string, string[]>> ValidateAsync(
        RegisterStudentRequest request, CancellationToken cancellationToken)
    {
        var errors = (await validator.ValidateAsync(request, cancellationToken)).Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

        if (!string.IsNullOrEmpty(request.Password))
        {
            var candidate = new ApplicationUser { UserName = request.Email?.Trim(), Email = request.Email?.Trim() };
            var passwordErrors = await PasswordPolicyErrorsAsync(candidate, request.Password);
            if (passwordErrors.Count > 0)
                errors[nameof(RegisterStudentRequest.Password)] = [.. passwordErrors];
        }

        return errors;
    }

    private async Task<bool> EmailIsTakenAsync(string email) =>
        await userManager.FindByEmailAsync(email) is not null;

    private static bool IsDuplicate(IdentityError error) =>
        error.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName);

    private static Dictionary<string, string[]> ToFieldErrors(IEnumerable<IdentityError> errors) =>
        errors
            .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal)
                ? nameof(RegisterStudentRequest.Password)
                : nameof(RegisterStudentRequest.Email))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());

    private static Result<RegisterStudentResponse> ValidationFailed(IReadOnlyDictionary<string, string[]> fieldErrors) =>
        Result<RegisterStudentResponse>.Failure(
            new AppError(ErrorCodes.ValidationFailed, "One or more fields are invalid.", fieldErrors));

    private static Result<RegisterStudentResponse> DuplicateAccount() =>
        Result<RegisterStudentResponse>.Failure(
            new AppError(ErrorCodes.DuplicateAccount, "An account with this email already exists."));

    private string UnknownUserPasswordHash() =>
        _unknownUserPasswordHash ??= userManager.PasswordHasher.HashPassword(
            new ApplicationUser(), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    // The §6.5 password policy as configured on Identity, so it is defined once for every password flow.
    private async Task<List<string>> PasswordPolicyErrorsAsync(ApplicationUser user, string password)
    {
        var errors = new List<string>();
        foreach (var passwordValidator in userManager.PasswordValidators)
        {
            var result = await passwordValidator.ValidateAsync(userManager, user, password);
            errors.AddRange(result.Errors.Select(e => e.Description));
        }

        return errors;
    }

    // A failed ChangePasswordAsync (the transaction is already rolled back). A wrong current password counts toward
    // lockout (D9) and is reported on currentPassword; a new password that Identity rejects is reported on newPassword.
    private async Task<Result<TokenResponse>> ChangePasswordFailedAsync(ApplicationUser user, IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
        {
            await userManager.AccessFailedAsync(user);
            if (await userManager.IsLockedOutAsync(user))
                return AccountLocked();

            return TokenRequestFailed(ErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { [nameof(ChangePasswordRequest.CurrentPassword)] = ["The current password is incorrect."] });
        }

        if (result.Errors.All(e => e.Code.StartsWith("Password", StringComparison.Ordinal)))
        {
            return TokenRequestFailed(ErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { [nameof(ChangePasswordRequest.NewPassword)] = [.. result.Errors.Select(e => e.Description)] });
        }

        throw new InvalidOperationException($"Changing the password failed: {string.Join(", ", result.Errors.Select(e => e.Code))}");
    }

    private static async Task<Dictionary<string, string[]>> ValidateFieldsAsync<T>(
        IValidator<T> fieldValidator, T request, CancellationToken cancellationToken) =>
        (await fieldValidator.ValidateAsync(request, cancellationToken)).Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

    // Account rules shared by login and refresh: the account must be Active (§6.6), and an expired temporary
    // password cannot be used (D7); an admin must issue a new one. Null when the account may receive tokens.
    private static Result<TokenResponse>? AccountStateFailure(ApplicationUser user, DateTimeOffset now)
    {
        if (user.AccountStatus != AccountStatus.Active)
            return TokenRequestFailed(ErrorCodes.AccountDeactivated, "This account has been deactivated.");

        if (user.MustChangePassword && user.TemporaryPasswordExpiresAt <= now)
        {
            return TokenRequestFailed(ErrorCodes.TemporaryPasswordExpired,
                "The temporary password has expired. Ask an administrator for a new one.");
        }

        return null;
    }

    // Exactly one role per user (§2.2, unique index on AspNetUserRoles.UserId). Anything else is corrupt data.
    private async Task<string> SingleRoleAsync(ApplicationUser user)
    {
        var roles = await userManager.GetRolesAsync(user);
        return roles.Count == 1
            ? roles[0]
            : throw new InvalidOperationException($"User {user.Id} has {roles.Count} roles; exactly one is required.");
    }

    private TokenResponse CreateTokenResponse(ApplicationUser user, string role, GeneratedRefreshToken refreshToken)
    {
        var accessToken = tokenService.CreateAccessToken(user, role);

        return new TokenResponse(
            TokenResponse.BearerTokenType,
            accessToken.Token,
            accessToken.ExpiresAt,
            refreshToken.Token,
            refreshToken.ExpiresAt,
            user.MustChangePassword,
            new AuthUserResponse(user.Id, user.FullName, user.Email!, role));
    }

    // A rotated token was presented again: revoke every still-active token of its family, including the newest
    // one, and log a warning (§6.6). Already-revoked rows keep their original reason and replacement link.
    // Not cancellable: a client that disconnects must not stop the revocation.
    private async Task<Result<TokenResponse>> RevokeFamilyAfterReuseAsync(RefreshToken presented, DateTimeOffset now)
    {
        var revoked = await sessions.RevokeFamilyAsync(presented.FamilyId,
            RefreshTokenRevocationReasons.ReuseDetected, now, CancellationToken.None);

        logger.LogWarning(
            "Refresh token reuse detected for user {UserId}, token family {FamilyId}; revoked {RevokedCount} active token(s).",
            presented.UserId, presented.FamilyId, revoked);

        return TokenRequestFailed(ErrorCodes.RefreshTokenReused,
            "This refresh token was already used. The session has been ended; please log in again.");
    }

    // The same answer for an unknown, malformed, expired or revoked refresh token.
    private static Result<TokenResponse> RefreshTokenInvalid() =>
        TokenRequestFailed(ErrorCodes.TokenInvalid, "The refresh token is invalid or has expired.");

    // The same answer for an unknown email and a wrong password.
    private static Result<TokenResponse> InvalidCredentials() =>
        TokenRequestFailed(ErrorCodes.InvalidCredentials, "The email or password is incorrect.");

    private static Result<TokenResponse> AccountLocked() =>
        TokenRequestFailed(ErrorCodes.AccountLocked, "This account is temporarily locked after too many failed attempts. Try again later.");

    private static Result<TokenResponse> TokenRequestFailed(
        string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        Result<TokenResponse>.Failure(new AppError(code, message, fieldErrors));
}
