using ExamProctoring.Domain.Exams;

namespace ExamProctoring.Application.Common;

// Exam settings, bound from the "Exams" configuration section and validated at startup.
public sealed class ExamOptions
{
    public const string SectionName = "Exams";

    public const int MinWarningLimit = 1;
    public const int MaxWarningLimit = 20;

    // D32: the configured MCQ maximum may not go below the fixed minimum number of options. No upper bound.
    public const int MinMaxMcqOptions = QuestionRules.MinMcqOptions;

    // D26: the general warning limit copied onto each new exam. A temporary configuration value until Admin-managed
    // settings exist; the Admin cannot change it through the application yet.
    public int WarningLimit { get; set; } = 20;

    // D32: the most options an MCQ may have, until Admin General Settings exist. Each question create and update checks
    // the question it writes against the value in force at that time (D34).
    public int MaxMcqOptions { get; set; } = 8;
}
