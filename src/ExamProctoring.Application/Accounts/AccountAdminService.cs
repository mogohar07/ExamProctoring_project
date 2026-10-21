using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExamProctoring.Application.Accounts;

// Account administration by admins (§4.3, §6.4, §6.9, D18-D24). Admin endpoints manage only Students and
// Instructors; they never create or manage Admin accounts (D21). So far: instructor creation, temporary passwords,
// deactivation and reactivation.
public sealed class AccountAdminService(
    UserManager<ApplicationUser> userManager,
    IAppDbContext db,
    AccountSessions sessions,
    IValidator<CreateInstructorRequest> createInstructorValidator,
    IValidator<DeactivateAccountRequest> deactivateValidator,
    IValidator<ReactivateAccountRequest> reactivateValidator,
    IOptions<AuthOptions> authOptions,
    IClock clock,
    ICurrentUser currentUser,
    ILogger<AccountAdminService> logger)
{
    // POST /api/admin/instructors (§6.4, D9, D18). Creates an Active Instructor with a temporary password that must be
    // changed at first login. The password is returned once in the result, and only its hash is stored.
    public async Task<Result<CreateInstructorResponse>> CreateInstructorAsync(
        CreateInstructorRequest request, CancellationToken cancellationToken)
    {
        if (await ValidationFailedAsync(createInstructorValidator, request, cancellationToken) is { } invalid)
            return Result<CreateInstructorResponse>.Failure(invalid);

        var fullName = request.FullName!.Trim();
        var email = request.Email!.Trim();

        // Email is unique across every account, whatever its role or status (D12).
        if (await EmailIsTakenAsync(email))
            return DuplicateAccount();

        var now = clock.UtcNow;
        var temporaryPassword = TemporaryPasswordGenerator.Generate();
        var instructor = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = fullName,
            AccountStatus = AccountStatus.Active,
            CreatedAt = now,
            CreatedById = currentUser.UserId,
            MustChangePassword = true,
            TemporaryPasswordExpiresAt = now.AddHours(authOptions.Value.TemporaryPasswordHours),
        };

        // Identity saves the user and the role separately; one transaction makes a failed creation leave nothing behind.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var created = await userManager.CreateAsync(instructor, temporaryPassword);
            if (!created.Succeeded)
            {
                if (created.Errors.Any(IsDuplicate))
                    return DuplicateAccount();

                throw new InvalidOperationException(
                    $"Could not create the instructor account: {string.Join(", ", created.Errors.Select(e => e.Code))}");
            }

            var roleAdded = await userManager.AddToRoleAsync(instructor, RoleNames.Instructor);
            if (!roleAdded.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not assign the {RoleNames.Instructor} role: {string.Join(", ", roleAdded.Errors.Select(e => e.Code))}");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);

            // Two requests with the same email at the same moment both passed the check above, and the unique
            // NormalizedEmail / NormalizedUserName index rejected the second insert (§6.4). Same answer as above.
            if (await EmailIsTakenAsync(email))
                return DuplicateAccount();

            throw;
        }

        logger.LogInformation("Admin {AdminId} created instructor account {InstructorId}.", currentUser.UserId, instructor.Id);

        return Result<CreateInstructorResponse>.Success(new CreateInstructorResponse(
            instructor.Id,
            instructor.FullName,
            instructor.Email!,
            RoleNames.Instructor,
            temporaryPassword,
            instructor.TemporaryPasswordExpiresAt!.Value));
    }

    // POST /api/admin/accounts/{id}/temporary-password (§6.4, D18, D19, D21). Replaces the password of a Student or
    // Instructor with a temporary one that must be changed at the next login, clears a lockout and ends every session.
    // The account status is not changed: a deactivated account stays deactivated. The password is returned once in the
    // result, and only its hash is stored.
    public async Task<Result<TemporaryPasswordResponse>> IssueTemporaryPasswordAsync(
        string accountId, CancellationToken cancellationToken)
    {
        if (!TryParseManagedAccountId(accountId, out var targetId))
            return Result<TemporaryPasswordResponse>.Failure(AccountNotFound());

        var now = clock.UtcNow;
        var temporaryPassword = TemporaryPasswordGenerator.Generate();
        var expiresAt = now.AddHours(authOptions.Value.TemporaryPasswordHours);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var account = await LockManagedAccountAsync(targetId, cancellationToken);
        if (account is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<TemporaryPasswordResponse>.Failure(AccountNotFound());
        }

        await EnsureMeetsPasswordPolicyAsync(account, temporaryPassword);
        account.PasswordHash = userManager.PasswordHasher.HashPassword(account, temporaryPassword);
        account.MustChangePassword = true;
        account.TemporaryPasswordExpiresAt = expiresAt;

        // D19: issuing a temporary password also ends a lockout.
        account.LockoutEnd = null;
        account.AccessFailedCount = 0;

        await SaveWithNewSecurityStampAsync(account, RefreshTokenRevocationReasons.TemporaryPasswordIssued, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Admin {AdminId} issued a temporary password for account {AccountId}.", currentUser.UserId, account.Id);

        return Result<TemporaryPasswordResponse>.Success(new TemporaryPasswordResponse(temporaryPassword, expiresAt));
    }

    // POST /api/admin/accounts/{id}/deactivate (§6.3, §6.4, D21, D22). The reason is required.
    public async Task<Result<bool>> DeactivateAsync(
        string accountId, DeactivateAccountRequest request, CancellationToken cancellationToken)
    {
        if (await ValidationFailedAsync(deactivateValidator, request, cancellationToken) is { } invalid)
            return Result<bool>.Failure(invalid);

        return await ChangeStatusAsync(accountId, AccountStatus.Deactivated, request.Reason!.Trim(),
            RefreshTokenRevocationReasons.AccountDeactivated, cancellationToken);
    }

    // POST /api/admin/accounts/{id}/reactivate (§6.3, §6.4, D21, D22). The reason is optional, and so is the body.
    public async Task<Result<bool>> ReactivateAsync(
        string accountId, ReactivateAccountRequest? request, CancellationToken cancellationToken)
    {
        request ??= new ReactivateAccountRequest(null);
        if (await ValidationFailedAsync(reactivateValidator, request, cancellationToken) is { } invalid)
            return Result<bool>.Failure(invalid);

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        return await ChangeStatusAsync(accountId, AccountStatus.Active, reason,
            RefreshTokenRevocationReasons.AccountReactivated, cancellationToken);
    }

    // A status change (§6.3, D22): recorded with when, by whom and why, with a new security stamp and every session of
    // the user revoked, in one transaction under the account row lock. Asking for the current status again changes
    // nothing and is a conflict.
    private async Task<Result<bool>> ChangeStatusAsync(
        string accountId, AccountStatus newStatus, string? reason, string revocationReason, CancellationToken cancellationToken)
    {
        if (!TryParseManagedAccountId(accountId, out var targetId))
            return Result<bool>.Failure(AccountNotFound());

        var now = clock.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var account = await LockManagedAccountAsync(targetId, cancellationToken);
        if (account is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<bool>.Failure(AccountNotFound());
        }

        if (account.AccountStatus == newStatus)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result<bool>.Failure(new AppError(ErrorCodes.AccountStatusUnchanged,
                newStatus == AccountStatus.Deactivated ? "The account is already deactivated." : "The account is already active."));
        }

        account.AccountStatus = newStatus;
        account.StatusChangedAt = now;
        account.StatusChangedById = currentUser.UserId;
        account.StatusReason = reason;

        await SaveWithNewSecurityStampAsync(account, revocationReason, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Admin {AdminId} set account {AccountId} to {AccountStatus}.", currentUser.UserId, account.Id, newStatus);

        return Result<bool>.Success(true);
    }

    // D21: the caller's own account and an id that is not a Guid are answered like an unknown account.
    private bool TryParseManagedAccountId(string accountId, out Guid id) =>
        Guid.TryParse(accountId, out id) && id != currentUser.UserId;

    // Takes the account row lock (§6.3) as the first write of the transaction, then loads the account, so a login,
    // refresh or password change in progress finishes first and nothing changes it until the commit. Null unless the
    // account exists and is a Student or Instructor (D21).
    private async Task<ApplicationUser?> LockManagedAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!await sessions.LockAccountAsync(accountId, cancellationToken))
            return null;

        var account = await userManager.FindByIdAsync(accountId.ToString());
        if (account is null)
            return null;

        return await userManager.GetRolesAsync(account) is [RoleNames.Student or RoleNames.Instructor] ? account : null;
    }

    // §6.3: a new security stamp, saved together with the other changes to the account, always comes with revoking every
    // session of the user in the same transaction. Existing access tokens then stop working on their next request.
    private async Task SaveWithNewSecurityStampAsync(
        ApplicationUser account, string revocationReason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var updated = await userManager.UpdateSecurityStampAsync(account);
        if (!updated.Succeeded)
            throw new InvalidOperationException($"Could not update the account: {string.Join(", ", updated.Errors.Select(e => e.Code))}");

        await sessions.RevokeAllForUserAsync(account.Id, revocationReason, now, cancellationToken);
    }

    // The generator always meets the password policy (§6.5); this stops a later policy change from going unnoticed.
    private async Task EnsureMeetsPasswordPolicyAsync(ApplicationUser account, string password)
    {
        foreach (var passwordValidator in userManager.PasswordValidators)
        {
            var result = await passwordValidator.ValidateAsync(userManager, account, password);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"The generated temporary password does not meet the password policy: {string.Join(", ", result.Errors.Select(e => e.Code))}");
            }
        }
    }

    private static async Task<AppError?> ValidationFailedAsync<T>(
        IValidator<T> fieldValidator, T request, CancellationToken cancellationToken)
    {
        var fieldErrors = (await fieldValidator.ValidateAsync(request, cancellationToken)).Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

        return fieldErrors.Count > 0
            ? new AppError(ErrorCodes.ValidationFailed, "One or more fields are invalid.", fieldErrors)
            : null;
    }

    private async Task<bool> EmailIsTakenAsync(string email) =>
        await userManager.FindByEmailAsync(email) is not null;

    private static bool IsDuplicate(IdentityError error) =>
        error.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName);

    private static Result<CreateInstructorResponse> DuplicateAccount() =>
        Result<CreateInstructorResponse>.Failure(
            new AppError(ErrorCodes.DuplicateAccount, "An account with this email already exists."));

    private static AppError AccountNotFound() => new(ErrorCodes.AccountNotFound, "The account was not found.");
}
