using Microsoft.AspNetCore.Identity;

namespace ExamProctoring.Domain.Users;

// Every account (Admin, Instructor, Student) in AspNetUsers (§5.2). Email is the login and the UserName.
// No university columns (D11); ConfirmPassword is a request field only and has no column (D12).
public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;

    public AccountStatus AccountStatus { get; set; } = AccountStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }

    // The admin who created an instructor; null for self-registered students and the seeded admin.
    public Guid? CreatedById { get; set; }

    // True while a temporary password is in use (§6.3); "PasswordChangeRequired" is not a separate status.
    public bool MustChangePassword { get; set; }

    public DateTimeOffset? TemporaryPasswordExpiresAt { get; set; }

    public DateTimeOffset? PasswordChangedAt { get; set; }

    public DateTimeOffset? StatusChangedAt { get; set; }

    public Guid? StatusChangedById { get; set; }

    public string? StatusReason { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}
