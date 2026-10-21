namespace ExamProctoring.Infrastructure.Persistence;

// First-admin seed settings (§6.8, D20), the "Seed" section: User Secrets in Development, the Seed__AdminEmail and
// Seed__AdminPassword environment variables elsewhere. Used only while no admin account exists.
public sealed class AdminSeedOptions
{
    public const string SectionName = "Seed";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }

    // Keeps the password out of logs and debugger output.
    public override string ToString() => nameof(AdminSeedOptions);
}
