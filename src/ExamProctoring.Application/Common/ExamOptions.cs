namespace ExamProctoring.Application.Common;

// Exam settings, bound from the "Exams" configuration section and validated at startup.
public sealed class ExamOptions
{
    public const string SectionName = "Exams";

    public const int MinWarningLimit = 1;
    public const int MaxWarningLimit = 20;

    // D26: the general warning limit copied onto each new exam. A temporary configuration value until Admin-managed
    // settings exist; the Admin cannot change it through the application yet.
    public int WarningLimit { get; set; } = 20;
}
