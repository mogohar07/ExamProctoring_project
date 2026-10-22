namespace ExamProctoring.Application.Abstractions;

// General exam settings (D26). Read from configuration for now; Admin-managed settings will replace the source later
// without changing the code that uses them.
public interface IExamSettings
{
    // The warning limit copied onto each new exam.
    int WarningLimit { get; }
}
