using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using ExamProctoring.Domain.Users;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ExamProctoring.Application.Auth;

// Authentication use cases (§4.3). So far: student sign-up and the caller's own account.
public sealed class AuthService(
    UserManager<ApplicationUser> userManager,
    IAppDbContext db,
    IValidator<RegisterStudentRequest> validator,
    IClock clock,
    ICurrentUser currentUser)
{
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

        // The §6.5 password policy as configured on Identity, so it is defined once for every password flow.
        if (!string.IsNullOrEmpty(request.Password))
        {
            var candidate = new ApplicationUser { UserName = request.Email?.Trim(), Email = request.Email?.Trim() };
            var passwordErrors = new List<string>();
            foreach (var passwordValidator in userManager.PasswordValidators)
            {
                var result = await passwordValidator.ValidateAsync(userManager, candidate, request.Password);
                passwordErrors.AddRange(result.Errors.Select(e => e.Description));
            }

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
}
