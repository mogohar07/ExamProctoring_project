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
// Instructors; they never create or manage Admin accounts (D21). So far: instructor creation.
public sealed class AccountAdminService(
    UserManager<ApplicationUser> userManager,
    IAppDbContext db,
    IValidator<CreateInstructorRequest> createInstructorValidator,
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
        var fieldErrors = (await createInstructorValidator.ValidateAsync(request, cancellationToken)).Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
        if (fieldErrors.Count > 0)
        {
            return Result<CreateInstructorResponse>.Failure(
                new AppError(ErrorCodes.ValidationFailed, "One or more fields are invalid.", fieldErrors));
        }

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

    private async Task<bool> EmailIsTakenAsync(string email) =>
        await userManager.FindByEmailAsync(email) is not null;

    private static bool IsDuplicate(IdentityError error) =>
        error.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName);

    private static Result<CreateInstructorResponse> DuplicateAccount() =>
        Result<CreateInstructorResponse>.Failure(
            new AppError(ErrorCodes.DuplicateAccount, "An account with this email already exists."));
}
