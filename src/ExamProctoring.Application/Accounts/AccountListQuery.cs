using ExamProctoring.Domain.Users;

namespace ExamProctoring.Application.Accounts;

// GET /api/admin/accounts?role=&status=&page=&pageSize= (§6.9, D23). Every value is optional; a missing one means no
// filter or the default. Role and status names match in any letter case.
public sealed record AccountListQuery(string? Role, string? Status, int? Page, int? PageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    // The only roles admins list or manage (D21).
    private static readonly string[] ListedRoles = [RoleNames.Student, RoleNames.Instructor];

    // The role's canonical name, or null when the value is not Student or Instructor.
    internal static string? ParseRole(string value) =>
        ListedRoles.FirstOrDefault(role => string.Equals(role, value, StringComparison.OrdinalIgnoreCase));

    // The status, or null when the value is not one of the status names (numbers are not accepted).
    internal static AccountStatus? ParseStatus(string value) =>
        Enum.GetValues<AccountStatus>()
            .Where(status => string.Equals(status.ToString(), value, StringComparison.OrdinalIgnoreCase))
            .Select(status => (AccountStatus?)status)
            .FirstOrDefault();
}
