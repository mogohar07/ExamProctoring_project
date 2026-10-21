namespace ExamProctoring.Application.Common;

// Account settings (§6.8), bound from the "Auth" configuration section and validated at startup.
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    // How long an admin-issued temporary password stays valid (D18).
    public int TemporaryPasswordHours { get; set; } = 72;
}
